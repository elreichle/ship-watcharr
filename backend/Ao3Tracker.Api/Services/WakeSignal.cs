namespace Ao3Tracker.Api.Services;

/// <summary>
/// Lets a request wake a background worker instead of leaving work that is already due to wait out
/// the poll interval.
///
/// A signal is a nudge to look, never a description of what to look at: a woken worker re-reads
/// everything it would have read on a tick. That is what makes losing or coalescing signals
/// harmless — the database stays the only authority on what there is to do.
///
/// Abstract because each worker needs its own: they poll different tables and one worker's nudge
/// must not spend the other's. Subclassing rather than registering two instances of one type keeps
/// them apart in the container by type, which is how everything else here is resolved.
/// </summary>
public abstract class WakeSignal
{
    // Capacity of one, so a burst of requests causes a single sweep rather than one per request.
    // The sweep the first signal triggers already picks up every one the others made due.
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Asks the worker to sweep now. Returns immediately; safe to call from any thread.</summary>
    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A sweep is already pending and has not started yet, so it will see this caller's work
            // too. Checking CurrentCount first instead would race a concurrent Wake and throw here
            // anyway.
        }
    }

    /// <summary>
    /// Waits for a wake or for <paramref name="timeout"/>, whichever comes first. True when woken.
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct = default) =>
        _signal.WaitAsync(timeout, ct);
}
