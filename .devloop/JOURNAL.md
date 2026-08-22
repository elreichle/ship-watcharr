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
- commit: 5a429f7 "Stop asking AO3 again for a page it has already refused"
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
