namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Wakes <see cref="ScrapeWorker"/> when a job becomes due between polls.
///
/// Following a tag creates its job with a null <c>NextRunAt</c>, which means "due now" — but the
/// worker only notices on its next tick, so without this a newly followed ship sat idle for up to a
/// minute before its first pass started. This closes that gap without shortening the tick, which
/// would cost a database sweep every few seconds for the rest of the process's life.
///
/// See <see cref="WakeSignal"/> for the mechanism and for why each worker has one of its own.
/// </summary>
public sealed class ScrapeWakeSignal : WakeSignal;
