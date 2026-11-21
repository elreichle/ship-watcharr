using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// The instance's one outbound channel to AO3: who may send next, how long since the last send,
/// and whether AO3 has asked this instance to stop sending for a while.
/// </summary>
/// <remarks>
/// <para>One of these per process, shared by every <see cref="RateLimitedAo3HttpClient"/> — it used
/// to be two static fields inside that class, a semaphore and a timestamp. It is an object of its
/// own now for three reasons, each a thing statics could not do. The scheduler needs to read the
/// hold below to defer a job to when AO3 said rather than a whole interval later; the waiters need
/// an order other than arrival, so a reader's download is not queued behind a backfill page; and a
/// test that provokes a long hold must not hold every other test in the process.</para>
///
/// <para><b>The hold.</b> AO3 answers a 429 with <c>Retry-After</c>, and what was observed in
/// production is that the value counts down to one deadline: every 429 inside a penalty window
/// names the same end time. That makes the ask instance-wide by nature, and before this it was
/// treated per request — the request that drew the 429 waited, and the next ship's request went out
/// into the same window a few seconds later and drew its own. One window failed fifteen ships in a
/// row at one request each. So the deadline is recorded here, once, and nothing goes out before it,
/// whoever is asking.</para>
///
/// <para><b>Order.</b> Waiters are released by <see cref="Ao3RequestPriority"/>, then by arrival.
/// This changes who goes next and nothing else: the spacing between sends is the same at every
/// priority, so the load on AO3 is unchanged by it.</para>
/// </remarks>
public sealed class Ao3RateGate
{
    private readonly Ao3HttpClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<Ao3RateGate> _logger;

    private readonly Lock _lock = new();
    private readonly PriorityQueue<Waiter, (int Priority, long Arrival)> _waiters = new();
    private long _arrivals;
    private bool _busy;

    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _heldUntil = DateTimeOffset.MinValue;
    private int _throttledInARow;

    // Both set while an admin has paused requests to AO3, and both null otherwise. The source is
    // completed on resume, which releases every request waiting on it.
    private DateTimeOffset? _pausedSince;
    private TaskCompletionSource? _resumed;

