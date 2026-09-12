# Backlog

Findings and ideas the loop does **not** work. One line each: `- <where> — <what> (source: T<n> review | T<n> iteration)`.
A human promotes an item into `tasks.md` between runs; the loop only adds here.

- `ShipsController` — `WatchedShip.NotificationsEnabled` has no writer: no PATCH on a subscription, and the field is read-only in `WatchedShipDto` and the frontend types. T16 made it load-bearing, so today the only way to stop being told about a ship is to unfollow it, which also stops its works reaching the library — the conflation the switch exists to prevent. Neither T16's nor T17's `delivers` covers it. (source: T16 review)
- `WorksController.cs:117`, and the shape T16 fixed in `NotificationsController` — `Skip((page - 1) * pageSize)` overflows int for a large `page`, so a malformed query string is a 500 rather than an empty page. (source: T16 review)
- Controllers write timestamps from `DateTime.UtcNow` (`WorksController`, `DownloadsController`, `SavedFiltersController`, and now `NotificationsController.MarkReadAsync`) while services take the injected `TimeProvider`. A notification's `CreatedAt` and `ReadAt` therefore come from two clocks, which under a test's fake clock lets a row be read before it was created. One clock or the other. (source: T16 review)
- `hooks/useDownloads.ts` — `refresh` has no sequencing against `request`: a poll that went out before a click can land after it and revert the row the response authoritatively set. The same race T17 fixed in `NotificationsProvider`, one hook over. (source: T17 review)
- `AdminScrapingController` / `ScrapingIdentityDto.DefaultContact` — settings.json is layered over environment variables by design, so once an admin saves an operator-contact override it becomes the *configured* value on the next boot, and `DefaultContact` reports the override as the thing "clear" would revert to. Seen in Docker, where `AO3_OPERATOR_CONTACT` from `.env` is what a clear should return to. (source: T20 iteration)
- `Ao3ShipIndexScraper.cs` — the `MaxPagesPerRun` ceiling stops with `Cap` and a null message, but `ScrapeWorker` takes `HitRequestCap`/`HitTimeCap` from the *budget*, which never fired: the run files as Succeeded with no cap flag and no message, so a pagination bug burning 200 pages a run reads as a healthy scrape. `BudgetStopMessage`'s doc justifies the silence on flags that are false here. (source: T50 review)
- `Ao3ShipIndexScraper.cs` — an incremental pass that stops on `Cap`/`TimeCap`/`Breaker` has no cursor and may not move its watermark, so a ship more than `MaxPagesPerRun` pages behind (reachable after a `Held`/`Breaker` stretch) re-walks pages 1–200 every tick and never reads page 201. `HeldAfterPageAsync` excludes `Cap` from its streak, so T45's bound does not see it. (source: T50 review)
- `Ao3ShipIndexScraper.cs:536` — `Ao3WorkBlurb.UpdatedAtIsApproximate` is parsed, stored and never consulted: a blurb missing the `updated_at` comment parses to midnight, so a work revised later the same day falls behind an intra-day watermark, lands in `alreadyHad` and ends the pass. `RevisedAtBound`'s slack puts it in the response; the client-side cut drops it. (source: T50 review)
- `Ao3ShipIndexScraper.cs:1307` — `ConcludeSweepAsync` returns on a null `LastFullSweepStartedAt` without clearing `FullSweepNextPage`, and `FullSweepIsDue` keys on that cursor: a ship in that state would sweep every tick for ever and never take an incremental pass. Unreachable today (`BeginSweep` writes both, nothing clears the timestamp) — `AbandonSweep` before the return closes it. (source: T50 review)
- `WorkIngestor.ApplyTags` / T10's re-fetch rule — a tag the author removes from a detail-fetched work is now never dropped unless a detail re-fetch happens, and T10 schedules one only when `Work.UpdatedAt` moves past `DetailFetchedAt`. AO3's revision timestamp tracks content, so a tag-only edit may move neither: the stale tag can outlive the work. A ceiling on detail-fetch age — re-fetch when `DetailFetchedAt` is older than N days — would bound it. Accepted by T51 as the cheaper side of the trade, not as a non-problem. (source: T51 iteration)
- `DownloadFetcher` / `Ao3WorkDetailScraper` — a download already fetches the work's own page for its links, and throws the rest of it away. Handing that markup to `IngestDetailAsync` would fill in a requested work's published date and full tag list for no extra request, ahead of the backlog's own turn. (source: T10 iteration)
- `WorkIngestor.Apply:236` — the guard's else arm sets `UpdatedAtIsApproximate = true` on every unreadable date, including a work whose date an earlier pass read exactly: the timestamp is preserved but the row stops claiming the second precision it has, until a pass that can read a date resets it. Gating that write on `work.UpdatedAt == DateTime.MinValue` would keep both columns and leave the first-seen case as it is. Pinned as current behaviour by `WorkIngestorTimestampTests`. (source: T54 iteration)
- `DownloadsPage.tsx:25` and `ShipsPage.tsx:197` — the same defect T55 fixed on the AO3-login block: the fetch's only "not loaded" signal is `list === null`, which a rejected load never clears, so a failed page load renders its error *and* a permanent "Loading…" below it, with nothing to retry. `WorksPage`/`FiltersPage`/`NotificationsPage` already guard theirs with `!error &&`. (source: T55 iteration)
- `TryArmAsync`'s `WHERE Status != Downloading` does not stop a re-arm landing on a row a worker has
  just *finished*: Complete passes the guard, so the controller can reset a completed request to
  Pending and null the file the fetch had recorded (recovered on the next drain, off disk). T77 chose
  that guard over a concurrency token; this is the interleaving it does not cover. Found by T59's
  review, not introduced by it.
