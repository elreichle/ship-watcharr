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
