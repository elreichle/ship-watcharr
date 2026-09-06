using System.Net;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>What one pass over the detail backlog did.</summary>
/// <param name="WorksSelected">Works this pass set out to read, which is what the backlog offered
/// it up to <see cref="Ao3WorkDetailScraper.MaxWorksPerPass"/> — not what it managed.</param>
/// <param name="WorksRead">Pages AO3 served that parsed as a work page.</param>
/// <param name="WorksWritten">Works whose row was rewritten from one of those pages.</param>
/// <param name="WorksGone">Works AO3 answered 404 for, and which are now marked deleted.</param>
/// <param name="RequestsMade">Requests that reached AO3. Cache hits are excluded, as everywhere.</param>
/// <param name="ParseWarnings">Fields missing from pages that were otherwise read, plus pages that
/// were served and could not be read as a work page at all.</param>
public sealed record WorkDetailPassResult(
    int WorksSelected,
    int WorksRead,
    int WorksWritten,
    int WorksGone,
    int RequestsMade,
    int ParseWarnings);

/// <summary>
/// Reads the works that have never had their own page read, or have been revised since it was.
/// </summary>
public interface IAo3WorkDetailScraper
{
    Task<WorkDetailPassResult> RunAsync(ScrapeBudget budget, CancellationToken ct = default);
}

/// <summary>
/// One request per work, for the two things a listing blurb does not carry: the publication date and
/// the complete tag list. See <see cref="Ao3WorkPageParser"/> for what a page yields,
/// <c>WorkIngestor.IngestDetailAsync</c> for what may be written from it, and
/// <see cref="WorkDetailWorker"/> for what drives this.
///
/// <para><b>Not an <see cref="IAo3Scraper"/>, deliberately.</b> That interface is the vocabulary of
/// the <c>ScrapeJob</c> scheduler, and a job is one row per ship — enforced by a unique index, created
/// when the first user watches a tag, and always carrying <see cref="Ao3ScraperKeys.ShipIndex"/>. A
/// scraper with a key of its own would therefore never be scheduled without a second job per ship and
/// the migration that allows one. It would also be the wrong shape: what a job walks is one tag's
/// listing, and a work's detail is neither ship-scoped nor paginated — it is a per-work fact, shared
/// by every ship the work appears under, and a ship-scoped pass would re-ask for the same work once
/// per tag that carries it. So this is a sibling of the ship walk driven by a worker of its own, the
/// way <c>DownloadFetcher</c> is, sharing the one thing that matters: the global rate gate inside
/// <see cref="IRateLimitedHttpClient"/>.</para>
///
/// <para><b>What it costs, and why the pass is small.</b> A detail fetch is the most expensive thing
/// this application asks AO3 for: one request per work, against an incremental pass's one request per
/// ship per six hours. A library of four thousand works is four thousand requests however they are
/// spread, so the only real question is over how long — see <see cref="MaxWorksPerPass"/> and
/// <see cref="WorkDetailWorker.PassInterval"/>, which together answer roughly a thousand works a day
/// while leaving the gate free for the scrapes and downloads queued behind them.</para>
/// </summary>
public sealed class Ao3WorkDetailScraper : IAo3WorkDetailScraper
{
    /// <summary>
    /// How many works one pass may read.
    ///
    /// Ten works at the shared 5–8 second gate is about a minute of this instance's only outbound
    /// channel, once every <see cref="WorkDetailWorker.PassInterval"/> — so the backlog drains in the
    /// background at a rate the ship walks and the download queue never wait behind for long. It is
    /// a floor on how long a large library takes to fill in, not a ceiling on anything: the budget
    /// stops the pass sooner whenever the instance is already spending its allowance elsewhere.
    /// </summary>
    internal const int MaxWorksPerPass = 10;

    /// <summary>
    /// How long after a work's page was read before a revision to the work earns another read.
    /// </summary>
    /// <remarks>
    /// The page is re-read after a revision for one reason: the author may have changed the tags,
    /// which the blurb carries an abbreviated copy of. But most revisions are chapters, and a work
    /// posting a chapter a day was costing a detail request a day for a tag list that had not
    /// moved. A week's floor turns that into one request a week, and what it costs is a tag added
    /// mid-week showing up at the end of it rather than the next pass — a stale tag, never a lost
    /// one, which is the side <c>WorkIngestor.ApplyTags</c> already chooses to err on. A work never
    /// read is unaffected: its first read is owed at once.
    /// </remarks>
    internal static readonly TimeSpan MinTimeBetweenReads = TimeSpan.FromDays(7);

