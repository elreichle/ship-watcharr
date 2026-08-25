# Journal

Append-only. One entry per iteration, newest last.

## 2026-08-22 — init

Baseline captured before any task ran, on `devloop/dashboard-completion` cut from
`ao3-ship-index-scraper` (2 commits ahead of `main`, tree clean):

- `cd backend && dotnet test` → 310 passed, 0 failed, 0 skipped
- `cd frontend && npm run build` → clean
- `cd frontend && npm run lint` → exit 0, two pre-existing `react(only-export-components)`
  warnings in `AuthContext.tsx` and `ThemeContext.tsx`. These predate the loop; leaving them is
  fine, fixing them is not this loop's job.

One known, accepted, unpatched advisory: `SQLitePCLRaw.lib.e_sqlite3` 2.1.11
(GHSA-2m69-gcr7-jv3q) has no fixed version available. `dotnet restore` warns about it on every
build. Not something a task should chase.

## 2026-08-22 — T1 Admin API for the instance AO3 credential — done

- did: Added `AdminAo3CredentialController` (`GET/PUT/DELETE /api/admin/scraping/ao3-credential`),
  admin-only, over the existing `IAo3InstanceCredentialStore`. Every response is the same
  status DTO — username, whether a login is stored, whether a session is cached, session
  timestamps — and no endpoint reads a password or cookie back out. Separate controller from
  `AdminScrapingController` (identity is how requests are attributed; this is what they are
  authorised as) but under the same route prefix.
- files: `Api/Controllers/AdminAo3CredentialController.cs`, `Api/Dtos/AdminDtos.cs`,
  `Tests/InstanceAo3CredentialControllerTests.cs`, `Tests/LibraryTestHost.cs`
- ran: `dotnet test --filter FullyQualifiedName~InstanceAo3Credential` → 9 passed;
  `dotnet test` → 319 passed (was 310); `npm run build` + `npm run lint` → clean
- commit: "Give an admin one place to enter the deployment's AO3 login" (the commit this entry ships in — a sha cannot name the commit that contains it)
- next: `LibraryTestHost` now also carries a real `UserManager` (`AddIdentityCore` + EF stores),
  real Data Protection over a temp key ring, and the instance credential store — so the next admin
  endpoint can be built there without re-plumbing any of it. `SeedUser` takes `isAdmin`.
  One gotcha worth knowing: `LibraryTestHost.AdminAo3Credential(user)` deliberately builds its
  controller in a **fresh scope per call**, unlike `Works`/`Ships`/`SavedFilters` which share one.
  The shared scope made a real test lie — the re-save-drops-the-session test passed a stale tracked
  entity through `SetCredentialAsync`, so EF saw no change to the session column and left the old
  cookie in the database. A real request never has that warm change tracker. Any future test that
  writes to the store out of band between two "requests" needs the per-request scope too.

## 2026-08-22 — T2 Scraping holds when no AO3 login is stored — done

- did: Added `ScrapingGate` — one scoped service answering both gates (an honest User-Agent, and a
  stored instance AO3 login), always evaluating both so a half-configured instance is told
  everything it is missing at once. `ScrapeWorker.RunDueJobsAsync` consults it every poll and
  returns early when it is shut: no `ScrapeRun` written, no `NextRunAt` advanced, no breaker
  touched, nothing fetched — the job stays due, so the next poll runs it the moment a login is
  saved. Logged on transition only, as the contact gate already was.
  `GET /api/admin/scraping/identity` now reports the same gate: `scrapingEnabled` means both gates
  pass, `problem` carries every reason, and two new fields (`identityConfigured`,
  `ao3LoginConfigured`) say which half is missing.
- files: `Api/Services/Scraping/ScrapingGate.cs` (new), `Api/Services/Scraping/ScrapeWorker.cs`,
  `Api/Controllers/AdminScrapingController.cs`, `Api/Dtos/AdminDtos.cs`, `Api/Program.cs`,
  `frontend/src/api/types.ts`, `frontend/src/pages/AdminScrapingPage.tsx`,
  `Tests/ScrapeWorkerGateTests.cs` (new), `Tests/LibraryTestHost.cs`
- ran: `dotnet test --filter FullyQualifiedName~ScrapeWorker` → 7 passed; `dotnet test` → 326 passed;
  `npm run build` + `npm run lint` → clean
- commit: "Hold scraping until this deployment has an AO3 login to scrape as"
- next: T3 has what it needs — `identityConfigured` / `ao3LoginConfigured` / `problem` on
  `/api/admin/scraping/identity`. **But that endpoint is admin-only**, and T3's banner is for every
  user, so T3 still needs a non-admin surface for "is a login configured": the Ships list already
  carries `scraperAvailable` per row and is the obvious place to put it beside.
  `RunDueJobsAsync` is now `internal` (the test project already has `InternalsVisibleTo`), and
  `LibraryTestHost` gained `NewScrapeWorker()`, `SaveAo3LoginAsync()`, `EvaluateScrapingGateAsync()`
  and an `AdminScraping(user)` controller builder over a real `IPersistedSettingsStore`.
  `AdminScrapingPage` deliberately keys "what AO3 currently sees" off `identityConfigured` rather
  than `scrapingEnabled` — a held instance still has a User-Agent.

## 2026-08-22 — T3 AO3 login in the UI, and a loud banner when it is missing — done

- did: System → Scraping grew an "AO3 login" section (username + password form, stored-account and
  cached-session status, Replace/Remove), and a red callout at the top of that page when the login
  is the only thing missing. The Ships page shows every user a red banner when
  `ao3LoginConfigured` is false: their ships are scheduled, nothing is being fetched, and their
  library stays empty until a login exists — admins are told to add it under System → Scraping,
  everyone else to ask an admin. Worded as a missing *login*, never a missing session.
  To feed a banner that non-admins must see, `GET /api/ships` now carries `ao3LoginConfigured`
  beside the existing `verificationEnabled`; both come from `ScrapingGate`, so the Ships page and
  the admin page cannot disagree with the worker. Added `.callout-error` (Obsidian
  `--text-error`), no hardcoded colours anywhere.
- files: `Api/Controllers/ShipsController.cs`, `Api/Dtos/ShipDtos.cs`,
  `frontend/src/pages/AdminScrapingPage.tsx`, `frontend/src/pages/ShipsPage.tsx`,
  `frontend/src/api/{client,types}.ts`, `frontend/src/index.css`,
  `Tests/ShipsControllerTests.cs`, `Tests/LibraryTestHost.cs`
- ran: `npm run build` + `npm run lint` → clean; `dotnet test` → 327 passed. Live check against a
  throwaway instance (free port, scratch `Storage__DataDirectory`): register admin → `/api/ships`
  reports `ao3LoginConfigured: false` → save login → `true` → delete → `false` again; a second,
  non-admin account sees the same flag and gets 403 from the credential endpoint; and the running
  worker logged "Scraping enabled…" on its next poll after the save, with no restart — the
  no-restart claim confirmed live rather than only in a test.
- commit: "Let an admin enter the AO3 login, and say loudly when there isn't one"
- next: **Do not stop a throwaway instance with `pkill -f Ao3Tracker.Api.dll`** — that pattern
  matches the systemd dev instance on 5110 (which runs the built DLL) and *not* a `dotnet run`
  instance (which is the apphost `…/net10.0/Ao3Tracker.Api`). I killed Emma's dev instance that way;
  systemd auto-restarted it within seconds, but capture `$!` and kill by PID instead. Also worth
  knowing: `dotnet build`/`dotnet test` in this loop rewrite the DLL that service runs, so it picks
  up loop changes whenever it restarts.
  For T4: the per-user credential UI in `AccountSettingsPage.tsx` and the `Ao3CredentialStatus`
  type/client calls are now the *only* remaining per-user login surface — the instance one is fully
  built, so retiring them costs nothing.

## 2026-08-22 — T4 Retire the per-user AO3 credential — done

- did: Deleted the per-user login end to end — `AccountController` (the ao3-credential one),
  `Ao3Credential`, `Ao3CredentialStore`/`IAo3CredentialStore`, the entity configuration, the
  `ApplicationUser.Ao3Credential` navigation, the DbSet, the two DTOs, the Account settings section,
  and the three client calls with their type. `Ao3Session` moved to
  `IAo3InstanceCredentialStore.cs`, which was the one thing worth keeping out of that file.
  Migration on both providers dropping `Ao3Credentials`. The account *email* controller is
  untouched — separate concern, still feeding the operator contact.
- files: deleted `Api/Controllers/AccountController.cs`, `Api/Models/Ao3Credential.cs`,
  `Api/Services/Credentials/{Ao3CredentialStore,IAo3CredentialStore}.cs`; edited
  `Api/Data/{AppDbContext.cs,Configurations/UserDataConfigurations.cs}`,
  `Api/Models/ApplicationUser.cs`, `Api/Dtos/AccountDtos.cs`, `Api/Program.cs`,
  `Api/Services/Credentials/{IAo3InstanceCredentialStore,Ao3InstanceCredentialStore}.cs`,
  `Api/Models/Ao3InstanceCredential.cs`, `Tests/Ao3InstanceCredentialStoreTests.cs`,
  `frontend/src/pages/AccountSettingsPage.tsx`, `frontend/src/api/{client,types}.ts`; added
  `Api/Data/Migrations/{Sqlite,Postgres}/*_RetirePerUserAo3Credential.cs`
- ran: `dotnet test` → 327 passed; `npm run build` + `npm run lint` → clean;
  `grep -rn "Ao3Credential\b" backend/Ao3Tracker.Api frontend/src` → only `InstanceAo3Credential*`
  names and migration history. Live check on a throwaway instance with an empty data directory:
  it booted, applied `20260822182050_RetirePerUserAo3Credential`, and its SQLite file has no
  `Ao3Credentials` table and does have `Ao3InstanceCredentials`.
