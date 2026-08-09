# AO3 Tracker

A self-hosted monitoring dashboard that scrapes [Archive of Our Own](https://archiveofourown.org)
data on behalf of its users. Multiple people can register accounts on one instance; each user
can separately connect their own AO3 login so scrapes run as them.

**Status: scaffold.** The scraping pipeline is fully wired end-to-end, but the only scraper
implemented so far is a placeholder that fetches `example.com`'s title — real AO3 scraping
logic hasn't been written yet. See [Extending the scaffold](#extending-the-scaffold).

## Architecture

- **Backend**: ASP.NET Core Web API (.NET 10), serving the built React app as static files —
  one process, one container.
- **Frontend**: React + TypeScript, built with Vite.
- **Database**: SQLite by default via EF Core — zero-config, one file, works out of the box
  for a self-hosted install. PostgreSQL is opt-in for people who want it, configured either
  through the admin **Database** settings page or `docker-compose.postgres.yml`. See
  [Database: SQLite by default, PostgreSQL if you want it](#database-sqlite-by-default-postgresql-if-you-want-it).
- **Background scraping**: an in-process `BackgroundService` (`ScrapeWorker`) polls due
  `ScrapeJob`s every minute and runs them through a pluggable `IAo3Scraper`.
- **Auth**: two independent concerns —
  1. *Dashboard login* — cookie-based ASP.NET Core Identity, multi-user, with one user
     flaggable as admin (`ApplicationUser.IsAdmin`; the first registered account gets it
     automatically).
  2. *AO3 credentials* — each user's own AO3 username/password, stored per-user, encrypted at
     rest with the Data Protection API, and never returned to the client after saving. The
     encrypted session cookie AO3 issues after login is stored separately from the password,
     so re-scraping doesn't require re-entering it every run.

### Key seams for future work

| Seam | Interface | Where |
|---|---|---|
| Scraper implementation | `IAo3Scraper` | `backend/Ao3Tracker.Api/Services/Scraping/IAo3Scraper.cs` |
| Scraper HTTP access | `IRateLimitedHttpClient` | `backend/Ao3Tracker.Api/Services/Scraping/IRateLimitedHttpClient.cs` |
| AO3 credential storage | `IAo3CredentialStore` | `backend/Ao3Tracker.Api/Services/Credentials/IAo3CredentialStore.cs` |
| DB provider selection | `Database:Provider` branch | `backend/Ao3Tracker.Api/Program.cs` |

## Respectful scraping — read this before adding real scrapers

AO3 is volunteer-run infrastructure. Every scraper **must** go through
`IRateLimitedHttpClient`, never a raw `HttpClient`. That wrapper is the one thing that keeps
the whole system polite, and it is not optional:

- Serializes every outbound request through a single gate — no matter how many users/jobs run
  concurrently, requests never leave faster than `Ao3HttpClient:MinDelayBetweenRequests` apart
  (default 5s).
- Retries `429`/`5xx` with exponential backoff, honoring `Retry-After` when present.
- Caches successful responses for `Ao3HttpClient:CacheDuration` (default 15 min) so unchanged
  pages aren't re-fetched.
- Sends a descriptive `User-Agent` (`Ao3HttpClient:UserAgent`) — set this to something that
  actually identifies your instance and a way to reach you before pointing it at real AO3 pages.

## Data model

- `Users` / `AspNetUsers` — ASP.NET Identity, plus `IsAdmin`.
- `Ao3Credentials` — one row per user; `EncryptedPassword` + `EncryptedSessionCookie` are
  Data-Protection-encrypted, never plaintext, never serialized back to the client.
- `ScrapeJobs` — per-user schedule config (`ScraperKey`, `Interval`, `IsEnabled`, `NextRunAt`).
- `ScrapeRuns` — one row per execution attempt (status, timing, error message).
- `ScrapedItems` — generic `{SourceUrl, Title, PayloadJson}` rows produced by a run. This is
  intentionally loose and will be redesigned into target-specific tables (works, bookmarks,
  stats, ...) once real scrape targets are defined.

## Database: SQLite by default, PostgreSQL if you want it

SQLite is the zero-config default so the app is installable and usable without setting up a
separate database server — everything lives in one file under the data directory. PostgreSQL
is there for anyone who wants it, but it's opt-in, not required.

**Switching to PostgreSQL through the app itself:** log in as an admin (the first account
registered on an instance becomes admin automatically), go to **Database** in the nav, choose
PostgreSQL, and paste a connection string. The backend validates it can actually connect
*before* saving anything — a bad connection string is rejected immediately with an error,
rather than silently bricking the instance. Once saved, the app restarts to apply it (under
`docker compose`'s `restart: unless-stopped`, that's automatic; for a bare `dotnet run` you
restart it yourself).

**Important — this does *not* migrate data.** Switching providers starts the newly-selected
database empty. There's no live copy from SQLite to PostgreSQL (or back). Pick a database
before you have real data if you can, or plan on exporting/re-entering things manually if you
switch later.

**Precedence, if you use both environment variables and the admin UI:** `Database:Provider`
and `Database:PostgresConnectionString` can be set via `appsettings.json`, environment
variables (`Database__Provider`, `Database__PostgresConnectionString`), or the persisted
`data/settings.json` file the admin UI writes to. Whichever settings.json says wins, once it
exists — it's read after environment variables/command-line args, so it's the actual source of
truth from that point on. To fully reset, stop the app and delete `data/settings.json` (not
the whole `data/` directory, unless you also want to lose the SQLite db and Data Protection
keys).

**Why not just support one database?** EF Core migrations aren't portable across providers
from a single migration history, so this uses two provider-specific `DbContext` subclasses
(`SqliteAppDbContext`, `PostgresAppDbContext`) sharing one entity model — see
`backend/Ao3Tracker.Api/Data/`. Everything else in the app depends on the shared abstract
`AppDbContext` base and doesn't know or care which provider is actually active.

**A design consequence worth knowing if you touch the entity model:** every timestamp in the
domain model is `DateTime` (always UTC), not `DateTimeOffset`. The SQLite provider can only
translate *equality* checks on `DateTimeOffset` columns — ordering and range comparisons
(`<`, `<=`, `>`, `>=`, including how the scheduler finds due jobs) throw at query time. `DateTime`
doesn't have this limitation. A `UtcDateTimeConverter` (`Data/UtcDateTimeConverter.cs`) forces
`DateTimeKind.Utc` on every value read back from either provider, so timestamps still
serialize to JSON with a `Z` and the frontend parses them correctly. If you add a new
timestamp property, use `DateTime`, not `DateTimeOffset`.

## Local development (without Docker)

Prerequisites: .NET 10 SDK, Node 20+, and the `dotnet-ef` tool
(`dotnet tool install --global dotnet-ef`). No database server needed — SQLite is the default.

1. Run the backend — migrations apply automatically on startup, and the SQLite db file is
   created under `backend/Ao3Tracker.Api/data/` on first run:
   ```
   cd backend/Ao3Tracker.Api
   dotnet run
   ```
   It listens on `http://localhost:5110` by default (see `Properties/launchSettings.json`).
2. In another terminal, run the frontend dev server (proxies `/api` to the backend):
   ```
   cd frontend
   npm install
   npm run dev
   ```
   Open the URL Vite prints (typically `http://localhost:5173`).

To test against PostgreSQL locally instead, set before `dotnet run`:
```
Database__Provider=Postgres
Database__PostgresConnectionString="Host=localhost;Database=ao3tracker;Username=ao3tracker;Password=..."
```

To add a new migration after changing entities/`AppDbContext`, generate it for **both**
providers (see "Why not just support one database?" above):
```
cd backend/Ao3Tracker.Api
dotnet ef migrations add <Name> --context SqliteAppDbContext -o Data/Migrations/Sqlite
dotnet ef migrations add <Name> --context PostgresAppDbContext -o Data/Migrations/Postgres
```

## Running via Docker Compose

```
cp .env.example .env
# edit .env — set AO3_USER_AGENT at minimum
docker compose up --build
```

The app is published at `http://localhost:8080` (or `$APP_PORT`), running on SQLite with no
further setup. Everything the instance needs to persist — the SQLite db, the Data Protection
key ring, and admin-configured settings — lives in the `app-data` named volume. **Do not
delete it**, or every stored AO3 credential becomes unrecoverable and users will need to
re-enter theirs.

To pre-configure PostgreSQL from first boot instead of using the admin UI (for people who
know they want Postgres and don't want to click through settings), add the override file and
also set the `POSTGRES_*` variables in `.env`:
```
docker compose -f docker-compose.yml -f docker-compose.postgres.yml up --build
```

### Environment variables

| Variable | Purpose |
|---|---|
| `AO3_USER_AGENT` | Sent as the scraper's User-Agent. Required — identify your instance and a contact method. |
| `AO3_MIN_DELAY` | Minimum spacing between scrape requests (`HH:MM:SS`), default `00:00:05`. |
| `APP_PORT` | Host port the app is published on, default `8080`. |
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | Only used with `docker-compose.postgres.yml`. |

The container also reads (with defaults baked into `appsettings.json`, overridable via the
same double-underscore env var convention EF/ASP.NET Core uses, e.g. `Ao3HttpClient__MaxRetries`):
`Ao3HttpClient:MaxRetries`, `Ao3HttpClient:InitialBackoff`, `Ao3HttpClient:CacheDuration`.

## Extending the scaffold

To add a real AO3 scraper:
1. Implement `IAo3Scraper` (see `PlaceholderScraper` for the shape) in
   `Services/Scraping/`, using `IRateLimitedHttpClient` for all HTTP access.
2. Register it: `builder.Services.AddScoped<IAo3Scraper, YourScraper>();` in `Program.cs`.
   `ScraperRegistry` picks it up automatically — no scheduling/persistence code changes needed.
3. Users create a `ScrapeJob` with `ScraperKey` matching your scraper's `Key`, from the
   dashboard's "New scrape job" form (the scraper key list is populated from
   `GET /api/scrape-jobs/scrapers`).
4. Once real scrape targets are defined, replace the generic `ScrapedItem` table with
   target-specific entities/tables as needed.

## Note on this scaffold's testing

This was built without a Docker daemon available in the environment that generated it, so
`docker compose up` itself has not been run — review the Dockerfile/compose files before
relying on them. Everything else has been exercised directly against a running instance
(`dotnet run` with SQLite), not just built: register → login → save AO3 credentials → create
a scrape job → the background worker picks it up within a minute → placeholder scraper fetches
`example.com` → a `ScrapedItem` appears via the API, with correctly UTC-tagged timestamps
throughout. The admin database-settings endpoint was verified too, including that it rejects
an unreachable PostgreSQL connection string with a 400 *before* persisting or restarting
anything (a real Postgres switch-over end-to-end was not exercised, to avoid touching
infrastructure outside this project without asking first).

One accepted, currently-unpatched issue: `Microsoft.EntityFrameworkCore.Sqlite` pulls in
`SQLitePCLRaw.lib.e_sqlite3`, which has an open NuGet advisory
([GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)) with no patched
version available at time of writing — this affects essentially every .NET project using
SQLite today, not something specific to this scaffold. Worth revisiting when a fix ships.