    private readonly AppDbContext _db;
    private readonly IRateLimitedHttpClient _http;
    private readonly IWorkIngestor _ingestor;
    private readonly WorkDetailAttempts _attempts;
    private readonly Ao3HttpClientOptions _options;
    private readonly ILogger<Ao3WorkDetailScraper> _logger;
    private readonly TimeProvider _time;

    public Ao3WorkDetailScraper(
        AppDbContext db,
        IRateLimitedHttpClient http,
        IWorkIngestor ingestor,
        WorkDetailAttempts attempts,
        IOptions<Ao3HttpClientOptions> options,
        ILogger<Ao3WorkDetailScraper> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _http = http;
        _ingestor = ingestor;
        _attempts = attempts;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkDetailPassResult> RunAsync(ScrapeBudget budget, CancellationToken ct = default)
    {
        var workIds = await SelectBacklogAsync(ct);
        if (workIds.Count == 0) return new WorkDetailPassResult(0, 0, 0, 0, budget.RequestsMade, 0);

        var read = 0;
        var written = 0;
        var gone = 0;
        var warnings = 0;

        foreach (var workId in workIds)
        {
            ct.ThrowIfCancellationRequested();

            // Before every fetch, which is the rule every pass in this codebase follows: the budget
            // owns the request cap, the wall-clock cap and the circuit breaker, and works left over
            // are simply first in line for the next pass.
            if (!budget.CanContinue(out var stopReason))
            {
                _logger.LogInformation(
                    "Detail pass stopped on {StopReason} with {Left} work(s) of this pass unread",
                    stopReason, workIds.Count - read - gone);

                break;
            }

            var url = Ao3WorkPageUrl.For(_options.BaseUrl, workId);

            ScrapeHttpResponse response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
            {
                // Counted, not thrown, and not retried inside the pass: unlike a listing page there
                // is nothing after this work that depends on reading it, so the next work is asked
                // for instead and this one stays at the head of the backlog. The breaker is what
                // stops a pass grinding through ten works against an archive that is down.
                budget.RecordFailure();
                _logger.LogWarning(ex, "Fetching the work page for {WorkId} failed", workId);

                continue;
            }

            if (response.FromCache) budget.RecordCacheHit();

            // A 404 counts with the successes, not against the breaker. It is the conclusive answer
            // this pass exists to be able to record — the work is gone — and charging it to
            // MaxConsecutiveFailures would cut a pass to three works over a run of deleted ones,
            // which is the very case the 404 branch below is for.
            else if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound)
                budget.RecordSuccess();
            else budget.RecordFailure();

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The one place this application is entitled to conclude a work is gone — see
                // Work.IsDeleted. A work vanishing from a ship's listing is not this: that almost
                // always means the author removed the relationship tag, and only a full sweep may
                // conclude even that.
                await MarkDeletedAsync(workId, ct);
                _attempts.RecordRead(workId);
                gone++;

                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                // Already retried where retrying helps: RateLimitedAo3HttpClient handles 429 and 5xx
                // with backoff, so a status arriving here is one AO3 has refused several times over.
                warnings++;
                RecordUnreadable(
                    workId, url, $"AO3 returned {(int)response.StatusCode}");

                continue;
            }

            var page = Ao3WorkPageParser.Parse(response.Content);

            // Two ways an answer can be no use, and neither may be written from. A document with no
            // work meta is not this work's page at all — the adult-content interstitial, a login page
            // for a work only registered users may read, a maintenance page. A work page carrying no
            // tags is a page whose markup moved: AO3 requires a fandom of every work, so an empty
            // list has observed nothing, and stamping DetailFetchedAt from it would additionally put
            // the listing pass into add-only mode for a work nothing could then correct.
            if (!page.IsWorkPage || page.Tags.Count == 0)
            {
                warnings++;
                RecordUnreadable(
                    workId, url,
                    page.IsWorkPage
                        ? $"the work page carried no tags ({response.Content.Length} characters)"
                        : $"what AO3 served is not a work page ({response.Content.Length} characters)");

                continue;
            }

            read++;
            warnings += page.ParseWarnings;
            _attempts.RecordRead(workId);

            try
            {
                if (await _ingestor.IngestDetailAsync(workId, page, ct)) written++;
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
            {
                // One work's write, contained — the same rule the download drain applies per queued
                // request. The listing pass writes the same rows from its own scope, so a save this
                // one loses is not a reason to drop the works behind it: the column is unstamped, so
                // the next pass asks again.
                _logger.LogError(ex, "Writing the detail page of work {WorkId} failed", workId);
            }
        }

        _logger.LogInformation(
            "Detail pass read {Read} of {Selected} work page(s): {Written} written, {Gone} gone, "
            + "{Warnings} parse warning(s), {Requests} request(s)",
            read, workIds.Count, written, gone, warnings, budget.RequestsMade);

        return new WorkDetailPassResult(workIds.Count, read, written, gone, budget.RequestsMade, warnings);
    }

