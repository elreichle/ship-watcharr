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
    /// </remarks>
    public void Hold(TimeSpan asked)
    {
        var cap = _options.MaxThrottleHold;
        var wait = asked > cap ? cap : asked;
        if (wait <= TimeSpan.Zero) return;

        var until = _time.GetUtcNow() + wait;

        lock (_lock)
        {
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
        else
        {
            _logger.LogWarning(
                "AO3 asked this instance to wait {Asked}. Holding every outbound request until {Until:u}.",
                asked, until);
        }
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
    /// previous send, and never before the hold.
    /// </summary>
    public async Task WaitForSlotAsync(TimeSpan spacing, CancellationToken ct)
    {
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
