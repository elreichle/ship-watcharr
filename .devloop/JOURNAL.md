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