- commit: "Leave exactly one place to enter an AO3 login"
- next: The carry-over test in `Ao3InstanceCredentialStoreTests` no longer goes through the deleted
  store — it encrypts under the literal purpose string `"Ao3Tracker.Ao3Credentials.v1"` and reads it
  back through the instance store, which pins the thing that actually matters: change that string
  and every credential saved before the migration becomes unreadable.
  The dev instance on 5110 runs the DLL this loop rebuilds, so its database has now had the table
  dropped too — that is the intended product change, not an accident, but it is not reversible from
  here.

## 2026-08-22 — T21 Pseud lookup folds case the way the key does — done

- did: Made "what a row is stored under" and "what it is looked up by" the same thing by
  construction. `WorkIngestor` grew two key functions — `TagKey` and `PseudKey` — that truncate to
  the column width *and* normalize, and every producer and consumer of those dictionaries now goes
  through them. `Ao3Pseud` gained `UsernameNormalized` / `PseudNameNormalized`, the unique index
  moved onto that pair, and the existing-row query filters on the normalized column instead of the
  rendered one. Two bugs closed: an author re-rendered in another case became a second row (and
  failed the page's save), and a tag over 200 characters — or an author name over 100 — missed its
  own row, so it was dropped from the work and then reconciled away on the next pass.
- files: `Api/Models/Ao3Pseud.cs`, `Api/Data/Configurations/WorkConfigurations.cs`,
  `Api/Services/Scraping/WorkIngestor.cs`, migrations
  `{Sqlite,Postgres}/*_NormalizedPseudIdentity.cs`, `Tests/WorkIngestorPseudTests.cs` (new),
  `Tests/WorksControllerTests.cs`, `Tests/SavedFiltersControllerTests.cs`
- ran: `dotnet test --filter FullyQualifiedName~Pseud` → 6 passed; `dotnet test` → 332 passed;
  frontend untouched. The migration's four SQL statements were pulled straight out of the shipped
  file and run against a SQLite database seeded with three capitalisations of one creator: they
  merged to the lowest id, repointed one work's link, dropped the link that would have collided,
  left an unrelated creator alone, and the unique index then created cleanly. A fresh instance
  booted and applied `20260822182752_NormalizedPseudIdentity` with the expected columns and
  indexes.
- commit: "Give a pseud one identity, whatever case AO3 rendered it in"
- next: The Postgres migration carries the identical SQL and could not be run here — there is no
  Postgres server in this environment. It is standard correlated-subquery SQL that both providers
  accept, but it is unverified, and T20's Docker task is the first place it could actually be
  exercised.
  One documented limitation, in the migration's own summary: the backfill uses SQL `UPPER`, which
  on SQLite folds ASCII only, while the app normalizes with `ToUpperInvariant`. AO3 usernames are
  ASCII; pseud names need not be. A pre-existing non-ASCII pseud on SQLite can therefore keep a
  normalized form the app spells differently, which costs a second row for that pseud the next time
  it is seen — not a failed save, and it does not compound.

## 2026-08-22 — T22 An unreadable blurb date must not end an incremental pass — done

- did: Split an incremental page into three groups instead of two. An undated blurb — the
  `DateTime.MinValue` the parser reports when neither date form is readable — is no longer counted
  as stale: it is ingested and then abstains, casting no vote in the watermark stop and proposing no
  watermark. The stop now counts *dated* works at or below the watermark (`alreadyHad`) rather than
  inferring them from `fresh.Count < listing.Works.Count`. Two guards around the new rule: a page on
  which everything abstains stops the pass with `Error` (so the watermark stays put) rather than
  walking the whole tag, but only when there is a next page — a last page nobody could date is a
  small tag, not a runaway. The backfill path gained the matching `MinValue` filter on the watermark
  proposal, which could previously have set a watermark of year 1. `RecordTotal` now reads `_time`
  like every other write in the class.
  From the `/code-review` pass, both fixed here because both are consequences of this change:
  the last-page false alarm above, and `WorkIngestor.Apply` storing year 1 as an *exact* date for a
  work first seen undated — it now marks such a row approximate, which is what the parser reports.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Api/Services/Scraping/WorkIngestor.cs`,
  `Tests/Ao3ShipIndexScraperTests.cs`, `Tests/LibraryTestHost.cs`
- ran: `dotnet test --filter FullyQualifiedName~Incremental` → 6 passed; `dotnet test` → 338 passed;
  `npm run build` + `npm run lint` → clean (the two pre-existing fast-refresh warnings only).
  The four original tests were confirmed red against the unpatched scraper before the fix went back
  in, so they pin the bug rather than the fix.
- commit: f3d678d "Let an undated blurb abstain instead of ending the pass"
- next: `LibraryTestHost` now has `Clock` (a settable `FixedClock` registered as the container's
  `TimeProvider`) — every fixture's clock is frozen at construction time, which nothing in the suite
  asserts a delta against, but a future test that wants elapsed time has to move `Clock.Now` itself.
  Note the verification filter `FullyQualifiedName~Incremental` matched **zero** tests before this
  task: no test name in the class contained the word. The six it matches now were named to make it
  bite. Worth checking the same for T23, whose filter is `~Ao3ShipIndexScraper` — that one does
  match the whole class.
  T23 is the last of the three pulled-forward defects, and it is in the same method
  (`ExecuteAsync`'s non-OK branch, ~line 168 now), so expect a merge-adjacent diff.

## 2026-08-22 — T23 A failing page must not be re-requested forever — done

- did: A non-OK response now ends the run instead of `continue`ing without advancing `page`. The
  walk deliberately adds no retry of its own: `RateLimitedAo3HttpClient.SendWithRetryAsync` has
  already retried a 429 or 5xx up to `MaxRetries` times with jittered backoff before the response
  reaches the scraper, and a status it did not consider retryable will not become OK by asking
  again. The run stops with `ScrapeStopReason.Error` and records the status. The transport-failure
  branch above it keeps its retry — nothing retried *that* one, since the client only retries
  responses, and the breaker is documented as its bound — with a comment at both sites so the
  asymmetry reads as a decision rather than an oversight. `ScrapeOutcome` gained an `ErrorMessage`
  that `ScrapeWorker` copies onto `ScrapeRun.ErrorMessage`, filled by all three `Error` stops in the
  class so the field is not half-populated. No migration: the column is unbounded TEXT on both
  providers.
- files: `Api/Services/Scraping/{Ao3ShipIndexScraper.cs,IAo3Scraper.cs,ScrapeWorker.cs}`,
  `Tests/Ao3ShipIndexScraperTests.cs`
- ran: `dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper` → 32 passed;
  `dotnet test` → 342 passed; `npm run build` + `npm run lint` → clean (the two pre-existing
  fast-refresh warnings only). Two of the four new tests were confirmed red first.
- commit: ecde66a "Stop asking AO3 again for a page it has already refused"
- next: **The task's premise was wrong and the measurement is the useful part.** T23 said the loop
  re-requests "until the budget runs out"; it does not — `budget.RecordFailure()` on that path
  opens the breaker after `MaxConsecutiveFailures`, and a page answering 500 was requested exactly
  **3** times, not 500. Worth knowing generally: a defect written into `tasks.md` from a review is a
  hypothesis, and this loop can measure it before believing it. The real costs were the ~12 requests
  (3 × the client's own retries) spent on an already-refused page and the misleading `Breaker` stop
  reason.
  T23's review found **five new defects, none of them in T23's diff** — all pre-loop scraper code,
  now queued as T24–T27 and all verified against the source. T24 (a multi-run backfill setting the
  watermark from its oldest pages) is the worst thing found in this loop so far: it makes any tag
  over ~4,000 works re-read its entire catalogue on every incremental pass, forever. T24 and T27 are
  pulled forward to run before T6.
  Note the verification filters for the new tasks: `~Watermark`, `~Backfill`, `~Author`,
  `~ScrapeWorker`. T22 learned the hard way that a filter matching zero tests looks like a pass —
  check each one bites before trusting it.

## 2026-08-22 — T24 A resumed backfill must not set the watermark from its oldest pages — done

- did: `FinishAsync` now decides the watermark from two conditions instead of one. The run must have
  read page 1 — the newest end of a `revised_at desc` listing, and the only place the tag's newest
  revision time can be observed — which `firstPage`, now threaded in from the walk, reports. On top
  of that, an incremental pass still needs a `Watermark` or `LastPage` stop, while a backfill that
  read page 1 may leave a watermark however it stopped. The "never backwards" comparison stays as a
  backstop, with a comment that no longer claims to be the thing protecting resumed backfills.
  The task's premise was right and its suggested fix was half of one — see below.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/Ao3ShipIndexScraperTests.cs`
- ran: `dotnet test --filter FullyQualifiedName~Watermark` → 7 passed (4 before this task);
  `dotnet test` → 345 passed; `npm run build` + `npm run lint` → clean, the two pre-existing
  fast-refresh warnings only. All three new tests were confirmed red against the unpatched scraper
  first: the resumed backfill really did write Jan 3 as the watermark.
- commit: a0d65c5 "Only let a pass that saw the newest works set the watermark"
- next: **The obvious fix would have reproduced the bug from the other side.** T24 suggested "only
  let a pass that began at page 1 propose a watermark", and that alone leaves any tag over ~4,000
  works with a null watermark *forever* — its first backfill run ends on `Cap`, which may not
  propose, and every later run starts above page 1, which also may not. A null watermark makes the
  incremental pass ask for the whole catalogue, hit the 200-page ceiling, stop on `Cap`, and repeat
  next tick. Hence the backfill relaxation, which is the half of the fix that is not in the task
  notes. Generalised in `DECISIONS.md`: a rule that refuses to conclude can cost what a rule that
  concludes too much costs, and **T28's audit table needs a column for what stays null**, or it will
  tabulate one direction and call the file clean.
  **T24 had no `/code-review` pass** — the agent died on the account's monthly spend limit (resets
  3:10pm America/Chicago) before reporting. Every other task in this loop got one, and this file has
  yielded ten defects across four review passes, so the commit is worth reviewing when limits allow.
  I read the diff myself; that is not the same thing.
  Filters checked to bite before trusting them, per T22's lesson: `~Watermark` matches 7, and T25's
  `~Backfill` matches 6. T27's `~ScrapeWorker` and T26's `~Author` are still unchecked.
  `ResumeBackfillAtAsync(shipId, page)` is new in the test file — it plants the ship state a capped
  run leaves behind, which is the only way to test a resumed run's conclusions without paying for a
  first run that would itself set the watermark.