- SQLite `Migrate()` at startup has no crash-safety story for a migration EF implements as a table
  rebuild: `PreviousDownloadCopy` drops and renames `Downloads` outside a transaction (EF warns), so a
  container killed in that window leaves the database needing hand repair. It is the first such
  migration here — worth a general answer (back up before migrating, or a rebuild-free rule) rather
  than a per-migration one.
- The detail pass has T60's stale-cache shape and no guard: it selects works where
  `UpdatedAt > DetailFetchedAt`, then reads the page through `GetAsync`, so a copy cached before the
  revision writes the previous version's tags and `PublishedAt` and stamps `DetailFetchedAt = now` —
  dropping the work out of the backlog holding stale detail until the next revision.
  `Work.UpdatedAtObservedAt` and `IRateLimitedHttpClient.GetFreshAsync` now exist to answer it.
- T60's mirror, pre-existing and untouched by it: a work page *newer* than the row. AO3 revises at
  10:00, no listing pass has seen it, a reader downloads at 10:05 — the page is fetched fresh, so
  T60's guard passes, and the new version's bytes are stored keyed to the old `Work.UpdatedAt`.
  `FindUsableFileAsync` then serves them to everyone asking for the old version. The exact fix a
  reviewer proposes — compare the link's `?updated_at=` epoch against `Work.UpdatedAt` — is the one
  the 2026-08-25 T13 entry rejected as different clocks; re-opening it needs that verified against a
  live pair, not re-argued.
- `DrainQueueAsync` returns on an empty queue *before* `ForgetRequestsNoLongerQueued`, so a request
  the controller settled without the worker (reader B's bytes land, reader A's row goes `Complete`)
  leaves its `_attempts` entry for ever. Re-arming that same row id later starts it at attempt 1, so
  it fails after two polls rather than three. From T62's review; the fix is to prune before the
  return, but the mid-drain reproduction is unverified.
- Nothing detects an AO3 session that dies *mid*-drain: `MayFetchAsync` checks once, a drain may run
  for hours, and a logged-out work page is a 200 with no download links — recorded as the permanent
  "no Epub download, it may be restricted" failure rather than held. From T62's review, unverified.
