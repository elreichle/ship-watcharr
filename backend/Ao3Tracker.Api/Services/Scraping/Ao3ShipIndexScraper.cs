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

    /// <summary>
    /// How many consecutive runs a ship may spend on a cursor the listing will not answer, before
    /// the backfill is written off as <see cref="ShipBackfillState.Failed"/>.
    ///
    /// Each such run costs at most two requests — the cursor and the page before it — so twelve of
    /// them is two dozen requests, spread over twelve scheduler intervals, before this gives up.
    /// Twelve rather than a handful because the allowance has to be wide enough for the *honest*
    /// case to converge inside it: <see cref="JumpCursorBackFrom"/> halves the cursor each time a
    /// retreat finds nothing either, so a listing that shrank by a factor of two thousand still
    /// finds a readable page with runs to spare, and anything that runs the allowance out is a
    /// listing not answering at any depth rather than one that merely lost pages.
    /// </summary>
    internal const int MaxStalledBackfillRuns = 12;

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

        // Blurbs on the pages this run read, which is not `worksSeen` — that counts what reached the
        // ingestor, and an incremental pass hands it only the works newer than the watermark. This
        // is the run's own tally of what the listing served it, and the only thing a filtered
        // listing's heading can honestly be compared against (see PlausiblyTheEndOfTheListing).
        var blurbsRead = 0;
        var worksSeen = 0;
        var worksAdded = 0;
        var worksUpdated = 0;
        var parseWarnings = 0;
        int? firstPage = null;
        int? lastPage = null;

        // Requests whose response reached the status branching below, so `pagesRequested == 1`
        // identifies the run's first answered request whatever that answer was — unlike
        // `pagesFetched`, which counts only pages that parsed, and `firstPage`, which is set only
        // once one has. A backfill's first request is the one that lands on the saved cursor.
        var pagesRequested = 0;

        // The page a stale-cursor retreat stepped back from, once per run. Both a bound on the
        // retreating and the thing that stops the walk turning round and re-asking for it, and —
        // in `retreatBecause` — what that page actually did, since the retreat has two routes into
        // it and a message naming the wrong one misdirects the operator reading the run history.
        int? retreatedFrom = null;
        string? retreatBecause = null;
        DateTime? newestSeen = null;

        // Whether this run read the listing while logged in, which is what the flag beside the
        // total records. Taken from the transport and from nothing else: the question is what this
        // run *sent*, and the client is the only thing that knows. False for every run until T5
        // teaches it to log in, which is the true answer rather than a placeholder.
        var readWhileLoggedIn = false;

        // Whether any page of this run put a number in Ship.LastKnownTotalWorks. Not the same
        // question as "was the listing unfiltered": an unfiltered pass whose heading would not
        // parse is entitled to write a total and writes none, and the flag it must then leave alone
        // belongs to whichever earlier run did write one.
        var wroteTotal = false;

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

            pagesRequested++;

            if (response.FromCache) budget.RecordCacheHit();
            else if (response.StatusCode == HttpStatusCode.OK) budget.RecordSuccess();
            else budget.RecordFailure();

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A 404 concludes nothing about how long a listing is. The only page entitled to
                // say a listing ended is a page that was *read* and offered no next link, and the
                // walk has two ways of getting one: the forward walk's own `!listing.HasNextPage`
                // stop below, and the retreat, which re-reads the page before a cursor and asks it.
                //
                // This branch used to end the walk on `lastPage == page - 1` — the page before was
                // read, and this one is not there. Note what the walk had to do to arrive at that:
                // it only ever advances past a page that offered a next link, so `lastPage` being
                // `page - 1` *means* a page this run read said page N exists. That is the same
                // evidence CursorMayBeStale two hundred lines below refuses to conclude from —
                // page N-1 read, offering a next link, page N absent — and the two disagreed only
                // on whether this run happened to be the one that read page N-1. One transient 5xx
                // during a retreat was the whole cost of crossing between them: a run left the
                // cursor at N-1, and the next healthy run read N-1, asked N, took the 404 and
                // retired the ship with page N onward never read.
                //
                // So this branch does not conclude either, and a listing that genuinely shrank out
                // from under the walk is not lost by that — it is deferred one run. The cursor is
                // already sitting on the 404'd page, so the next run's first request lands there,
                // CursorMayBeStale recognises it, and the retreat re-reads page N-1 and takes the
                // listing's own word: no next link there any more and the backfill completes on
                // evidence; still a next link and the ship is stalled rather than retired, bounded
                // by MaxStalledBackfillRuns.
                if (CursorMayBeStale(context.Mode, page, pagesRequested, retreatedFrom))
                {
                    retreatBecause = $"AO3 returned 404 for page {page.ToString(CultureInfo.InvariantCulture)}";
                    RetreatFromStaleCursor(ship, page, retreatBecause);
                    retreatedFrom = page;
                    page--;
                    continue;
                }

                _logger.LogError(
                    "AO3 returned 404 for page {Page} of ship {ShipId} ({Tag}) at {Url}. "
                    + "Treating this as an error rather than the end of the listing.",
                    page, ship.Id, ship.CanonicalTagName, url);

                stopReason = ScrapeStopReason.Error;
                errorMessage = retreatedFrom is { } from
                    ? JumpCursorBackFrom(
                        ship, from, retreatBecause!,
                        $"page {page.ToString(CultureInfo.InvariantCulture)} before it returned 404 as well")
                    : lastPage == page - 1
                        ? $"AO3 returned 404 for page {page.ToString(CultureInfo.InvariantCulture)}, which "
                            + $"page {(page - 1).ToString(CultureInfo.InvariantCulture)} offered a next link to"
                        : $"AO3 returned 404 for the first page requested, {url}";

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

            // Whether the page can be taken for the end of the listing, evaluated before the page
            // is counted as read — because the retreat below sets it aside unread, and counting it
            // would make the two routes to a stale cursor differ again in exactly the way this task
            // exists to stop. `firstPage` is the one that matters: FinishAsync asks "did this run
            // see page 1" to decide whether it may move the watermark, so a cursor page the run
            // could not read, standing in for page 1, silently throws away a watermark the retreat
            // then went on to earn. It also put a `FirstPageFetched` above `LastPageFetched` in the
            // run history — an inverted range an operator has no way to read.
            var unreadable = listing.Works.Count == 0
                && !PlausiblyTheEndOfTheListing(listing, page, listingWasFiltered, blurbsRead);

            // The other route to a stale cursor, and the reason it is handled beside the 404 rather
            // than in that branch alone: which of the two AO3 serves for a page that no longer
            // exists is AO3's choice, not a difference in what happened.
            if (unreadable && CursorMayBeStale(context.Mode, page, pagesRequested, retreatedFrom))
            {
                retreatBecause =
                    $"page {page.ToString(CultureInfo.InvariantCulture)} parsed to no works, and "
                    + WhyNotTheEnd(listing, page, listingWasFiltered, blurbsRead);
                RetreatFromStaleCursor(ship, page, retreatBecause);
                retreatedFrom = page;
                page--;
                continue;
            }

            pagesFetched++;
            parseWarnings += listing.ParseWarnings;
            firstPage ??= page;
            lastPage = page;

            // Before the heading is trusted for anything. A page that could not be read must not
            // get to write the ship a number either: ParseTotalWorks falls back to the trailing
            // digits of any h2.heading when it finds no "Works", so an AO3 soft-error page served
            // as 200 with <h2 class="heading">Error 404</h2> would put LastKnownTotalWorks = 404
            // over a real 4,317 and stamp it as freshly read — the same field, and the same
            // mis-conclusion, that RecordTotal's filter guard exists to prevent.
            if (unreadable)
            {
                _logger.LogError(
                    "Page {Page} for ship {ShipId} ({Tag}) parsed to no works from {Length} characters of "
                    + "HTML, and {Reason}. Treating this as a parse failure rather than the end of the listing.",
                    page, ship.Id, ship.CanonicalTagName, response.Content.Length,
                    WhyNotTheEnd(listing, page, listingWasFiltered, blurbsRead));

                stopReason = ScrapeStopReason.Error;
                errorMessage = retreatedFrom is { } from
                    ? JumpCursorBackFrom(
                        ship, from, retreatBecause!,
                        $"page {page.ToString(CultureInfo.InvariantCulture)} before it did not read either")
                    : $"Page {page} parsed to no works, and {WhyNotTheEnd(listing, page, listingWasFiltered, blurbsRead)}";

                break;
            }

            blurbsRead += listing.Works.Count;

            readWhileLoggedIn |= response.Authenticated;

            // A restricted work is documented as invisible to a logged-out request, so one arriving
            // on a response the client says it did not authenticate means one of those two beliefs
            // is wrong. Reported, never acted on: letting the page overrule the transport about what
            // this run sent would write the flag `true` over a total demonstrably fetched without a
            // session — the exact claim the field exists to make trustworthy — on the strength of a
            // markup premise nothing in this repo has verified. That premise is T39's business, and
            // this line is how it would first announce itself.
            //
            // Read over every work on the page rather than the ones handed to the ingestor: the two
            // sets are equal on an unfiltered pass today, but only as an accident of where the
            // watermark filter is applied, and the question here is what the run was shown.
            if (!response.Authenticated && listing.Works.Any(w => w.IsRestricted))
            {
                _logger.LogWarning(
                    "Page {Page} for ship {ShipId} ({Tag}) carried a restricted work, which AO3 is not "
                    + "expected to show a request without a session — and this request carried none. "
                    + "The listing's total is being recorded as read anonymously; if that is wrong, it "
                    + "is the restricted-work premise that is wrong.",
                    page, ship.Id, ship.CanonicalTagName);
            }

            wroteTotal |= RecordTotal(ship, listing, listingWasFiltered);

            if (listing.Works.Count == 0)
            {
                // Nothing on the page contradicts an empty tag, so the walk concludes — which for a
                // backfill means Complete. Still logged: it is also what a markup change on a small
                // tag looks like, and the response size tells "AO3 served us an error page" apart
                // from "AO3 served us a listing we can no longer read".
                _logger.LogWarning(
                    "Page {Page} for ship {ShipId} ({Tag}) parsed to no works from {Length} characters "
                    + "of HTML. Either the tag is empty or the listing markup has changed.",
                    page, ship.Id, ship.CanonicalTagName, response.Content.Length);

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

            // The retreat's answer, in the case where the listing sides with the cursor. This run
            // already asked for the page after this one and got nothing readable back; the page
            // before it insisting that page exists does not make a second identical request any
            // likelier to be served, and making one is how a walk turns into a loop. Stop, leave
            // the cursor pointing at it, and let the next run ask once at the scheduler's spacing.
            if (retreatedFrom == page + 1)
            {
                _logger.LogWarning(
                    "Page {Page} for ship {ShipId} ({Tag}) still offers page {Next}, which did not answer "
                    + "earlier in this run. Leaving the cursor there for the next run.",
                    page, ship.Id, ship.CanonicalTagName, retreatedFrom);

                stopReason = ScrapeStopReason.Error;
                errorMessage =
                    $"Page {retreatedFrom.Value.ToString(CultureInfo.InvariantCulture)} did not answer, and "
                    + $"page {page.ToString(CultureInfo.InvariantCulture)} before it still offers a next one";
                break;
            }

            page++;
        }

        await FinishAsync(
            context, ship, stopReason, startPage, firstPage, newestSeen, wroteTotal, readWhileLoggedIn,
            askedStaleCursor: retreatedFrom is not null, ct);

        return new ScrapeOutcome(
            pagesFetched, budget.RequestsMade, worksSeen, worksAdded, worksUpdated,
            parseWarnings, firstPage, lastPage, stopReason, errorMessage);
    }

    // ---- a cursor pointing past the end of a listing that shrank -------------------------------

    /// <summary>
    /// Whether this run's first request landed on a backfill cursor the listing may no longer have
    /// a page for.
    ///
    /// The situation: a run stopped with <see cref="Ship.BackfillNextPage"/> at N+1 because page N
    /// offered a next link; before the next run, works were deleted or hidden and the listing shrank
    /// so that N+1 no longer exists. AO3 may answer that with a 404 or with a 200 carrying an empty
    /// listing, and the two branches used to conclude opposite things about it — the 404 the end of
    /// the listing, so the backfill completed with its back catalogue unread; the empty 200 a parse
    /// failure, so the ship re-requested the same page on every scheduled run forever. Neither
    /// reading was wrong so much as unentitled: a page that did not answer cannot say why.
    ///
    /// So neither concludes. Both retreat to the page before the cursor and ask *it*, because the
    /// listing is the only authority on how long it is and this is a question it can answer: a Next
    /// link there means the cursor's page is supposed to exist and this run's failure was passing;
    /// no Next link means the listing really does end before the cursor, and the backfill is
    /// complete on evidence rather than on a guess about what a 404 meant.
    ///
    /// Once per run. Retreating twice would be a backwards walk inside one run, and the walk-back a
    /// listing that lost several pages needs is paid a page per run instead, bounded by
    /// <see cref="MaxStalledBackfillRuns"/>.
    ///
    /// This is also where a listing that shrank *mid-walk* is answered, one run later. A 404 the
    /// walk runs into after reading the page before it no longer concludes anything either — the
    /// walk only advances past a page offering a next link, so that 404 is the same contradiction
    /// as this one and differs only in which run read page N-1. The run stops with the cursor on
    /// the page that did not answer, which makes it this run's question next time.
    /// </summary>
    private static bool CursorMayBeStale(ScrapeRunMode mode, int page, int pagesRequested, int? retreatedFrom) =>
        mode == ScrapeRunMode.Backfill && pagesRequested == 1 && page > 1 && retreatedFrom is null;

    /// <summary>
    /// Steps the cursor back one page before the walk re-aims at it.
    ///
    /// Moving the *stored* cursor is what carries the question into the next run if this one cannot
    /// finish it: if the retreat's page reads, the backfill's own advance writes the cursor straight
    /// back to where it was. Retreating can never skip anything — it only re-reads pages, and
    /// ingestion is idempotent.
    /// </summary>
    private void RetreatFromStaleCursor(Ship ship, int page, string because)
    {
        _logger.LogWarning(
            "Backfill cursor for ship {ShipId} ({Tag}) points at page {Page}, but {Because}. Re-reading "
            + "page {Previous} to let the listing say whether page {Page} should exist.",
            ship.Id, ship.CanonicalTagName, page, because, page - 1);

        ship.BackfillNextPage = page - 1;
    }

    /// <summary>
    /// Where the cursor goes when the retreat's page did not read either, and the run-history line
    /// saying why.
    ///
    /// Two pages in a row unanswerable says the listing is shorter than the cursor by an unknown
    /// amount, not by one — so stepping back one page a run does not converge, and a large shrink
    /// would exhaust <see cref="MaxStalledBackfillRuns"/> and write off a backfill over a listing
    /// that was never broken. That is not hypothetical here: the AO3 login is instance-level, and
    /// a lapsed one drops every restricted work out of the listing at once.
    ///
    /// So halve it. Backwards only, so it can no more skip a page than the retreat can, and it
    /// finds a readable page in a number of runs logarithmic in the cursor rather than linear —
    /// which is what makes the allowance a bound on a broken listing rather than on a big one. The
    /// pages between the landing point and where the walk was are simply re-read.
    /// </summary>
    private string JumpCursorBackFrom(Ship ship, int cursor, string cursorBecause, string retreatBecause)
    {
        var landing = Math.Max(1, cursor / 2);

        _logger.LogWarning(
            "Backfill of ship {ShipId} ({Tag}) found neither page {Cursor} nor page {Previous} readable; "
            + "moving the cursor back to page {Landing} to look for a page the listing will answer for.",
            ship.Id, ship.CanonicalTagName, cursor, cursor - 1, landing);

        ship.BackfillNextPage = landing;

        return $"{cursorBecause}, and {retreatBecause}, so the listing is shorter than the backfill "
            + $"cursor by more than one page; retrying from page {landing.ToString(CultureInfo.InvariantCulture)}";
    }

    // ---- what a page with no readable works may conclude ---------------------------------------

    /// <summary>
    /// Whether a page that parsed to no works may be taken for the end of the listing.
    ///
    /// It matters because the caller turns that into <see cref="ScrapeStopReason.LastPage"/>, and
    /// <see cref="FinishAsync"/> turns a backfill's <c>LastPage</c> into
    /// <see cref="ShipBackfillState.Complete"/> — the strongest conclusion this pass can reach, and
    /// one nothing later revisits. A page that merely could not be *read* used to take it as
    /// readily as an empty tag did, so a 200 maintenance page or a listing markup change at page 57
    /// of a 3,000-page backfill recorded the ship as fully backfilled with the rest never read. The
    /// 404 branch was hardened against precisely that; this is the same conclusion reached by
    /// another route.
    ///
    /// Four pieces of evidence say the listing did not end here, all of them already parsed:
    ///
    /// <list type="bullet">
    /// <item>No listing container in the document. Whatever was served is not a results page — an
    /// empty body, a static maintenance page, something a proxy substituted. An empty *tag* still
    /// renders the container, so this tells the two apart rather than guessing from length.</item>
    /// <item><c>page > 1</c> — on an unfiltered listing. AO3 404s past the last page rather than
    /// serving an empty 200, so the walk only got above page 1 because a page advertised more — one
    /// this run read, or one an earlier run read before leaving the cursor here.
    ///
    /// Under a <c>revised_at</c> bound that argument does not hold: the Next link comes off a result
    /// count that can race the blurbs, so one work leaving the window between the two requests
    /// answers page 2 with a well-formed empty listing. Held against it, a quiet incremental pass
    /// stops with <see cref="ScrapeStopReason.Error"/> — which may not move the watermark — and the
    /// ship re-reads the same two pages on every tick forever, having read the newest end of the
    /// listing in full each time.
    ///
    /// So the filtered case asks the heading instead, which is the same question the unfiltered
    /// case asks it one item below with a different denominator. A filtered heading counts the
    /// filter's result set, and an incremental pass always starts at page 1, so
    /// <paramref name="blurbsRead"/> — this run's own tally of blurbs served — is the number it is
    /// comparable with. Counting more than the run was served is the listing saying there is more,
    /// and this stops for the same reason the unfiltered walk does. Counting no more than that,
    /// or carrying no readable heading at all, leaves nothing on the page contradicting the end —
    /// and the race above shrinks that count, so the case this waiver exists for is the case where
    /// the heading agrees.
    ///
    /// A backfill is never filtered (<see cref="RevisedAtBound"/> gates on
    /// <see cref="ScrapeRunMode.Incremental"/>), so nothing the waiver reaches is a walk that could
    /// conclude <see cref="ShipBackfillState.Complete"/>.</item>
    /// <item>A Next link: the page says itself that there is more after it.</item>
    /// <item>A heading counting works the blurbs do not contain. Only on an unfiltered listing: a
    /// <c>revised_at</c>-filtered request's heading counts the filter's result set, not the tag
    /// (see <see cref="RecordTotal"/>), so a quiet incremental pass reading zero blurbs under a
    /// heading is the healthy case and holding it against the page would fail every tick.</item>
    /// </list>
    ///
    /// None of the four: an empty tag, as far as anything on the page can say, and the walk
    /// concludes.
    /// </summary>
    private static bool PlausiblyTheEndOfTheListing(
        Ao3ListingPage listing, int page, bool listingWasFiltered, int blurbsRead) =>
        listing.HasListing
        && (page == 1 || (listingWasFiltered && !FilteredHeadingSaysMore(listing, blurbsRead)))
        && !listing.HasNextPage
        && !(listing.TotalWorks > 0 && !listingWasFiltered);

    /// <summary>
    /// Whether a filtered listing's heading counts more works than the run has been served.
    ///
    /// Read only where <see cref="PlausiblyTheEndOfTheListing"/> has waived <c>page > 1</c>, and
    /// deliberately silent when the heading did not parse: no heading is no evidence, and the
    /// container plus the absent Next link are what the conclusion rests on there.
    /// </summary>
    private static bool FilteredHeadingSaysMore(Ao3ListingPage listing, int blurbsRead) =>
        listing.TotalWorks is { } matched && matched > blurbsRead;

    /// <summary>
    /// Which of <see cref="PlausiblyTheEndOfTheListing"/>'s conditions actually failed, phrased for
    /// the run history — where an operator diagnosing a stuck backfill reads it. Named after the
    /// evidence rather than written as one sentence covering all of it, because a message saying
    /// "the listing says there are more" over a page carrying neither a heading nor a Next link
    /// tells that operator the opposite of what happened.
    /// </summary>
    private static string WhyNotTheEnd(Ao3ListingPage listing, int page, bool listingWasFiltered, int blurbsRead)
    {
        if (!listing.HasListing) return "the response carried no listing at all, so it is not a results page";
        if (listing.HasNextPage) return "the page still offers a next one";
        if (listing.TotalWorks > 0 && !listingWasFiltered)
            return $"the heading counts {listing.TotalWorks.Value.ToString(CultureInfo.InvariantCulture)} works in the tag";

        if (listingWasFiltered && FilteredHeadingSaysMore(listing, blurbsRead))
            return $"the heading counts {listing.TotalWorks!.Value.ToString(CultureInfo.InvariantCulture)} works "
                + $"matching this run's date filter and the run has been served "
                + $"{blurbsRead.ToString(CultureInfo.InvariantCulture)}";

        // The unfiltered walk's remaining evidence. The filtered walk waives it and answers with the
        // heading above instead, so a filtered page failing none of these conditions is the end of
        // the listing and never asks why it is not.
        return $"page {page.ToString(CultureInfo.InvariantCulture)} was only reached because an earlier page offered a next one";
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
    /// <returns>Whether the total on the ship is now this page's, which is what decides the
    /// ownership of <see cref="Ship.LastKnownTotalWasAuthenticated"/> beside it.</returns>
    private bool RecordTotal(Ship ship, Ao3ListingPage listing, bool listingWasFiltered)
    {
        if (listingWasFiltered) return false;
        if (listing.TotalWorks is not { } total) return false;

        ship.LastKnownTotalWorks = total;
        ship.LastKnownTotalWorksAt = _time.GetUtcNow().UtcDateTime;
        return true;
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

    /// <summary>
    /// What a finished backfill run leaves on the ship: whether the walk is over, and how long this
    /// ship has been stuck on a cursor the listing will not answer.
    ///
    /// The counter is the bound <see cref="CursorMayBeStale"/> needs. A stalled run is one the
    /// archive answered and that got no further through the listing for it, so it will ask again
    /// next time; <see cref="MaxStalledBackfillRuns"/> of those in a row and the backfill is
    /// <see cref="ShipBackfillState.Failed"/> — not <see cref="ShipBackfillState.Complete"/>, which
    /// would claim a back catalogue that was never read. The ship keeps its incremental pass (see
    /// ScrapeWorker's mode choice, which backfills only a NotStarted or InProgress ship), so it goes
    /// on collecting new works; what it stops doing is spending two requests a run on a question
    /// nothing is answering. Closing the gap left behind is a full sweep's job.
    /// </summary>
    private void RecordBackfillProgress(
        Ship ship, string stopReason, int startPage, int? firstPage, bool askedStaleCursor, DateTime now)
    {
        if (stopReason == ScrapeStopReason.LastPage)
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = now;
            ship.BackfillStalledRuns = 0;

            _logger.LogInformation(
                "Backfill of ship {ShipId} ({Tag}) completed at page {Page}",
                ship.Id, ship.CanonicalTagName, ship.BackfillNextPage);

            return;
        }

        // Cleared by forward progress and nothing else. Reading *a* page is not enough: a run that
        // retreated to page 1 and found that unreadable too has read a page, learned nothing, and
        // would clear the streak that is meant to be counting exactly this.
        if (ship.BackfillNextPage > startPage)
        {
            ship.BackfillStalledRuns = 0;
            return;
        }

        // No forward progress. Whether that counts against the ship turns on whether the archive
        // answered at all. A run that asked about its cursor, or that got pages it could not use,
        // has been told something; a run stopped by the budget, the breaker, a transport failure or
        // a refused status has not — and writing off a back catalogue because AO3 was down for an
        // afternoon is exactly the mis-conclusion this counter must not make.
        if (!askedStaleCursor && firstPage is null) return;

        ship.BackfillStalledRuns++;

        if (ship.BackfillStalledRuns >= MaxStalledBackfillRuns)
        {
            ship.BackfillState = ShipBackfillState.Failed;

            _logger.LogError(
                "Backfill of ship {ShipId} ({Tag}) has spent {Runs} runs without getting past page {Page}, "
                + "which the listing will not answer for; giving up on the back catalogue. The incremental "
                + "pass continues; a full sweep is what can close the gap.",
                ship.Id, ship.CanonicalTagName, ship.BackfillStalledRuns, ship.BackfillNextPage);
        }
    }

    private async Task FinishAsync(
        ScrapeContext context,
        Ship ship,
        string stopReason,
        int startPage,
        int? firstPage,
        DateTime? newestSeen,
        bool wroteTotal,
        bool readWhileLoggedIn,
        bool askedStaleCursor,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        if (context.Mode == ScrapeRunMode.Incremental) ship.LastIncrementalRunAt = now;

        if (context.Mode == ScrapeRunMode.Backfill)
            RecordBackfillProgress(ship, stopReason, startPage, firstPage, askedStaleCursor, now);

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

        // Assigned by whichever run wrote the total, and by no other. Ship documents this as
        // whether the run that produced *the stored total* was logged in, so the two travel
        // together or the pair says something neither run did: a filtered pass, or an unfiltered
        // one whose heading would not parse, leaves the flag to the run whose number is still on
        // the ship.
        //
        // Assignment, not a latch. A logged-in run stamps the flag; the session lapses; a later
        // anonymous run reads a fresh total that is short by however many restricted works the tag
        // holds. Left true, the flag tells T15's sweep to allow for an invisibility the stored
        // number no longer has — so a run that replaces the total replaces the flag, downwards
        // included.
        if (wroteTotal) ship.LastKnownTotalWasAuthenticated = readWhileLoggedIn;

        await _db.SaveChangesAsync(ct);
    }
}
