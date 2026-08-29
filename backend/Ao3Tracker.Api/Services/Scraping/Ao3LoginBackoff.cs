namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// How long to leave AO3 alone after it refuses to log this instance in.
///
/// Without this, a wrong password costs the archive two requests a minute for ever. Due jobs are
/// deliberately *held* rather than failed when a login does not work — nothing advances
/// <c>NextRunAt</c>, so they stay due — and the worker therefore asks again on the very next poll.
/// Roughly 2,880 requests a day, half of them failed POSTs to the login endpoint, from an instance
/// whose stored password will not become correct by being tried again. That is indistinguishable
/// from credential stuffing at AO3's end, and it is the exact traffic shape the rest of this
/// subsystem exists to avoid.
///
/// The schedule climbs 5 → 15 → 30 → 60 minutes and stays there, so a permanently wrong password
/// settles at 48 requests a day instead of 2,880.
///
/// **A person fixing the problem must not have to wait it out**, which is the whole difficulty with
/// backing off a configuration error. So the backoff is not a timer the operator is subject to: the
/// admin endpoint calls <see cref="Reset"/> whenever the credential is saved or cleared, and the
/// next poll tries immediately. The cooldown only ever delays retrying something nobody has touched.
///
/// Singleton, and the only mutable state here — the same shape as <see cref="ScrapeWakeSignal"/>,
/// and for the same reason: one long-lived worker and one scoped request have to agree about it.
/// </summary>
public sealed class Ao3LoginBackoff
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(60),
    ];

    private readonly Lock _gate = new();
    private int _consecutiveFailures;
    private DateTime? _retryAfter;

    /// <summary>Whether a login may be attempted now, or is still inside a cooldown.</summary>
    public bool MayAttemptAt(DateTime utcNow)
    {
        lock (_gate) return _retryAfter is null || _retryAfter <= utcNow;
    }

    /// <summary>When the current cooldown ends, or null when there is none.</summary>
    public DateTime? RetryAfter
    {
        get { lock (_gate) return _retryAfter; }
    }

    /// <summary>Records a login that worked, clearing any cooldown.</summary>
    public void RecordSuccess() => Reset();

    /// <summary>
    /// Records a login AO3 would not complete, and returns when the next attempt becomes due.
    /// Applies to a refusal and to an unreachable archive alike: neither is helped by being asked
    /// again in sixty seconds, and both are states a person or the archive has to resolve.
    /// </summary>
    public DateTime RecordFailure(DateTime utcNow)
    {
        lock (_gate)
        {
            var wait = Schedule[Math.Min(_consecutiveFailures, Schedule.Length - 1)];
            _consecutiveFailures++;
            _retryAfter = utcNow + wait;
            return _retryAfter.Value;
        }
    }

    /// <summary>
    /// Forgets the cooldown, so the next poll logs in again. Called when the stored credential
    /// changes — an operator who has just corrected a password is owed an immediate attempt, not a
    /// wait for a backoff that was measuring the old one.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _retryAfter = null;
        }
    }
}
