using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// One ship the current user watches, joined to the shared scrape state behind it.
/// </summary>
/// <param name="ShipId">The shared <c>Ship</c>, not the per-user subscription — it is what the
/// unwatch and works-filter endpoints take, and the subscription row id is of no use to a client.</param>
/// <param name="WorkCount">Works currently in this ship's index. Excludes ones a completed sweep
/// found had left the tag, and ones AO3 has deleted.</param>
/// <param name="WatcherCount">How many users on this instance watch the tag. Shown so it is
/// obvious that unwatching does not throw the scraped data away when someone else still wants it.</param>
/// <param name="ScraperAvailable">Whether an implementation is registered for the job's scraper
/// key. False on this build for every ship — see <c>Ao3ScraperKeys.ShipIndex</c>.</param>
/// <param name="VerificationState">"Pending", "Verified" or "NotFoundOnAo3".</param>
/// <param name="VerificationError">Why the last check settled nothing. Present only while Pending,
/// and only after at least one attempt.</param>
/// <param name="RequestedTagName">What this user typed, when AO3 turned out to call the tag
/// something else. Null in the ordinary case — the UI shows it only to explain the difference.</param>
/// <param name="BackfillNextPage">The listing page the back-catalogue walk will ask for next, or
/// null before it has started. Where the last run *landed*, not how deep the walk ever got — the
/// halving retreat moves it backwards. Shown so that "stuck" and "gave up" can name a page rather
/// than nothing, and so a restart has a default.</param>
/// <param name="BackfillResumePage">Where a restart resumes the backfill under way — the deepest
/// page a run has read, or the page an admin last named. Null before either. Nothing but a restart
/// lowers it, so shown beside the cursor it is the only thing on this page that makes the halving
/// retreat visible to an operator at all.</param>
/// <param name="BackfillStalledRuns">Consecutive runs that got no further through the listing. Non-
/// zero means the ship is spending requests on a page AO3 will not answer, which the run history
/// knew and this page did not.</param>
/// <param name="FullSweepNextPage">The listing page the sweep under way will ask for next, or null
/// when no sweep is in flight — which is the whole of "is this ship being swept". A sweep displaces
/// the ship's incremental pass for as many ticks as it takes, so without this a ship can spend days
/// collecting no new works with nothing on this page saying why.</param>
/// <param name="LastFullSweepStartedAt">When the most recent sweep began walking page 1. Also what
/// the next sweep is spaced from, finished or not — see <c>ScrapeWorker.FullSweepIsDue</c>. Left
/// standing by an abandoned sweep, which is how one can be read here: a start with no completion
/// after it and nothing in flight.</param>
/// <param name="LastFullSweepCompletedAt">When a sweep last walked the listing to its end and was
/// entitled to conclude what had left the tag. Null until one has, and never written by a sweep
/// that concluded nothing.</param>
public record WatchedShipDto(
    int ShipId,
    string TagName,
    bool NotificationsEnabled,
    DateTime WatchedSince,
    int WorkCount,
    int WatcherCount,
    string BackfillState,
    DateTime? LastScrapedAt,
    DateTime? NextScrapeAt,
    bool IsScheduled,
    bool ScraperAvailable,
    string VerificationState,
    string? VerificationError,
    string? RequestedTagName,
    int? BackfillNextPage,
    int? BackfillResumePage,
    int BackfillStalledRuns,
    int? FullSweepNextPage,
    DateTime? LastFullSweepStartedAt,
    DateTime? LastFullSweepCompletedAt,
    DateTime? WholeListingReadLoggedInAt,
    DateTime? FullSweepRequestedAt);

/// <summary>
/// The ships list, wrapped so it can carry one instance-wide fact alongside them.
/// </summary>
/// <param name="VerificationEnabled">
/// Whether this instance may check tags against AO3 at all — the operator-contact gate, and
/// nothing else. False leaves a page full of ships stuck on "Checking…" with no visible
/// explanation, and the setting that fixes it is admin-only, so the flag is reported to every user
/// even though the configuration behind it is not.
/// </param>
/// <param name="Ao3LoginConfigured">
/// Whether the deployment's AO3 login is stored. Readable by every user, not just admins: a
/// non-admin whose library is empty is owed the reason, and this is the reason. Scraping is held
/// while it is false — see <see cref="Services.Scraping.ScrapingGate"/>.
/// </param>
public record WatchedShipsDto(
    IReadOnlyList<WatchedShipDto> Ships,
    bool VerificationEnabled,
    bool Ao3LoginConfigured);

/// <summary>
/// A tag to start watching, exactly as AO3 renders it — <c>Clarke Griffin/Lexa</c>.
/// </summary>
/// <remarks>
/// Deliberately does not require a <c>/</c>. It reads like a safe check for "is this really a
/// couple tag", but AO3 uses <c>&amp;</c> for platonic pairings and lists poly ships with several
/// slashes, so the rule would reject valid relationship tags while still admitting any typo that
/// happens to contain a slash. The UI says what shape is expected; the API does not enforce it.
/// </remarks>
/// <remarks>
/// The message is on the attribute, not only in the controller: <c>[ApiController]</c> short-
/// circuits on model validation, so a blank tag never reaches the action and the controller's own
/// check — which still guards a direct call — never gets to phrase the error. Both say the same
/// thing so the caller sees one wording either way.
/// </remarks>
public record AddWatchedShipRequest(
    [Required(ErrorMessage = "Enter the relationship tag you want to track.")] string TagName);

/// <summary>
/// Where an admin wants a written-off backfill to start again.
/// </summary>
/// <param name="FromPage">
/// The listing page to resume from, 1-based. Null means the ship's stored cursor — where its last
/// run landed, which is not where the walk got to: the halving retreat drags that cursor down once
/// per stalled run, so a ship written off after reading forty pages is usually parked at page 1.
/// It is the honest default because it is the only page this instance records, and it is why the
/// choice exists at all.
/// </param>
public record RestartBackfillRequest(int? FromPage);

/// <summary>
/// What a restart left on the ship. Deliberately not a <see cref="WatchedShipDto"/>: that row is
/// scoped to one user's subscription, and this endpoint acts on the shared ship on behalf of an
/// admin who may not be watching it at all.
/// </summary>
public record BackfillRestartedDto(
    int ShipId,
    string TagName,
    string BackfillState,
    int? BackfillNextPage,
    int BackfillStalledRuns);

/// <summary>
/// What a recheck left on the ship. A <see cref="BackfillRestartedDto"/>'s sibling, and not a
/// <see cref="WatchedShipDto"/> for the same reason: it describes the shared ship, on behalf of an
/// admin who may not be watching it at all.
/// </summary>
public record VerificationRecheckedDto(int ShipId, string TagName, string VerificationState);

/// <summary>
/// What queuing a sweep left on the ship, and when the check that will start it is due. A
/// <see cref="BackfillRestartedDto"/>'s sibling, and not a <see cref="WatchedShipDto"/> for the same
/// reason.
/// </summary>
public record FullSweepQueuedDto(
    int ShipId,
    string TagName,
    DateTime FullSweepRequestedAt,
    DateTime? NextScrapeAt);
