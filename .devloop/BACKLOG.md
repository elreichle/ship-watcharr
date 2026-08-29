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
