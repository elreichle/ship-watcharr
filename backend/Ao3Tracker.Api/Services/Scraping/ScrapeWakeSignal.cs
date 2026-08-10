namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Lets a request wake <see cref="ScrapeWorker"/> instead of leaving a job that is already due to
/// wait out the poll interval.
///
/// Following a tag creates its job with a null <c>NextRunAt</c>, which means "due now" — but the
/// worker only notices on its next tick, so without this a newly followed ship sat idle for up to a
/// minute before its first pass started. This closes that gap without shortening the tick, which
/// would cost a database sweep every few seconds for the rest of the process's life.
///
/// A signal is a nudge to look, never a description of what to look at: the worker re-reads every
/// due job when it wakes. That is what makes losing or coalescing signals harmless — the schedule in
/// the database stays the only authority on what runs.
/// </summary>
public sealed class ScrapeWakeSignal
{
    // Capacity of one, so a burst of follows causes a single sweep rather than one per follow. The
    // sweep the first signal triggers already picks up every job the others made due.
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
