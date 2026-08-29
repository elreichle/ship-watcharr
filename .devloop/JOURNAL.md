# Journal

Append-only. One entry per iteration, newest last.

> The 46 entries before this point live in [`JOURNAL-archive.md`](JOURNAL-archive.md) (2026-08-22 — init → 2026-08-28 — T77 One pass over the download path instead of eleven — done). Do not read it end to end; `grep` it for a task id when a live entry points into it.

## 2026-08-28 — T16 Notifications when a followed ship gains works — done

- did: A per-user `Notification` per watcher per work a followed ship gains, written where works are
  ingested under three conditions together — new to the ship, incremental pass, ship already had a
  watermark — plus list / unread-count / mark-read, a per-user cap, and deletion on unwatch.
- files: `Api/Models/Notification.cs`, `Api/Controllers/{Notifications,Ships}Controller.cs`,
  `Api/{Dtos,Data}/**`, `Api/Services/Scraping/{WorkIngestor,Ao3ShipIndexScraper}.cs`, `Tests/*`
- ran: `~Notification` → 26 (**0 before**); `dotnet test` → 875 (849); build + lint clean. 14
  mutations, all red — one only once its test was rewritten: a second scrape stops at the watermark,
  never reaching the ingestor.
- commit: f4efe9c
- next: **T17, the UI**, its only blocker. 4 review fixes in; 3 BACKLOG lines.

## 2026-08-28 — T17 The notification UI — done

- did: A Notifications page listing what followed ships gained, each line linking to its work,
  marking read per row and all at once; a sidebar badge on shared context, so a row read on the
  page drops the count at once; polled at 60s, not at all while the tab is hidden.
- files: `frontend/src/pages/NotificationsPage.tsx`, `hooks/useNotifications.ts`, `App.tsx`,
  `components/{NotificationsProvider,NavItem,Sidebar,AppLayout,navigation}`, `api/*`, `*.css`
- ran: `npm run build` clean; `npm run lint` → the two known warnings only; `dotnet test` → 875
  (unchanged, no backend change). **No live check**: the task's verification line asks for one but
  carries no `verify: live`, the marker the skill makes deciding.
- commit: c598c0f
- next: **T20** — blockers T4, T14, T17, T19 all done. Review found 5, all in this diff, all fixed.

## 2026-08-29 — T20 Docker, actually run — done

- did: Ran `docker compose up --build` for the first time. The runtime stage built an `appuser` with
  an `adduser` the aspnet:10.0 image lacks (it ships `app`, uid 1654), and with no `.dockerignore`
  the context copied the dev instance's `appdata/` into the image. Both fixed; README corrected.
- files: `Dockerfile`, `.dockerignore`, `README.md`
- ran: fresh-volume `up --build` → boots, migrates, serves UI, admin + contact + AO3 login + ship;
  `down && up` → all survives and the worker **decrypts** the stored login. `dotnet test` → 875;
  `npm run build` clean; `npm run lint` → two known warnings.
- commit: 911beea
- next: **T10**, but its blocker T51 is still todo — re-check the index. The run pointed
  `Ao3HttpClient__BaseUrl` at a closed local port so a fake credential never reached AO3.

## 2026-08-29 — T48 The byline starts at "by", not at the title's link — done

- did: `BylineWords` begins at the *last* "by" in the heading, so the title is excluded by the
  separator, not by happening to be a non-author anchor — the thing the reshaping T26 defends
  against removes. No separator, no byline words, no authority to say a work has no creators.
- files: `Api/Services/Scraping/Ao3BlurbParser.cs`, `Tests/Ao3BlurbParserTests.cs`
- ran: `dotnet test --filter ~Ao3BlurbParser` → 50 (46 before); `--filter ~Author` → 17;
  `dotnet test` → 879 (875 before); `npm run build` + lint → clean, two known warnings. No review:
  66 lines, all private.
- commit: 58cf917
- next: **T49.** The task's own `~Author` filter does not match the tests it added — the byline
  tests are named for the heading, not the author; `~Ao3BlurbParser` is the filter that covers them.

