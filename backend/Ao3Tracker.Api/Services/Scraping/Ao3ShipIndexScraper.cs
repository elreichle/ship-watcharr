using System.Globalization;
using System.Net;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Walks a relationship tag's works index on AO3.
///
/// Two passes over the same listing, differing only in where they start and where they stop:
///
/// <list type="bullet">
/// <item><description>
/// <see cref="ScrapeRunMode.Incremental"/> reads newest-first from page 1 and stops as soon as it
/// reaches works it has already ingested. On a tag nobody has updated it is one request.
/// </description></item>
/// <item><description>
/// <see cref="ScrapeRunMode.Backfill"/> walks forward from a saved page cursor into the back
/// catalogue, and expects to run out of budget long before it runs out of pages. It resumes from
/// the cursor next time rather than starting over.
/// </description></item>
/// </list>
///
/// <see cref="ScrapeRunMode.FullSweep"/> is not implemented here. It is the only pass allowed to
/// conclude a work has *left* a tag, which needs a complete walk to have finished before absence
/// means anything — a different stopping rule, not a different starting page, so it gets its own
/// implementation rather than a flag on this one.
/// </summary>
public sealed class Ao3ShipIndexScraper : IAo3Scraper
{
    /// <summary>
    /// Hard ceiling on pages per run, independent of the request budget. The budget already bounds
    /// cost; this bounds a pagination *bug* — a "Next" link that always points forward would
    /// otherwise walk until the budget ran out, every run, forever.
    /// </summary>
    private const int MaxPagesPerRun = 200;

    private readonly AppDbContext _db;
    private readonly IRateLimitedHttpClient _http;
    private readonly IWorkIngestor _ingestor;
    private readonly Ao3HttpClientOptions _options;
    private readonly ILogger<Ao3ShipIndexScraper> _logger;
    private readonly TimeProvider _time;