- The `try`/`catch` T62 added around `ExecuteAsync`'s startup sweep is defence-in-depth no test pins:
  `ReleaseInterruptedFetchesAsync` catches everything non-shutdown itself, so the only way through it
  is an exotic throw from the `finally`. The reviewer's suggested repro (a failing scope factory) is
  caught inside and does not reach it.
- A refused state write reverts the row to the value it held when the reader clicked, which may
  itself be an optimistic value an earlier refused write left there — so two failures in a row can
  leave the row showing state the server never accepted. Predates T68 and is untouched by it.
- `Ao3DownloadLinks.Parse` returns an empty menu with no signal when `BaseUrl` cannot be parsed,
  which reads to the caller as "AO3 offers no such format". A misconfigured *origin* is now named
  by DownloadFetcher's page check, but an unparsable one is still silent.

- No PostgreSQL server has executed any migration on this branch: the Postgres twins are verified
  by `diff` against the SQLite text and by reading, never by running. A container running the two
  migration-merge cases would close it.
- `frontend/src/pages/ShipsPage.tsx`: `describeStatus`'s doc block is orphaned above
  `canRestartBackfill`'s — pre-existing, noticed by T78's review; a one-line move.
- A page that parsed to *some* works and offers no Next link takes `LastPage` at
  `Ao3ShipIndexScraper.cs:637` with its heading unread, and a backfill's `LastPage` is `Complete`
  — so a pagination markup change files a 60,000-work tag as fully backfilled off page 1's twenty
  blurbs, with `TotalWorks` sitting there saying 60,000. Same shape as T80, the non-empty route;
  `HeadingCountsMoreThanTheRunWasServed` is already the predicate for it. (T80's review.)
  T82's review adds the *incremental* consequence off the same unguarded stop: that run has
  `firstPage == 1` and stops `LastPage`, so `FinishAsync` moves the watermark to page 1's newest and
  everything behind it is out of reach of every later pass — filed `Succeeded`. One fix site, two
  losses.
- A ship denied *after* it started a pass is pinned in that pass for ever: `ExecuteAsync`'s
  `NotFoundOnAo3` return sits above `FinishAsync`, so `BackfillStalledRuns` never moves and
  `FullSweepNextPage` is never cleared — and `FullSweepIsDue` returns true unconditionally on a
  non-null sweep cursor. No requests, but a `Failed` run every interval with no exit. T83 gives the
  operator a route back; this is the half that would still be stuck without one. (T82's review.)
- A sweep whose page 1 reads "0 Works" concludes `LastPage` and marks the ship's whole library
  missing on a `Succeeded` run: `RecordTotal` overwrites `LastKnownTotalWorks = 0` from that same
  page, so both of `ConcludeSweepAsync`'s guards pass by construction and the *previously stored*
  total is never consulted. Recovery waits for the next sweep, since `WorkIngestor` clears
  `MissingSinceAt` only for works re-ingested. Untested in `Ao3ShipIndexFullSweepTests`. (T82's
  review.)
- `MaxPagesPerRun` (200) is below `MaxRequestsPerRun` (500), so every backfill run of a tag deeper
  than 200 pages hits the page ceiling first — logging a warning documented as bounding "a
  pagination *bug*" and recording `StopReason = "cap"` with `HitRequestCap` and `HitTimeCap` both
  false. Routine noise, and a stop reason no flag corroborates. (T82's review.)
- `ScrapeBudget.CanContinue` returns on the breaker before evaluating the cap checks, which are what
  set `HitRequestCap`/`HitTimeCap`. A run that spent its allowance on the iteration the breaker
  opened reports `Breaker` with both flags false. (T82's review.)
- `ShipsController.EnsureScheduledAsync` schedules any ship whose verification is not `NotFoundOnAo3`,
  so a follow arriving while a tag is unverified enables its job and wakes the worker — a typo'd tag
  is walked before AO3 answers, and after T83 a rechecked one is too. Accept-on-trust as designed;
  keying on `== Verified` would change what following an unknown tag does, so it is a decision, not a
  fix. (T83's review.)
- The Ships page's per-ship work count is instance-wide and excludes works a sweep marked missing, while T84 keeps such a work in the reader's feed and ship stats row once they have marked it — the two numbers describe different questions and say so nowhere.
- Unfollowing deletes the reader's notifications with an `ExecuteDeleteAsync` that commits before the `SaveChangesAsync` removing the subscription, so a cancelled request loses them for good while the follow stands — and the sidebar's unread badge is not refreshed by the unfollow either. (T86's review.)
- `RateLimitedAo3HttpClient.GetAsync` — a session AO3 ends partway through a run is discarded, and every later page of that run goes out logged out until the next poll logs in again: a sweep abandons, a backfill loses its claim to `WholeListingReadLoggedInAt`. Rare now that Cloudflare's 30-minute cookie no longer dates the session, but unmeasured. Logging in again from inside the transport would need the establisher, which itself depends on the transport. (source: 2026-09-10 howl diagnosis)
- `Work.IsRestricted` is false on every work stored before the lock selector was fixed (726bff6), and a work is corrected only when a pass re-reads its blurb. A full re-read of each ship corrects all of them; nothing else revisits old works. (source: 2026-09-10 howl diagnosis)
- `Ao3ShipIndexScraper` incremental pass — its stop and watermark compare `updated_at` (`Work.UpdatedAt`) against a listing AO3 sorts and filters by `revised_at`, and the 2026-09-12 captures show `updated_at` running up to 8 days ahead and out of order down a page. On a tag with more than a page since the watermark, a page holding an "already had" work might end the pass before a later page is read. Unverified as a real loss; T90's `RevisedOn` is what a fix would compare. (source: Phase 1 captures, 5506610)
- `Ao3ShipIndexScraper` still describes the sweep as monthly: the page-1 count-match `<remarks>` ("once a month per ship", "the monthly re-read of every old work's blurb") and the abandon warning's "the next one starts over from page 1 after the sweep interval", which is untrue for a ship already read logged in. Since T89 a sweep is a ship's first logged-in walk or an admin's request. Possibly T93's. (T89)
- `AdminShipsController` backfill restart clears `FullSweepNextPage` but not `RecentSweepNextPage`, so a re-read in flight resumes after the restarted backfill with its old window. Harmless (the backfill re-stamps every `LastSeenAt`, so nothing is wrongly concluded) but wasted pages. (T91)
- `ConcludeRecentSweepAsync` trusts that a work's revision date only moves forward. If AO3's `revised_at` can move *earlier* (a chapter deleted, a backdated edit), such a work leaves the window without leaving the tag and a re-read marks it missing until a full sweep sees it. Unverified. Nothing else clears the mark on a covered ship: incremental passes only read recent revisions, later windows exclude it, and no sweep is scheduled. Needs a capture of a work whose newest chapter was deleted or backdated. (T91, review of beeeed8)
- A full sweep ingests silently, so a work posted since the last incremental pass is linked without a notification and the next incremental pass finds it already held — never announced. Rare since T89 (first logged-in walk or an admin's request); T91 fixed the same gap for the re-read by announcing works newer than the watermark. (T91 review)
- `Ao3RateGateTests.Every_redirect_hop_is_spaced_like_any_other_request` compares wall-clock `DateTimeOffset.UtcNow` stamps against a 400 ms gate and failed once in a full `dotnet test` run on 2026-09-11 (passed 3/3 alone and on the full rerun) — a timing flake under load, not a gate defect as far as seen. (T92)
- `ShipsPage` `describeLastRecentSweep` — a re-read put away by a full sweep starting has no completion, so the page says it "did not finish", which reads as a failure rather than superseded; `lastFullSweepStartedAt` ≥ its start would tell the two apart. Wording only, unreproduced. (T92 review)