## 2026-08-29 — T49 The stale-cursor retreat's warning throws instead of retreating — done

- did: Reworded the retreat's template so both page numbers are named once each — seven
  placeholders over six args, so any provider rendering it threw the retreat away — and gave
  `CapturingLoggerProvider` opt-in rendering, which `Ao3ShipIndexScraperTests` now uses.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`,
  `Tests/{Ao3ShipIndexScraperTests,CapturingLoggerProvider}.cs`
- ran: `~Backfill` → 39 (**38 before**, not 13); `dotnet test` → 880 (879); build → no CA2017;
  `npm run build` + lint → clean.
- commit: 0d2c011
- next: **T50.** The old template reds **five** tests here, not just the new one — class-wide
  rendering is the seam. No other API template reuses a name, so "check the rest of the file" is
  closed; `Ao3ShipIndexFullSweepTests` renders nothing yet.

## 2026-08-28 — T50 Page 1 accepts the filtered contradiction page 2 refuses — done

- did: The heading is now compared against the denominator it counts — zero unfiltered, the run's
  blurb tally filtered — on every page, so a filtered page 1 counting 4,317 over no blurbs stops
  with `Error`, not a silent `LastPage`. No extra request; a quiet pass gets `0 Works` and still
  concludes. The quiet-pass test's fixture *was* the contradiction: now `total: 0`, the old one
  moved to the test refusing it.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/Ao3ShipIndexScraperTests.cs`,
  `.devloop/scraper-audit.md` (C5, C7, G3, H)
- ran: `~Ao3ShipIndexScraper` → 103 (**not 67**), 1 red first; `dotnet test` → 881 (880); build +
  lint clean.
- commit: 05239f5
- next: **T51.** Review: 5 findings, none in this diff → BACKLOG; one is T80 found independently.

## 2026-08-28 — T51 A blurb's tag list must not delete what a detail fetch added — done

- did: The rule, in `ApplyTags`: **a source may delete only within a scope it observed completely.**
  A blurb owns a work's tags until `Work.DetailFetchedAt` is set, then adds but never deletes, so
  T10's fuller list survives the next pass. No schema change. Per-type scoping and a `WorkTag`
  provenance column are rejected in the comment; `ApplyAuthors`/`ApplySeries` keep the reconcile.
  Cost: a stale tag, never a lost one; BACKLOG has the unbounded case.
- files: `Services/Scraping/WorkIngestor.cs`, `Tests/WorkIngestorPseudTests.cs`, audit E8
- ran: `~Ingest` → 15 (**11 before, not 7**), 2 red first; `dotnet test` → 885 (881); build + lint
  clean. Inverting the guard reds all four tests.
- commit: fa72ce9
- next: **T10** — its only blocker. Review: 2 findings, in-diff, fixed.

## 2026-08-29 — T79 Does an anonymous listing show restricted works? — done

- did: No — they are withheld outright, not marked. Emma's matched pair measures the premise under
  the scraper's warning and under `LastKnownTotalWasAuthenticated`: 12,285 works anonymously against
  13,736 with a session, same twenty on page 1, every facet up a tenth. The warning stays as the
  alarm for the day that stops holding (DECISIONS).
- files: `Scraping/Ao3ShipIndexScraper.cs`, `Tests/Ao3RestrictedWorkVisibilityTests.cs` (new),
  `Tests/Fixtures.cs`, 4 fixtures, `Tests/Ao3Login{Page,Establisher}Tests.cs`
- ran: `~Restricted` → 5 (**2 before**); `dotnet test` → 888 (885); build + lint clean.
- commit: 7059218; ad26602 redacts username + CSRF from all four fixtures
- next: **T10**, unblocked by T51. The shell has network now; `spec.md`'s "no network" line is stale.

## 2026-08-29 — T10 Per-work detail fetch — done

- did: A parser for a work's own page, a pass spending one request per work on the backlog
  (never-fetched, then revised-since), an ingest that reconciles tags — that page is the complete
  observation T51 defers to — and a worker behind the ship walk's gate. **Not an `IAo3Scraper`**: a
  `ScrapeJob` is one row per ship, so a new key is never scheduled (DECISIONS).