    public Ao3ShipIndexScraper(
        AppDbContext db,
        IRateLimitedHttpClient http,
        IWorkIngestor ingestor,
        IOptions<Ao3HttpClientOptions> options,
        ILogger<Ao3ShipIndexScraper> logger,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _http = http;
        _ingestor = ingestor;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Key => Ao3ScraperKeys.ShipIndex;

    public bool Supports(ScrapeRunMode mode) =>
        mode is ScrapeRunMode.Incremental or ScrapeRunMode.Backfill;

    public async Task<ScrapeOutcome> ExecuteAsync(ScrapeContext context, CancellationToken ct = default)
    {
        var ship = context.Ship;
        var budget = context.Budget;

        // A tag AO3 has denied is not scrapable, and asking anyway is a guaranteed 404 on a shared
        // rate gate. The schedule is normally disabled for these already; this is the belt to that
        // braces, since a job can be re-enabled by a follow before verification catches up.
        if (ship.VerificationState == ShipVerificationState.NotFoundOnAo3)
            return ScrapeOutcome.Empty(ScrapeStopReason.LastPage);

        var startPage = context.Mode == ScrapeRunMode.Backfill ? Math.Max(1, ship.BackfillNextPage ?? 1) : 1;

        // Captured before the walk. Advancing the watermark to the newest work seen is only safe on
        // a run that reached its natural end — see CompleteIncremental.
        var watermark = ship.IncrementalWatermarkUtc;

        var page = startPage;
        var pagesFetched = 0;
        var worksSeen = 0;
        var worksAdded = 0;
        var worksUpdated = 0;
        var parseWarnings = 0;
        int? firstPage = null;
        int? lastPage = null;
        DateTime? newestSeen = null;
        var sawRestricted = false;

        string stopReason;

        if (context.Mode == ScrapeRunMode.Backfill) BeginBackfill(ship);

        while (true)
        {
            if (!budget.CanContinue(out var budgetStop))
            {
                stopReason = budgetStop!;
                break;
            }

            if (pagesFetched >= MaxPagesPerRun)
            {
                _logger.LogWarning(
                    "Ship {ShipId} ({Tag}) hit the {Max}-page ceiling in one run; stopping",
                    ship.Id, ship.CanonicalTagName, MaxPagesPerRun);
                stopReason = ScrapeStopReason.Cap;
                break;
            }

            var url = BuildUrl(ship, page, context.Mode, watermark);

            ScrapeHttpResponse response;
            try
            {
                response = await _http.GetAsync(url, ct);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
            {
                // Counted against the budget, not thrown. A transport failure part-way through a
                // backfill still leaves everything before it committed and the cursor pointing at
                // the page that failed, which is exactly where the next run should start.
                //
                // The filter deliberately catches request timeouts, which arrive as
                // TaskCanceledException — see ScrapeCancellation for what letting one through cost.
                budget.RecordFailure();
                _logger.LogWarning(ex, "Fetching {Url} for ship {ShipId} failed", url, ship.Id);

                if (!budget.CanContinue(out var afterFailure)) { stopReason = afterFailure!; break; }
                continue;
            }

            if (response.FromCache) budget.RecordCacheHit();
            else if (response.StatusCode == HttpStatusCode.OK) budget.RecordSuccess();
            else budget.RecordFailure();

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Walking off the end of a listing is how a backfill finishes; AO3 404s rather than
                // serving an empty page past the last one.
                //
                // But only *past* a page we actually read. A 404 on the very first request of a run
                // says nothing about the end of the listing — it means the URL was wrong or the tag
                // is gone — and calling that "complete" would mark a ship fully backfilled having
                // read nothing at all, then never look again. That is precisely what a wrong
                // address did on the first live run.
                if (pagesFetched == 0)
                {
                    _logger.LogError(
                        "AO3 returned 404 for the first page of ship {ShipId} ({Tag}) at {Url}. "
                        + "Treating this as an error rather than the end of the listing.",
                        ship.Id, ship.CanonicalTagName, url);

                    stopReason = ScrapeStopReason.Error;
                }
                else
                {
                    stopReason = ScrapeStopReason.LastPage;
                }

                break;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning(
                    "AO3 returned {Status} for {Url} (ship {ShipId})", (int)response.StatusCode, url, ship.Id);

                if (!budget.CanContinue(out var afterError)) { stopReason = afterError!; break; }
                continue;
            }

            var listing = Ao3BlurbParser.ParseListing(response.Content);

            pagesFetched++;
            parseWarnings += listing.ParseWarnings;
            firstPage ??= page;
            lastPage = page;

            RecordTotal(ship, listing);

            if (listing.Works.Count == 0)
            {
                // A tag really can be empty, so this is still a legitimate end of the walk. It is
                // also exactly what a markup change looks like, and the two are indistinguishable
                // from here — so the first page yielding nothing is logged loudly enough to be
                // findable, with the response size to tell "AO3 served us an error page" apart from
                // "AO3 served us a listing we can no longer read".
                if (pagesFetched == 1)
                {
                    _logger.LogWarning(
                        "Page {Page} for ship {ShipId} ({Tag}) parsed to no works from {Length} characters "
                        + "of HTML. Either the tag is empty or the listing markup has changed.",
                        page, ship.Id, ship.CanonicalTagName, response.Content.Length);
                }

                stopReason = ScrapeStopReason.LastPage;
                break;
            }

            var fresh = context.Mode == ScrapeRunMode.Incremental
                ? listing.Works.Where(w => watermark is null || w.UpdatedAt > watermark).ToList()
                : listing.Works;

            if (fresh.Count > 0)
            {
                var result = await _ingestor.IngestAsync(ship, fresh, ct);
                worksSeen += result.WorksSeen;
                worksAdded += result.WorksAdded;
                worksUpdated += result.WorksUpdated;

                var pageNewest = fresh.Max(w => w.UpdatedAt);
                if (newestSeen is null || pageNewest > newestSeen) newestSeen = pageNewest;

                sawRestricted |= fresh.Any(w => w.IsRestricted);
            }

            // The watermark stop, and the reason an incremental pass is normally one request: the
            // listing is newest-first, so the first page that is not entirely new means everything
            // after it is older still.
            if (context.Mode == ScrapeRunMode.Incremental && fresh.Count < listing.Works.Count)
            {
                stopReason = ScrapeStopReason.Watermark;
                break;
            }

            if (context.Mode == ScrapeRunMode.Backfill)
            {
                // Advanced only once the page's works are committed, so a crash resumes on the page
                // that was in flight rather than after it.
                ship.BackfillNextPage = page + 1;
                TrackBackfillFloor(ship, listing);
                await _db.SaveChangesAsync(ct);
            }

            if (!listing.HasNextPage)
            {
                stopReason = ScrapeStopReason.LastPage;
                break;
            }

            page++;
        }

        await FinishAsync(context, ship, stopReason, newestSeen, sawRestricted, ct);

        return new ScrapeOutcome(
            pagesFetched, budget.RequestsMade, worksSeen, worksAdded, worksUpdated,
            parseWarnings, firstPage, lastPage, stopReason);
    }

    // ---- URLs --------------------------------------------------------------------------------

    /// <summary>
    /// The listing URL for one page.
    ///
    /// Always addressed by the escaped tag name, never by <see cref="Ship.Ao3TagId"/>. The id would
    /// be preferable in principle — it survives AO3 renaming the tag mid-walk — but AO3 answers
    /// <c>/tags/{id}/works</c> with a 404, which a first run against a real tag demonstrated: the
    /// harvested id 64864081 404'd where the name form served the same tag's index. The id is still
    /// worth harvesting; it is just not an address for this endpoint, whatever else it may address.
    /// </summary>
    internal string BuildUrl(Ship ship, int page, ScrapeRunMode mode, DateTime? watermark)
    {
        var segment = ship.TagUrlSegment;

        var query = new List<string>
        {
            // Explicit rather than relying on AO3's default, which is a site preference and not a
            // guarantee. Every stopping rule here assumes newest-first ordering.
            "work_search%5Bsort_column%5D=revised_at",
        };

        if (page > 1) query.Add($"page={page.ToString(CultureInfo.InvariantCulture)}");

        // Asking AO3 to exclude what we already have is what keeps a routine pass to one request on
        // a large tag. Day-granular, and deliberately given a day's slack, so it can only ever
        // return *more* than needed — the exact cut is made client-side against the watermark.
        if (mode == ScrapeRunMode.Incremental && watermark is { } since)
        {
            var from = since.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            query.Add($"work_search%5Brevised_at%5D={Uri.EscapeDataString($"> {from}")}");
        }

        return $"{_options.BaseUrl.TrimEnd('/')}/tags/{segment}/works?{string.Join('&', query)}";
    }

    // ---- ship state --------------------------------------------------------------------------

    private void BeginBackfill(Ship ship)
    {
        if (ship.BackfillState == ShipBackfillState.NotStarted)
        {
            ship.BackfillState = ShipBackfillState.InProgress;
            ship.BackfillStartedAt = _time.GetUtcNow().UtcDateTime;
            ship.BackfillNextPage ??= 1;
        }
    }

    private static void RecordTotal(Ship ship, Ao3ListingPage listing)
    {
        if (listing.TotalWorks is not { } total) return;

        ship.LastKnownTotalWorks = total;
        ship.LastKnownTotalWorksAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Tracks the oldest revision time seen so far. A reading that moves *up* means pages shifted
    /// under the walk — works are re-sorted as they are edited — which a later full sweep is the
    /// only thing that can put right.
    /// </summary>
    private void TrackBackfillFloor(Ship ship, Ao3ListingPage listing)
    {
        if (listing.Works.Count == 0) return;

        var oldest = listing.Works.Where(w => w.UpdatedAt > DateTime.MinValue).ToList();
        if (oldest.Count == 0) return;

        var floor = oldest.Min(w => w.UpdatedAt);

        if (ship.BackfillMinUpdatedAtSeen is { } previous && floor > previous)
        {
            _logger.LogInformation(
                "Ship {ShipId} ({Tag}) backfill saw a non-monotonic page boundary at page {Page}: "
                + "oldest {Floor:o} is newer than the previous floor {Previous:o}. The listing shifted; "
                + "a full sweep will be needed to close the gap.",
                ship.Id, ship.CanonicalTagName, ship.BackfillNextPage, floor, previous);
            return;
        }

        ship.BackfillMinUpdatedAtSeen = floor;
    }

    private async Task FinishAsync(
        ScrapeContext context,
        Ship ship,
        string stopReason,
        DateTime? newestSeen,
        bool sawRestricted,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        if (context.Mode == ScrapeRunMode.Incremental) ship.LastIncrementalRunAt = now;

        if (context.Mode == ScrapeRunMode.Backfill && stopReason == ScrapeStopReason.LastPage)
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = now;

            _logger.LogInformation(
                "Backfill of ship {ShipId} ({Tag}) completed at page {Page}",
                ship.Id, ship.CanonicalTagName, ship.BackfillNextPage);
        }

        // Advanced only when the pass ended for a reason that means "there was nothing more to
        // read", never when it ran out of budget or hit an error. A watermark moved past unreached
        // works would skip them permanently — no later incremental pass looks that far back again.
        //
        // A completed backfill counts, and must: it starts at page 1 and so has seen the newest work
        // in the tag, and leaving the watermark null afterwards would make the very next incremental
        // pass walk the entire catalogue again. A backfill that merely *resumed* is covered by the
        // same rule from the other side — it can only ever propose an older timestamp than the truth,
        // and the comparison below refuses to move the watermark backwards.
        var reachedTheEnd = stopReason is ScrapeStopReason.Watermark or ScrapeStopReason.LastPage;

        if (reachedTheEnd && newestSeen is { } newest
            && (ship.IncrementalWatermarkUtc is null || newest > ship.IncrementalWatermarkUtc))
        {
            ship.IncrementalWatermarkUtc = newest;
        }

        if (sawRestricted) ship.LastKnownTotalWasAuthenticated = true;

        await _db.SaveChangesAsync(ct);
    }
}
