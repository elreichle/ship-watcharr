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

        // Captured before the walk, because the walk may move it. What is allowed to move it, and
        // what a given run has seen enough of the listing to conclude, is decided in FinishAsync.
        var watermark = ship.IncrementalWatermarkUtc;

        // Whether this run's requests narrow the listing to a date range, read from the same place
        // BuildUrl reads it so that changing when the filter applies cannot leave this behind. Both
        // its inputs are fixed for the length of a run, so every page of the run answers the same.
        var listingWasFiltered = RevisedAtBound(context.Mode, watermark) is not null;

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

        // Set alongside a stopReason that reports trouble, so the run history says what the trouble
        // was. Null on every healthy stop.
        string? errorMessage = null;

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

                // Unlike a non-OK status below, this one *does* re-ask for the same URL. Nothing
                // retried it: SendWithRetryAsync only retries responses, so a transport failure or
                // a timeout has had exactly one attempt, and those are the failures most likely to
                // succeed on the next. The re-asking is bounded by the breaker, which counts
                // consecutive failures and is documented for precisely this — MaxConsecutiveFailures
                // attempts, spaced by the shared 5-8s gate, and then the run gives up.
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
                    errorMessage = $"AO3 returned 404 for the first page requested, {url}";
                }
                else
                {
                    stopReason = ScrapeStopReason.LastPage;
                }

                break;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                // The walk adds no retry of its own, because one has already happened:
                // RateLimitedAo3HttpClient retries 429 and 5xx up to MaxRetries times with jittered
                // backoff, so a non-OK response arriving here is one AO3 has already refused
                // several times over. Every other status — 403, 410, an unfollowed redirect — is
                // not going to become OK by asking a fourth time either.
                //
                // This used to `continue` without advancing `page`, which re-sent the identical URL
                // until the circuit breaker tripped: MaxConsecutiveFailures further round trips,
                // each itself up to MaxRetries attempts, all spent on a page already refused — and
                // then a run recorded as "the archive is down" when a single page was refusing.
                //
                // Neither pass loses anything by stopping here. The watermark only moves for a run
                // that reached the end of the listing (see FinishAsync), and a backfill's cursor
                // still points at this page — so the page is retried, once per run at the
                // scheduler's spacing, rather than in a tight loop inside one.
                _logger.LogWarning(
                    "AO3 returned {Status} for {Url} (ship {ShipId}); stopping the run",
                    (int)response.StatusCode, url, ship.Id);

                stopReason = ScrapeStopReason.Error;
                errorMessage = $"AO3 returned {(int)response.StatusCode} for {url}";
                break;
            }

            var listing = Ao3BlurbParser.ParseListing(response.Content);

            pagesFetched++;
            parseWarnings += listing.ParseWarnings;
            firstPage ??= page;
            lastPage = page;

            RecordTotal(ship, listing, listingWasFiltered);

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

            var incremental = context.Mode == ScrapeRunMode.Incremental;

            // Three groups on an incremental page, not two, because an undated blurb is neither
            // new nor already-had.
            //
            // Ao3BlurbParser.ParseUpdatedAt reports a date it could read in neither form as
            // DateTime.MinValue. That is not "very old", it is "no age at all", and treating it as
            // stale used to end the pass on the spot *and* let the watermark advance to the newest
            // work on the pages the run did reach — so the works behind the stop became
            // unreachable, no later incremental pass looking back that far again.
            //
            // So an undated work abstains: it is ingested, because it is a real work and losing it
            // is worse than storing it with an unknown revision time, but it casts no vote in the
            // stopping rule and cannot move the watermark.
            var undated = incremental
                ? listing.Works.Where(w => w.UpdatedAt == DateTime.MinValue).ToList()
                : [];

            var fresh = incremental
                ? listing.Works
                    .Where(w => w.UpdatedAt > DateTime.MinValue && (watermark is null || w.UpdatedAt > watermark))
                    .ToList()
                : listing.Works;

            // Whatever is left over: dated, and no newer than the watermark. This is what stops the
            // walk, counted rather than inferred from fresh.Count so that abstentions cannot be
            // mistaken for works we already have.
            var alreadyHad = incremental ? listing.Works.Count - fresh.Count - undated.Count : 0;

            var toIngest = undated.Count == 0 ? fresh : [.. fresh, .. undated];

            if (toIngest.Count > 0)
            {
                var result = await _ingestor.IngestAsync(ship, toIngest, ct);
                worksSeen += result.WorksSeen;
                worksAdded += result.WorksAdded;
                worksUpdated += result.WorksUpdated;

                sawRestricted |= toIngest.Any(w => w.IsRestricted);
            }

            // Only dated works propose a watermark. On a backfill `fresh` is the whole page, which
            // can hold MinValue readings — hence the filter here rather than a Max over the lot.
            var dated = incremental ? fresh : fresh.Where(w => w.UpdatedAt > DateTime.MinValue).ToList();
            if (dated.Count > 0)
            {
                var pageNewest = dated.Max(w => w.UpdatedAt);
                if (newestSeen is null || pageNewest > newestSeen) newestSeen = pageNewest;
            }

            // The watermark stop, and the reason an incremental pass is normally one request: the
            // listing is newest-first, so the first page that is not entirely new means everything
            // after it is older still.
            if (incremental && alreadyHad > 0)
            {
                stopReason = ScrapeStopReason.Watermark;
                break;
            }

            // Every blurb on the page abstained, so nothing on it says where in the listing we are.
            // Reading on would walk the whole tag on every incremental pass — the cost this pass
            // exists to avoid — so stop, and stop with a reason that leaves the watermark where it
            // was rather than pretending the walk finished.
            //
            // Only where there is more to walk. A last page whose dates are all unreadable is a
            // small tag the parser is struggling with, not a runaway walk: the page after it does
            // not exist, so there is no cost to prevent, and calling that an error would leave the
            // watermark null and repeat the same complaint on every pass forever.
            if (incremental && fresh.Count == 0 && undated.Count > 0 && listing.HasNextPage)
            {
                _logger.LogError(
                    "No blurb on page {Page} for ship {ShipId} ({Tag}) carried a readable date, so the "
                    + "incremental pass has nothing to stop on. The listing markup has probably changed.",
                    page, ship.Id, ship.CanonicalTagName);

                stopReason = ScrapeStopReason.Error;
                errorMessage = $"No blurb on page {page} carried a readable date";
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

        await FinishAsync(
            context, ship, stopReason, firstPage, newestSeen, sawRestricted, listingWasFiltered, ct);

        return new ScrapeOutcome(
            pagesFetched, budget.RequestsMade, worksSeen, worksAdded, worksUpdated,
            parseWarnings, firstPage, lastPage, stopReason, errorMessage);
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

        if (RevisedAtBound(mode, watermark) is { } bound)
            query.Add($"work_search%5Brevised_at%5D={Uri.EscapeDataString(bound)}");

        return $"{_options.BaseUrl.TrimEnd('/')}/tags/{segment}/works?{string.Join('&', query)}";
    }

    /// <summary>
    /// The <c>work_search[revised_at]</c> bound a run's requests carry, or null when they ask for
    /// the whole tag.
    ///
    /// Asking AO3 to exclude what we already have is what keeps a routine pass to one request on a
    /// large tag. Day-granular, and deliberately given a day's slack, so it can only ever return
    /// *more* than needed — the exact cut is made client-side against the watermark.
    ///
    /// One function rather than a condition in <see cref="BuildUrl"/>, because a second caller
    /// needs the same answer: a filtered listing's heading counts the filter's result set, not the
    /// tag, and <see cref="RecordTotal"/> has to know which it is looking at.
    /// </summary>
    private static string? RevisedAtBound(ScrapeRunMode mode, DateTime? watermark) =>
        mode == ScrapeRunMode.Incremental && watermark is { } since
            ? $"> {since.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : null;

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

    /// <summary>
    /// Stores AO3's "N Works in ..." heading as the tag's total — from an unfiltered listing only.
    ///
    /// The heading counts whatever result set the request produced, so an incremental pass carrying
    /// a <c>revised_at</c> bound prints the number of works revised since the watermark, which on a
    /// quiet tag is a single digit. Written to <see cref="Ship.LastKnownTotalWorks"/> that is not
    /// merely wrong, it is wrong in the direction that matters: the field is documented as the
    /// figure a full sweep checks itself against before concluding works have left the tag, and a
    /// tag backfilled to 4,317 works reading 2 is a tag a sweep would call emptied.
    ///
    /// Gated on the filter rather than on the mode, so the rule survives the filter's conditions
    /// changing — a backfill of a ship that has a watermark still asks for the whole listing, and
    /// its heading still counts the tag.
    /// </summary>
    private void RecordTotal(Ship ship, Ao3ListingPage listing, bool listingWasFiltered)
    {
        if (listingWasFiltered) return;
        if (listing.TotalWorks is not { } total) return;

        ship.LastKnownTotalWorks = total;
        ship.LastKnownTotalWorksAt = _time.GetUtcNow().UtcDateTime;
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
        int? firstPage,
        DateTime? newestSeen,
        bool sawRestricted,
        bool listingWasFiltered,
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

        // Two conditions, and both are about what the run was in a position to *know*.
        //
        // First: it must have read page 1. The listing is revised_at desc, so page 1 is where the
        // newest work in the tag is, and only a run that read it can say what the newest revision
        // time is. That is every incremental pass, and the *first* run of a backfill only — a
        // backfill resuming at its cursor starts at page 57 and reads some of the oldest works in
        // the tag, so its newest reading is an ancient date. Made the watermark, it would send
        // every later incremental pass asking for everything revised since then: most of the tag,
        // on every tick, walking to the page cap and stopping for a reason that does not let the
        // watermark move, so the next pass does it again. Forever.
        //
        // (The alternative was a new column carrying a backfill's newest-seen across its resumes.
        // Rejected as schema for something already known: the run that reads page 1 has exactly the
        // reading that column would hold, and the resumed runs have nothing to add to it.)
        var readTheNewestEnd = firstPage == 1;

        // Second: where the run stopped — which matters for an incremental pass and not for a
        // backfill.
        //
        // An incremental pass that stopped early leaves works between its watermark and the newest
        // thing it read unvisited, and nothing looks that far back again, so only a stop meaning
        // "there was nothing more to read" may move it — never a budget stop or an error.
        //
        // A backfill from page 1 is not exposed to that: nothing it failed to reach is newer than
        // what it read, and the pages it skipped are held by its cursor for a later run. It may
        // therefore leave a watermark however it stopped, and must — any tag big enough to need
        // several runs ends its first one on the cap, and if that left no watermark the resumed
        // runs may not set one either, so the ship would reach Complete with none at all.
        var mayPropose = readTheNewestEnd
            && (context.Mode == ScrapeRunMode.Backfill
                || stopReason is ScrapeStopReason.Watermark or ScrapeStopReason.LastPage);

        // Never backwards. Nothing above should now be able to propose an older timestamp than the
        // one on record, but a watermark that moved back would re-read everything between the two
        // on the next pass, so the guard stays as the cheap backstop for a rule proved wrong later.
        if (mayPropose && newestSeen is { } newest
            && (ship.IncrementalWatermarkUtc is null || newest > ship.IncrementalWatermarkUtc))
        {
            ship.IncrementalWatermarkUtc = newest;
        }

        // Set only where RecordTotal writes, and for the same reason. Ship documents this as
        // whether the run that produced *the stored total* was logged in, so a filtered pass — which
        // produces no total — must not set it: doing so leaves a total counted logged-out, with
        // restricted works invisible and the count therefore short, wearing an authenticated run's
        // flag. That is the mis-conclusion the field exists to prevent, and T15's sweep is what
        // would act on it.
        //
        // Two things here are still wrong and are T30's, not this pass's: the flag is a one-way
        // latch that no later anonymous run can clear, and `sawRestricted` is computed over newly
        // ingested works only, so a page whose restricted works were all already held does not set
        // it even on a genuinely authenticated run.
        if (!listingWasFiltered && sawRestricted) ship.LastKnownTotalWasAuthenticated = true;

        await _db.SaveChangesAsync(ct);
    }
}
