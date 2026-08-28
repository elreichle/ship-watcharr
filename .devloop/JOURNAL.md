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

## 2026-08-24 — T11 Requesting a download — done

- did: `DownloadsController` — `POST /api/works/{id}/downloads` queues a format, `GET
  /api/downloads` lists the caller's, `DELETE /api/downloads/{id}` drops one. A request finds one of
  four things and only two of them write: nothing (queued), this exact version already on disk
  (complete on the spot, no AO3 request), a fetch already `Downloading` (left alone — the worker
  owns that row), or a failed/stale request (re-armed, which completes it instantly if the current
  version's bytes turned up meanwhile). Idempotent per (reader, work, format) on the model's own
  unique index, so a second ask is the first row rather than a second queue entry. No migration:
  `Download` and `WorkDownloadFile` were in `InitialCreate` on both providers already.
- files: `Api/Controllers/DownloadsController.cs` (new), `Api/Dtos/DownloadDtos.cs` (new),
  `Tests/DownloadsControllerTests.cs` (new), `Tests/LibraryTestHost.cs`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Download` → 26 passed (0 before — new class, and
  `--list-tests` confirms the filter matches exactly those 26); `dotnet test` → 488 passed (462
  before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only.
  Sixteen mutations applied one at a time, every one red.
- commit: 872026a
- next: **Two mutations survived the first pass and both were the same mistake — a guard whose case
  the fixture never constructs.** Dropping `existing.WorkDownloadFileId == onDisk.Id` from the
  staleness check left the suite green, because the only stale test moved the work on *without*
  seeding bytes for the version it moved to — so `onDisk` was null and the id comparison never ran.
  Fixed by `Re_arms_a_stale_request_onto_the_version_another_reader_already_has`, which is the real
  shape: the request holds v1, the work is at v2, and another reader's fetch already put v2 on disk.
  The second was the size guard on the leave-alone branch, unreachable until the in-flight test
  seeded a file. **This is T6's, T8's and T9's lesson a fourth time**, and the detector the last
  three entries name works: write the mutation first, and if you cannot say which assertion breaks,
  the assertion is not there yet.
- **One design decision changed under mutation pressure and is better for it.** The first cut left
  a `Pending` request alone alongside a `Downloading` one. That mutation was behaviourally
  invisible, which is what exposed it as wrong rather than merely unpinned: re-arming a `Pending`
  row writes nothing *unless* the bytes appeared on disk since it was made, in which case it stops
  being a fetch anyone has to perform. Only `Downloading` is untouchable now — the worker owns that
  row. `ToDto`'s `Status == Complete ? size : null` guard went the same way: once the leave-alone
  call site passed the size only when the row actually holds the file, the guard was redundant, and
  a redundant guard is an unpinned branch.
- **What T12 needs from this, now in its notes.** The queue has **no wake signal** — a request is a
  row and nothing tells anyone about it, so T12 owns however the worker learns of one
  (`ScrapeWakeSignal` and `ShipsController`'s use of it are the shape). A row T12 may touch is one
  whose status is `Pending`; `Downloading` means someone already holds it. And `DownloadStatus` has
  no `Ready` despite T12's `delivers` line saying so — the completed state is `Complete`, already on
  the wire.
- **The downloads list is the one list in this app not scoped to watched ships**, and it is worth
  knowing before T14 renders it. A download is something the reader asked for, not a view of the
  library; hiding it when they unfollow the ship would hide the only handle able to delete it and
  strand the file. `Keeps_listing_a_request_whose_ship_the_reader_has_stopped_watching` pins it.
- **The review found nothing in this diff and two things on the branch, both already queued.**
  `LastKnownTotalWasAuthenticated` is **T44**, now its *third* independent arrival from the same two
  lines — the pattern T8's entry noted about T38 repeating itself, and the answer is again in the
  task list rather than the code. The feed's note editor is **T57**, and the review added half a
  symptom the task did not have: the success handler also closes the editor unconditionally, so a
  reader who closes mid-save and reopens has it slammed shut. T57's notes now say the captured-draft
  guard does not cover that half.
- **No live check, and that is not a gap here.** T11 ships no UI; T14 is the task that drives these
  endpoints through a browser, and `t9-live.sh` in the scratchpad is what it should copy.
  Filters checked to bite, per T22's lesson: `~Download` matches 26 and covers every new test. Still
  zero and still suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by
  design.

## 2026-08-24 — the three AO3 captures land — not a task

- did: Nothing to a line of code. Emma saved `ao3-login-page.html`, `ao3-work-page.html` and
  `ao3-empty-listing.html` under `backend/Ao3Tracker.Tests/Fixtures/`; I verified each is the page
  it claims to be, read the answers out of them, and moved the plan to match. T5, T10, T13 and T39
  came off `blocked` — **the list has nothing blocked for the first time.**
- files: the three fixtures (new), `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test` → 488 passed, unchanged; the fixtures are not in the `.csproj` yet and the
  tasks that consume them will add them.
- commit: 1347c59
- **If you are picking up T12, the entry above this one is out of date — read T13 first.** T11's
  handoff told you to isolate a URL-construction function taking a work id, as a placeholder seam
  T13 would replace. The capture says there is nothing to construct: the real URLs are
  `/downloads/{workId}/{slug}.{ext}?updated_at={unix}`, the slug an unstated truncation of the
  title and `updated_at` AO3's own stamp, so a download reads the work's page first and costs two
  rate-gated requests. Build to T13's notes, not to T11's. Everything else in T11's handoff — the
  queue having no wake signal, `Pending` being the only status T12 may touch, `Complete` rather
  than `Ready` — still holds.
- **T39 got smaller and its premise held.** `HasListing` is right; a zero-result index does render
  `ol.work.index.group`, above a `0 Works in <tag>` heading. The task is now the test that pins it,
  and it is still worth writing — the existing tests reached that path through a helper that emits
  the container unconditionally, so they proved the premise by assuming it. This is the same
  detector the last four entries keep naming, arriving from the other side: an assertion that
  cannot fail is not evidence, even when the thing it assumes turns out to be true.
- **T58 is new and is not a correctness bug.** The incremental pass sends
  `work_search[revised_at]`, which the tag-listing endpoint discards — Emma found it in a browser,
  and the captured filter form confirms it (`date_from`/`date_to`, no `revised_at`). The
  client-side cut means the right works were always read; what was lost is the load reduction
  `RevisedAtBound`'s comment claims. Fix it as a politeness bug and leave the watermark logic
  alone. It also means `listingWasFiltered` has been describing an intent rather than a fact, which
  entangles it with T30 and T44 — once the parameter works, check the total logic is right because
  it is right, not because two errors cancelled.
- next: T5 is the first `todo` in file order with no blockers, and its fixture is in place. Its one
  trap is in the notes: the capture holds two forms carrying an `authenticity_token`, and the first
  one on the page is the header dropdown's, not the login form's.

## 2026-08-25 — T5 Authenticate to AO3 and reuse the session — done

- did: The scraper logs in. Three seams the way the index scraper is three seams — `Ao3LoginPage`
  (pure: reads `form#new_user`'s token, action and *field names*, and reads any AO3 page for whether
  it was served to a session), `Ao3SessionEstablisher` (the two-request round trip), and
  `Ao3SessionProvider` (decides whether one is needed). `IRateLimitedHttpClient` grew a logged-out
  uncached GET and a form POST; `RateLimitedAo3HttpClient` now attaches the cached cookie to every
  scrape, reads back off each page whether AO3 honoured it, and discards a session AO3 has stopped
  accepting. `ScrapeWorker` establishes a session before running due jobs, and holds them when the
  login is refused, exactly as it holds them when no login is stored.
