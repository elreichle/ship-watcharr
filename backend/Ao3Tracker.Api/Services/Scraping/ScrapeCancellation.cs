namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Tells a real shutdown apart from an <see cref="HttpClient"/> timeout.
///
/// Both arrive as <see cref="OperationCanceledException"/> — <c>HttpClient</c> reports its own
/// <c>Timeout</c> elapsing as a <see cref="TaskCanceledException"/>, which derives from it. So the
/// obvious-looking filter <c>when (ex is not OperationCanceledException)</c> does not mean "not a
/// shutdown": it also declines to catch every request timeout, letting one escape the handler that
/// was supposed to absorb it.
///
/// That is not theoretical. It took the whole application down on the first live scrape: a page
/// request hit the 30-second timeout, the exception passed through the scraper's handler and the
/// worker's, failed the <see cref="BackgroundService"/>, and stopped the host — from one slow page.
///
/// The token is the only thing that actually knows. If it has not been signalled, nobody asked for
/// this to stop, so it is a failed request and belongs to the budget and the circuit breaker.
/// </summary>
public static class ScrapeCancellation
{
    /// <summary>
    /// Whether an exception is this operation genuinely being cancelled, rather than a request
    /// timing out. Use as an exception filter's negation:
    /// <c>catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))</c>.
    /// </summary>
    public static bool IsShutdown(Exception exception, CancellationToken ct) =>
        exception is OperationCanceledException && ct.IsCancellationRequested;
}
