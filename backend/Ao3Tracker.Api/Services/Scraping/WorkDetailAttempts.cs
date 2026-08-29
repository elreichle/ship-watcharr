using System.Collections.Concurrent;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Which works this process has asked AO3 for and could not read an answer from, and how often in
/// a row — so that a work whose page never reads stops being asked for.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> <see cref="Ao3WorkDetailScraper"/> selects its backlog
/// deterministically: never-fetched first, newest revision first, tie-broken by id. Nothing but a
/// successful read takes a work out of that backlog, so without this a handful of works whose pages
/// cannot be read — an AO3 markup change, a work only its author can see, an address answering 403 —
/// sit at the head of every pass for ever. Ten of them fill the pass, and no other work in the
/// library is ever detail-fetched again. Three of them answering non-OK is worse: they spend
/// <c>MaxConsecutiveFailures</c> and stop the pass on the breaker, every pass, indefinitely.</para>
/// <para><b>In memory rather than a column</b>, and so forgotten by a restart — the same trade
/// <c>DownloadWorker</c> makes for the same reason: a column would be two migrations for a count
/// nothing outside this loop reads, and an instance restarted often enough merely re-attempts a
/// hopeless work. Only works that failed appear here, and a work that reads is forgotten.</para>
/// <para><b>Answers only.</b> A transport failure — the archive unreachable, a socket reset — is
/// counted by the budget's circuit breaker and never here: it says nothing about the work, and
/// counting it would write off ordinary works over an outage that spanned three passes.</para>
/// </remarks>
public sealed class WorkDetailAttempts
{
    /// <summary>
    /// Unreadable answers in a row before a work is left alone for the life of the process.
    ///
    /// Three, as for a queued download, and for the same reason: enough to tell a page that is
    /// refusing from one that failed once, and every one of the three is logged with the work's id
    /// and the address — so nothing is written off that has not been reported three times over.
    /// </summary>
    public const int MaxAttempts = 3;

    private readonly ConcurrentDictionary<long, int> _unreadable = new();

    /// <summary>The works the backlog query must now skip. Empty on a healthy instance.</summary>
    public IReadOnlyList<long> WrittenOff => [.. _unreadable.Where(e => e.Value >= MaxAttempts).Select(e => e.Key)];

    /// <summary>Whether this work has spent its attempts.</summary>
    public bool IsWrittenOff(long workId) => _unreadable.GetValueOrDefault(workId) >= MaxAttempts;

    /// <returns>How many unreadable answers this work has given in a row, including this one.</returns>
    public int RecordUnreadable(long workId) => _unreadable.AddOrUpdate(workId, 1, (_, count) => count + 1);

    /// <summary>Forgets a work: its page read, so whatever was wrong with it is over.</summary>
    public void RecordRead(long workId) => _unreadable.TryRemove(workId, out _);
}