- files: `Scraping/{Ao3WorkPageParser,Ao3WorkDetailScraper,WorkDetail{Worker,Attempts},WorkIngestor}.cs`,
  `Models/Work.cs`, `Program.cs`, `DownloadFetcher.cs`, `Tests/Ao3WorkPage*`
- ran: `~Ao3WorkPage` → 36 (**0 before**); `dotnet test` → 924 (888); build + lint clean; booted on
  :5348. 8 mutations, each red in one place.
- commit: 9dd0781
- next: **T53**. Review: 5 in-diff, all fixed — chiefly an unreadable page starving the backlog.

## 2026-08-29 — T53 Which pass a ship gets — done

- did: A theory over all four `ShipBackfillState` values asserting the mode the worker hands the
  scraper *and* the mode on the `ScrapeRun` row, with the sweep arm held off (`LastFullSweepStartedAt
  = now`) so backfill-vs-incremental is what is pinned. `Failed` had never been run end to end.
- files: `backend/Ao3Tracker.Tests/ScrapeWorkerModeChoiceTests.cs` (new; no source change)
- ran: `~ScrapeWorker` → 22 (18 before); `dotnet test` → 928 (924); build clean, lint 2 pre-existing
  warnings. 3 mutations of the ternary and of `Mode = mode`, each red only where expected.
- commit: 21c37b9
- next: **T54**. No review: 78 lines, one new test file, no source touched.

## 2026-08-29 — T54 An unreadable date must not erase the date a working pass read — done

- did: Two tests on the ingestor's `UpdatedAt` guard — a work re-seen through an undated blurb keeps
  the date an earlier pass read; a work first seen undated still takes the next readable date. Only
  the first-seen arm was pinned before. No production change.
- files: `backend/Ao3Tracker.Tests/WorkIngestorTimestampTests.cs` (new)
- ran: `~Ingest` → 17; `dotnet test` → 930 (928 before); build clean, lint 2 pre-existing warnings.
  3 mutations of the guard and its else arm, each red only in the test that owns it.
- commit: d480e43
- next: **T55**. The else arm also marks a *preserved* exact date approximate — asserted as current
  behaviour, written up in `BACKLOG.md`. No review: 77 lines, one new test file, no source touched.

## 2026-08-29 — T55 A failed credential fetch leaves the AO3 login block loading forever — done

- did: The AO3-login read got its own error state, kept apart from the form's `loginError`, rendered
  with a `Try again` button in place of the spinner — `credential === null` was the only "not loaded"
  signal and a rejected fetch never clears it, so a failed read showed an error *and* "Loading…".
- files: `frontend/src/pages/AdminScrapingPage.tsx`
- ran: `npm run build` → clean; `npm run lint` → the 2 known fast-refresh warnings; `dotnet test` →
  930 passed. No live browser check: the entry carries no `verify: live` line.
- commit: d4778fe
- next: **T56**. `DownloadsPage`/`ShipsPage` have the identical defect (other pages guard theirs with
  `!error &&`) — one line in `BACKLOG.md`, not folded in. No review: 27 lines, one file.

## 2026-08-29 — T56 A concurrent clear turns another edit into a 500 — done

- did: `SetWorkState`'s read-then-write is a bounded retry (3): a lost insert writes onto the winner,
  a lost update re-inserts, and the clear branch retries too — since a re-insert can now put a row
  under a clear that assumed absence. The update loss was a bare 500 with the edit lost.
- files: `backend/Ao3Tracker.Api/Controllers/WorksController.cs`,
  `backend/Ao3Tracker.Tests/LibraryTestHost.cs`, `backend/Ao3Tracker.Tests/UserWorkStateTests.cs`
- ran: `dotnet test` → 934 passed; `npm run build` → clean; `npm run lint` → the 2 known warnings
- commit: 821c301
- next: **T57**. `code-review medium` found 3 (all low, all folded in: an XML doc block that had
  drifted onto the wrong member, the clear-path hole above, a transaction caveat on the new seam).