- files: `Api/Services/Scraping/{Ao3LoginPage,Ao3Cookies,Ao3SessionEstablisher,Ao3SessionProvider}.cs`
  (new), `Api/Services/Credentials/IAo3SessionCache.cs` (new),
  `Api/Services/Scraping/{IRateLimitedHttpClient,RateLimitedAo3HttpClient,ScrapeWorker}.cs`,
  `Api/Services/Credentials/IAo3InstanceCredentialStore.cs`, `Api/Program.cs`,
  `Tests/Ao3Login{Page,Establisher,Cookie,Provider,Transport,Worker}Tests.cs` + `Tests/Fixtures.cs`
  (new), `Tests/{LibraryTestHost,JitterTests,Ao3Tracker.Tests.csproj}`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3Login` → 97 passed (0 before — all new classes,
  and the filter matches exactly those 97, nothing pre-existing); `dotnet test` → 585 passed (488
  before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only.
  Thirty-nine mutations applied one at a time, every one red by the end. Also booted a throwaway
  instance on :5187 with a scratch data directory: it starts, both workers come up, and the gate
  holds with both reasons.
- commit: dcf6258
- next: **Two mutations survived the first sweep and one of them was the trap the task notes were
  written to prevent.** The capture holds the `authenticity_token` in *three* places, not the two
  the notes name — there is a `<meta name="csrf-token">` in the head at offset 2556, before either
  form. The guard test replaced "the first occurrence" believing it to be the header dropdown's; it
  was the meta tag's, both forms kept the real value, and `QuerySelector("form")` passed the test
  that exists to catch exactly that. **This is the fifth arrival of the lesson the last four entries
  name, and the first time it landed on a test written on purpose to construct the case.** Knowing
  the trap was not enough; running the mutation is what caught it. The test now distinguishes all
  three sites and a sibling asserts the capture really holds three copies, so the day AO3 stops
  repeating the token the guard says so instead of silently stopping.
  The second survivor was a harness artefact worth remembering: both `HttpClient`s wrapped the *same*
  stub handler, so "the login goes through the transport that does not follow redirects" was
  unobservable — a stub handler replaces precisely the component whose configuration is under test.
  Two stubs now, and the assertion is which one received the POST.
- **Two real defects found by reading the diff rather than by a test.** (1) `Uri.TryCreate(path,
  UriKind.Absolute, …)` **succeeds on Linux** for `/users/login`, yielding `file:///users/login` — a
  form action read off the page was being resolved against local disk. Caught by the first
  establisher test to assert the posted URL; the scheme is now checked explicitly, and this is worth
  remembering anywhere else in this codebase resolves a path out of markup. (2) The session cache is
  reached from inside the shared HTTP client, which runs in the *scrape job's* scope — so discarding
  a dead session called `SaveChangesAsync` on the walk's own `DbContext` and would have committed
  whatever the ingestor had tracked at that moment. `Ao3SessionCache` is now a singleton that owns
  the scope it reads and writes in, and `Ao3LoginSessionScopeTests` constructs the case (an unsaved
  `Ship` in the caller's context that must still be unsaved afterwards). Both mutations kill.
  **Anything reached from inside a walk that writes needs its own scope** — this is T27's lesson
  arriving from a new direction.
- **T44 has stopped being latent and its task now says so.** `ScrapeHttpResponse.Authenticated` was
  a hardcoded `false` and is now read off each page's own markup, so a logged-in run genuinely mixes
  true and false pages and `LastKnownTotalWasAuthenticated` can now be stamped onto a total read
  without a session. Four readers have derived that fix from the same two lines; take it early.
  One detail T5 adds: `Authenticated` is false for a page carrying **no** evidence either way, not
  only for an anonymous one, so one unparseable page in a logged-in run is enough to reach it —
  no cache entry required.
- **The expiry mechanism is a markup reading, and that is not a shortcut.** AO3 answers a dead
  session with a 200 and the anonymous view, never a 401, so nothing at the transport level can
  notice. `nav#greeting` vs `#new_user_session_small` is validated from both sides against real
  captures — the work page and the empty listing were saved logged in, the login page logged out.
  A page carrying neither marker is `Unknown` and changes nothing, deliberately: treating "no
  evidence" as "logged out" would throw a working session away over every 404.
- **`/code-review high` found six, four of them in this diff and one of them serious.** The high one
  is the politeness bug this project cares most about: a *refused* login was retried on every poll
  for ever. Held jobs deliberately do not advance `NextRunAt`, so they stay due, so the next poll
  asks again — two AO3 requests a minute, half of them failed POSTs to `/users/login`, from an
  instance whose stored password will not become correct by being retried. Roughly 2,880 a day, and
  indistinguishable from credential stuffing at the archive's end. `Ao3LoginBackoff` now climbs
  5 → 15 → 30 → 60 minutes and stays there (48/day at the cap). **The hard part was not the backoff
  but not punishing the operator with it**: a cooldown on a configuration error is a cooldown on the
  person fixing it. So the admin credential endpoint calls `Reset()` on save and on clear, and the
  next poll tries immediately — the wait only ever applies to something nobody has touched. Three
  tests, one per path (backs off, lifts on its own, lifts at once when the credential is saved).
  The other three in this diff were all in cookie handling and all real: (a) a page AO3 served logged
  out was still cached under the `session:` key, so the run that re-authenticated found the dead
  session's anonymous copy waiting under the key its fresh cookie computes — fifteen minutes of
  reading the logged-out archive immediately after logging in to avoid exactly that; (b) `ExpiresAt`
  was computed over the raw `Set-Cookie` headers, which include *deletions*, so a
  `Set-Cookie: x=1; Max-Age=0` alongside the session dated the whole session to that instant and sent
  the instance back to log in on every poll — the jar now carries each cookie's expiry and the
  lifetime is read off what survived; (c) "AO3 set no cookies" could never fire, because the form
  fetch had already put the anonymous pre-login cookie in the jar — so a POST that set nothing at all
  was accepted and that anonymous cookie stored as the instance's session. Each is now pinned and
  each mutation dies.
  The two findings outside this diff: T44 (see above), and the transport-exception containment I had
  already fixed while waiting for the review — arrived at independently from the same reading, which
  is the second time on this branch that a defect and its fix have been derived twice.
- **What the next tasks inherit.** T10 and T12 both fetch pages that are richer when logged in, and
  they get that for free — `GetAsync` attaches the session and nothing else has to know. T12 in
  particular should note that a download response is a *file*, so `ReadSessionState` will read
  `Unknown` on it and `Authenticated` will be false; that is correct and not a bug to work around.
  The login POST is the only write this software makes to AO3 and the spec's non-goals list is now
  load-bearing rather than aspirational — `IRateLimitedHttpClient.PostFormAsync`'s doc comment says
  so at the seam where someone would be tempted.
  Filters checked to bite, per T22's lesson: `~Ao3Login` matches 97 and covers every new test in all
  nine new classes. Still zero and still suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's
  `~PagesFetched` is zero by design.

## 2026-08-25 — T44 The authenticated-total flag must come from the request that read the total — done

- did: `Ship.LastKnownTotalWasAuthenticated` is now written inside `RecordTotal`, from the
  `ScrapeHttpResponse.Authenticated` of the very page whose heading became the total, in the same
  three lines that write the number and its timestamp. The run-scoped `readWhileLoggedIn` OR and the
  `wroteTotal` accumulator are gone, along with two `FinishAsync` parameters and the assignment that
  used to sit at the end of the walk. Nothing computes the flag any more, so nothing can drift from
  what it describes.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Api/Models/Ship.cs`,
  `Tests/Ao3ShipIndexScraperTests.cs`, `.devloop/{tasks,DECISIONS,JOURNAL,scraper-audit}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper` → 69 passed (2 of them new and
  red before the fix, for the right reason both times); `dotnet test` → 587 passed (585 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Two mutations,
  both killed: `= authenticated` → `|= authenticated` kills the two "a later read replaces the flag"
  tests, and hardcoding `authenticated: true` at the call site kills four.
- commit: f083538
- next: **Taken ahead of T12's turn in file order, on the run-order note's own instruction**, and
  that note is now rewritten — file order is plain again and T12 is next. Read T13's "What the
  capture says" before starting T12; that instruction has not been consumed by anything yet.
- **The two-line fix was the easy half; the doc comment was the load-bearing half.** `Ship`'s own
  summary said the flag records "the run that produced the total". That sentence is why five readers
  in a row derived the same defect and none of them could point at a line that was wrong — the code
  matched its documentation exactly, and the documentation was one word too coarse. It now says
  *request*, and says why a run is not fine-grained enough to be the answer. Anywhere else in this
  codebase a field is documented against "the run", check whether it means the request.
- **The three ways a single run mixes both auth answers** are worth keeping together, because no one
  of them alone makes the case obvious: a cached page preserves `Authenticated: false` whatever
  session the run holds; a page carrying no evidence either way (a 404, a file body, an AO3 soft
  error) also reads false, because T5 deliberately collapses *unknown* to false so a 404 cannot
  discard a working session; and after T5 a session can die mid-run, so page 1 can be logged in and
  every page after it anonymous. The last of those needs no cache entry and no unparseable page, and
  is the one that makes this reachable on an ordinary healthy instance.
- **`RecordTotal` rewrites the total on every unfiltered page with a readable heading**, which is
  easy to miss when reading the walk — it looks like a once-per-run write. So within a run the *last*
  such page owns both the number and the flag, and the new test
  `Takes_the_Authenticated_flag_from_the_last_page_that_wrote_the_total` is the one that pins it.
  Anything later that wants "the total as page 1 read it" has to say so; today the code says the
  opposite and now has a test agreeing with itself.
- **The audit's D13 gap is closed** — `.devloop/scraper-audit.md` row D13 and its findings table both
  updated rather than left claiming an open gap. The audit is a document future tasks read as current;
  a closed gap it still lists is a re-derivation waiting to happen, which is the exact failure this
  task existed to end.
- **T44's `/code-review high` found two, neither in this diff, and both escalate a task already on
  the list rather than adding one.** (1) T49 was filed as a cosmetic placeholder bug; the review
  checked it empirically against a real logging provider and it **throws** — six placeholders over
  five arguments makes `string.Format` fail, `Logger.Log` rethrows as `AggregateException`, and the
  exception unwinds before `ship.BackfillNextPage = page - 1`, so the stale-cursor retreat never
  retreats and the ship re-sends the same failing request every run for ever. The suite is blind to
  it because `LibraryTestHost` registers no logging providers. T49's title and notes now say so.
  (2) `BackfillStalledRuns` increments for any backfill that read no page, not only for a stale
  cursor — `firstPage ??= page` runs before the `unreadable` break — so an AO3 outage retires every
  followed ship's back catalogue through a `Failed` state nothing can leave. Added to T38, which
  owns the exit, because fixing one half without the other leaves the bug.
  **The lesson for this loop: a warning the build has been printing since T37 was triaged as
  cosmetic by three passes and is a hang.** CA2017 is the one warning in this build; it was read as
  a formatting nit and never run. Nothing in the suite formats a log message.

## 2026-08-25 — T12 The download worker — done

- did: Downloads are fetched. Four seams: `Ao3DownloadLinks` (pure — reads the download menu off a
  work's page, because AO3's addresses cannot be constructed), `IRateLimitedHttpClient.DownloadAsync`
  (streams a file through the same rate gate, size-capped and deadline-bounded), `DownloadFetcher`
  (page → link → temp file → SHA-256 → atomic move → shared row), and `DownloadWorker` (a
  `BackgroundService` beside `ScrapeWorker`: the same two gates, one `ScrapeBudget` per drain, woken
  by `DownloadWakeSignal`, and re-queues fetches a restart interrupted). The transport's send path
  became generic over *how* a response is read, so a download and a page share one gate, one retry
  policy and one User-Agent by construction rather than by being written twice. `ScrapeWakeSignal`
  and the new `DownloadWakeSignal` now share a `WakeSignal` base. No migration — `Download` and
  `WorkDownloadFile` were in `InitialCreate` on both providers already.
- files: `Api/Services/Downloads/{DownloadWorker,DownloadFetcher,DownloadPaths,DownloadWakeSignal}.cs`
  (new), `Api/Services/Scraping/Ao3DownloadLinks.cs` (new), `Api/Services/WakeSignal.cs` (new),
  `Api/Services/Scraping/{IRateLimitedHttpClient,RateLimitedAo3HttpClient,Ao3HttpClientOptions,ScrapeWakeSignal}.cs`,
  `Api/Controllers/DownloadsController.cs`, `Api/Program.cs`,
  `Tests/{DownloadWorkerTests,Ao3DownloadLinksTests,Ao3DownloadTransportTests}.cs` (new),
  `Tests/LibraryTestHost.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~DownloadWorker` → 28 passed (0 before — new class,
  and the filter matches exactly those 28); `--filter ~Download` → 72 (26 before, T11's);
  `dotnet test` → 633 passed (587 before); `npm run build` + `npm run lint` → clean, the two known
  fast-refresh warnings only. Twenty-three mutations applied one at a time, every one red by the end. Also booted a throwaway
  instance on :5193 with a scratch data directory: it starts, both workers come up, and the download
  worker's startup reconciliation runs.
- commit: 7766481, with the review's fixes in f397956
- next: **T13 has no blocker any more and is next in file order.** It was written expecting to undo
  T12's URL construction; there is none to undo, because T12 was built to the capture. What is left
  is a test per format and the two questions no capture can answer — a stale `updated_at`, and
  whether the links work anonymously. **Its verification filter was `~DownloadUrl`, which matched
  nothing**; it is now `~Ao3DownloadLinks`. T13 also claimed AZW3 is not in `Ao3DownloadFormat` —
  the enum has carried `Azw3 = 5` since `InitialCreate`, and that note is corrected rather than left
  as a choice nobody made.
- **One mutation survived, and it was the same shape as the four entries before this one.** Removing
  the fetcher's own `Status != Pending` guard left the suite green, because the worker's query
  already selects only `Pending` rows — so no test could ever hand the fetcher anything else. The
  guard is not redundant: the worker reads ids in one scope and fetches in another, and a concurrent
  request can settle a row in between (another reader's fetch lands, and their next request completes
  this one off the bytes now on disk). Claiming it anyway would re-fetch a file the instance already
  has. Pinned by `Refuses_a_request_that_stopped_being_queued_while_it_waited`, which reaches the
  fetcher directly through a new `LibraryTestHost.FetchDownloadAsync` — going through the worker
  would have been asserting the query rather than the rule.
- **The serious defect I found was in reading my own diff, not in a test: a stalled download holds
  the global rate gate.** The transfer happens *inside* the gate semaphore, and `HttpClient.Timeout`
  stops applying once `ResponseHeadersRead` has the headers — so a socket that goes quiet mid-file
  would block every outbound request this instance makes, for as long as it stayed open. One hung
  download would stop the scraper entirely rather than merely failing. `DownloadTimeout` (5 minutes)
  is now a linked deadline over the whole send, and the fetcher turns it into a `Failed` request
  saying AO3 stopped sending, rather than the bare "the operation was canceled" the worker's
  catch-all would otherwise have written. **Anything this codebase does inside that gate has to be
  bounded in time** — the gate is process-wide and static.
- **The size cap is the other thing worth remembering.** This is the first code in the project that
  writes a remote body to disk, and a chunked response has no length until it has finished arriving,
  so "how much disk does one click cost" was a question only AO3 could answer. `MaxDownloadBytes`
  (64 MB, far above any real AO3 download) bounds it; a response past it is abandoned and reported,
  never stored. Both halves of that matter: a truncated file that got a row would be served to
  readers as a copy of the work.
- **Every failure names which half failed, and none of them retries.** A download is two requests
  now, so "it failed" is ambiguous in a way it never was for a scrape: the page can 404 while the
  file would have been fine, and the page can parse to a menu offering no such format. Each is one
  `Failed` row with a message, and a work AO3 has taken down is asked for once rather than on every
  poll for ever. The one thing that is *not* a failure is the drain's budget running out — that
  releases the request back to `Pending`, and the page it needs is still in the response cache when
  the next poll picks it up.
- **What T14 inherits, now in its notes.** `RelativePath` is relative to the data directory and must
  be resolved with `DownloadPaths.Absolute`, never treated as a path; `DownloadPaths.Extension` gives
  the filename's extension; `ErrorMessage` on a failed row already names the half that failed, so the
  UI should show it rather than a generic failure; and re-requesting a queued format answers with the
  existing row, so the button needs no guard against a second click.
- **`/code-review high` reported after `7766481` was committed; its fixes are in a second commit.**
  Eight findings. Its two highest were the gate stall I had already fixed before committing (the
  reviewer read the pre-fix tree, and **confirmed the mechanism empirically** — with
  `ResponseHeadersRead` and a 2s `HttpClient.Timeout`, a body taking 10s completes without throwing,
  which is the fact the deadline exists for) and T49, which is not this diff's. Five were real and
  are fixed; one is queued as T59.
- **The serious one: a 200 is not evidence that what arrived is the file.** The transport follows
  redirects, so AO3 declining a download — a restricted work whose session dies in the seconds
  between reading the work's page and fetching the link, which `DownloadAsync` cannot notice because
  it deliberately never reads session state off a file body — answers by redirecting to the login
  form, which is a 200 carrying HTML. It was being stored, hashed, moved into place, given a row and
  reported `Complete`: a login page on disk under a name saying it is an EPUB, with a checksum
  agreeing. `LandedOnTheFile` now checks where the request ended up, on the extension rather than the
  whole address so a redirect that still serves the file is not refused for moving it. A
  Content-Type check would not have worked — the HTML download format really is `text/html`.
- **The security one: the instance's session cookie was on offer to whatever host the markup named.**
  `Resolve` took any absolute http(s) URL and `DownloadAsync` attaches the session to whatever it is
  handed. Unreachable in practice today thanks to the `li.download` scoping, but a work page renders
  author-supplied HTML. A link must now name the page's own host, and `pageUrl` stopped being
  optional. **Anywhere in this codebase a URL read out of markup is then fetched with credentials,
  the host is the check that matters** — the scheme check that was already there is the other half.
- **T59 is a re-decision the review found, not a bug.** `Arm` nulls `WorkDownloadFileId` on a stale
  re-request, which T11 chose deliberately; the half that choice did not weigh is that the file is
  not deleted, so a reader whose refetch fails is left with neither a working row nor a reachable
  copy of bytes still on disk. Queued rather than changed, because reverting T11's rule reintroduces
  the worse failure it was written against and because T14 renders whatever state the answer invents.
- **Two of the eight were documentation, both mine.** Inserting `WakeTheWorker` above `Arm` left two
  `<summary>` blocks on one member and stranded `Arm`'s documentation; and a comment in `FailAsync`
  described a guarantee about `WorkDownloadFileId` the controller does not provide. Worth counting
  as findings rather than tidying: the second was a comment asserting the exact behaviour T59 exists
  because the code does not have.
  Filters checked to bite, per T22's lesson: `~DownloadWorker` matches 28 after the review's fixes
  (25 at the first commit) and covers every new test in the worker's own file; `~Ao3DownloadLinks`
  matches 13 and `~Ao3DownloadTransport` 5, which are the two seams that file does not reach. Still zero and still suspect: T31 `~TotalWorks`,
  T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.

## 2026-08-25 — T13 Confirm AO3's real download URLs — done

- did: Test-only. Five formats pinned to the addresses `ao3-work-page.html` actually carries, one
  `[InlineData]` each with the URL copied rather than generated, plus a completeness guard comparing
  the parsed keys against `Enum.GetValues<Ao3DownloadFormat>()`. The two questions no capture can
  answer are settled by making the app's behaviour safe under every answer rather than by guessing:
  a stale `updated_at` AO3 refuses is the already-pinned file-half failure with no retry; one AO3
  redirects to the current file is accepted, and the row's version key is now pinned to come from
  `Work.UpdatedAt` and not from the address the request ended at; and the anonymous question is
  settled by this deployment never asking anonymously — both halves authenticated, `GetLoggedOutAsync`
  untouched, and the transport still identifying the instance when there is no session to attach.
- files: `Tests/{Ao3DownloadLinksTests,DownloadWorkerTests,Ao3DownloadTransportTests}.cs`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3DownloadLinks` → 18 passed (11 before);
  `--filter ~Download` → 80 (72 before); `dotnet test` → 641 passed (633 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only.
  Five mutations, each red where it should be and nowhere surprising: dropping `azw3` from
  `FormatOf` reds the Azw3 case and the completeness guard; swapping `mobi` and `pdf` reds exactly
  those two cases; refusing every redirect reds the new redirect test with six older ones; keying
  the file by fetch time instead of `Work.UpdatedAt` reds the new test with three older ones; and
  dropping the User-Agent when there is no cookie reds the new transport test **and nothing else**,
  which is what makes that one worth having.
- commit: 8207d46
- next: **T14 is next in plain file order** — its blocker (T12) is done, and T12's journal entry
  already lists what it inherits. T10 is earlier and still blocked by T51.
- **The review found nothing in this diff and eight things in the branch, and none were folded in.**
  A test-only task that quietly grows a security fix and a `BackgroundService` fix is a diff nobody
  can review, so all eight are **T61–T68**. Two I verified by reading the code rather than trusting
  the report: T61, the login POST sending the instance's username and plaintext password to whatever
  host the form action names — the exact twin of the host check T12's review added to
  `Ao3DownloadLinks.Resolve` one iteration earlier; and T62, `DiscardPartialFiles` throwing out of
  `ExecuteAsync` on an unreadable partials directory and stopping the host at boot, on a path that
  runs on every boot, in a class whose own comment says nothing may end this loop. **T62 was added
  by T12's review** — a fix for a disk leak that introduced a startup crash. The other six are
  marked reported-not-verified in their own notes; whoever takes one should check the mechanism
  first.
- **The lesson worth carrying: a rule written down in DECISIONS is not a rule that got applied.**
  T12's review wrote "anywhere in this codebase a URL read out of markup is then fetched with
  credentials, the host is the check that matters", fixed the one place it had found, and nobody
  swept for the others. T61 is the place it did not reach, and it is the place with the most to
  lose. When a fix comes with a general rule, grep for the rule.
- **T13 asked two questions and the useful answer was a third.** Neither "rejected" nor "redirected"
  is harmful, which is why neither needed answering — but working out *how a stale address arises*
  found the fifteen-minute page cache, and the possibility neither question covers: AO3 simply
  serving the old version's file at the old address. That is T60, and it is the version-keyed path's
  own failure mode arriving through the cache instead of through the filename.
- **Filter check, per T22's lesson.** `~Ao3DownloadLinks` (T13's own) matches 18, up from 11, and
  reaches only the parser — the two settlement tests live at the seams they are about, so
  `~Download` (80, up from 72) is the filter that covers the whole task. Still zero and still
  suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.

## 2026-08-25 — T61 The login POST must not send the password to whatever host the form names — done

- did: `Ao3SessionEstablisher` refuses to post the credential anywhere but the configured archive.
  The form action is resolved as before and then measured against `LoginPath` under
  `Ao3HttpClientOptions.BaseUrl` — whole origin, not host — and a mismatch is a `Failed` login that
  names the address it refused. The same origin comparison replaced the host comparison in
  `Ao3DownloadLinks.Resolve`, which the review found still permitted a scheme downgrade.
- files: `Api/Services/Scraping/{Ao3SessionEstablisher,Ao3DownloadLinks}.cs`,
  `Tests/{Ao3LoginEstablisherTests,Ao3DownloadLinksTests}.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3Login` → 100 passed (97 before, 99 with the two
  tests the previous iteration left in the tree, one of them red); `--filter ~Download` → 81 (80
  before); `dotnet test` → 645 passed (641 before); `npm run build` + `npm run lint` → clean, the
  two known fast-refresh warnings only. Two mutations: removing the establisher's check reds both
  refusal tests, and narrowing either comparison from `GetLeftPart(UriPartial.Authority)` back to
  `.Host` reds exactly the downgrade test on that side and nothing else.
- commit: a1e0e02
- next: **T14 is next in plain file order** — its blocker (T12) is done, and T12's journal entry
  lists what it inherits. T10 is earlier and still blocked by T51.
- **This iteration inherited a claimed task, which is the mechanism working.** T61 was
  `in_progress` with two tests in the working tree and no fix — a previous turn marked it and died
  after writing the red test. Nothing was lost: the mark said where to look, the diff said how far
  it got, and the first thing this turn ran was that red test. The tests were kept as written.
- **The comparand was the decision, not the check.** The obvious move is to copy T12's twin exactly
  and compare the action against the page it came from — but the login page is fetched by the
  transport that *does* follow redirects, so `page.FinalUrl` is a value the archive's own redirects
  can move, and `Absolute` already resolves a relative action against `BaseUrl` regardless of where
  the page ended up. Measuring against the configured archive is both stricter and more consistent
  with what a relative action already does. A twin is not always a copy.
- **The origin, not the host, and that difference turned out to be live.** The download check
  compares hosts, which is enough for a cookie already scoped to one; a plaintext password is not,
  so this one compares scheme, host and port together. The review then found the same gap in the
  download path — an `http://` link on an https page keeps the host and gets the session cookie in
  the clear — so the stricter rule went there too. **The check that was copied from the download
  path came back stricter and fixed it.**
- **The sweep the previous entry asked for was actually run.** T13's journal recorded the lesson
  "when a fix comes with a general rule, grep for the rule", and this task existed because nobody
  had. Every outbound call site in the API is now accounted for: `ShipVerifier` and
  `Ao3ShipIndexScraper` build URLs from `BaseUrl` and a tag name; `DownloadFetcher` builds the work
  page URL and fetches one checked link; the login POST is this task. Exactly two fetch targets in
  this codebase come out of markup and both are checked. Other hrefs the parsers read
  (`Ao3BlurbParser`'s work, pseud and series links, `Ao3LoginPage`'s greeting) are parsed for ids
  and names and never fetched — worth knowing so the next sweep is shorter.
- **A refused action is a refused login, and inherits the backoff.** `Ao3SessionProvider` records it
  through `Ao3LoginBackoff` (5 → 15 → 30 → 60 minutes, lifted the moment the operator saves the
  credential again), so a login page whose form has moved is not re-fetched every sixty seconds.
  Nothing had to be added for that; it is worth writing down because it is the difference between a
  security check and a security check that hammers the archive.
- **The review found five and four are queued** — see the T61 review entry in `DECISIONS.md`. Its
  finding 3 is T67, already on the list from T13's review and now verified rather than reported;
  T69, T70 and T71 are new and all in T12's download code. The one folded in was the one this
  task's own doc comment had already claimed to be true.
  Filters checked to bite, per T22's lesson: `~Ao3Login` matches 100 and covers all three new
  establisher tests; `~Ao3DownloadLinks` matches 19 and covers the downgrade test. Still zero and
  still suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.

## 2026-08-26 — T14 Downloads in the UI — done

- did: `GET /api/downloads/{id}/file` streams a completed request's bytes with a sanitized
  filename; a Downloads page lists the reader's queue with live status, size, the failure message
  and Save/Try again/Remove; five format buttons and this work's requests sit on a work's own page.
  One `useDownloads` hook behind both pages — the list, the ask, the drop, and polling that runs
  while anything is `Pending` or `Downloading` and stops on the load that finds nothing is.
- files: `Api/Controllers/DownloadsController.cs`, `Tests/{DownloadsControllerTests,LibraryTestHost}.cs`,
  `frontend/src/hooks/useDownloads.ts`, `frontend/src/pages/DownloadsPage.tsx`,
  `frontend/src/components/WorkDownloads.tsx`, `frontend/src/downloads.ts` (all new),
  `frontend/src/{App.tsx,index.css,api/client.ts,api/types.ts,components/navigation.ts,pages/WorkDetailPage.tsx}`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only;
  `dotnet test --filter FullyQualifiedName~DownloadsController` → 52 passed (26 before);
  `--filter ~Download` → 107 (81 before); `dotnet test` → 671 passed (645 before). Seven mutations,
  every one red on the test that names it: dropping the containment check, dropping the ownership
  scoping, dropping the missing-file check, using the raw title (nine cases red, and the Russian one
  green — which is what that case is for), taking the length cut without the surrogate check,
  judging the title one UTF-16 unit at a time, and naming HTML as `text/html`.
- commit: 34ca322
- next: **T18 (Statistics API) is next in plain file order** — its blocker (T6) is done. Everything
  between is blocked: T15 still waits on T38, T40, T46 and T52; T16 and T17 wait on T15. T10 is
  earlier still and still blocked by T51.
- **The live check ran end to end against a stub, and it is worth rebuilding rather than
  re-deriving.** `scratchpad/live/` holds `stub.js` (a node stand-in for AO3: the login fixture with
  its relative form action, the captured work page with its download hrefs rewritten to the stub's
  own origin, and an EPUB payload) and `browse.js` (chrome-headless-shell over CDP). The API ran on
  :5312 with a scratch data directory, `Ao3HttpClient__BaseUrl` at the stub and the delays at
  0.2–0.4s; vite on :5313 with `BACKEND_URL` pointed at it. **Two things it needed that are not
  obvious.** The captured work page names `archiveofourown.org` in its download menu and T12's
  origin check refuses a link that is not same-origin with the page it was read from, so the stub
  has to rewrite those hrefs — rewriting them is what *exercises* the check rather than dodging it.
  And the headless shell defaults to a window narrow enough that the sidebar rails and renders no
  child links at all; `--window-size=1400,900` is what made the Downloads entry visible.
- **What the live check proved, in the stub's own log:** login page, login POST, work page with
  `view_adult=true`, then the file — four requests, every one carrying
  `ShipWatcharr/0.1 (+contact: …; instance/…)`. The bytes that came back out of
  `/api/downloads/1/file` were byte-for-byte the stub's payload, under
  `Content-Disposition: attachment; filename="We Chose to Wait a live check test.epub";
  filename*=UTF-8''…` — from a title of `We Chose to Wait: a "live" check / test`. Clicking MOBI in
  the browser went `Fetching…` → `Failed · AO3 answered 404 for the file itself` **without a
  reload**, which is the polling and the per-half failure message both working in one go.
- **Seeding a library for a live check does not need the scraper.** Ship, WatchedShip, Work and
  ShipWork went into the scratch SQLite file with python's `sqlite3` while the API held it open.
  There is no `sqlite3` binary on this machine; python has the module built in. Use the work id the
  captured page is of (70441196) so the address `DownloadFetcher` builds is one the stub answers.
- **The review did not run, and this diff is the least-reviewed on the branch.** `/code-review high`
  was launched and died on the account's monthly spend limit before reading anything. The pass was
  done by reading the diff instead. It found three: `Html` falling through to the `_` arm rather than
  being named (an exception should read as a decision, and the fallback should stay what it says it
  is); the sanitiser judging chars rather than runes, which reduced any title outside the basic
  plane to `work-{id}`; and a re-requested row jumping to the top of the list and back down on the
  next poll, because re-arming does not change `RequestedAt`. All three are fixed here. A later
  branch-wide review should read this diff first.
- **One mutation survived and the fix is the same shape as T12's.** Dropping
  `Status != DownloadStatus.Complete` from the file endpoint's guard left the suite green, because
  `Arm` moves the status and the file reference together and no test could construct a queued
  request that still named bytes. That state is precisely what **T59** is deciding whether to
  create. `Will_not_serve_a_request_that_is_queued_while_still_naming_a_copy` constructs it directly
  and T59's notes now point at it — the guard is what stops "keep the reference" from meaning "serve
  the previous version as the answer to the refetch".
- **The path check nothing can currently trip.** `RelativePath` is written only by
  `DownloadPaths.Relative`, out of a work id and an enum, so it cannot escape the data directory.
  It is checked anyway: this endpoint is the one place in the app where a value read out of the
  database becomes a file handed to whoever asked, and a row naming `../../etc/passwd` would be
  served in full to any signed-in reader. **The branch's own lesson, applied one input over** — T61
  swept every URL read out of markup; this is the same rule for a path read out of storage.
- **Six leaked API processes from earlier iterations are still running** (pids 650339, 651962,
  663142, 783152, 993804, 1004997 at the time of writing), each a `dotnet run` throwaway a previous
  live check never killed. Left alone rather than cleaned up, because they are not this task's and
  killing by pattern is what the "never pkill" rule exists to prevent. Worth an operator's attention.
  This iteration's own three (API, vite, stub) were killed by pid; the systemd dev instance was not
  touched.
  Filters checked to bite, per T22's lesson: `~DownloadsController` matches 52 (26 before) and
  covers every new test; `~Download` matches 107. Still zero and still suspect: T31 `~TotalWorks`,
  T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.

## 2026-08-26 — T18 Statistics API — done

- did: `GET /api/stats` answers with two lenses over the caller's library — `corpus` (totals, works
  per month of last revision, the AO3 rating mix, kudos and word-count histograms, the ten most
  prolific creators) and `reading` (status mix, words read, and the reader's half-stars laid against
  what the archive made of the same works) — plus a `ships` table where the two meet, one row per
  watched ship carrying both its corpus size and how much of it this reader has marked. `?shipId=`
  narrows every figure and 404s on a ship the caller does not watch. Query-only, no schema, no
  migration.
