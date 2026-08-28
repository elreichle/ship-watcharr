using System.Globalization;
using System.Net;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// How many consecutive incremental runs may stop short at the same page before the walk stops
    /// asking for the page after it.
    ///
    /// The backfill's counter above is a bound on a *conclusion* and is therefore wide; this one
    /// bounds nothing but wasted requests, so it can be narrow. Three runs is enough to tell a page
    /// that is refusing from one that failed once, and every one of those three is recorded failed
    /// with a message naming the page — so the hold never lands on a ship whose trouble has not
    /// already been reported three times over.
    /// </summary>
    internal const int MinStuckIncrementalRuns = 3;

    /// <summary>
    /// How many held runs pass before the walk spends one request re-asking the page it is holding
    /// at.
    ///
    /// The hold has to be temporary: the page stopped answering for a reason nothing here knows,
    /// and a listing that heals must not need an operator to notice. So the held runs are counted
    /// too, and this many of them in a row lifts the hold for one run. The cost of the hold is
    /// therefore one request every <see cref="ProbeHeldPageEveryNthRun"/> runs instead of one every
    /// run, and the cost of being wrong about the page is a delay of that many scheduler intervals
    /// rather than a permanent one.
    /// </summary>
    internal const int ProbeHeldPageEveryNthRun = 8;

    /// <summary>
    /// How many finished runs the streak is read out of. Both counts below are `TakeWhile`s over
    /// this window, so a window shorter than either constant silently caps it — and capping
    /// <see cref="MinStuckIncrementalRuns"/> is not a smaller hold but no hold at all, since
    /// <c>stuck</c> could then never reach it. Derived from both rather than written as a literal
    /// so that raising either one on its own cannot turn the bound off with nothing to show for it.
    /// </summary>
    private const int StreakWindow =
        MinStuckIncrementalRuns > ProbeHeldPageEveryNthRun ? MinStuckIncrementalRuns : ProbeHeldPageEveryNthRun;

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
        //
        // It reports `Denied` rather than `LastPage`: this run made no request, so "walked off the
        // end of the listing" is the one thing it cannot claim — and that claim is one refactor
        // away from being acted on, since the only thing keeping it from marking the backfill
        // Complete is that this return sits above FinishAsync.
        if (ship.VerificationState == ShipVerificationState.NotFoundOnAo3)
            return ScrapeOutcome.Empty(ScrapeStopReason.Denied) with
            {
                ErrorMessage =
                    $"AO3 has denied the tag {ship.CanonicalTagName}; no page of it was requested, "
                    + "and nothing re-verifies a tag once its verification has settled.",
            };

        var startPage = context.Mode == ScrapeRunMode.Backfill ? Math.Max(1, ship.BackfillNextPage ?? 1) : 1;

        // Read once, before the walk, from the run history this pass has already written. Only the
        // incremental pass needs it: a backfill that cannot get past a page has a cursor to carry
        // the question into the next run and MaxStalledBackfillRuns to bound it, and an incremental
        // pass has neither — it restarts at page 1 every time and re-asks the same refusing page.
        var heldAfter = context.Mode == ScrapeRunMode.Incremental
            ? await HeldAfterPageAsync(context.Job.Id, ct)
            : null;

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

        // Pages AO3 served this run a body for, whether or not the parser could make a listing of
        // it. Narrower than `pagesRequested`, which counts a 404 and a refused status too; wider
        // than `pagesFetched`, which counts only the ones that read.
        //
        // RecordBackfillProgress is the only reader, and it needs exactly this middle: its stalled
        // counter must distinguish "AO3 told this run nothing" — down, refusing, or cut off by the
        // budget — from "AO3 answered with something this run could not use", and only the second
        // is the ship's problem to be counted against. `firstPage` stood in for that reading while
        // an unreadable page still set it; now that one does not, the distinction needs its own
        // name rather than a side effect of a counter about something else.
        var pagesServed = 0;

        // Pages AO3 answered 404 for. The third state `pagesServed` cannot express: a 404 is not a
        // body this run could not use, and it is not the archive telling this run nothing either —
        // it is the archive answering definitively that the page it was asked for is not there.
        //
        // RecordBackfillProgress is the only reader, for the one cursor position where nothing else
        // reaches it. At page 1 no retreat can run (CursorMayBeStale requires page > 1), so a ship
        // pointed at a tag AO3 no longer serves 404s its only request, serves no page, retreats
        // from nothing, and would otherwise ask again every run for ever with Failed unreachable.
        var pagesNotFound = 0;

        // The page a stale-cursor retreat stepped back from, once per run. Both a bound on the
        // retreating and the thing that stops the walk turning round and re-asking for it, and —
        // in `retreatBecause` — what that page actually did, since the retreat has two routes into
        // it and a message naming the wrong one misdirects the operator reading the run history.
        int? retreatedFrom = null;
        string? retreatBecause = null;
        DateTime? newestSeen = null;

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
                pagesNotFound++;

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

            pagesServed++;

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

            // Before the heading is trusted for anything, and before any counter moves. A page
            // that could not be read must not get to write the ship a number either.
            // ParseTotalWorks now requires the word beside the digits, so an AO3 soft-error page
            // served as 200 with <h2 class="heading">Error 404</h2> reads as no total rather than
            // as 404 — but this guard stays: a page that parsed to nothing is not a page whose
            // heading has earned the ship's size, whatever that heading says. Two independent
            // reasons a bad page cannot overwrite LastKnownTotalWorks, which is the field a full
            // sweep checks itself against before concluding works have left the tag.
            if (unreadable)
            {
                // The blurb warnings are named here rather than added to `parseWarnings` because
                // this page is about to be un-counted: a run reporting warnings from a page its
                // PagesFetched says it never read is the same conflation, one field over. On the
                // page that matters — a listing whose markup changed under the parser — this is
                // the number that says the blurbs were there and unreadable, rather than absent.
                _logger.LogError(
                    "Page {Page} for ship {ShipId} ({Tag}) parsed to no works from {Length} characters of "
                    + "HTML with {Warnings} unreadable blurbs, and {Reason}. Treating this as a parse "
                    + "failure rather than the end of the listing.",
                    page, ship.Id, ship.CanonicalTagName, response.Content.Length, listing.ParseWarnings,
                    WhyNotTheEnd(listing, page, listingWasFiltered, blurbsRead));

                stopReason = ScrapeStopReason.Error;
                errorMessage = retreatedFrom is { } from
                    ? JumpCursorBackFrom(
                        ship, from, retreatBecause!,
                        $"page {page.ToString(CultureInfo.InvariantCulture)} before it did not read either")
                    : $"Page {page} parsed to no works, and {WhyNotTheEnd(listing, page, listingWasFiltered, blurbsRead)}";

                break;
            }

            // Only now. Every one of these four describes a page this run *read*, and until T40
            // they were written three lines above the break — so a fresh backfill whose page 1 was
            // a 200 maintenance page filed PagesFetched = 1, FirstPageFetched = 1,
            // LastPageFetched = 1, WorksSeen = 0: a run claiming a page it could not read, over
            // a counter whose own summary says "listing pages successfully parsed". T37 fixed the
            // identical conflation on the retreat path, which sets its page aside by `continue`ing
            // above; this is the other route to the same page, and it was missed.
            //
            // `firstPage` is the one with teeth. FinishAsync reads `firstPage == 1` as "this run
            // saw the newest end of the listing" and lets it move the watermark, so an unreadable
            // page 1 was one non-null `newestSeen` away from proposing a watermark off a page that
            // parsed to nothing — harmless only because the break above means no work was ever
            // ingested to set it. That is the shape of accident T24 was.
            //
            // `lastPage`'s reader is the 404 branch's `lastPage == page - 1`, and that arithmetic
            // is untouched: the walk only advances past a page that offered a next link, which an
            // unreadable page never does — it breaks. So the page before a 404 is a page that read.
            pagesFetched++;
            parseWarnings += listing.ParseWarnings;
            firstPage ??= page;
            lastPage = page;

            blurbsRead += listing.Works.Count;

            // A restricted work is documented as invisible to a logged-out request, so one arriving
            // on a response the client says it did not authenticate means one of those two beliefs
            // is wrong. Reported, never acted on: letting the page overrule the transport about what
            // this run sent would write the flag `true` over a total demonstrably fetched without a
            // session — the exact claim the field exists to make trustworthy — on the strength of a
            // markup premise nothing in this repo has verified. That premise is T79's business, and
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

            RecordTotal(ship, listing, listingWasFiltered, response.Authenticated);

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
                TrackBackfillFloor(ship, page, listing);
                await _db.SaveChangesAsync(ct);
            }

            if (!listing.HasNextPage)
            {
                stopReason = ScrapeStopReason.LastPage;
                break;
            }

            // The page after this one is the one the last several runs each spent a request on and
            // got nothing back from. Do not spend another. Deliberately below the LastPage stop
            // above, so a listing that has since shrunk to end here still ends the run healthily
            // and still moves the watermark — the hold applies to asking for the next page, not to
            // reading this one.
            if (heldAfter is { } held && page >= held.Page)
            {
                _logger.LogWarning(
                    "Page {Next} for ship {ShipId} ({Tag}) has not answered for at least the last {Runs} runs, "
                    + "so it was not requested this one. Page {Page} was read as usual, and the page after it "
                    + "is asked for again after {Probe} held runs.",
                    page + 1, ship.Id, ship.CanonicalTagName, held.Runs, page, ProbeHeldPageEveryNthRun);

                stopReason = ScrapeStopReason.Held;
                errorMessage =
                    $"Page {(page + 1).ToString(CultureInfo.InvariantCulture)} has not answered for at least "
                    + $"the last {held.Runs.ToString(CultureInfo.InvariantCulture)} runs; it was not requested "
                    + "this run";
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
            context, ship, stopReason, startPage, firstPage, newestSeen,
            askedStaleCursor: retreatedFrom is not null, pagesServed: pagesServed,
            pagesNotFound: pagesNotFound, ct);

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

    // ---- a page an incremental pass cannot get past ---------------------------------------------

    /// <summary>
    /// The page an incremental walk may not ask past this run, and how long it has been stuck there.
    ///
    /// <paramref name="Runs"/> is a floor, not a total: the streak is read out of a window
    /// <see cref="ProbeHeldPageEveryNthRun"/> runs deep, so a ship stuck for fifty runs reports the
    /// window's depth. Everything reading it says "at least" for that reason — the number is there
    /// to tell one bad afternoon from a refusal, and widening the query to make it exact would buy
    /// nothing either decision needs.
    /// </summary>
    private sealed record HeldPage(int Page, int Runs);

    /// <summary>
    /// The deepest page recent incremental runs of this job have all managed to read before
    /// stopping short — once enough of them have stopped at the same place for that to be a
    /// refusal rather than an accident.
    ///
    /// The shape being bounded: a ship whose page 1 is entirely newer than the watermark and offers
    /// a next link, followed by a page 2 that will not answer. Nothing in the walk concludes
    /// anything from that — correctly, since concluding the end of the listing would move the
    /// watermark past works page 2 holds and no later incremental pass looks behind a watermark
    /// (T24). But the run stops with <see cref="ScrapeStopReason.Error"/>, an <c>Error</c> may not
    /// move the watermark, and an incremental pass has no cursor — so the next run rebuilds the
    /// identical two requests, and so does every run after it, for ever. Two ways in: a 404 on
    /// page 2, and a filtered page 2 carrying no heading to say the result set ended (see
    /// <see cref="FilteredHeadingSaysThisIsAll"/>, whose own doc names this as its price).
    ///
    /// What is wrong there is only the second request. The first is doing its job — page 1 is where
    /// new works appear, and every one of these runs ingests them — so the bound is on the *depth*
    /// of the walk and not on how often the ship is scraped or on what it may conclude. Holding at
    /// page N gives up nothing the erroring runs were achieving: they were not getting past N
    /// either. What it does not do, and must not, is decide anything about the listing; the
    /// watermark stays exactly where <see cref="FinishAsync"/> left it.
    ///
    /// Derived from <c>ScrapeRuns</c> rather than counted into a column on the ship. The run
    /// history already records what each run read and why it stopped, so a counter would be a
    /// second copy of it to keep in step — and the reset half of exactly that pairing is what
    /// <see cref="MaxStalledBackfillRuns"/> needed two tasks to get right. A streak read from
    /// history cannot fall out of step: one healthy run and it is gone, with nothing to remember to
    /// clear.
    ///
    /// Returns null — the walk goes as deep as it likes — in three cases. When the streak is short.
    /// When the most recent run read no page at all: runs that read nothing name no page to hold at,
    /// and "the page after none" is page 1, which the hold — sitting below the read rather than
    /// above it — would turn into refusing page 2 on the strength of failures that never got as far
    /// as asking for it. And once every <see cref="ProbeHeldPageEveryNthRun"/> held runs, which is
    /// how a listing that heals is found again without an operator.
    /// </summary>
    private async Task<HeldPage?> HeldAfterPageAsync(int jobId, CancellationToken ct)
    {
        // Id descending is the order the runs started in, and a job's runs never overlap, so among
        // the finished ones that is the order they ended in too. `CompletedAt != null` is what
        // leaves out this run's own row — the worker opens it before the scraper is called, and it
        // carries no stop reason and no page yet, so counting it would end every streak at nothing.
        var recent = await _db.ScrapeRuns
            .AsNoTracking()
            .Where(r => r.ScrapeJobId == jobId
                && r.Mode == ScrapeRunMode.Incremental
                && r.CompletedAt != null)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.StopReason, r.LastPageFetched })
            .Take(StreakWindow)
            .ToListAsync(ct);

        if (recent.Count == 0) return null;
        if (recent[0].LastPageFetched is not { } page) return null;

        // Held runs count towards the streak alongside the errors that started it: a run that did
        // not ask is not evidence the page has recovered, and dropping them would end the streak on
        // the first held run and restore the every-tick request this exists to stop.
        var stuck = recent
            .TakeWhile(r =>
                r.LastPageFetched == page
                && r.StopReason is ScrapeStopReason.Error or ScrapeStopReason.Held)
            .Count();

        if (stuck < MinStuckIncrementalRuns) return null;

        var heldInARow = recent
            .TakeWhile(r => r.LastPageFetched == page && r.StopReason == ScrapeStopReason.Held)
            .Count();

        return heldInARow >= ProbeHeldPageEveryNthRun ? null : new HeldPage(page, stuck);
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
    /// comparable with. Counting no more than the run was served is the listing agreeing that this
    /// is all of it, and the race above is exactly what shrinks that count, so the agreement is the
    /// case the waiver exists for.
    ///
    /// Anything else keeps <c>page > 1</c>, including a page whose heading did not parse at all.
    /// The waiver replaces one piece of evidence with another and is not entitled to fire on a page
    /// carrying neither: no heading is no evidence, and no evidence is not permission (see
    /// <see cref="FilteredHeadingSaysThisIsAll"/>, which is where reading it the other way round
    /// cost a run's worth of works).
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
        && (page == 1 || (listingWasFiltered && FilteredHeadingSaysThisIsAll(listing, blurbsRead)))
        && !listing.HasNextPage
        && !(listing.TotalWorks > 0 && !listingWasFiltered);

    /// <summary>
    /// Whether a filtered listing's heading says the run has been served the whole result set.
    ///
    /// Read only where <see cref="PlausiblyTheEndOfTheListing"/> waives <c>page > 1</c>, and it is
    /// what the waiver rests on rather than a side condition on it: a page carrying no readable
    /// heading is no evidence, and no evidence is not permission. Stated the other way round — as
    /// "the heading does not say there is more" — the absent heading read as agreement, and a
    /// filtered page 2 with the container, no Next link, no blurbs and no heading satisfied every
    /// condition and concluded <see cref="ScrapeStopReason.LastPage"/>. That moves the watermark to
    /// page 1's newest reading, and the listing being <c>revised_at</c> descending, everything page
    /// 2 would have held is older than that and newer than the old watermark: skipped by every
    /// later incremental pass, silently, on a run recorded as a success.
    ///
    /// The price is that such a page stops the run with <see cref="ScrapeStopReason.Error"/>
    /// instead, and an incremental pass has no retreat to recover with — so it re-reads the same
    /// two pages every tick until the heading comes back. That is the trade this scraper has made
    /// every time it has been offered: a refusal costs requests at the scheduler's spacing and
    /// files a failed run naming the page, and a wrong conclusion costs works permanently and says
    /// nothing.
    /// </summary>
    private static bool FilteredHeadingSaysThisIsAll(Ao3ListingPage listing, int blurbsRead) =>
        listing.TotalWorks is { } matched && matched <= blurbsRead;

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

        if (listingWasFiltered && page > 1 && !FilteredHeadingSaysThisIsAll(listing, blurbsRead))
            return listing.TotalWorks is { } matched
                ? $"the heading counts {matched.ToString(CultureInfo.InvariantCulture)} works "
                    + $"matching this run's date filter and the run has been served "
                    + $"{blurbsRead.ToString(CultureInfo.InvariantCulture)}"
                : $"page {page.ToString(CultureInfo.InvariantCulture)} was only reached because an earlier "
                    + "page offered a next one, and it carries no heading to say the date filter's results "
                    + "ended here";

        // The unfiltered walk's remaining evidence. A filtered page past the first is answered by
        // the branch above whether it carries a heading or not, and a filtered page 1 waives this
        // condition outright — so nothing filtered reaches here.
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

            // Part of starting, not a tidy-up. A streak counts *consecutive* runs of one backfill
            // getting nowhere, so a walk that is beginning has no streak by definition — and a ship
            // arriving here carrying one (a NotStarted row written by hand, or a future path that
            // rewinds a ship to NotStarted) would otherwise inherit a count it did not earn and give
            // up on its first stalled run rather than its twelfth.
            ship.BackfillStalledRuns = 0;
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
    ///
    /// <see cref="Ship.LastKnownTotalWasAuthenticated"/> is written here, from
    /// <paramref name="authenticated"/>, because Ship documents it as whether the request that
    /// produced *the stored total* was logged in — so the two have to be assigned in the same
    /// breath or the pair says something no request did. A run mixes both answers across its pages:
    /// a cached page preserves <c>Authenticated: false</c>, a page carrying no evidence either way
    /// (a 404, a file body) reads false as well, and after T5 a session can die mid-run, so every
    /// later page goes out anonymous. Accumulated over the run instead, the flag would stamp
    /// "counted while logged in" on a heading demonstrably read without a session.
    ///
    /// Assignment, not a latch, and in two senses. Within a run, every unfiltered page with a
    /// readable heading rewrites the total, so the last one to write owns the flag. Across runs, a
    /// logged-in run stamps the flag, the session lapses, and a later anonymous run reads a fresh
    /// total short by however many restricted works the tag holds — left true, the flag would tell
    /// T15's sweep to allow for an invisibility the stored number no longer has. A filtered pass,
    /// or an unfiltered one whose heading will not parse, writes neither and leaves both to
    /// whichever run's number is still on the ship.
    /// </summary>
    /// <param name="authenticated">Whether the response this heading was read from was served to a
    /// session, as the transport read it back off that page and off no other.</param>
    private void RecordTotal(
        Ship ship, Ao3ListingPage listing, bool listingWasFiltered, bool authenticated)
    {
        if (listingWasFiltered) return;
        if (listing.TotalWorks is not { } total) return;

        ship.LastKnownTotalWorks = total;
        ship.LastKnownTotalWorksAt = _time.GetUtcNow().UtcDateTime;
        ship.LastKnownTotalWasAuthenticated = authenticated;
    }

    /// <summary>
    /// Tracks the oldest revision time seen so far. A reading that moves *up* means pages shifted
    /// under the walk — works are re-sorted as they are edited — which a later full sweep is the
    /// only thing that can put right.
    ///
    /// <paramref name="page"/> is passed in rather than read off the ship because the cursor has
    /// already been advanced past it by the time this runs, and the page a human needs named in the
    /// warning is the one the shift was seen on, not the walk's next stop.
    /// </summary>
    private void TrackBackfillFloor(Ship ship, int page, Ao3ListingPage listing)
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
                ship.Id, ship.CanonicalTagName, page, floor, previous);
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
        Ship ship, string stopReason, int startPage, bool askedStaleCursor, int pagesServed,
        int pagesNotFound, DateTime now)
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
        //
        // `pagesServed` rather than `firstPage`, which used to say this by accident: it was set
        // before the unreadable-page break, so "AO3 served a body" and "the parser read it" were
        // the same fact and either reading of the guard gave the same answer. T40 separated them,
        // and the guard wants the first — a page that arrived and did not read is the case this
        // counter is counting. Reading it as the second instead freezes the streak for a backfill
        // whose cursor has reached page 1: `CursorMayBeStale` requires `page > 1`, so no retreat
        // can run there, `askedStaleCursor` is false for ever, and a ship parked on an unanswerable
        // page 1 would re-request it once a run with `Failed` unreachable. Counting it is only
        // defensible because T38 made the write-off recoverable — `POST /api/admin/ships/{id}/
        // backfill/restart` puts a Failed backfill back to InProgress — so the bound now ends a
        // pointless request-a-run loop rather than retiring a back catalogue permanently.
        //
        // `pagesNotFound` is the third of them, and the one this guard used to get wrong. A 404 is
        // the archive answering: the page asked for is not there. That is a fact about the request,
        // not about the archive's health, and twelve runs of it is not an afternoon's outage — it is
        // a ship pointed at a tag AO3 has stopped serving, which is precisely what the counter is
        // for. It matters only at page 1, since every deeper cursor gets a retreat and so is counted
        // by `askedStaleCursor` already.
        //
        // What that ends is the backfill — the ship stops being InProgress for ever and the run
        // history finally says why. It does not end the requests: ScrapeWorker gives a Failed
        // backfill an incremental pass, which asks page 1, takes the same 404, and does it again
        // every tick. T45's held-page bound cannot catch that one, because the streak it reads is
        // keyed on a page some run got through — see T82.
        if (!askedStaleCursor && pagesServed == 0 && pagesNotFound == 0) return;

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
        bool askedStaleCursor,
        int pagesServed,
        int pagesNotFound,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        if (context.Mode == ScrapeRunMode.Incremental) ship.LastIncrementalRunAt = now;

        if (context.Mode == ScrapeRunMode.Backfill)
            RecordBackfillProgress(
                ship, stopReason, startPage, askedStaleCursor, pagesServed, pagesNotFound, now);

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

        await _db.SaveChangesAsync(ct);
    }
}