## 2026-08-22 — T27 One scope per job, and a failed save that does not escape the finally — done

- did: Two changes and one folded-in line. **A scope per job**: `RunDueJobsAsync` now selects job
  *ids* and gives each job its own scope, so the `AppDbContext`, scraper and ingestor it uses are
  its own; the job and its ship are re-read inside that scope, and a per-job `catch` contains
  anything that still escapes so one ship's failure costs one ship's run. **A terminal write that
  cannot throw**: `PersistCompletionAsync` tries the job's own context and, on failure, writes the
  run and job rows through a fresh scope, because a scrape that failed *because* a save was refused
  leaves that rejected change set tracked — EF Core never detaches it — and saving again from a
  `finally` threw out of the job entirely, leaving the run `Running` and the job due again on the
  next minute-poll. **Folded in from the review**: a scraper reports most failures by *returning*
  `StopReason = Error`, and `RunJobAsync` recorded those as `Succeeded`; it now reads the stop
  reason. The `Program.cs` comment claiming a scope per job was left alone — it is now true.
- files: `Api/Services/Scraping/ScrapeWorker.cs`, `Tests/LibraryTestHost.cs`,
  `Tests/ScrapeWorkerJobIsolationTests.cs` (new), `Tests/ScrapeWorkerRunStatusTests.cs` (new)
- ran: `dotnet test --filter FullyQualifiedName~ScrapeWorker` → 12 passed (7 before this task);
  `dotnet test` → 350 passed; `npm run build` + `npm run lint` → clean, the two pre-existing
  fast-refresh warnings only. All five new tests were confirmed red first — the three isolation
  ones by the `DbUpdateException` escaping `RunJobAsync`'s `finally` at line 263, which is the bug
  itself in a stack trace.
- commit: 8ed1941 "Give each scrape job its own scope, and a way to record a failed run"
- next: **The test seam is the reusable part.** `LibraryTestHost` gained a
  `LibraryTestHost(Action<IServiceCollection>?, params IAo3Scraper[])` constructor, because the
  `params` one registers the scrapers it is handed as *singletons* and this task needed a **scoped**
  scraper — one sharing the job's own `AppDbContext`, since a poisoned change set on that context is
  the whole failure. `PoisoningScraper` in the isolation tests adds two `Ship` rows under one
  normalized tag, which is a unique-index violation and the shape of the real thing. `StubScraper`
  now takes an optional stop reason and error message, which is what makes a *returned* failure
  testable at all.
  Filters checked to bite, per T22's lesson: `~ScrapeWorker` matched 7 before this task and 12 now.
  The new tasks' filters are **unchecked**: T29 `~Total`, T30 `~Authenticated`, T31 `~TotalWorks`,
  T32 `~Monotonic`, T33 `~Ingest`. T29's and T31's are the ones to distrust — `~Total` and
  `~TotalWorks` overlap, and T29's note records that the one existing total test passes only
  because its ship has no watermark.
  T27's review found **six** defects; one was in T27's own method (folded in) and five are queued as
  T29–T33, all verified against the source. **T29 and T30 are now in T28's `blocked-by` and T15's**,
  because both decide what a pass writes back to the ship and both feed `LastKnownTotalWorks` — the
  number a full sweep checks itself against before concluding works have left a tag. T29 is the one
  to take next: an incremental pass overwrites a ship's total with the count of its own date-filtered
  result set, so a tag backfilled to 4,317 works reads 2 after one quiet pass, silently, forever.
  This was the fifth review pass over this scraper and the pre-loop defect count is now fifteen. The
  rate is not falling. Four of T27's five are the same shape: a value written back to the ship that
  nothing downstream can tell is wrong.

## 2026-08-22 — T29 An incremental pass must not overwrite a ship's total with a filtered count — done

- did: `RecordTotal` now takes the tag's total only from a listing that carried no
  `work_search[revised_at]` bound. The bound moved into one function, `RevisedAtBound(mode,
  watermark)`, that `BuildUrl` and the gate both read — gating on the *filter actually sent* rather
  than on the mode, so a backfill or sweep growing a date window later cannot quietly re-break this.
  **Folded in from the review**: `LastKnownTotalWasAuthenticated` is now written only where the
  total is. Skipping the total on a filtered pass while still latching the flag would have let the
  two come from different runs — a logged-out backfill's undercount wearing a later authenticated
  pass's flag — and that divergence would have been *introduced by this diff*, so it was a one-line
  consequence rather than a new task. T30 keeps the rest of itself.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/Ao3ShipIndexScraperTests.cs`
- ran: `dotnet test --filter FullyQualifiedName~Total` → 8 passed (6 before this task);
  `--filter ~Authenticated|~Total` → 11 passed; `dotnet test` → 354 passed;
  `npm run build` + `npm run lint` → clean, the two pre-existing fast-refresh warnings only.
  Both defect tests were confirmed red first.
- commit: eeba5d2 "Read a ship's total only from a listing that asked for the whole tag"
- next: **The fix trades a wrong number for a stale one, and T15 has to notice.** Only an unfiltered
  pass writes the total now — a ship's first incremental pass, every backfill, and eventually every
  sweep. A ship past its backfill keeps the same `LastKnownTotalWorks` indefinitely. That is the
  right trade (a stale figure with an honest `LastKnownTotalWorksAt` beside it is checkable; a fresh
  figure that is secretly the size of a date filter is not) but **T15 must read the timestamp, not
  just the number**. Nothing outside the scraper reads either field yet, so no UI needed correcting.
  **A test-fixture trick worth reusing.** `Blurb(restricted: true)` renders the lock symbol AO3 puts
  in the heading. The negative test (a filtered pass must *not* set the flag) would pass vacuously if
  that markup did not parse as restricted, so it is paired with
  `Records_that_an_unfiltered_pass_read_the_total_while_logged_in`, which is green only if the symbol
  really bites. Write the positive alongside any negative that depends on a fixture detail.
  Filters checked to bite, per T22's lesson: `~Total` matched 6 before this task and 8 after;
  `~Authenticated` (T30's) now matches 3, having matched 1 before. Still unchecked: T25 `~Backfill`
  (6 at T24), T26 `~Author`, T31 `~TotalWorks`, T32 `~Monotonic`, T33 `~Ingest`, T34 `~Backfill`,
  T35 `~Pseud`.
  T29's review found **four** defects; one was this diff's and is folded in, three are queued as
  T34–T36 and all were verified against the source. **T34 leads the run order** — any page parsing
  to zero works stops with `LastPage`, and a backfill's `LastPage` means `Complete`, so one 200
  maintenance page retires a ship from backfilling forever. It is the same species as T29 and T24,
  and `listing.TotalWorks` already carries the signal that tells a parse failure from an empty tag.
  Sixth review pass, eighteen pre-loop defects. **Three of this pass's four were outside the
  scraper's walk** — a migration (T35) and the admin page (T36) — the first time a pass has found
  more outside it than in. That reads as the walk's density falling rather than the tree's rising.

## 2026-08-22 — T34 A backfill must not call itself complete off a page that parsed to nothing — done

- did: The `listing.Works.Count == 0` branch now concludes the listing has ended only when nothing
  on the page contradicts it: page 1, no Next link, and no populated heading on an unfiltered
  request. Any of the three says otherwise and the run stops with `Error`, leaving
  `BackfillNextPage` where it is so the page is re-asked once per run at the scheduler's spacing.
  The heading check is gated on `listingWasFiltered` — T29's flag — because a filtered listing's
  heading counts the filter's result set, and a quiet incremental pass reading zero blurbs under
  one is the healthy case, not a parse failure. **T25's half (2) is done here**: it was the same
  block and the same question, and answering only T34's half would have meant writing a rule for a
  later iteration to rewrite. T25 is re-scoped in `tasks.md` to its 404 half.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/Ao3ShipIndexScraperTests.cs`,
  `.devloop/{tasks.md,DECISIONS.md}`
- ran: `dotnet test --filter FullyQualifiedName~Backfill` → 9 passed (6 before this task);
  `--filter ~Ao3ShipIndexScraper` → 44 passed, then 47 after the review fixes; `dotnet test` → 359
  passed, then 362; 
  `npm run build` + `npm run lint` → clean, the two pre-existing fast-refresh warnings only.
  All three defect tests were confirmed red first, and the two guard tests green first — the
  latter matters, because they are the ones that would have caught an over-broad rule.