- files: `Api/Data/StatsQueries.cs`, `Api/Dtos/StatsDtos.cs`, `Api/Controllers/StatsController.cs`,
  `Tests/StatsControllerTests.cs`, `Tests/StatsQueryTranslationTests.cs` (all new),
  `Api/Data/WorkQueries.cs`, `Api/Controllers/WorksController.cs`, `Tests/LibraryTestHost.cs`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Stats` → 62 passed (0 before — the filter matches
  nothing that existed, checked per T22's lesson); `dotnet test` → 733 passed (671 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Fourteen
  mutations, every one killed by the test that names it — dropping the 404 guard, reading every
  user's states instead of the caller's, turning the bucket comparison into `<`, restricting the
  status mix to works with a state row, an unweighted average rating, zero instead of null for an
  empty mean, dropping ships the aggregate returned nothing for, ordering ships by display text,
  scoring an unrated work as zero, ranking the least prolific first, counting a crossover once per
  ship in the corpus total, folding the per-ship read count over every status, and lifting the
  top-ten cap.
- commit: 119563f
- next: **T19 (the Statistics page) is next in plain file order** and its blocker is now done. Read
  T18's section in `tasks.md` for the response shape — the short version is that every fixed
  vocabulary arrives zero-filled in a stable order so a chart can index by position, buckets carry
  their bounds as well as their label, and averages are null rather than zero for an empty library.
  Everything between T14 and T18 is still blocked; T10 is earlier still and still blocked by T51.
- **One mutation survived on the first pass, and the surviving one named a real gap in the test
  rather than in the code.** Reading an unrated work as a zero left the suite green, because the
  test for it seeded a work with *no state row at all* — which the mutation also skips. The state
  that catches it is a row that exists with a status and a null rating, which is what marking
  something read without scoring it produces, and is the common case for any reader who marks more
  than they rate. The test now constructs it. Same shape as T14's surviving mutation: the guard was
  right and no test could reach the state it guards.
- **The per-ship figures were written correlated and had to be rewritten grouped.**
  `library.Count(x => x.Ships.Any(sw => sw.ShipId == w.ShipId))` per figure per watched ship reuses
  `WorkQueries.Library` verbatim, reads like a sentence, and compiled to **eighty-two lines of SQL**:
  five full subqueries over the works table, re-executed once per ship. Replaced with one grouped
  pass joining the library to `ShipWorks`, with the caller's status and rating looked up once per row
  and folded. Checking the generated SQL cost one throwaway test; it is worth doing for any query on
  this codebase that reads too well.
- **`ToQueryString()` under Npgsql is a new kind of test here and it earned its place twice.** No
  PostgreSQL server exists in this loop's shell, so "translates on both providers" was going to be an
  assertion rather than a check. `StatsQueryTranslationTests` builds a `PostgresAppDbContext` on a
  connection string it never opens and compiles each query through the real Npgsql translator. Its
  coverage list is read off `StatsQueries` by reflection rather than maintained by hand — and that
  caught a query I had added without a case, during this task, exactly as intended.
- **The live check ran against a throwaway instance and is worth repeating for any API task.** API
  on :5321 with a scratch data directory, registered through `/api/auth/register` with a cookie jar,
  library seeded straight into the scratch SQLite file with python's `sqlite3` (per T14's note —
  there is no `sqlite3` binary on this machine). It proved the three things controller tests cannot:
  the route is registered and 401s unauthenticated, the whole DTO tree serializes to camelCase JSON
  with no cycle, and the numbers are right end to end — a followed ship with no works listed at zero
  and sorted first by its normalized name, a month series with a real gap in it (January then March),
  a status mix summing to the corpus, `?shipId=` narrowing everything, and 404 for a ship the caller
  does not watch. **The seeding needs the schema's own NOT NULL columns**, which are fewer than the
  model suggests — `pragma table_info` first, do not guess from the entity class. The instance was
  killed by pid.
- **`/code-review high` ran this time**, and its three findings are in DECISIONS: two folded in (the
  bucket-array validation, and one shared predicate for "which ships does this reader watch"), one
  false. It also flagged that the diff was being rewritten under it, which is fair — the `PerShip`
  refactor landed mid-review. Launch the review after the diff has settled.
- **The six leaked API processes from earlier iterations are still running** (pids 650339, 651962,
  663142, 783152, 993804, 1004997 as of T14's entry). Still not this task's to clean up, still worth
  an operator's attention. This iteration's own throwaway was killed by pid; the systemd dev instance
  was not touched.
  Filters checked to bite, per T22's lesson: `~Stats` matches 62 and covers every new test, and
  matched zero before this task. Still zero and still suspect: T31 `~TotalWorks`, T32 `~Monotonic`.
  T40's `~PagesFetched` is zero by design.

## 2026-08-26 — T19 The Statistics page — done

- did: A Statistics view at `/stats`, under Dashboard between Downloads and Schedules. Two lenses
  over the caller's library — six tiles for the corpus, five for their own reading — a per-ship
  table where the two meet with a read-through bar, a column chart of works per month of last
  revision, bar lists for the AO3 rating mix, the kudos and length histograms, the status mix and
  the ten most prolific creators, and a table of the reader's half-stars against what the archive
  made of the same works. `?shipId=` narrows every figure and is in the URL, so a narrowed view is
  linkable. No charting library: every bar is a `width`/`height` percentage on a `<span>` coloured
  from Obsidian tokens.
- files: `frontend/src/pages/StatsPage.tsx` (new), `frontend/src/{App.tsx,index.css}`,
  `frontend/src/api/{client.ts,types.ts}`, `frontend/src/components/navigation.ts`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only;
  `dotnet test` → 733 passed (unchanged — this task touched no backend code); a live check against
  a throwaway instance on :5331 with vite on :5332, driven through chrome-headless-shell.
- commit: 2eba093
- next: **T20 is next in file order and is not selectable** — it waits on T17, which waits on T16,
  which waits on T15, which waits on T38/T40/T46/T52. Nothing between T10 and T20 is selectable
  either. **The first selectable task is T31**, and from there the run is the long tail of scraper
  and UI defects (T31–T71). Those are what unblock T15, and T15 is what unblocks the rest of the
  plan — so the tail is now the critical path, not a cleanup queue.
- **The live check is the reason three of the review's findings could be closed with evidence rather
  than argument, and the recipe is now cheap enough to be routine.** `scratchpad/live/` holds
  `browse.js` (chrome-headless-shell over CDP, using node 22's own `WebSocket` — the previous
  iteration's copy needed `ws`, which is not required). API on :5331 with a scratch data directory
  and `--no-launch-profile`, registered through `/api/auth/register` with a cookie jar, library
  seeded straight into the scratch SQLite file with python's `sqlite3`, vite on :5332 with
  `BACKEND_URL` pointed at the API. Three things it needed that are not obvious: the shell's default
  window is too narrow for the sidebar to render its child links (`--window-size=1400,1000` fixes
  it, as T14 found); `Page.captureScreenshot` shoots the viewport, so a tall page needs
  `Emulation.setDeviceMetricsOverride` first and a region needs `clip` with a `scale`; and reading
  *drawn geometry* rather than text is what makes a chart checkable — `getBoundingClientRect().width`
  per bar caught nothing, but counting columns that own a `.month-bar-fill` caught every empty month
  drawing a bar.
- **Seeding the defect is what verified the fix.** The review's first finding was that one work with
  an unreadable date puts `{year: 1, month: 1}` at the head of the month series, and the page filled
  from there — 24,300 columns. Inserting a work at `0001-01-01` into the scratch database reproduced
  it against the real endpoint, and the same insert then proved the cap: 240 columns, a note reading
  "1 work is dated before September 2006 and is not drawn", and the page still responsive. **The
  server's own comment said this would happen** — `WorksByUpdatedMonth` explains that it refuses to
  zero-fill because a misparsed year would materialise centuries of buckets. It hands the span to
  the client and the client filled it unbounded. A comment explaining why a hazard is *someone
  else's* problem is a comment worth reading as a task for whoever becomes someone else.
- **An empty library has four answers, not one.** No ships followed; ships followed but the AO3
  login is missing so every scrape is held; one ship narrowed to that has nothing in it; and the
  whole library empty with everything configured. The first three were checked live (the login one
  by starting without a credential, the ship one by saving a fake credential and reloading). The
  login wording is lifted from `ShipsPage` deliberately — the same cause said the same way in both
  places, and it names an *admin* to non-admin readers rather than showing them a form they cannot
  use.
- **Two mutations, both caught by the browser rather than the compiler.** Removing the
  `month.workCount > 0` guard makes every gap draw a 1px bar (32 columns, 32 bars, where 11 months
  have works); removing the `Number.isInteger` guard sends `shipId=NaN` and turns `?shipId=abc` into
  an error page under a picker that says "Everything you follow". There is no test runner in
  `frontend/` by decision, so for UI work the live check *is* the mutation test — which is worth
  saying out loud, because "verified by build and lint" would have caught neither.
- **The review's other two findings were older code and are on the list**: T72 is new
  (`Ao3DownloadLinks` measures against the post-redirect page URL, not the configured archive —
  verified here), and the session-login race was already T63 from T13's review as *reported, not
  verified*, so T63 is now marked verified and carries the extra consequence this reviewer named.
  Filing a twin for a defect already on the list is a real cost in a loop that reads the list every
  iteration; T73 was drafted and deleted for that reason.
- **One leaked API process from an earlier iteration is still running** (pid 1963836, `dotnet run
  --project Ao3Tracker.Api`), down from the six T14 and T18 recorded — so somebody has been
  clearing them. Not this task's to kill, and killing by pattern is what the "never pkill" rule
  exists to prevent. This iteration's own three (API, vite, chrome) were killed by pid; the systemd
  dev instance (pid 2033) was not touched.
  Filters checked to bite, per T22's lesson: this task added no tests, so no new filter to check —
  `dotnet test` matched the same 733 as the baseline, which is the assertion that the backend was
  not touched. Still zero and still suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's
  `~PagesFetched` is zero by design.

## 2026-08-26 — T31 A singular listing heading must not be read as the tag's name — done

- did: `Ao3BlurbParser.ParseTotalWorks` finds the count with `(?<count>\d[\d,.]*)\s+Works?\b` —
  singular as well as plural, first match rather than last — and answers null when no such number is
  in the heading. The trailing-digits fallback is gone, so a tag name carrying digits can no longer
  donate them to the ship's size and an "Error 404" heading is no longer a tag of 404 works.
- files: `Api/Services/Scraping/{Ao3BlurbParser,Ao3ShipIndexScraper}.cs` (the second is a comment
  only), `Tests/{Ao3BlurbParserTests,Ao3ShipIndexScraperTests}.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3BlurbParser` → 46 passed (36 before, and 5 red
  when the tests were written before the fix); `dotnet test` → 743 passed (733 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Three
  mutations: narrowing `Works?` to `Works` reds both singular cases; restoring the trailing-digits
  fallback reds all three "not a work count" cases; taking the last match instead of the first
  survived until a case was written for it, and that case is now in the theory.
- commit: d6045a6
- next: **T32 is next in plain file order** and has no blockers. `dotnet build` emits **CA2017** on
  `Ao3ShipIndexScraper.cs:534` — a log template with more placeholders than arguments — which is
  plausibly T32's own defect wearing a compiler warning; read it before starting. Everything from
  T31 to T71 is the tail that unblocks T15, and T15 unblocks T16/T17/T20, so this tail is the
  critical path rather than a cleanup queue.
- **A surviving mutation named the missing case, again.** Taking the last regex match instead of the
  first left the suite green, because no heading under test had a second "N Works" in it. The one
  that catches it is a *tag name* containing the words — "1 - 20 of 4,317 Works in Prompt: 5 Works
  of Fiction" — which is exactly the class of input this task exists for: everything after "in" is a
  name somebody else chose. Third iteration running where the mutation that survived was a gap in
  the tests rather than in the code.
- **The task's own verification filter matched nothing, and had never matched anything.**
  `--filter FullyQualifiedName~TotalWorks` returns "No test matches" — the journal has been flagging
  it as "still zero and still suspect" every iteration since T22, and this is the first iteration to
  reach the task and act on it. The filter is now `~Ao3BlurbParser`. **Worth generalising: a
  verification command written at planning time is a guess about test names that do not exist yet**,
  and T22's ritual of checking that a filter bites is what turns the guess into a finding. T32's
  `~Monotonic` is the next one on that list, and it is next in file order — expect to have to fix it
  the same way.
- **Removing a fallback closed a second hole nobody was looking at.** The trailing-digits scan was
  what made an AO3 soft error served as 200 (`<h2 class="heading">Error 404</h2>`) offer 404 as the
  tag's size; `Ao3ShipIndexScraper`'s readability guard was the only thing between that number and
  `LastKnownTotalWorks`, and a test at `Ao3ShipIndexScraperTests` line ~1007 exists to pin exactly
  that. The guard stays and the test stays — two independent reasons on a field a full sweep checks
  itself against — but both comments were rewritten, because a comment that says "the parser will
  hand you rubbish, so I guard it" is wrong once the parser stops.
- **The review found nothing in this diff and five in the download code.** Three were already on the
  list (T67, T70, T64 — T64 promoted from *reported* to *verified*, with a worse consequence than it
  had been filed with), two are new (T73, T74). See DECISIONS. Three reviews running have now landed
  most of their findings in T12/T14's code, which is the newest and least-reviewed on the branch —
  T14's own entry predicted this. A dedicated pass over the download path would probably be cheaper
  than meeting it one finding at a time, and is worth considering as a task once the T31–T71 tail
  thins out.
- **One leaked API process is still running** (pid 1963836, `dotnet run --project Ao3Tracker.Api`).
  Not this task's, and killing by pattern is what the "never pkill" rule exists to prevent. This
  task started no processes of its own; the systemd dev instance (pid 2033) was not touched.
  Filters checked to bite, per T22's lesson: `~Ao3BlurbParser` matches 46 and covers all ten new
  cases. `~TotalWorks` matched zero and is retired — see above. Still zero and still suspect: T32
  `~Monotonic`, which is the next task. T40's `~PagesFetched` is zero by design.

## 2026-08-26 — T32 The non-monotonic-boundary warning names the wrong page — done

- did: `TrackBackfillFloor` takes the page it is looking at as a parameter and logs that, instead of
  `ship.BackfillNextPage` — which the line above it has already advanced to `page + 1`, so the
  message named the walk's next stop rather than the page the listing shifted under. Added
  `CapturingLoggerProvider`, the seam the test needed to see a log line at all.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/CapturingLoggerProvider.cs` (new),
  `Tests/Ao3ShipIndexScraperTests.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Monotonic` → 1 passed (red first at "Expected: 2,
  Actual: 3", which is the defect itself); `dotnet test` → 744 passed (743 before); `dotnet build
  --no-incremental` → no new warnings; `npm run build` + `npm run lint` → clean, the two known
  fast-refresh warnings only. Mutation: logging `page + 1` reds the new test.
- commit: 2ab7766
- next: **T33 is next in plain file order** and has no blockers — `.AsSplitQuery()` on
  `WorkIngestor.LoadExistingWorksAsync`, the cheapest task on the list, verified by the existing
  ingest tests staying green. **Check `~Ingest` bites before trusting it.** The build's one code
  warning, CA2017 on `Ao3ShipIndexScraper.cs:536`, is **T49's** and was left alone on purpose.
- **The predicted dud filter was a dud, and that is now three in a row.** T31's entry said
  `~Monotonic` was "still zero and still suspect" and would need fixing the same way `~TotalWorks`
  did. It matched nothing, because no test existed — the task was filed as cosmetic, and a cosmetic
  task gets a verification command nobody expects to have to write code for. It bites now. Filters
  checked this iteration: `~Monotonic` 1, `~Backfill` 13, `~Download` untouched. The ritual is
  earning its keep; keep doing it first, not last.
- **The seam this task needed is the interesting part, not the one-argument fix.** `TrackBackfillFloor`
  writes nothing and returns nothing — the log line *is* the behaviour — so there was no way to pin
  it without capturing a record, and `LibraryTestHost` has never had a logging provider. The obvious
  move (a provider that formats, like the console one) would have thrown on
  `RetreatFromStaleCursor`'s six-placeholders-over-five-arguments template and made T49 a
  prerequisite of T32. `CapturingLoggerProvider` therefore **never calls the formatter**: it keeps
  the structured values and lets a test ask what `{Page}` was bound to. **T49 needs the opposite
  seam** — its defect only exists at format time — and must add a second, opt-in one rather than
  make this one render.
- **A stopping rule whose only output is a log line has no other test seam.** Worth remembering for
  the rest of the T33–T71 tail: several of those tasks are about what an operator gets told, and
  they can all use this provider now. Assert on the named value, not on the sentence.
- **The line is `LogInformation`, though every reference to it calls it a warning** — the task title,
  its notes, and the surrounding comments. Not changed: T32's delivers is the page number, and the
  level is a separate judgement about how loudly a shifted listing should announce itself. Someone
  deciding that should decide it for the whole file at once; the test's local variable was renamed
  from `warning` to `boundary` so the tests at least stop asserting something untrue in their names.
- **The review found nothing in this diff and four elsewhere; three were already filed.** T70, T64
  and T74 were all re-derived independently — T70 with a sharper consequence (`MaxConsecutiveFailures
  = 3` means a one-minute AO3 blip permanently fails exactly the first three queued downloads before
  the breaker starts holding the rest). One is new: **T75**, the login cooldown measured from before
  the round trip, verified by reading `Ao3SessionProvider`. **Four reviews running have landed nearly
  every finding in the download path** — T64/T67/T70/T71/T73/T74 are six open tasks over the same few
  hundred lines. A single dedicated pass over that subsystem is now clearly cheaper than six
  iterations; worth proposing as a task rather than repeating this observation a fifth time.
- **The leaked API process is still running** — pid 1963836 (`dotnet run --project Ao3Tracker.Api
  --no-launch-profile`) and its child 1964058, up 10h26m, first noted in T31's entry. Left alone
  again: it is not this task's, and killing by pattern is what the "never pkill" rule exists to
  prevent — the pattern matches the systemd dev instance (pid 2033, up 3d) too. If a future
  iteration wants the port back, kill **1963836 by pid**, not by name. This task started no
  processes of its own and did not touch the dev instance.

## 2026-08-26 — T33 One page of known works should not read a cartesian product — done

- did: `WorkIngestor.LoadExistingWorksAsync` says `.AsSplitQuery()`, so the three collection
  `Include`s stop multiplying out — a page of twenty known works with ~15 tags, ~2 authors and ~1
  series each was reading a three-way `LEFT JOIN` product with every `Works` column repeated in each
  row, on every incremental pass over works that had not changed. Added the second-pass test that
  covers all three joins, which is the invariant the `Include`s exist for and which `Work.Series` had
  nowhere.
- files: `Api/Services/Scraping/WorkIngestor.cs`, `Tests/WorkIngestorPseudTests.cs`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ingest` → 9 passed (8 before); `dotnet test` → 745
  passed (744 before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings
  only. Mutation: dropping `Include(w => w.Series)` reds the new test with a `DbUpdateException`.
- commit: 50f5941
- next: **T35 is next in plain file order** (`NormalizedPseudIdentity` collides on a third
  capitalisation) and has no blockers. Its own notes name the hard part: getting a test around a
  migration, and the seam is undecided — settle that before writing the fix, and do **not** rewrite
  the applied migration in place. Its filter is `~Pseud`, which currently matches this file's 9 (it
  bites, but on tests that say nothing about migrations — expect to add the test the filter is
  supposed to be about).
- **The verification filter bit, for the first time in four tasks.** `~Ingest` matched 8 before the
  change and 9 after. T31 and T32 both opened with a filter that had never matched anything; this one
  matched, and matched tests that actually walk the code path (three of the eight are second-pass
  re-ingests). The ritual costs one command and has now paid twice and cleared once — keep doing it
  first.
- **A behaviour-preserving task has no red-first test, so the evidence has to come from somewhere
  else.** Two places, both cheap. A throwaway probe printed `ToQueryString()` with and without the
  split against the **Postgres** provider: without, one statement with three stacked `LEFT JOIN`s;
  with, the root query plus EF's own note that more follow, and an `ORDER BY w."Id"` EF adds itself.
  That probe is also why no Npgsql translation test was added — the translator was watched accepting
  it. And the new test is what makes the diff worth committing: it pins the rule the query's comment
  states, on the leg nothing tested.
- **`Work.Series` was the untested third of a three-part invariant, and nothing said so.** Tags and
  authors each had a second-pass test; series had none in any file, and the failure mode is loud
  (`DbUpdateException` on the unique key) rather than silent, so it would have surfaced in
  production as a broken pass rather than as quiet data loss. Worth generalising: when a comment
  says "all three of these must X", check that three tests exist, not that the comment is true.
- **The class doc was widened instead of starting a second test file.** `WorkIngestorPseudTests` is
  named for pseuds and is really about join reconciliation across re-reads; a series-shaped twin
  would have duplicated its 30-line blurb helper. The name is now the wrong half of the truth and
  the doc is the right one — a rename is a task for whoever next touches the file.
- **The review found nothing in this diff and four elsewhere, and all four were already on the list**
  (T64, T63, T67/T73, T74) — the first review in five to add no task. It also re-derived a worse
  consequence for T64: two concurrent fetches mean two `File.Move`s onto one destination and two
  `WorkDownloadFile` inserts, only the second race-handled. **Five reviews running have landed in the
  download path**, which is now nine open tasks (T63/T64/T67/T69/T70/T71/T73/T74/T75) over a few
  hundred lines. Three iterations have observed that one dedicated pass would be cheaper; it is now
  written into the run order as something to *file*, because observing it again is the pattern.
- **Leaked processes, unchanged and growing older.** pid 1963836 (`dotnet run --project
  Ao3Tracker.Api --no-launch-profile`, 10h38m) and the chrome-headless-shell tree from T19's live
  check (1994238 and its children, plus 1996041, ~10h). None of them this task's; killing by pattern
  is what the "never pkill" rule exists to prevent, since the pattern also matches the systemd dev
  instance (pid 2033, up 3d08h). Kill **by pid** if a future iteration wants the port or the memory.
  This task started no processes of its own and did not touch the dev instance.
  Filters checked to bite, per T22's lesson: `~Ingest` 8 → 9, covering the new test. Still zero and
  still suspect: none known — T31 retired `~TotalWorks` and T32 fixed `~Monotonic`. T40's
  `~PagesFetched` is zero by design.

## 2026-08-26 — T35 The pseud dedup migration collides on a third capitalisation — done

- did: `NormalizedPseudIdentity`'s merge now thins each work's author links to one per creator —
  keeping the lowest pseud id present in the normalized group — before repointing what is left at
  the group's `MIN(Id)`. The old `DELETE` only dropped a link when the work was *already* linked to
  the canonical row, so three spellings with the work linked to the second and third dropped
  nothing and the repoint then moved two links onto one row, failing `PK_WorkAuthors` mid-upgrade.
  Corrected in both providers, in place.
- files: `Api/Data/Migrations/Sqlite/20260822182752_NormalizedPseudIdentity.cs`,
  `Api/Data/Migrations/Postgres/20260822182800_NormalizedPseudIdentity.cs`,
  `Tests/PseudMigrationTests.cs` (new), `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~PseudMigration` → 4 passed (2 red first, both with
  `SQLite Error 19: UNIQUE constraint failed: WorkAuthors.WorkId, WorkAuthors.PseudId` — the defect
  itself); `--filter ~Pseud` → 13 (9 before); `dotnet test` → 749 passed (745 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Mutation:
  replacing the normalized-group match with `1 = 1` reds
  `Two_different_creators_on_one_work_both_survive` and nothing else.
- commit: baf862d
- next: **T36 is next in plain file order**, `blocked-by: none`. Two things this iteration filed that
  change what the run looks like: **T76**, a verified defect in the same migration (below), and
  **T77**, the download-path pass the run order has been asking three iterations for — take T77
  **instead of T63** when the run reaches T63, not from its position at the end of the file.
- **The task's warning was conditional and the condition was false.** Its notes said not to rewrite
  an applied migration *if it has shipped anywhere*; `git branch --contains` says this one exists
  only on `devloop/dashboard-completion`. So it was corrected in place. What makes that safe is
  narrower than "it is a dev branch": the old merge either produced the right answer or threw and
  rolled back, so no database can be holding a wrong state that a repair migration would have to
  find. Check that property, not the branch name, before doing this again.
- **The suite could not reach migration SQL at all until this task.** Everything starts from
  `EnsureCreated`, which builds today's schema and executes no migration. `PseudMigrationTests`
  takes `IMigrator` off the context's infrastructure, migrates to the revision *before* the one
  under test, seeds with `ExecuteSqlRaw` (the entity model describes today's schema and cannot
  write a row to yesterday's), and migrates one step. It targets the migration by name rather than
  migrating to latest, so a future migration cannot red it for unrelated reasons. **Any of the
  remaining migration-shaped defects can use this now.**
- **The review died on the account's monthly spend limit — and its last thought was the best thing
  it produced.** Second review lost this way after T14's; the limit resets at 22:20 America/Chicago.
  The notification carried one sentence of the agent's reasoning: *"Let me empirically verify a
  suspicion about the migration's pseud deletion cascading."* Chased by hand, that is **T76**, and
  it is filed as verified rather than reported: `SavedWorkFilterAuthors` has a `PseudId` FK to
  `Ao3Pseuds` declared `onDelete: Cascade`, the migration repoints `WorkAuthors` only, and so the
  final `DELETE FROM "Ao3Pseuds"` silently takes a saved filter's author criterion with the loser.
  A throwaway probe through the new seam confirmed it — `PRAGMA foreign_keys` reads `1`, pseuds
  merged to `[1]`, `SavedWorkFilterAuthors` came out **empty**. **Read what a dead agent was doing
  when it died before concluding the diff went unreviewed.**
- **T76 was not folded into this task**, though the file was open and the pattern was already
  written. T35 delivers "the upgrade does not fail"; T76 is an upgrade that succeeds and loses data,
  in a different table, and it needs a judgement T35 does not — what `Exclude` means when an include
  and an exclude of one creator merge onto one row. The seam is what makes it cheap for whoever
  takes it.
- **Only SQLite was executed.** The Postgres twin's changed block is byte-identical (`diff`) and
  `dotnet ef migrations script --context PostgresAppDbContext` renders it, so the C# is right and
  the SQL is what was intended — but no PostgreSQL server ran it. Not new to this task; it is how
  every migration on this branch stands. The correlated references were moved out of the subquery's
  `JOIN ... ON` clauses into its `WHERE` so the form needs no argument about where PostgreSQL allows
  an outer reference without `LATERAL`.
- **`Position` comes out of the merge with gaps** — a work linked to spellings at positions 0 and 1
  keeps position 0. Harmless: `WorkIngestor` rewrites every position from blurb order on the next
  ingest (`WorkIngestor.cs:206`), and nothing reads Position expecting it to be contiguous.
- **Leaked processes, unchanged.** pid 1963836 (`dotnet run --project Ao3Tracker.Api
  --no-launch-profile`) and its child, plus T19's chrome-headless-shell tree (1994238 and children,
  1996041). None of them this task's; kill **by pid** if a future iteration wants the port. Killing
  by pattern is what the "never pkill" rule exists to prevent — the pattern matches the systemd dev
  instance (pid 2033) too. This task started no processes of its own and did not touch it.
  Filters checked to bite, per T22's lesson: `~Pseud` matched 9 before (all `WorkIngestorPseudTests`,
  none about migrations — as predicted) and 13 after; `~PseudMigration` matches the 4 new ones.
  Still zero and still suspect: none known. T40's `~PagesFetched` is zero by design.

## 2026-08-27 — T36 The scraping-identity page shows blockers that are not about identity — done

- did: "What AO3 currently sees" now renders `identityProblem` — the identity gate's blocker alone,
  a new field on `ScrapingGateState` and the DTO — instead of `problem`, which is every blocker
  joined; and the login callout above it lost its `identity.identityConfigured &&` guard. A fresh
  install missing both used to print "No AO3 login is stored for this instance" inside the block
  about the User-Agent while the callout that exists for the login was suppressed.
- files: `Api/Services/Scraping/ScrapingGate.cs`, `Api/Dtos/AdminDtos.cs`,
  `Api/Controllers/AdminScrapingController.cs`, `Tests/ScrapeWorkerGateTests.cs`,
  `frontend/src/api/types.ts`, `frontend/src/pages/AdminScrapingPage.tsx`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter ~ScrapeWorkerGate` → 9 passed (7 before); `dotnet test` → 751 passed
  (749 before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only;
  a live check on a throwaway instance (API :5341, vite :5342, chrome-headless-shell) through all
  three states. Mutations: propagating every blocker as `IdentityProblem` reds all three new backend
  assertions; putting both frontend defects back reproduces the exact symptom in the browser.
- commit: 9004c7d
- next: **T37 is `done` already; the next `todo` in plain file order is T38** (an operator way back
  out of a failed backfill), `blocked-by: none`. T10 is earlier and still blocked by T51. Remember
  **T77 is taken instead of T63** when the run reaches T63.
- **The task's two suggested fixes were not alternatives.** Its notes said "either render only the
  identity blocker in that section, or drop the `identityConfigured &&` guard — the second is
  smaller", but its `delivers` names both outcomes, and dropping the guard alone leaves the identity
  section still printing the login blocker. Worth generalising: **when a task's notes offer a
  cheaper option than its `delivers` describes, `delivers` is the contract** — the notes are one
  reader's guess at the implementation, written before anybody opened the file.
- **The page had no honest way to render half of `problem`.** `ScrapingGateState` exposed `Blockers`
  (never sent to the client) and `Problem` (joined by `\n\n`), so the alternative to a new field was
  splitting the joined string in the page — putting the separator and the blocker order into the
  frontend, which is the drift the gate's class doc exists to prevent. See DECISIONS.
- **The live check is the mutation test for UI work, again.** There is no test runner in `frontend/`
  by decision, so both frontend defects were put *back* and the page reloaded: the callout vanished
  and the login blocker reappeared under "What AO3 currently sees", which is the defect verbatim.
  Restoring the fix restored the correct render. The recipe is now three iterations old and cheap:
  `browse.js` from T19's scratchpad adapted in about ten minutes, node 22's own `WebSocket`, no
  `npm install`. **A fresh install is already the both-missing case** — registration takes no email,
  the contact falls back to the admin's email, so there is nothing to arrange: register and look.
- **`problem` is now read by no frontend code.** Left in the DTO and the TS interface deliberately:
  it is the endpoint's honest "every reason at once" view and the worker's log uses the same shape.
  Deleting a field because its one consumer stopped needing it would narrow the API to today's page.
- **The review ran to completion for the first time in three tasks, and found nothing in the diff.**
  It stated T36's invariant back in its own words — `IdentityProblem` non-null exactly when
  `IdentityConfigured` is false, still `Blockers[0]`, one construction site — which is the assertion
  worth having from a reviewer that read the whole branch. Its four other findings produced **no new
  task**: two are T40 and T57 with sharper notes now folded in, one is T63 (six reviews running,
  absorbed by T77), and one was a claim that T35 rewrote an already-applied migration. That last one
  is checkably wrong on its premise — `git log --diff-filter=A` puts the migration's commit on this
  branch only — and argues past T35's real reasoning, which was the *rollback* property rather than
  the branch name. See DECISIONS. **Worth generalising: a review that reads the whole branch will
  re-derive the list, and its findings need checking against the list and against the journal before
  being filed** — the second check is the one that caught this.
- **Leaked processes from earlier iterations, unchanged.** pid 1963836 (`dotnet run --project
  Ao3Tracker.Api --no-launch-profile`) and its child, plus T19's chrome-headless-shell tree
  (1994238 and children, 1996041). None of them this task's; kill **by pid** if a future iteration
  wants the port. This iteration's own three (API 2516941, vite 2517739/2517755, chrome 2518875)
  were killed by pid and :5341/:5342 confirmed free; the systemd dev instance (pid 2033) was not
  touched.
  Filters checked to bite, per T22's lesson: `~ScrapeWorkerGate` matched 7 before and 9 after, and
  the task's own written verification (`npm run build && npm run lint`) is not a filter at all —
  it cannot see this change, which is why the live check is the verification and the build is the
  floor. Still zero and still suspect: none known. T40's `~PagesFetched` is zero by design.

## 2026-08-27 — T38 An operator way back out of a failed backfill — done

- did: `POST /api/admin/ships/{id}/backfill/restart` (new `AdminShipsController`) puts a `Failed`
  backfill back to `InProgress` from a page the admin names or the ship's stored cursor, zeroing
  `BackfillStalledRuns`, clearing `BackfillCompletedAt` and dropping `BackfillMinUpdatedAtSeen`. It
  refuses a backfill that is not `Failed`, a tag AO3 has denied, a ship with no enabled schedule, and
  a page below 1. `BeginBackfill` zeroes the counter too. `WatchedShipDto` gained
  `BackfillNextPage`/`BackfillStalledRuns`, and the Ships page renders both a given-up and a merely
  stalled backfill with a `warning` tone and the numbers behind them, plus the restart form for
  admins.
- files: `Api/Controllers/AdminShipsController.cs` (new), `Api/Controllers/ShipsController.cs`,
  `Api/Dtos/ShipDtos.cs`, `Api/Models/Ship.cs`, `Api/Services/Scraping/Ao3ShipIndexScraper.cs`,
  `Tests/BackfillRestartTests.cs` (new), `Tests/LibraryTestHost.cs`,
  `Tests/Ao3ShipIndexScraperTests.cs`, `Tests/ShipsControllerTests.cs`,
  `frontend/src/{api/types.ts,api/client.ts,index.css,pages/ShipsPage.tsx}`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter ~Backfill` → 31 passed (13 before); `dotnet test` → 769 (751 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only; a live check on
  a throwaway instance (API :5351, vite :5352, chrome-headless-shell) through the given-up render,
  the restart click, the stalled-but-running render, and both new refusals. Mutations: deleting the
  controller's counter reset, deleting `BeginBackfill`'s, and deleting the floor clear each red a
  different test and nothing else.
- commit: a84cfaf
- next: **T39 is next in plain file order** (`blocked-by: none`) — its fixture landed on 2026-08-24
  and the task shrank to a test that pins `HasListing`, so read its own notes rather than its title.
  **T40 now owns the stalled-run increment guard** as well as the `firstPage` placement, and its
  notes carry the whole argument and name the test it has to re-decide. **T78 is new.** T77 is still
  taken instead of T63.
- **Only half of what the notes asked for was built, deliberately.** T38's notes said "decide both
  halves here": ship the recovery path *and* narrow `RecordBackfillProgress`'s increment guard to
  `askedStaleCursor`. Tracing the narrowing against
  `Gives_up_on_a_backfill_that_spends_run_after_run_on_a_cursor_nothing_answers` kills it: the cursor
  halves 10 → 5 → 2 → 1, and at page 1 `CursorMayBeStale` is false by construction (`page > 1`), so
  `askedStaleCursor` is false from that run on, the counter freezes at 3, and `Failed` becomes
  unreachable — a ship asking an unanswerable page once a run for ever. The test's own comment states
  the current behaviour as intended. Since T36's review that whole argument already lives on **T40**,
  whose one-line move of `firstPage ??= page` *is* the narrowing made at its root. **The review
  independently derived the same trap**, which is the strongest evidence available that the split is
  right rather than convenient. See DECISIONS.
- **`delivers` was the contract again, cutting the other way.** T36's entry generalised "when a
  task's notes offer a cheaper option than `delivers` describes, `delivers` is the contract". Here
  the notes offered *more*, and the same rule applies: build the `delivers` line, hand the surplus to
  the task that owns it, and write down why. Both halves of that convention are now on the record.
- **The review found three real defects in this diff and they are all fixed.** The floor guard
  compared `fromPage` against a cursor the halving retreat had already dragged below the pages the
  floor came from, so the common case kept a floor that blinds shift detection for the whole re-walk
  (now dropped unconditionally); four places claimed the cursor was "where the walk gave up" when it
  is where the last run *landed*; and the restart was offered and accepted for `NotFoundOnAo3` and
  unscheduled ships, which can never run, leaving a row saying "in progress" for ever. Its fourth
  finding is T40's, and two of its sub-points were acted on here — `Ship.cs`'s summary sentence now
  describes what the counter counts instead of what it was meant to, and the new `BeginBackfill` test
  asserts `InRange(0, 1)` rather than `== 1` so a test about the reset does not pin T40's decision
  about the increment.
- **The cursor is a worse default than it looks, and that is now T78.** `BackfillNextPage` is where
  the last run landed; `JumpCursorBackFrom` halves it once per stalled run, so a ship that read 39
  pages is stored on page 1 and the one-click restart re-walks all 39 at the shared gate. Idempotent,
  so it is politeness rather than correctness — filed rather than folded in, because the honest fix
  wants a "deepest page read" column and two migrations.
- **The live check caught nothing and was still worth running twice.** First pass verified the three
  renders and a real restart click end to end; second pass, after the review's copy and gating
  changes, verified the corrected wording and both new refusals in the browser as well as at the API.
  **A trap for the next iteration: the Bash tool's working directory persists between calls**, and
  launching vite from `backend/Ao3Tracker.Api` (left over from an earlier `cd backend`) served an
  empty document and had `npx` silently install a second vite. Symptom was a 39-character DOM with a
  correct `location.pathname`, which reads like a CDP problem and is not. Launch with an absolute
  `cd` in the same command.
- **Leaked processes from earlier iterations, unchanged.** pid 1963836 (`dotnet run --project
  Ao3Tracker.Api --no-launch-profile`) and its child, plus T19's chrome-headless-shell tree (1994238
  and children, 1996041). None of them this task's; kill **by pid**. This iteration's own six were
  killed by pid across two rounds and :5351/:5352/:9333 confirmed free; the systemd dev instance
  (pid 2033) was not touched.
  Filters checked to bite, per T22's lesson: `~Backfill` matched 13 before and 31 after — the new
  class is named `BackfillRestartTests` so every test in it matches whatever the method is called.
  Still zero and still suspect: none known. T40's `~PagesFetched` is zero by design.

## 2026-08-27 — T39 Confirm what AO3 serves for a works index with no results — done

- did: Wrote the tests that hold what the 2026-08-24 capture already showed. New
  `Ao3EmptyListingTests` pins the five markup facts a zero-result index carries — the container is
  present, it is classed `work index group` rather than the bare `index group` the parser's fallback
  guesses at, no works, no parse warnings, a `0 Works` heading, no pagination. Two tests in
  `Ao3ShipIndexScraperTests` walk the same capture through a whole run: the filtered/incremental side
  stops `LastPage` rather than `Error`, and the unfiltered/backfill side reaches `Complete`. No
  production code changed except two comments repointed from T39 to T79.
- files: `Tests/Ao3EmptyListingTests.cs` (new), `Tests/Ao3ShipIndexScraperTests.cs`,
  `Api/Services/Scraping/Ao3ShipIndexScraper.cs` (comment only),
  `.devloop/{tasks,DECISIONS,JOURNAL,scraper-audit}.md`
- ran: `dotnet test --filter ~Listing` → 24 passed (17 before); `dotnet test` → 776 (769 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Mutations:
  breaking `FindListing` reds `Renders_the_results_container...` and both scraper tests; making
  `ParseTotalWorks` return null for a zero count reds `Counts_its_empty_result_set_in_the_heading`
  and nothing else; changing `PlausiblyTheEndOfTheListing`'s `page == 1` to `page == 0` reds only the
  backfill test — the filtered one survives on the heading waiver, which is the design saying so.
- commit: e9f58c3
- next: **T40 is next in plain file order** and is on T15's `blocked-by`, so it is the load-bearing
  one; T38's journal entry, not T40's notes, carries the argument about the increment guard.
  **T79 and T80 are both new and both from this task.**
- **The review found one thing in this diff and it was in a document, not in the code.** I wrote
  "T28's §C7 is closed" into `scraper-audit.md` and `DECISIONS.md` because the capture answers the
  premise C7 was parked on. It does not close C7: `PlausiblyTheEndOfTheListing` short-circuits on
  `page == 1` *before any heading is read*, so an unfiltered page 1 with the container, no works, no
  Next link and no heading still concludes `Complete` — a ten-thousand-work back catalogue written
  off in one request. What the capture buys is the freedom to *fix* it without stranding genuinely
  empty tags. Now **T80**, and C7 is open again in the audit.
- **The disproof was a passing test I had already read.** `Still_treats_an_empty_first_page_as_an_empty
  _tag` uses `Page(1, [])`, whose helper emits no heading, and asserts `Complete`. I read that helper
  in this iteration, mutation-tested against it, and still wrote the claim. The lesson worth carrying:
  **a note saying "X is now safe" is a claim about code, and is worth grepping the code for before it
  is written down** — the capture answered a question about AO3, and I let that stand in for an answer
  about this repo.
- **A task rewritten around new evidence can lose an obligation that lives in a code comment.** T39's
  notes were revised in place when the fixture landed, as an answer sheet for the three questions the
  capture settles. A fourth — that AO3 does not show restricted works to an anonymous request — lived
  only in `Ao3ShipIndexScraper.cs:335` ("that premise is T39's business") and in one scraper test, and
  did not survive the edit. Closing T39 would have left two comments in shipped code pointing at a
  `done` task. Now T79, `blocked` on a capture, and both comments repointed. **`grep` the source tree
  for the task id before closing a task whose scope was rewritten, not only `.devloop/`.**
- **`git checkout <path>` to revert a throwaway probe also reverts the task's own work in that file.**
  I added a probe test to `Ao3ShipIndexScraperTests.cs`, then reverted it with `git checkout` on the
  path — which took both new tests and the T79 comment repoint with it, since the file was modified
  and not committed. Rebuilt from the tool call that wrote them. Revert a probe with the same
  targeted edit that added it, or commit first.
- **The probe was not needed anyway.** The question it was going to answer — what an unfiltered
  page 1 with no heading does — is asserted by an existing green test. Reaching for a scratch
  experiment before checking whether the suite already answers the question cost the file.
- **The review's other three findings are all already on the list**, each independently re-derived
  for the sixth consecutive review: `DownloadWorker`'s unguarded startup sweep is **T62**, a transport
  failure permanently failing a reader's download is **T70**, and two workers racing
  `EnsureSessionAsync` is **T63**. **T77 — one dedicated pass over the download path — is still the
  cheapest thing on the list**, and this is now the fourth iteration to write that down without acting
  on it.
- Filters checked to bite, per T22's lesson: `~Listing` matched 17 before and 24 after; the new class
  is `Ao3EmptyListingTests` so every method in it matches whatever it is called, and both scraper
  tests carry `Listing` in their own names. T79's `~Restricted` matches 1 today.
- Leaked processes from earlier iterations, unchanged and none of them this task's: pid 1963836
  (`dotnet run`) and child, and T19's chrome-headless-shell tree (1994238, 1996041). This task started
  no processes — it is test-only, and needed no live check. The systemd dev instance (pid 2033) was
  not touched.

## 2026-08-27 — T40 An unreadable page must not be counted as a page that was read — done

- did: Moved `pagesFetched++`, `parseWarnings +=`, `firstPage ??= page` and `lastPage = page` below
  the `if (unreadable) break` in the walk, so a page AO3 served but the parser could not read
  advances no counter and names no boundary on the non-retreat path either — T37 had fixed only the
  retreat. Kept the dropped warning count visible by adding `{Warnings}` to the parse-failure
  `LogError`. Then settled the increment question T38 handed over: `RecordBackfillProgress`'s
  stalled guard now reads a new `pagesServed` counter (200s whose body reached the parser) instead
  of `firstPage`, which had been standing in for "AO3 answered" only because it was set above the
  break.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Api/Models/Ship.cs`,
  `Tests/Ao3ShipIndexScraperTests.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter ~PagesFetched` → 5 passed (**0 before**, by design); `dotnet test` →
  781 (776 before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings
  only. No live check: backend-only, and the seam the spec names for this is the controller/unit
  tests.
- commit: (recorded next iteration)
- next: **T45 is next in plain file order** and is the incremental twin of what T40 just decided for
  the backfill — a pass that cannot get past page 1 with no bound on how long it keeps asking. The
  shape of the answer here (find the fact the guard actually wants, give it its own name, do not
  re-purpose a counter that means something else) is the one to reach for. **T15 is down to two
  blockers, T46 and T52**, and nothing else stands between it and the rest of the plan.
- **The trap T38 wrote down was real, and the mutation proves it.** Reverting the guard to
  `!askedStaleCursor && firstPage is null` reds `Gives_up_on_a_backfill_that_spends_run_after_run_on_
  a_cursor_nothing_answers` and the new stalled-run test and nothing else — the cursor halves
  10 → 5 → 2 → 1, `CursorMayBeStale` requires `page > 1` so no retreat can run at page 1,
  `askedStaleCursor` goes false for ever, the streak freezes at 3 and `Failed` is unreachable. Two
  prior readers (T38 and its reviewer) derived this from the code without running it; running it
  cost one `sed` and confirmed it exactly. **Where a previous iteration hands over a predicted test
  failure, spend the two minutes to reproduce it before designing around it** — it is the cheapest
  possible confirmation that the design constraint is real rather than remembered.
- **Three mutations, each red in a different place.** (1) Guard deleted entirely → only
  `Counts_no_stalled_run_when_a_refused_status_left_PagesFetched_at_zero` reds, so the "AO3 told us
  nothing" half is pinned. (2) Guard back to `firstPage is null` → the two tests above red, so the
  "AO3 told us something unusable" half is pinned. (3) The four counter lines back above the break →
  four of the five new tests red. The two halves of the guard are therefore held by different tests,
  which is what makes the pair worth having rather than one test asserting both.
- **`delivers` was the contract again, and this time it cost something worth naming.** "advances no
  counter" covers `parseWarnings`, so it moved with the other three — but it is also the one number
  that tells "the markup changed and the blurbs are unreadable" apart from "the page is empty", and
  dropping it silently would have made the run history worse at the exact failure it exists for. The
  resolution was not to keep the counter but to move the fact to where it is still true: the error
  log line already fires for that page, so it now carries `{Warnings}`. **A counter you are about to
  stop recording is worth one look at what reads it before it goes.**
- **A helper the parser will not select produces no warning.** The test needed a page that is
  unreadable *and* carries parse warnings, and the obvious `<li class="blurb">` with no id produces
  neither: `SelectBlurbs` filters on `li.blurb` whose `Id` starts with `work_`, so an id-less blurb
  is never selected and never counted. `Nameless()` emits `id="work_"` — selected, then unnameable,
  which is the shape that increments `warnings`. Worth knowing before writing any future test about
  parse warnings.
- **The review died on the monthly spend limit for the third time on this branch** (T14, T35, now
  T40; it resets 20:30 America/Chicago). Reviewed by reading instead — the diff is 88 lines of
  production code, most of it comment. One thing that pass changed: `FinishAsync`'s call site had
  `askedStaleCursor: retreatedFrom is not null, pagesServed, ct`, a named argument followed by
  positional ones, which compiles only because the named one sits in its own position; `pagesServed:`
  is now named too. **Three lost reviews on one branch is a pattern, not an accident** — an iteration
  that wants one should check the reset time before launching.
- Filters checked to bite, per T22's lesson: `~PagesFetched` matched **0 before and 5 after**, which
  is what the task's own verification line warned about. The five are named individually rather than
  by class, because they belong in `Ao3ShipIndexScraperTests` beside the helpers they use — so a
  sixth test added to this rule has to carry `PagesFetched` in its own name or the filter will not
  see it.
- Leaked processes from earlier iterations, unchanged and none of them this task's: pid 1963836
  (`dotnet run`) and its child, and T19's chrome-headless-shell tree (1994238, 1996041). This task
  started none of its own — test-only, no live check. The systemd dev instance (pid 2033) was not
  touched.
