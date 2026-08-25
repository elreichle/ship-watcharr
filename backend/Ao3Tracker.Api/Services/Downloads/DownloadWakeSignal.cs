namespace Ao3Tracker.Api.Services.Downloads;

/// <summary>
/// Wakes <see cref="DownloadWorker"/> when a reader asks for a file between polls.
///
/// A queued download is a row and nothing else: without this, someone clicking "EPUB" waits out the
/// poll interval before the fetch even starts queueing behind the rate gate. Its own signal rather
/// than the scraper's, so that asking for a file does not start a sweep of the scrape schedule.
///
/// See <see cref="WakeSignal"/> for the mechanism.
/// </summary>
public sealed class DownloadWakeSignal : WakeSignal;