## 2026-08-29 — T57 The feed's note editor drops text typed while a save is in flight — done

- did: `commitNote` captures a touch counter — bumped by typing in, opening, or closing any note
  editor — and resets nothing if it moved during the write; `showNoteEditor` is the only door onto
  `openNoteId`. Text typed after Save was replaced by the sent copy, and a reopened editor was shut.
- files: `frontend/src/pages/WorksPage.tsx`
- ran: `npm run build` → clean; `npm run lint` → the 2 known warnings; `dotnet test` → 934 passed
- commit: 6c28ad5
- next: **T58**. One counter, not the detail page's `draft === sent`: that guard cannot see the
  close/reopen symptom, which is the second half of this task's `delivers`. 45 lines, review skipped.

## 2026-08-29 — T58 The incremental pass sends a filter AO3 discards — done

- did: `BuildUrl` sends `work_search[date_from]` — the key the captured filter form offers under
  "Date Updated" — not `/works/search`'s `work_search[revised_at]`, which the tag listing discarded,
  serving the whole tag every routine pass. `RevisedAtBound` → `DateFromBound`, keeping
  `AddDays(-1)`: inclusive now, so a day wider, and this bound decides no boundary.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`,
  `Tests/{Ao3ShipIndexScraperTests,Ao3ShipIndexFullSweepTests}.cs`
- ran: `dotnet test` → 935 (934 before); build + lint clean. `~BuildUrl`, the task's own filter,
  matches **zero**. Mutation: old key back → 2 red.
- commit: f0993da
- next: **T59.** `listingWasFiltered` is now true of the request; the total logic was left alone
  deliberately — see DECISIONS.

## 2026-08-29 — T59 A refetch must not cost a reader the copy they already have — done

- did: split the request's answer from the reader's copy — `WorkDownloadFileId` still refused unless
  Complete, new `PreviousWorkDownloadFileId` holds the bytes they already had, served while queued or
  failed, cleared when a replacement lands. Written only for a file checked on disk.
- files: `Api/{Models/Download,Dtos/DownloadDtos,Controllers/DownloadsController,
  Data/Configurations/UserDataConfigurations,Services/Downloads/DownloadFetcher}.cs`, 2 migrations,
  `Tests/Download{sController,Worker}Tests.cs`, `frontend/src/{api/types,pages/DownloadsPage,
  components/WorkDownloads}`
- ran: `dotnet test` → 943 (935 before); build + lint clean. Mutation: drop the carry-over → 5 red.
- commit: cf78fd6
- next: **T60.** Two findings the review raised are pre-existing — in BACKLOG.

## 2026-08-29 — T60 A download must not read its address off a page the work has moved past — done

- did: compared the page's age against the revision's — `Work.UpdatedAtObservedAt` (ours, stamped by
  the ingestor only on a move) against `ScrapeHttpResponse.FetchedAt` (stamped before caching).
  Older page → re-read via the new `GetFreshAsync`; an unchanged work stays a cache hit.
- files: `Api/{Models/Work,Services/Scraping/{IRateLimitedHttpClient,RateLimitedAo3HttpClient,
  WorkIngestor},Services/Downloads/DownloadFetcher}.cs`, 2 migrations, `Tests/{DownloadWorker,
  WorkIngestorTimestamp,Ao3ResponseCache}Tests.cs`, `Tests/LibraryTestHost.cs`
- ran: `dotnet test` → 951 (943 before); build + lint clean. 3 mutations → 2, 1, 1 red.
- commit: 2548205
- next: **T62.** Review's medium is T60's mirror (page *newer* than the row) — pre-existing, in
  BACKLOG.

## 2026-08-29 — T62 The startup partials sweep can take the whole API down with it — done

- did: guarded `Directory.GetFiles` in `DiscardPartialFiles` — an unreadable partials directory is
  logged, not thrown — and wrapped `ExecuteAsync`'s startup sweep in the loop's own guard, since
  "nothing may end this loop" applies to the step before it too.
- files: `Api/Services/Downloads/DownloadWorker.cs`, `Tests/DownloadWorkerTests.cs`
- ran: `dotnet test` → 952 (951 before); new test red without the guard; build + lint clean.
- commit: 4ef36b1
- next: **T68.** Review's other two (a leaked `_attempts` entry, a session dying mid-drain) are
  pre-existing and in BACKLOG.

## 2026-08-29 — T68 Two edits to one work's state can silently keep the older one — done

- did: each row's state writes now queue behind that row's last one (`stateWriteChains`, keyed by
  work id; the detail page keeps one chain, reset when it moves to another work) — the write tokens
  only ever governed which *response* repainted, never the order the whole-state PUTs landed in.
- files: `frontend/src/pages/WorksPage.tsx`, `frontend/src/pages/WorkDetailPage.tsx`
- ran: `npm run build` → clean; `npm run lint` → the 2 known warnings; `dotnet test` → 952 passed
- commit: 489fdca
- next: **T72.** Review skipped: ~20 substantive lines, the rest re-indentation, no new surface.

## 2026-08-29 — T72 A download link is measured against the archive, not where the page landed — done

- did: `Ao3DownloadLinks.Parse` checks every href's origin against the configured `BaseUrl`, the
  page URL demoted to a base for relative hrefs; the comparison moved to a new `Ao3Origin` the
  login establisher shares.
- also: per-link checking still let a substituted page *choose* the file, so `DownloadFetcher`
  refuses a page whose `FinalUrl` is not the archive. Two backlog lines from the review.
- files: `Services/Scraping/Ao3{DownloadLinks,Origin,SessionEstablisher}.cs`,
  `Services/Downloads/DownloadFetcher.cs`, `Ao3DownloadLinksTests.cs`, `DownloadWorkerTests.cs`
- ran: `dotnet test` → 963 passed; `npm run build` clean; `npm run lint` → the 2 known warnings
- commit: 8d8a61a
- next: **T76.**

## 2026-08-29 — T76 The pseud merge cascade-deletes a saved filter's author criterion — done

- did: `NormalizedPseudIdentity` thins and repoints `SavedWorkFilterAuthors` before deleting the
  losing pseuds, so a criterion moves onto the survivor instead of cascading away with it. Thinned
  in `(Exclude, PseudId)` order: an include beats an exclude, being what kept the filter narrow.
- files: `Api/Data/Migrations/{Sqlite/20260822182752,Postgres/20260822182800}_NormalizedPseudIdentity.cs`,
  `Tests/PseudMigrationTests.cs`
- ran: `~PseudMigration` → 9 (5 red first, criteria **empty**); `dotnet test` → 968; `npm run build`
  clean; lint → 2 known warnings. Review: no defects, and it reds all 5 with the new SQL deleted.
- commit: 0131913
- next: **T78.** Editing in place is safe by T35's test, re-checked: this branch only, and it ran
  against an empty `Ao3Pseuds`.

## 2026-08-29 — T78 A restart resumes where the walk read to — done

- did: `Ship.BackfillResumePage` — raised by every backfill page read, never by the halving retreat,
  replaced by a page an admin names, cleared when a backfill begins. Restart and the Ships prefill
  default to it; the status line names it beside the cursor when they differ.
- files: `Api/{Models/Ship,Dtos/ShipDtos,Controllers/{Admin,}ShipsController,Services/Scraping/Ao3ShipIndexScraper}.cs`,
  two `*_BackfillResumePage` migrations, `Tests/{Ao3ShipIndexScraper,BackfillRestart}Tests.cs`,
  `frontend/src/{api/types.ts,pages/ShipsPage.tsx}`
- ran: `~Backfill` → 48 (4 red first); `dotnet test` → 974; build clean; lint → 2 known.
- commit: 12afd3f
- next: **T80** (T39 removed its blocker). Review's two findings fixed in the amend — which is why
  the column is `ResumePage`, not `DeepestPageRead`.
