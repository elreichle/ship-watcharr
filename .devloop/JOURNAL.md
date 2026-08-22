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
