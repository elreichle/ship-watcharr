using Ao3Tracker.Api.Services.Credentials;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Makes sure the instance has a usable AO3 session before a poll's due jobs run.
/// See <see cref="Ao3SessionProvider"/>.
/// </summary>
public interface IAo3SessionProvider
{
    Task<Ao3LoginResult> EnsureSessionAsync(CancellationToken ct = default);
}

/// <summary>
/// The caller half of the login: decides whether one is needed, and performs at most one.
///
/// Separated from <see cref="Ao3SessionEstablisher"/> so that "do we need to log in" and "how does
/// one log in" fail independently, and so nothing that merely wants a session has to know about
/// authenticity tokens.
///
/// It is also what keeps the dependencies acyclic. The establisher fetches through
/// <see cref="IRateLimitedHttpClient"/>, and that client reads the cached cookie through the
/// narrower <see cref="IAo3SessionCache"/> — which cannot log in. Nothing that logs in is reachable
/// from the thing that attaches cookies, so an expired session is re-established between runs by
/// this, never re-entrantly in the middle of a request.
/// </summary>
public sealed class Ao3SessionProvider : IAo3SessionProvider
{
    private readonly IAo3SessionCache _sessions;
    private readonly IAo3SessionEstablisher _establisher;
    private readonly Ao3LoginBackoff _backoff;
    private readonly TimeProvider _time;

    public Ao3SessionProvider(
        IAo3SessionCache sessions,
        IAo3SessionEstablisher establisher,
        Ao3LoginBackoff backoff,
        TimeProvider? timeProvider = null)
    {
        _sessions = sessions;
        _establisher = establisher;
        _backoff = backoff;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Ao3LoginResult> EnsureSessionAsync(CancellationToken ct = default)
    {
        // The cached cookie is the whole point of caching it: a healthy instance makes this call
        // every poll and sends no request at all.
        var cached = await _sessions.GetUsableAsync(ct);
        if (cached is not null) return new Ao3LoginResult(true);

        var now = _time.GetUtcNow().UtcDateTime;

        // A login that has just been refused is not going to be accepted sixty seconds later, and
        // the jobs waiting on it stay due either way. See Ao3LoginBackoff for why this cannot simply
        // retry every poll, and for why saving a corrected credential skips the wait.
        if (!_backoff.MayAttemptAt(now))
        {
            return new Ao3LoginResult(false, Error:
                $"The last attempt to log in to AO3 did not succeed. The next one is due at "
                + $"{_backoff.RetryAfter:u}; saving the login again at System → Scraping tries "
                + "immediately. Due jobs are held until then, with nothing recorded against them.");
        }

        var result = await _establisher.LogInAsync(ct);

        if (result.Success) _backoff.RecordSuccess();
        else _backoff.RecordFailure(now);

        return result;
    }
}
