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