- commit: 1c9915c "T34: Refuse to call a backfill complete off a page that read as nothing"
- next: **The rule that was rejected is the part worth carrying.** `ship.LastKnownTotalWorks` was
  considered as a fourth signal, to close the one hole left open — a page 1 from which neither a
  heading nor a blurb parsed still concludes "empty tag". It was rejected because T29 made that
  figure deliberately stale on a ticking ship, so a tag whose works were genuinely all deleted
  would become unconcludable: the "refuses to conclude" cost T24 recorded, paid for a narrower
  hole than the three page-local signals already close. **That is a T28 row**, and the kind T28
  says is most valuable — a rule that concludes because nothing on the page contradicts it.
  **Filters checked to bite, per T22's lesson, and two of them do not.** `~Backfill` 6 → 9,
  `~Author` (T26) 10, `~Ingest` (T33) 6, `~Pseud` (T35) 6. But **`~TotalWorks` (T31) and
  `~Monotonic` (T32) match zero tests today** — those tasks must name their new tests to match, or
  their verification will look like a pass while running nothing. T30's `~Authenticated` was 3 at
  T29.
  **The review landed after the commit and found three things the diff itself created** — folded in
  as `642cbb9`, tests confirmed red first, so T34 is two commits. (1) `RecordTotal` ran *before* the
  readability guard, so a soft-error page's `<h2>Error 404</h2>` wrote `LastKnownTotalWorks = 404`
  over a real 4,317 — `ParseTotalWorks` takes trailing digits when it finds no "Works", which is
  T31's defect reached through this diff. (2) The residual hole named above turned out to have a
  clean signal after all, and a better one than the `LastKnownTotalWorks` that was rejected: an
  empty tag renders `ol.work.index.group`, a maintenance page does not. `Ao3ListingPage.HasListing`
  is new. The fake HTTP client's default response is `("", OK)` — that shape was one stray test from
  asserting `Complete`. (3) The error message claimed "the listing says there are more" even where
  the only failing condition was `page > 1`, and that string is rendered verbatim to an operator on
  `SchedulesPage`.
  **The fourth finding is T37, and it is the most important thing this iteration produced.** T25's
  planned `page == 1` fix for the 404 branch and T34's rule for the empty-200 branch conclude
  *opposite* things about one real situation — a cursor pointing past a listing that has since
  shrunk. Two branches of one method, written four iterations apart, each locally reasonable and
  jointly incoherent. T25 is now blocked from picking a side on its own, and T37 is in T28's
  `blocked-by`: this is the strongest case yet for that audit existing.
  **A second form of T22's lesson, worth more than the first.** This task's declared verification
  filter `~Backfill` ran 2 of its 8 tests — and neither of the two that guard against the new rule
  firing on every healthy incremental tick. A filter matching *nothing* looks suspicious; a filter
  matching *some* does not. Corrected to `~Ao3ShipIndexScraper` (47 tests).
  The run order's next entries are T37, then T25 (re-scoped, smaller than it reads), then T26.

## 2026-08-23 — T37 A backfill stuck on a page that is neither readable nor gone — done