    public Ao3RateGate(IOptions<Ao3HttpClientOptions> options, TimeProvider time, ILogger<Ao3RateGate> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The moment AO3 has asked this instance to stay quiet until, or null when it has not — or the
    /// moment has passed.
    /// </summary>
    public DateTimeOffset? HeldUntil
    {
        get
        {
            lock (_lock) return _heldUntil > _time.GetUtcNow() ? _heldUntil : null;
        }
    }

    /// <summary>When an admin paused requests to AO3, or null while they are not paused.</summary>
    public DateTimeOffset? PausedSince
    {
        get
        {
            lock (_lock) return _pausedSince;
        }
    }

    /// <summary>
    /// Stops every request to AO3 from being sent until <see cref="Resume"/>. A request already
    /// waiting for its turn, or arriving later, waits for the resume, then goes out with the usual
    /// spacing. Nothing is failed or dropped. Pausing again while paused keeps the first date.
    /// </summary>
    /// <remarks>
    /// The workers check <see cref="ScrapingGate"/> before starting anything and sit out a pause
    /// without recording a run. This is the backstop for whatever was already in flight when the
    /// pause began, and for any caller that does not check. Nothing leaves the process while
    /// paused, whoever is asking.
    /// </remarks>
    public void Pause(DateTimeOffset since)
    {
        lock (_lock)
        {
            if (_pausedSince is not null) return;
            _pausedSince = since;
            _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _logger.LogWarning("Requests to AO3 are paused. Nothing will be sent to AO3 until they are resumed.");
    }

    /// <summary>Ends a pause and releases every request waiting on it. Does nothing when not paused.</summary>
    public void Resume()
    {
        TaskCompletionSource? resumed;

        lock (_lock)
        {
            resumed = _resumed;
            _resumed = null;
            _pausedSince = null;
        }

        if (resumed is null) return;

        resumed.TrySetResult();
        _logger.LogInformation("Requests to AO3 are resumed.");
    }

    /// <summary>
    /// Records that AO3 asked for <paramref name="asked"/> of quiet. Nothing at all is sent before
    /// then, at any priority.
    /// </summary>
    /// <remarks>
    /// Never shortens a hold already recorded, and never exceeds
    /// <see cref="Ao3HttpClientOptions.MaxThrottleHold"/>: the deadline is a number the archive
    /// chooses, and one misread date header — or one very bad day — must not be able to park the
    /// instance until it is restarted. Past the cap the request that drew the ask still stops
    /// retrying (see <see cref="Ao3HttpClientOptions.MaxRetryAfter"/>), so the instance is not
    /// coming back early with the same request; it is coming back late with fewer.
    ///
    /// A 429 that follows another with no answer between them doubles the hold, to at most
    /// <see cref="Ao3HttpClientOptions.ThrottleEscalationCeiling"/>. What AO3 asks for is a floor,
    /// not a promise that asking again then will be welcome. After the November 2026 outage it
    /// asked for ten seconds, refused the request made once they had passed, and went on like that
    /// for days. <see cref="RecordAnswered"/> ends the run of 429s.
    /// </remarks>
    public void Hold(TimeSpan asked)
    {
        if (asked <= TimeSpan.Zero) return;

        DateTimeOffset until;
        TimeSpan wait;
        int inARow;

        lock (_lock)
        {
            inARow = ++_throttledInARow;
            wait = Escalate(asked, inARow);

            until = _time.GetUtcNow() + wait;
            if (until <= _heldUntil) return;
            _heldUntil = until;
        }

        if (wait < asked)
        {
            _logger.LogWarning(
                "AO3 asked this instance to wait {Asked}; holding every outbound request for {Held}, the most "
                + "this instance will park itself for, and sending nothing to AO3 until {Until:u}.",
                asked, wait, until);
        }
        else if (wait > asked)
        {
            _logger.LogWarning(
                "AO3 asked this instance to wait {Asked}, but that is {InARow} refusals in a row. Holding "
                + "every outbound request for {Held}, until {Until:u}.",
                asked, inARow, wait, until);
        }
        else
        {
            _logger.LogWarning(
                "AO3 asked this instance to wait {Asked}. Holding every outbound request until {Until:u}.",
                asked, until);
        }
    }

    /// <summary>
    /// Records that AO3 answered a request with something other than a 429 or a 5xx, which ends
    /// any run of 429s: the next 429 is held for what it asks and no more.
    /// </summary>
    /// <remarks>
    /// A 5xx leaves the count alone. It says the archive is struggling, not that it has stopped
    /// refusing this instance, so it is no reason to come back sooner.
    /// </remarks>
    public void RecordAnswered()
    {
        lock (_lock) _throttledInARow = 0;
    }

    /// <summary>
    /// What to hold for, given what AO3 asked and how many 429s in a row this one makes: the ask
    /// doubled once per earlier 429 in the run, up to the escalation ceiling. An ask past that
    /// ceiling is taken as asked. Everything is capped at
    /// <see cref="Ao3HttpClientOptions.MaxThrottleHold"/>.
    /// </summary>
    private TimeSpan Escalate(TimeSpan asked, int inARow)
    {
        var ceiling = _options.ThrottleEscalationCeiling;
        var wait = asked;

        for (var i = 1; i < inARow && wait < ceiling; i++) wait *= 2;
        if (wait > ceiling) wait = asked > ceiling ? asked : ceiling;

        var cap = _options.MaxThrottleHold;
        return wait > cap ? cap : wait;
    }

    /// <summary>
    /// Takes the channel. Returns once no other request holds it, with waiters served by priority
    /// and then by arrival. Release with <see cref="Exit"/>, in a <c>finally</c>.
    /// </summary>
    public Task EnterAsync(Ao3RequestPriority priority, CancellationToken ct)
    {
        Waiter waiter;

        lock (_lock)
        {
            if (!_busy)
            {
                _busy = true;
                return Task.CompletedTask;
            }

            waiter = new Waiter();
            _waiters.Enqueue(waiter, ((int)priority, _arrivals++));
        }

        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(static (state, token) =>
                ((Waiter)state!).Completion.TrySetCanceled(token), waiter);

            waiter.Completion.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration, TaskScheduler.Default);
        }

        return waiter.Completion.Task;
    }

    /// <summary>Hands the channel to the best waiter, or frees it when there is none.</summary>
    public void Exit()
    {
        lock (_lock)
        {
            // A waiter whose token was cancelled while it queued has already completed its task
            // and is skipped; the hand-off goes to the first one still waiting.
            while (_waiters.TryDequeue(out var next, out _))
            {
                if (next.Completion.TrySetResult()) return;
            }

            _busy = false;
        }
    }

    /// <summary>
    /// Waits, with the channel held, until a send is allowed: <paramref name="spacing"/> after the
    /// previous send, never before the hold, and never while requests are paused.
    /// </summary>
    public async Task WaitForSlotAsync(TimeSpan spacing, CancellationToken ct)
    {
        // First, so the spacing and the hold below are measured from when the pause ended rather
        // than from when this request started waiting.
        while (true)
        {
            Task? resumed;
            lock (_lock) resumed = _resumed?.Task;
            if (resumed is null) break;

            _logger.LogInformation("Holding this request: requests to AO3 are paused.");
            await resumed.WaitAsync(ct);
        }

        DateTimeOffset due, heldUntil;

        lock (_lock)
        {
            due = _lastSentAt + spacing;
            heldUntil = _heldUntil;
        }

        var now = _time.GetUtcNow();

        if (heldUntil > due)
        {
            due = heldUntil;
            if (due > now)
            {
                _logger.LogInformation(
                    "Holding this request for {Wait} — AO3 asked this instance to stay quiet until {Until:u}.",
                    due - now, heldUntil);
            }
        }
        else if (due > now)
        {
            _logger.LogDebug("Rate limiting: waiting {Delay} (target spacing {Target})", due - now, spacing);
        }

        if (due > now) await Task.Delay(due - now, _time, ct);
    }

    /// <summary>
    /// Stamps a send. Called after every request that left the process, whether or not it was
    /// answered: a timed-out send is still a send AO3 had to field.
    /// </summary>
    public void MarkSent()
    {
        lock (_lock) _lastSentAt = _time.GetUtcNow();
    }

    private sealed class Waiter
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
