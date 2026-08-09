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
- **Database**: PostgreSQL via EF Core/Npgsql. The provider is isolated to one `AddDbContext`
  call in `Program.cs`; swapping to SQL Server later is a config + package change, not a
  schema rewrite.
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
| DB provider | `AddDbContext<AppDbContext>` | `backend/Ao3Tracker.Api/Program.cs` |

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

## Local development (without Docker)

Prerequisites: .NET 10 SDK, Node 20+, a running PostgreSQL instance, and the `dotnet-ef` tool
(`dotnet tool install --global dotnet-ef`).

1. Create a database and update `backend/Ao3Tracker.Api/appsettings.Development.json` (or set
   `ConnectionStrings__Default` as an env var) if your local Postgres isn't at
   `Host=localhost;Port=5432;Database=ao3tracker;Username=ao3tracker;Password=changeme`.
2. Run the backend — migrations apply automatically on startup:
   ```
   cd backend/Ao3Tracker.Api
   dotnet run
   ```
   It listens on `http://localhost:5110` by default (see `Properties/launchSettings.json`).
3. In another terminal, run the frontend dev server (proxies `/api` to the backend):
   ```
   cd frontend
   npm install
   npm run dev
   ```
   Open the URL Vite prints (typically `http://localhost:5173`).

To add a new migration after changing entities/`AppDbContext`:
```
cd backend/Ao3Tracker.Api
dotnet ef migrations add <Name> -o Data/Migrations
```

## Running via Docker Compose

```
cp .env.example .env
# edit .env — set POSTGRES_PASSWORD and AO3_USER_AGENT at minimum
docker compose up --build
```

The app is published at `http://localhost:8080` (or `$APP_PORT`). Postgres data and the Data
Protection key ring persist in named volumes (`db-data`, `dataprotection-keys`) — **do not
delete the `dataprotection-keys` volume**, or every stored AO3 credential becomes
unrecoverable and users will need to re-enter theirs.

### Environment variables

| Variable | Purpose |
|---|---|
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | Postgres container credentials; also used to build the app's connection string. |
| `AO3_USER_AGENT` | Sent as the scraper's User-Agent. Required — identify your instance and a contact method. |
| `AO3_MIN_DELAY` | Minimum spacing between scrape requests (`HH:MM:SS`), default `00:00:05`. |
| `APP_PORT` | Host port the app is published on, default `8080`. |

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

This was built without a running Postgres or Docker daemon available in the environment that
generated it. Verified: the backend builds cleanly and the initial EF Core migration was
generated successfully; the frontend builds and lints cleanly. **Not yet verified**: an actual
`docker compose up` run, migrations applying against a live Postgres, and the full
register → configure AO3 credential → create scrape job → see scraped data flow in a browser.
Do that verification pass before relying on this beyond local exploration.