- did: One rule where there were two contradictory ones. A backfill run's *first* request landing on
  a cursor above page 1 and getting back either a 404 or a 200 with nothing readable is one
  situation — a cursor pointing past a listing that shrank — and `CursorMayBeStale` recognises it
  from both branches. Neither concludes any more: the run retreats one page and asks the listing,
  which is the only authority on its own length and *can* answer. A Next link on that page means the
  cursor's page is supposed to exist, so the run stops and the cursor stays; no Next link means the
  listing ends there, and `LastPage` → `Complete` is reached on evidence. The bound is
  `Ship.BackfillStalledRuns` (new column, both providers): runs the archive answered that got no
  further, cleared by forward progress, and at 12 the backfill is `ShipBackfillState.Failed` — the
  enum's fourth value, which nothing had ever set — so the ship falls back to its incremental pass
  instead of spending two requests a run forever. The 404 guard is now `lastPage == page - 1`, which
  **closes T25** entirely: nothing of it was left once the branch was rewritten.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Api/Models/Ship.cs`,
  `Api/Data/Migrations/{Sqlite,Postgres}/*BackfillStalledRuns*`, `Tests/Ao3ShipIndexScraperTests.cs`,
  `.devloop/{tasks.md,DECISIONS.md}`
- ran: `dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper` → 53 passed, then 55 after the
  review fixes (47 before this task); `dotnet test` → 370 passed; `npm run build` + `npm run lint` →
  clean, the two pre-existing fast-refresh warnings only. All six original tests were confirmed red
  against the unpatched scraper in one run — 6 failed / 47 passed — which also confirmed the task
  broke nothing that was already pinned.
- commit: d3be48e "T37: Settle what a backfill cursor past a shrunken listing may conclude"
- next: **The bound was the hard half, not the rule.** The retreat was the easy decision — T37's
  notes named it — and it took twenty minutes. What took the rest was that a bound on retrying is
  only honest if the *legitimate* cases finish inside it, and the first version's one-page-per-run
  walk-back did not: a listing that lost dozens of pages would have exhausted the allowance and been
  written off having never been broken. Hence the halving. **Generalising, and it belongs in T28's
  table: a give-up threshold is a conclusion drawn from failure, and it inherits every objection
  this loop has raised to those** — it needs the same "what is this entitled to conclude" scrutiny
  as a stopping rule, and the question to ask it is which honest scenario it fires on first.
  **`ShipBackfillState.Failed` is reachable for the first time and has no exit.** Queued as **T38**
  and added to T15's `blocked-by`. Nothing in the product moves a ship out of `Failed`, so a
  backfill written off during an outage stays written off. `BackfillNextPage` is deliberately left
  where the walk gave up so a restart has somewhere honest to resume from, and resetting
  `BackfillStalledRuns` has to be part of that restart or the ship gives up again immediately.
  **T39 is the one to read before touching the scraper again**, and it is `blocked` on a fixture.
  T34's `HasListing` decides an empty tag from a not-a-results-page on a premise about AO3's markup
  that nothing verifies — and the test helper `Page(n, [])` emits the container unconditionally, so
  the suite proves the premise by assuming it. If it is wrong, every quiet incremental pass on every
  ship errors forever, which is worse than the defect T34 fixed. **Ask Emma for a capture**: any tag
  under a far-future `work_search[revised_at]` bound produces it, and that is the exact shape a
  quiet pass sends. T5, T10, T13 and now T39 are all waiting on files only a human can fetch — four
  of thirty-nine tasks, and the count is growing.
  Filters checked to bite, per T22's lesson: `~Ao3ShipIndexScraper` 47 → 55 and covers every test
  this task wrote. Still unchecked and still suspect: T31 `~TotalWorks` and T32 `~Monotonic` matched
  **zero** tests at T34 and nothing since has added any — those two tasks must name their new tests
  to match or their verification will run nothing at all. T26's `~Author` (10) and T30's
  `~Authenticated` (3) were checked at T34 and T29.
  Seventh review pass, and the first whose findings were **all four in the diff under review** —
  three of T37's own making, one in T34's commit from the pass before. The pre-loop defect count is
  unchanged at eighteen. That is the walk finally running out of inherited defects; what it is
  finding now is what this loop is writing.
  Run order after this: T26, then T30, then T28's audit — which now has three more rows waiting for
  it (what a 404 may conclude, what a stalled backfill's terminal state is, and the give-up rule
  above) and only two blockers left.

## 2026-08-23 — T26 An unreadable byline must not erase authorship — done

- did: Split one boolean into the two facts it was carrying. `Ao3WorkBlurb.IsAnonymous` is `bool?`:
  false when the byline named someone, true when it says "Anonymous", null when it could not be read
  — and `WorkIngestor` writes neither the column nor the author rows on null, because `ApplyAuthors`
  reconciles and an empty set is a deletion, not silence. `ParseAuthors` became `ParseByline`, which
  also counts the warning the parser's own contract promised and never raised. The word "Anonymous"
  is read only from the byline: link text that is not `rel="author"` is skipped, and the scan stops
  at the first "for", so a gift to an anonymous recipient cannot speak for the work's authorship.
- files: `Api/Services/Scraping/{Ao3BlurbParser,Ao3WorkBlurb,WorkIngestor}.cs`,
  `Tests/{Ao3BlurbParserTests,WorkIngestorPseudTests}.cs`, `.devloop/{tasks,DECISIONS}.md`
- ran: `dotnet test --filter ~Author` → 17 passed (10 before this task); `dotnet test` → 379 passed
  (370 before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only.
  Every new test was confirmed red against the unpatched code: the three parser ones by running them
  before the change, the ingestor one by removing the guard, the two gift ones by restoring the old
  scan. No schema change, so no migrations.
- commit: 0a8a318 "T26: Tell an unreadable byline apart from a work with no creators"
- next: **The review's finding was the fix reintroducing the defect it was fixing.** Excluding the
  title from the "Anonymous" scan and stopping there looked complete; it left the gift clause in
  scope, and under the exact markup change this task defends against — AO3 dropping `rel="author"` —
  every *gifted* work would have parsed as anonymous and lost its creators. Generalising, and it
  belongs in T28's table beside T37's note: **when a fix turns one signal into a conclusion, ask
  what else in the same document can produce that signal.** A heading holds four people's names.
  **Queued from this review, none of it in T26's diff:** T40 (T37's fix for counting an unread page
  as read landed on the retreat path only — the non-retreat path still does it), T41 (the retreat's
  log line: six placeholders, five arguments — the one compiler warning in the backend build, found
  at this task's baseline check), and one line added to T38 naming `BeginBackfill` as where
  `BackfillStalledRuns` is not cleared. **T39 was re-reported independently by this pass** — second
  review, same unverified `HasListing` premise. It is still the one to read before touching the
  scraper, and still blocked on a capture only Emma can fetch. Five of forty-one tasks now wait on
  files: T5, T10, T13, T39.
  Filter check, per T22: `~Author` 10 → 17 and covers every test this task wrote. T40's `~PagesFetched`
  and T41's build check are named in those tasks as matching **zero** tests today, deliberately.
  Still unverified and still suspect: T31's `~TotalWorks` and T32's `~Monotonic`, both still zero.
  **Run order is now T30, then T28's audit** — and with T26 done, every pre-loop scraper defect this
  loop inherited is closed. The eighth review pass found four things and three were the loop's own
  code, continuing T37's observation: what the reviews find now is what this loop is writing.


## 2026-08-23 — T30 `LastKnownTotalWasAuthenticated` must describe the total it sits beside — done

- did: The flag belongs to the run that wrote the total. `RecordTotal` returns whether it wrote,
  and `wroteTotal` — not `!listingWasFiltered` — is what lets a run assign the flag, which also
  closes the case the filter gate never covered: an unfiltered pass whose heading will not parse
  writes no total and used to stamp the flag anyway. Assignment, not a latch, so a later anonymous
  run reading a fresh total clears it. **Where "anonymous" comes from is the decision that took the
  time.** `flag = sawRestricted` would have closed the latch and opened something worse: a
  restricted work proves a run was logged in, but its absence proves nothing, so an authenticated
  pass over an unrestricted tag writes a false *false* — and that is the direction that hurts, since
  it tells T15's sweep the stored total was counted at its own visibility when it is really the
  higher logged-in count. So `ScrapeHttpResponse` gained `bool Authenticated`, on the response
  rather than the run because a cached page is the page the *caching* request was given.
- files: `Api/Services/Scraping/{Ao3ShipIndexScraper,IRateLimitedHttpClient,RateLimitedAo3HttpClient}.cs`,
  `Api/Models/Ship.cs`, `Tests/Ao3ShipIndexScraperTests.cs`, `.devloop/{tasks,DECISIONS}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper` → 58 passed (55 before this
  task); `--filter ~Authenticated` → 6 passed (2 before); `dotnet test` → 382 passed (379 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. All three new
  tests confirmed red first. The two tests the review's fix re-shaped were confirmed to bite by
  mutation — restoring the disjunct and dropping the `wroteTotal` gate fails three of the six.
- commit: 970b12d "T30: Give the authenticated-total flag to the run that wrote the total"
- next: **The review's finding was that my fix kept a premise it should have dropped.** The first
  version read `response.Authenticated || listing.Works.Any(w => w.IsRestricted)`, carrying over the
  pre-loop belief that a lock symbol proves a session. The disjunct's only reachable effect is to
  overrule the transport's *no* with a guess off the page — before T5 the client never authenticates,
  and after T5 the disjunct is redundant except in exactly that contradiction. It is now
  `response.Authenticated`, with the contradiction logged rather than resolved. **Generalising, and
  it belongs in T28's table: a proxy signal keeps looking sound right up until the real signal
  arrives beside it, and then it is only ever a way to disagree with it.** T39 is the same shape one
  method away, and this is the third review to land on an unverified AO3-markup premise in this file.
- **Half of T30 turned out to be already dead, and the notes say why.** `sawRestricted` over
  `toIngest` only is unreachable since T29: it needs a pass with a watermark, such a pass is
  filtered, and a filtered pass writes no total and cannot touch the flag. On an unfiltered pass
  `toIngest` *is* `listing.Works`. Fixed anyway — the equality is an accident of where the watermark
  filter is applied, not a rule — but with no test, because there is no behaviour to pin. Worth
  knowing before T28 tabulates it as a live rule.
- **Queued: T42, and it leads the run order.** `PlausiblyTheEndOfTheListing` requires `page == 1`,
  so an *incremental* pass whose page 2 comes back a well-formed empty listing stops with `Error` —
  which cannot move the watermark — and the ship re-reads the same two pages every tick forever.
  The heading condition one line below is already gated on `listingWasFiltered` for this exact
  reason; this one is not. In T28's `blocked-by` with T37's precedent, and in T15's.
  The other finding went to **T38** rather than becoming a task: entering `Failed` does not reset
  `BackfillStalledRuns` and neither reset site is reachable from `Failed`, so even a hand-edited
  state re-fails on the next run. T38 already owns the missing exit; this is its other half.
  Filters checked to bite, per T22's lesson: T30's declared `~Authenticated` matched **2**, one of
  them in an unrelated controller, and missed this field's own positive test — the third form of
  that lesson, where the filter names the subject and the tests are named for the behaviour.
  Corrected in `tasks.md` to `~Ao3ShipIndexScraper`; it now matches 6 either way. Still zero and
  still suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.
  **Run order is now T42, then T28's audit** — whose `blocked-by` is down to that one task.

## 2026-08-23 — T42 An incremental pass must not stop with `Error` on a page it was told to expect — done

- did: `PlausiblyTheEndOfTheListing`'s `page == 1` is now `page == 1 || (listingWasFiltered && the
  heading does not say there is more)`. The `page > 1` argument — AO3 404s past the last page — is
  about an unfiltered listing; under a `revised_at` bound the Next link comes off a count that can
  race the blurbs, so a work leaving the window between two requests answers page 2 with a
  well-formed empty listing, and the run stopped with `Error`, which may not move the watermark.
  The filtered case asks the heading instead of waiving it: a filtered heading counts the filter's
  result set, an incremental pass always starts at page 1, so `blurbsRead` — a new counter, the
  run's own tally of what the listing served, not `worksSeen`'s tally of what reached the ingestor —
  is the number it is comparable with.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/Ao3ShipIndexScraperTests.cs`,
  `.devloop/{tasks,DECISIONS}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper` → 64 passed (58 before this
  task); `dotnet test` → 388 passed (382 before); `npm run build` + `npm run lint` → clean, the two
  known fast-refresh warnings only. The defect test was confirmed red first. Both halves of the new
  condition were mutated separately: removing the waiver fails the two tests that want the end
  concluded, removing the heading guard fails the one that wants it refused. Dropping the page
  condition entirely fails five, three of them T34/T37 backfill tests — which is the confirmation
  T42 asked for rather than the assumption it warned against. No schema change, no migrations.
- commit: 47b5582 "T42: Let a filtered incremental pass end on an empty page past the first"
- next: **The first version of this fix was the review's finding, and it was worse than the defect.**
  `page == 1 || listingWasFiltered` leaves a filtered page needing only a container and no Next link
  to be taken for the end — and the heading is waived for filtered listings one line below, so a
  page reading "60 Works" over zero blurbs was accepted, `LastPage` satisfied `mayPropose`, and the
  watermark moved past works nothing looks back for. **Generalising, and it belongs in T28's table
  beside T30's and T37's notes: when a rule is waived because its argument does not hold, ask what
  that rule was carrying, not only whether it was sound.** `page > 1` was carrying "the listing says
  there is more", and the filtered listing says that with a number rather than a Next link. The
  defect cost two requests a tick and self-healed; the first fix lost works silently, which is the
  direction T24 already ruled on.
  **Queued from this review: T43, and it leads the run order.** The 404 branch's `lastPage ==
  page - 1` guard concludes `Complete` from exactly the evidence `CursorMayBeStale` refuses to
  conclude from, and the reviewer demonstrated the crossing against a running server: one transient
  5xx during a retreat leaves the cursor at N-1, and the next healthy run walks into the guard and
  retires the ship with page N onward unread. T37's question, asked of the branch T37 did not
  rewrite. Also **T44** — `readWhileLoggedIn` ORs across the run while `RecordTotal` writes per
  page, so T30's flag can still describe a request other than the one that wrote the total; latent
  until T5 and wrong the day T5 lands.
  **T40 and T38 were both re-reported by this pass** and neither is new. A finding arriving twice
  from two independent reviews is the only signal this loop gets about which queued tasks are
  actually costing something — T39 has now arrived three times, T40 twice, T38 twice.
  **T39 is one premise further into load-bearing** because of this task: the waiver leaves
  `HasListing` carrying more weight on a filtered page than it carried before, and `HasListing`
  rests on the AO3-markup claim T39 exists to settle. Still `blocked` on a capture only Emma can
  fetch, along with T5, T10 and T13 — five of forty-four tasks now wait on files.
  Filters checked to bite, per T22's lesson: `~Ao3ShipIndexScraper` 58 → 64 and covers all six new
  tests; `~Backfill` 13, still green, which is the guard on this diff. Still zero and still suspect:
  T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.
  **Run order is now T43, then T28's audit** — whose `blocked-by` is down to that one task again,
  for the fourth time, each time a rule about what a stop may conclude.

## 2026-08-23 — T43 A 404 must not conclude what the retreat beside it refuses to conclude — done

- did: Removed the 404 branch's `lastPage == page - 1` → `LastPage` conclusion outright. The
  condition only looks like "past the end of the listing" until you read it beside the advance rule
  fifteen lines below: the forward walk moves past a page only when that page offered a next link,
  so `lastPage == page - 1` *means* a page this run read said page N exists — the same evidence
  `CursorMayBeStale` refuses to conclude from, differing only in which run read page N-1. Stated
  correctly ("a 404 past a page that was read *and offered no next link*") the guard is unreachable,
  because a page with no next link ends the walk where it stands and is never followed by a request.
  The genuine shrink is deferred, not lost: the run stops with the cursor on the page that did not
  answer, so the next run's first request lands there and T37's retreat asks the listing.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/Ao3ShipIndexScraperTests.cs`,
  `.devloop/{tasks.md,DECISIONS.md}`
- ran: `dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper` → 67 passed (64 before this
  task, one test replaced and four added); `dotnet test` → 391 passed (388 before); `npm run build`
  + `npm run lint` → clean, the two known fast-refresh warnings only. All four new tests confirmed
  red by mutation — putting the old guard back fails exactly those four and nothing else, which is
  also the confirmation that the removal broke nothing already pinned. Re-ran both suites after the
  review agent reported having briefly reverted and restored the test file: 67 / 391, unchanged. No
  schema change, no migrations.
- commit: 4e4ab01 "T43: Stop reading a 404 as the end of a listing the page before said continues"
- next: **The test I deleted was the defect, written down.**
  `Treats_a_404_past_the_last_page_as_the_end_of_the_walk` built page 1 *with* a next link and left
  page 2 missing, under a comment reading "AO3 404s rather than serving an empty page past the end of
  a listing" — but a healthy last page has no next link, so the fixture and the premise it cited were
  about different events, and the suite had been proving the wrong one green since before this loop
  started. **Generalising, and it belongs in T28's table: when a rule looks sound, check whether its
  own test constructs the situation the comment claims, or a different one that happens to reach the
  same branch.** T39 is the same species one method away — a premise the helper satisfies by
  construction — which is now the fourth independent arrival of that shape.
  **The review found the consequence I had already accepted, and I overruled it — the first
  iteration to fold in nothing.** Refusing to conclude leaves an *incremental* pass with no recovery,
  because `CursorMayBeStale` is backfill-only and an incremental pass restarts at page 1: page 1
  fresh with a next link, page 2 404ing, gives the same two requests every tick forever with the
  watermark frozen. The review's shape was to keep the old conclusion for incremental; declined,
  because that is T24's and T42's ruling twice over — concluding costs page 2's works permanently
  and silently, refusing costs two requests per scheduler interval and files a failed run naming the
  page. The pass also keeps ingesting page 1 every tick, so it is a gap and not a stopped library.
  **What the finding is right about is the missing bound**, which a backfill has and this does not.
  That is **T45**, and deliberately not a line in this diff: closing it needs a decision about what a
  stuck incremental pass should *do*, and "give up on new works" is not a terminal state this
  product can have.
  **Queued from this review, and T47 leads the run order.** It is in T42's code from one iteration
  ago: the filtered waiver reads `FilteredHeadingSaysMore`'s "no heading is no evidence" false as
  permission, so a filtered page 2 with a container, no next link, no works and no heading concludes
  `LastPage` and moves the watermark past everything it would have held. It leads because it is the
  only conclusion left in the walk that is **silent** — T45, T46, T38 and T40 all announce
  themselves in the run history; this one reports success. Also **T46** (a 404 on a run's first
  request never counts as a stalled run, so a backfill cursored at page 1 stays `InProgress`
  forever, never falls back, never reaches `Failed`; T37's code, T38's family, on T15's `blocked-by`)
  and **T48** (`SaysAnonymous` keeps the title out of the byline only because the title is an anchor,
  so T26's fix stops holding under the very markup change it defends against).
  Filters checked to bite, per T22's lesson: `~Ao3ShipIndexScraper` 64 → 67 and covers all four new
  tests; `~Backfill` 13, named as T46's filter and confirmed non-zero. Still zero and still suspect:
  T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.
  **T28's `blocked-by` is one task again — the fifth time running, and the fifth time it is a rule
  about what a stop may conclude** (T34, T37, T42, T43, T47). Five iterations have now each closed
  one and queued the next, which is the argument for doing the audit rather than the sixth of these.
  Run order after this: T47, then T28.

## 2026-08-23 — T47 A filtered page carrying no evidence at all must not end the pass — done

- did: The filtered waiver now rests on the heading instead of merely surviving it.
  `!FilteredHeadingSaysMore(...)` became `FilteredHeadingSaysThisIsAll(...)` —
  `TotalWorks is { } matched && matched <= blurbsRead` — so a filtered page past the first whose
  heading did not parse keeps `page > 1` and does not conclude `LastPage`. The negation was the
  defect: the helper returns false for an absent heading deliberately, its own comment saying "no
  evidence", and the negation read that false as permission. `WhyNotTheEnd` gained the matching
  branch so the run history says "carries no heading to say the date filter's results ended here"
  rather than quoting a count that does not exist.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/Ao3ShipIndexScraperTests.cs`,
  `.devloop/{tasks,DECISIONS}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper` → 67 passed (67 before: one
  test replaced, not added); `dotnet test` → 391 passed (391 before); `npm run build` + `npm run
  lint` → clean, the two known fast-refresh warnings only. The new test was confirmed red first,
  failing with `lastPage` where `error` was wanted — the defect exactly as T47 described it. Both
  halves mutated separately: restoring the absent-heading permission fails only the new test,
  dropping the waiver entirely fails only `Ends_a_filtered_pass_on_an_empty_page_whose_heading_
  agrees_it_was_served_everything`. No schema change, no migrations.
- commit: 61faf31 "T47: Require a heading before a filtered page past the first may end the pass"
- next: **The test I deleted was the defect, for the second iteration running.** T42's
  `Lets_a_filtered_pass_end_on_an_empty_page_past_the_first` built page 2 as `Page(2, [])` — no
  heading — so the suite asserted `LastPage` and a moved watermark on precisely the page this task
  refuses. The waiver's real case was already pinned by the `..._whose_heading_agrees_...` test, so
  the deleted one pinned nothing but the defect. T43 deleted a test for the same reason one
  iteration earlier. **Generalising, and it is now a T28 row about tests rather than rules: a fix
  and its test are written in the same sitting by the same reasoning, so a wrong premise produces a
  test that agrees with it. Read the fixture, not the test name.**
  **The trade is T45's, one shape wider.** Such a page now stops with `Error`, and an incremental
  pass has no retreat — so it re-reads the same two pages every tick until the heading returns.
  Recorded in T45's notes: unlike the 404 that motivated that task, this shape is a page AO3
  answered 200 with a listing container on it, so whatever bound T45 settles on has to cover both.
  **The review found the asymmetry this diff creates and it is queued as T50, not folded in.**
  `page == 1` short-circuits the whole clause, so a filtered *page 1* with a heading counting N > 0
  over zero blurbs still concludes `LastPage` with a null error — the ship ingests nothing and the
  run is recorded a success, every tick. Same contradiction, one page earlier. Left out of this diff
  because it is not what T47 delivers (no watermark moves — `newestSeen` is null with no blurbs), it
  overturns a decision T42 stated in a doc comment and pinned with a test, and refusing costs a
  request per tick with no recovery, which is the bound T45 owes first.
  `Reads_a_quiet_filtered_pass_with_a_populated_heading_as_nothing_new` is the third fixture in this
  family: `Page(1, [], total: 4317)` under a watermark asserts that 4,317 filtered matches over zero
  served is healthy.
  **T40 and T44 were each re-reported, T40 for the third time and T44 for the second** — still the
  only signal this loop gets about which queued tasks are costing something. **T49** was queued from
  this task's own baseline before the review independently found it: `RetreatFromStaleCursor` binds
  five arguments to six placeholders, so the operator reads a literal `{Page}`, and it is the one
  analyzer warning the API build prints — which is why the next one will arrive unnoticed.
  Filters checked to bite, per T22's lesson: `~Ao3ShipIndexScraper` 67 and covers the new test;
  `~Backfill` 13, unchanged, which is the guard on this diff. Still zero and still suspect: T31
  `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.
  **Run order is now T28's audit alone, its `blocked-by` empty for the first time.** Five tasks
  stood in that slot one after another — T34, T37, T42, T43, T47 — every one a rule about what a
  stop may conclude, and this one a defect inside the previous one's fix. Every stopping defect
  still queued announces itself in the run history; this was the last silent one.


## 2026-08-23 — T28 Audit the scraper's walking and stopping rules — done

- did: Wrote `.devloop/scraper-audit.md` — 60-odd rows over eight sections covering where each pass
  starts (§A), where it stops (§B), what a page with no works may conclude (§C), what is written
  back to the ship (§D), what the ingestor may erase (§E), what the run history is told (§F), the
  five rules-about-rules the last six iterations paid for (§G), and where every gap went (§H). Each
  row names the rule, the file and line, the passes it applies to, what it entitles them to
  conclude, and the test that pins it. Fixed nothing, as the task requires; queued four new tasks,
  added three edges to T15's `blocked-by`, and folded T41 into T49 as a duplicate.
- files: `.devloop/scraper-audit.md` (new), `.devloop/{tasks,DECISIONS}.md`. No source changed.
- ran: `dotnet test` → 391 passed (391 before — this task writes no code); `npm run build` +
  `npm run lint` → clean, the two known fast-refresh warnings only. Verification: all 66 test names
  cited in the audit were checked against `dotnet test --list-tests` (391 tests) and each matches
  exactly one test — none matches zero — and four were re-run as real `--filter` invocations to
  confirm the discovery list agrees with the runner. Every row claiming *unpinned* or *no rule* was
  checked the same way and by grep over the test sources: `MaxPagesPerRun`, `LastIncrementalRunAt`,
  `Interrupted`, `BackfillState` in the worker tests, and any assertion of `UpdatedAt` preservation
  across two ingests all return nothing.
- commit: 21e2cd3 "T28: Write down the walking and stopping rules the scraper actually has"
- next: **The audit's own finding is that two iterations queued the same defect twice.** T41 and T49
  are one CA2017 warning in one log line, found at two different baselines by two fresh contexts,
  neither noticing the other. That is this loop's failure mode appearing in the task list rather
  than in the code, and it is the clearest argument that the audit was worth an iteration: a
  fifty-task list read from a cold start is not something the next pass can hold in its head.
  **Four new tasks, and only two of them are defects.** T51 is the live one: `WorkIngestor.ApplyTags`
  reconciles a work's tags against the listing blurb's list and deletes everything else, which is
  correct while the blurb is the only source and destructive the moment T10 fetches a fuller list
  from the work's own page — every detail fetch undone by the next incremental pass, silently, on a
  run recorded a success. It is on T10's `blocked-by`. T52 is the other: a run the circuit breaker
  stopped is recorded `Succeeded`, so an operator watching the Schedules page sees green while a
  ship collects nothing. T53 and T54 are rules with no test under them — the mode choice three
  fixes rest on, and the ingestor's unreadable-date preservation.
  **What the table is actually for is §D and §E, and they are addressed to T15.** D12: nothing
  refreshes `LastKnownTotalWorks` once a ship has a watermark, because every incremental pass on
  such a ship is filtered and a filtered heading may not write the total — so the sweep is both the
  only pass that would refresh the number and the only one documented to check against it. E6/E7:
  `IsDeleted` and `MissingSinceAt` are cleared by every ingest and set by nothing, so T15 writes the
  first code in this app that ever concludes absence, with the reading side already shipped and
  every test proving only the clear. Both are in T15's notes now, along with the three new
  `blocked-by` edges (T40, T44, T52), each added because the audit found a queued task to be
  load-bearing for the sweep specifically rather than merely open.
  **The `page == 1` short-circuit is the last unexamined clause in the walk** and it has two open
  conclusions, not one. T50 is the filtered half, already queued. The unfiltered half — page 1 with
  the container, no works, no Next link and *no heading at all* completes a backfill — went to T39's
  capture list rather than becoming a task, because it cannot be decided without knowing whether a
  zero-result AO3 index renders a heading. That is now the third question waiting on that one file,
  and T39 has been re-reported by four independent reviews.
  **A verification detail the next iteration should not re-learn:** `--filter FullyQualifiedName~X`
  matches case-insensitively — `~Backfill` runs 13 tests although no test name contains a capital
  `Backfill`. Checking a filter with a case-sensitive `grep` over a test list under-reports it.
  Filters checked to bite, per T22's lesson: every one of the 66 names in the audit, listed above.
  Still zero and still suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero
  by design. The new tasks name `~Ingest` (7) and `~ScrapeWorker` (12), both confirmed non-zero.
  **The review found nothing in the diff and one thing on the branch.** `/code-review low` over a
  docs-only diff reported `AdminScrapingPage.tsx`: a rejected credential fetch leaves the login
  block rendering "Loading…" forever while the identity block thirty lines above it handles the
  same case correctly. Verified by reading, queued as **T55**, not folded in — a frontend fix
  riding along in a documentation commit is the wandering diff the policy forbids, and T28 fixes
  nothing by construction. It is in T36's file; whoever takes either should read both.
  **The `Run order` section is gone and file order applies.** The next task is T6 — per-user work
  state — the first planned work this loop has taken in fourteen iterations. Everything the run
  order was carrying is in `.devloop/scraper-audit.md`; read it before touching the walk.

## 2026-08-23 — T6 Per-user work state: rate, mark read, annotate — done

- did: `GET/PUT /api/works/{id}/state` on `WorksController`, and every row of `GET /api/works` now
  carries the caller's own state. Reading status, half-star rating and note round-trip; a wholly
  empty state is stored as **no row** and both forms read back as `WorkStateDto.Cleared`, which is
  the canonical-cleared choice T6's notes asked for. `WorkQueries.StatesOf` is the per-caller
  predicate, beside `Library`, closed over and used as a correlated subquery in the list projection
  — `Work` gets no navigation to `UserWorkState`, because a navigation is loadable without saying
  whose state it is. No migration: the table and its check constraint already existed.
- files: `Api/Controllers/WorksController.cs`, `Api/Data/WorkQueries.cs`, `Api/Dtos/WorkDtos.cs`,
  `Tests/UserWorkStateTests.cs` (new), `Tests/LibraryTestHost.cs`, `frontend/src/api/types.ts`,
  `.devloop/{tasks,DECISIONS}.md`
- ran: `dotnet test --filter FullyQualifiedName~UserWorkState` → 22 passed (0 before — new class);
  `dotnet test` → 413 passed (391 before); `npm run build` + `npm run lint` → clean, the two known
  fast-refresh warnings only. A clean `dotnet build --no-incremental` adds no analyzer warning; the
  only one is still T49's CA2017. Fifteen mutations applied one at a time and reverted, every one
  red. The suite was run 30 times over for order-dependence, since the new tests share the fixture's
  single in-memory connection: 413 every time.
- commit: c36d2d9 "T6: Give each reader their own status, rating and note on a work"
- next: **One mutation survived the first round, and it was a test naming a guard without
  constructing its case.** `Refuses_a_reading_status_it_does_not_offer` sends `"Abandoned"`, so
  deleting `Enum.IsDefined` from the parse left the suite green — a *word* cannot reach that check,
  because `Enum.TryParse` has already rejected it. Only a **number** gets past `TryParse`, which
  hands back whatever byte was asked for, defined or not: `"99"` would have been written to the
  column. Fixed by adding `Refuses_a_status_number_no_reading_status_has`, and the mutation then
  bit. **This is T43's and T47's lesson arriving in new code rather than old** — read what the
  fixture constructs, not what the test is called — and it is the argument for mutating every
  clause of a new guard rather than trusting that a passing test about it covers it.
- **The one review finding in this diff was folded in; both others were already queued.** The race:
  two of one reader's requests for one work, together — exactly what T7's inline star and status
  controls on a single row produce — both read no row, both insert, and the loser 500s on the unique
  index. Fixed with `ShipsController.ResolveShipAsync`'s existing shape, plus the symmetric clear
  path. Neither race is pinned, and **its precedent is not pinned either** — one SQLite connection
  in the fixture means there is no seam to open the window. Said out loud in DECISIONS so the gap
  reads as a decision rather than an oversight. The other two findings were **T44** (third arrival)
  and **T45** (second), both against the branch rather than the diff.
- **What T7 and T8 need from this, now written into their notes.** T7: `WorkListItem.state` is
  already on every row, so no extra fetch — but `PUT` **replaces**, so a control that sends only the
  field it changed silently wipes the other two. T8: the left join is the only correct shape for
  "unread", *and* a row saying `None` still exists whenever a status is cleared while a rating
  stands, so the predicate has to accept both; `StatesOf` is the clause to join through.
- **A property worth knowing before T18's statistics:** unfollowing a ship removes only the
  `WatchedShip` row, so a reader's state survives, becomes unreachable (404, by the same
  `WorkQueries.Library` scoping the list uses), and comes back intact if they re-follow. That
  matches the existing "job disabled, not deleted" policy in `UnwatchShip` rather than contradicting
  it, but it does mean statistics must not assume every `UserWorkState` row is reachable.
- **This was the first planned task in fifteen iterations** and the first to touch no scraper code.
  The next by file order is T7, whose `blocked-by` is now satisfied.
  Filters checked to bite, per T22's lesson: `~UserWorkState` matches 22 and covers every new test
  (`--list-tests` grepped case-insensitively, per T28's note). Still zero and still suspect:
  T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.

## 2026-08-24 — T7 Rating and status controls on the feed — done

- did: Every Works row grew a "Yours" cell — a reading-status select, a half-star rating, and a note
  button opening an editor in a row of its own under it. `RatingStars` is a `role="slider"` over ten
  half-steps: one tab stop per row with arrows/Home/End/Delete, a pointer click read as a fraction
  of the control's width, and clicking the rating you gave clears it. The filled half of a star is a
  clipped copy of the *same* ★ over the empty one, with `data-fill` of `none`/`half`/`full` and the
  widths in CSS — no Lucide path invented, no inline style. Unrated reads "Unrated" beside the stars
  and carries `data-rated` and `aria-valuetext`, so it cannot be mistaken for half a star. Writes are
  optimistic, reconciled against the response, reverted *and reported in the row* on failure, and
  ordered by a per-work token so an older answer never lands last. Every control sends the whole
  `WorkState`, because PUT replaces. The existing `Rating` header became `AO3 rating`.
- files: `frontend/src/components/RatingStars.tsx` (new), `frontend/src/pages/WorksPage.tsx`,
  `frontend/src/api/{client,types}.ts`, `frontend/src/index.css`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only;
  `dotnet test` → 413 passed (413 before — no backend code in this diff). Live check below.
- commit: a36e4e7 "T7: Let a reader rate, mark and annotate a work from the feed"
- next: **The live check ran a real browser, and it is worth keeping.** `~/.cache/ms-playwright`
  holds a `chrome-headless-shell` even though playwright itself is in no `node_modules`, and node 22
  has a built-in `WebSocket`, so CDP can be driven from a ~200-line script with **no new dependency
  and nothing added to `frontend/`** — which the spec forbids. The harness is in the scratchpad
  (`t7-live.sh` + `t7-drive.mjs`): throwaway API on a free port, scratch data dir, Vite with
  `BACKEND_URL` pointed at it, two works seeded straight into SQLite (nothing may touch AO3), then
  click/keyboard/type against the rendered page. It proved things curl cannot: the status select
  carries the rating it did not touch, an unrated row renders differently from a half-star one, and
  a refused write both reverts and prints. **T9, T14, T17 and T19 are all UI tasks and should reuse
  it rather than re-derive it.**
  **The first run was green for the wrong reason and the second caught it.** Reloading the instant
  the optimistic update paints cancels the PUT still in flight, so "set a rating, reload, re-read"
  passed on timing luck and then failed on a fresh database — the API log showed the click's INSERT
  simply absent. Fixed in the harness by waiting until the *server* reports the value before
  reloading. This is **T43's and T47's lesson in a test harness rather than a test**: read what the
  fixture actually does, not what its name claims. The app-level residue is real but out of scope
  and not a defect: a reader who clicks a star and reloads within the same tick loses it, which is
  what optimistic UI over HTTP costs and what a `beforeunload` guard would cost more.
  **The review's three frontend findings were all one mistake — page-level state for per-row work.**
  A single `noteDraft`, a single `savingNote` boolean, and an unconditional `setOpenNoteId(null)` on
  completion each meant one row's editing could destroy another's. All three folded in by scoping to
  `work.id`. **Generalising, and it belongs beside T28's rows about tests: a page that renders N of
  something needs its editing state keyed by which one, and the smell is a `useState` scalar beside
  a `.map`.** T9's detail page renders one work and will not show this; T14's downloads list and
  T17's notifications will.
  **T56 is queued and deliberately not in this diff**: `SetWorkState`'s update path answers 500 when
  a concurrent clear removes the row under it, which T7's controls make an ordinary shape rather
  than a theoretical one. Backend code, backend verification, and a decision of its own about what
  "handled" means — see its notes.
  **Nothing new is unpinned that was not already**, because this project has no frontend test runner
  by decision; what stands in for one is the harness above, and it is the reason this entry claims
  more than "it typechecks".

## 2026-08-24 — T8 Reading status and rating as saved-filter criteria — done

- did: Three typed criteria on `SavedWorkFilter` — `ReadingStatus`, `MinUserRating`,
  `MaxUserRating` — migrated on both providers and applied by `WorkQueries.ApplyFilter`, which now
  takes the caller's `StatesOf` queryable as a parameter so the list and the match count share both
  the predicate and the answer to *whose* reading it is. "Unread" is a `NOT EXISTS` over "marked as
  anything", covering the work with no state row and the row left saying `None` alike. A rating bound
  drops unrated works, by decision. The editor grew a "Your own reading" section, and the status
  labels moved out of `WorksPage.tsx` into `frontend/src/readingStatus.ts`.
- files: `Api/Models/SavedWorkFilter.cs`, `Api/Data/WorkQueries.cs`,
  `Api/Controllers/{SavedFilters,Works}Controller.cs`, `Api/Dtos/SavedFilterDtos.cs`,
  `Api/Data/Migrations/{Sqlite,Postgres}/*ReadingCriteriaOnSavedFilters*`,
  `Tests/SavedFiltersControllerTests.cs`, `frontend/src/readingStatus.ts` (new),
  `frontend/src/pages/{FiltersPage,WorksPage}.tsx`, `frontend/src/api/types.ts`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~SavedFilter` → 55 passed (39 before);
  `dotnet test` → 429 passed (413 before); `npm run build` + `npm run lint` → clean, the two known
  fast-refresh warnings only. Thirteen mutations applied one at a time, every one red — plus the live
  check below.
- commit: 9944b3d
- next: **A mutation run can be green because the build was stale, and it cost most of a debug loop.**
  `shutil.move` restores a file with its *original* mtime, so MSBuild saw a source older than the DLL
  built from the mutated copy and skipped the rebuild — the next `dotnet test` then ran the previous
  mutation, and a suite that had just passed went red with no source change. Restores in a mutation
  harness must **rewrite** the file, not move it back. The mutation *results* all survived the
  mistake (each mutation is written with a fresh mtime, so its own run rebuilt correctly); only the
  run *after* a restore is poisoned. `mutate2.py` in the scratchpad is the corrected shape.
  **One mutation genuinely survived, and it was the count path.** Passing everyone's states instead of
  the caller's to `DescribeAsync` left the suite green: `Counts_a_per_user_criterion_with_the_same_predicate_that_lists_it`
  exercises one user, so an unscoped join gives the same number. **T6's lesson again — a test that
  names a guard without constructing its case** — fixed by asserting `MatchingWorkCount` inside
  `Reads_only_the_applying_readers_marks`, where a second user's marks exist. The generalisation is
  worth keeping: the shared-predicate property means the list and the count are two call sites, and
  pinning one pins nothing about the other.
  **The live check ran a real browser again and it earned its keep.** T7's harness adapted in about
  twenty lines (`t8-live.sh` + `t8-drive.mjs` in the scratchpad; three works instead of two). It marks
  9001 Read and rates 9002 through the real feed controls, builds both filters in the real editor, and
  reads the list back: "Unread" matches **2** — the never-touched work with no row *and* the rated one
  whose row says `None` — while "Loved" (≥4 stars) matches 1, and the feed shows exactly those works
  under each. That is the two-shapes-of-absence case proven in a browser, not only in SQLite.
  **T9, T14, T17 and T19 should copy `t8-live.sh` rather than `t7-live.sh`** — it seeds three works,
  which is the minimum for a filter test, and its driver already has the "pick an option out of a
  labelled select inside a named section" helper the filter and download UIs both need.
  **The review found nothing in this diff and two things on the branch, both already queued.** The
  `LastKnownTotalWasAuthenticated` mis-pairing is **T44**, second arrival; `BeginBackfill` not
  resetting `BackfillStalledRuns` is **T38**, now the *third* independent report of one defect. Both
  task entries now say so, so the fourth reader fixes it instead of re-deriving it. This is the audit's
  own finding recurring, and the answer is in the task list rather than in the code.
  **T9 is next by file order** — a page per work, `blocked-by: T6`, satisfied. It renders one work, so
  T7's keyed-editing-state smell does not apply to it; `SummaryHtml` is untrusted AO3 markup and is
  the thing to be careful with there.
  Filters checked to bite, per T22's lesson: `~SavedFilter` matches 55 and covers every new test
  (`--list-tests` grepped case-insensitively, per T28's note). Still zero and still suspect:
  T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.

## 2026-08-24 — T9 A page per work — done

- did: `GET /api/works/{id}` and `/works/:workId` behind it — everything the database already holds
  about one work (summary, tags by kind, series, authors in byline order, full stats, language, the
  reader's own ships) plus their own state, editable in place with the feed's `RatingStars` and a
  note editor. It fetches nothing from AO3. The feed's title now opens this page and AO3 moved into
  the byline. New: `WorkSummaryHtml.Sanitize`, which is the first thing in this app that hands a
  browser a work's summary at all — an allowlist of element *names*, **no attributes at all**,
  script/style/raw-text elements dropped whole, everything else unwrapped to its words, walked
  iteratively with a depth cap.
- files: `Api/Services/Html/WorkSummaryHtml.cs` (new), `Api/Controllers/WorksController.cs`,
  `Api/Dtos/WorkDtos.cs`, `Tests/WorkDetail{,Summary}Tests.cs` (new),
  `frontend/src/pages/WorkDetailPage.tsx` (new), `frontend/src/pages/WorksPage.tsx`,
  `frontend/src/api/{client,types}.ts`, `frontend/src/{App.tsx,index.css}`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~WorkDetail` → 33 passed (0 before — new classes);
  `dotnet test` → 462 passed (429 before); `npm run build` + `npm run lint` → clean, the two known
  fast-refresh warnings only. Eleven mutations applied one at a time, every one red. Live check in a
  real browser, twice — before and after the review's fixes.
- commit: 82d8fe7
- next: **The tests passed on their first run, which is a warning rather than a result.** Nothing was
  seen to fail except the compile, so two of them were checked for constructing their case and both
  were weak: the tag-order test seeded tags already in the expected order (so ordering was unpinned),
  and the whose-state test had *both* readers mark the work (so an unscoped query could land on the
  right row by luck). Fixed by scrambling the seed order and by adding a test where only the *other*
  reader has marked it. **This is T6's and T8's lesson arriving a third time — a test that names a
  guard without constructing its case** — and the cheap detector is: write the mutation first, and if
  you cannot say which assertion it breaks, the assertion is not there yet.
  **One mutation deliberately survives and it is written down.** Removing the sanitizer's `MaxDepth`
  cap leaves the suite green: the 5,000-deep test asserts only that it returns and keeps its words,
  which is what the iterative walk buys. The cap is belt-and-braces about what is handed downstream,
  not about surviving the walk, and DECISIONS says so rather than leaving it as an unpinned constant.
  **The summary sanitizer is the piece to be careful with, and it is server-side by decision.** The
  frontend has no sanitizer and this project adds no frontend dependency lightly, so `SummarySafeHtml`
  arrives already safe and the page renders it with `dangerouslySetInnerHTML` — the raw column reaches
  no client. The rule has no attribute parsing in it *on purpose*: keeping `href` means being right
  about `javascript:` and its encodings for ever. Anchors lose their destination and keep their words.
  If T10's fixture shows AO3 summaries carrying something the allowlist drops, widen the list of
  names — never the attributes.
  **T10 inherits the reading side already built**: `PublishedAt`/`DetailFetchedAt` are on the wire and
  both rows render as "Not fetched yet", and the tag list is one `[{ type, name }]` array, so a fuller
  list from a work's own page needs no DTO change. Its notes now say so. T10 stays `blocked` on the
  fixture and on T51.
  **The live harness is `t9-live.sh` + `t9-drive.mjs` in the scratchpad**, adapted from T8's in about
  forty lines: it seeds a work with a script-carrying summary, six tags, two authors and a series,
  clicks the feed row's title, and asserts the script is gone *including its source text*, that no
  attribute survived, that the link's words stayed, that "Published" reads "Not fetched yet", and that
  marks made on the detail page survive a reload. `rm -rf` is blocked by a hook — use `mktemp -d`, as
  this one now does. **T14, T17 and T19 should copy it**: it already drives a single-record page,
  which the downloads and notification views are not, but its CDP plumbing and its
  wait-for-the-server-before-reloading helper are what those tasks would otherwise re-derive.
  **The review found three, all real, all in the new code.** The one worth remembering: a page keyed
  by a route parameter needs its *write* path guarded against the parameter changing, not only its
  load path — React Router reuses the component, so a rating set on one work could land on the next.
  Generalising T7's "state keyed by which row", one page further: **a component that outlives its own
  subject must invalidate in-flight writes when the subject changes.** The third finding lives in
  `WorksPage.tsx` too and was deliberately left there as **T57** rather than fixed in this diff.
  Filters checked to bite, per T22's lesson: `~WorkDetail` matches 33 and covers every new test in
  both new classes (`--list-tests` grepped case-insensitively, per T28's note). Still zero and still
  suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.