    /// <summary>
    /// The works most worth a request, newest first among those that have never had one.
    /// </summary>
    /// <remarks>
    /// <para>Two things put a work here: no page has ever been read for it, or it has been revised
    /// since one was. The second is what keeps a fetched tag list from going permanently stale, and
    /// it is bounded by AO3's own revision timestamp rather than by an interval of ours — a work
    /// nobody has edited is never asked for twice.</para>
    /// <para>Never-fetched works come first, because they are the ones whose detail page is currently
    /// showing a reader "Not fetched yet". Within each group, the most recently revised first: a
    /// library filling in from the top is the order somebody watching it would choose.</para>
    /// <para>Only works in the library — a work with no ship link is one nothing references, left
    /// behind by an unfollow — and never a work already known deleted, which would be one request per
    /// pass for ever at an address AO3 has already answered 404 for.</para>
    /// </remarks>
    private async Task<List<long>> SelectBacklogAsync(CancellationToken ct)
    {
        // Almost always empty, and materialized rather than queried through: this is process memory,
        // not a table. See WorkDetailAttempts for what a work has to do to get on it.
        var writtenOff = _attempts.WrittenOff;

        // A revised work waits until its last read is a week old — see MinTimeBetweenReads.
        var readBefore = _time.GetUtcNow().UtcDateTime - MinTimeBetweenReads;

        return await _db.Works
            .Where(w => !w.IsDeleted)
            .Where(w => !writtenOff.Contains(w.Id))
            .Where(w => _db.ShipWorks.Any(sw => sw.WorkId == w.Id))
            .Where(w => w.DetailFetchedAt == null
                || (w.UpdatedAt > w.DetailFetchedAt && w.DetailFetchedAt <= readBefore))
            .OrderBy(w => w.DetailFetchedAt == null ? 0 : 1)
            .ThenByDescending(w => w.UpdatedAt)

            // Tie-broken by the key, so a backlog of works sharing a revision timestamp — which a
            // backfill's whole page does, since undated blurbs all read as DateTime.MinValue — has a
            // total order. Without it two passes can be handed the same ten works for ever.
            .ThenBy(w => w.Id)
            .Take(MaxWorksPerPass)
            .Select(w => w.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Records an answer this pass could not use, and says so — including, once a work has spent its
    /// attempts, that nothing will ask for it again until this process restarts.
    /// </summary>
    private void RecordUnreadable(long workId, string url, string because)
    {
        var attempts = _attempts.RecordUnreadable(workId);

        if (attempts < WorkDetailAttempts.MaxAttempts)
        {
            _logger.LogWarning(
                "Nothing was written for work {WorkId} from {Url}: {Because}. Attempt {Attempt} of {Max}.",
                workId, url, because, attempts, WorkDetailAttempts.MaxAttempts);

            return;
        }

        // At error, because this is the state an operator has to act on: the work's own page is
        // never read again on this instance until it restarts, and if the cause is AO3 reshaping its
        // markup then every work is about to arrive here.
        _logger.LogError(
            "Nothing was written for work {WorkId} from {Url}: {Because}. That is {Attempt} unreadable "
            + "answers in a row, so it will not be asked for again before this instance restarts.",
            workId, url, because, attempts);
    }

    private async Task MarkDeletedAsync(long workId, CancellationToken ct)
    {
        var work = await _db.Works.FirstOrDefaultAsync(w => w.Id == workId, ct);
        if (work is null) return;

        work.IsDeleted = true;
        work.DeletedAt = _time.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AO3 answered 404 for work {WorkId}; it is recorded as deleted and will not be asked for again",
            workId);
    }
}
