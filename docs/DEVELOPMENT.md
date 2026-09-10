# Developing Ship Watcharr

This is the technical companion to the [README](../README.md): how the app is put together, how
to run it outside Docker, and what to keep in mind when changing it. Code identifiers are quoted
as they are in the source, including the older `Scrape*`/`Ao3*` names that predate the current
wording.

## Architecture

- **Backend**: ASP.NET Core Web API (.NET 10), serving the built React app as static files. One
  process, one container.
- **Frontend**: React + TypeScript, built with Vite, under `frontend/`.
- **Database**: SQLite by default via EF Core, one file under the data directory. PostgreSQL is
  opt-in, configured through the admin **Database** page or `docker-compose.postgres.yml`. See
  [Two database providers](#two-database-providers).
- **Background work**: an in-process `BackgroundService` (`ScrapeWorker`) polls due `ScrapeJob`s
  every minute and runs them through a pluggable `IAo3Scraper`. A second worker,
  `ShipVerificationWorker`, confirms newly followed tags against the archive.
- **Auth**, two independent concerns:
  1. *Dashboard login*: cookie-based ASP.NET Core Identity, multi-user, with `IsAdmin` on
     `ApplicationUser`. The first registered account gets it automatically.
  2. *The AO3 login*: one username/password for the whole deployment (`Ao3InstanceCredential`, a
     single row held to one by a check constraint), saved by an admin under **System**, encrypted
     at rest with the Data Protection API, and never returned to the client after saving. The
     session cookie AO3 issues after login is cached beside it as a disposable cache; the password
     is the durable source of truth.

### Seams for a second source

Scheduling, budgets, persistence and the request gate are site-agnostic already. Adding another
site means implementing these alongside the AO3 versions. The interface names still carry `Ao3`,
which is honest about what exists today; renaming them is worth doing when a second source
actually lands.

| Seam | Interface | Where |
|---|---|---|
| Source implementation | `IAo3Scraper` | `backend/Ao3Tracker.Api/Services/Scraping/IAo3Scraper.cs` |
| Tag existence / synonyms | `IShipVerifier` | `backend/Ao3Tracker.Api/Services/Scraping/ShipVerifier.cs` |
| Outbound HTTP | `IRateLimitedHttpClient` | `backend/Ao3Tracker.Api/Services/Scraping/IRateLimitedHttpClient.cs` |
| AO3 credential storage | `IAo3InstanceCredentialStore` | `backend/Ao3Tracker.Api/Services/Credentials/IAo3InstanceCredentialStore.cs` |
| DB provider selection | `Database:Provider` branch | `backend/Ao3Tracker.Api/Program.cs` |

## Talking to the archive

AO3 is volunteer-run infrastructure. Every outbound request **must** go through
`IRateLimitedHttpClient`, never a raw `HttpClient`. That wrapper is what keeps the whole system
polite, and it is not optional:

- Serialises every outbound request through a single gate. However many users or jobs are
  active, requests never leave closer together than the configured spacing.
- Spaces requests by a **random 5–8 s** (`Ao3HttpClient:MinDelayBetweenRequests` /
  `MaxDelayBetweenRequests`). Randomising raises the *average* delay above the floor and stops
  several instances settling into lockstep. A `Max` below `Min` clamps up to `Min`, so a
  misconfiguration can only slow things down.
- Retries `429`/`5xx` with exponential backoff (jittered ±20%), honouring `Retry-After` verbatim
  when present.
- Treats a `429` as addressed to the whole instance, because that is how AO3 sends it: its
  `Retry-After` counts down to one deadline per penalty window. The gate holds every outbound
  request from every worker until that deadline (capped at `MaxThrottleHold`, default 1 h), and a
  run stopped by one is put back just past it rather than a full interval later. A single request
  waits out an ask of up to `MaxRetryAfter` (default 15 min) itself; a longer ask fails the
  request and leaves the hold to the gate.
- Spaces every redirect hop like any other request. A synonym tag's `302` is a second request
  AO3 has to field.
- Serves waiters at the gate in priority order: downloads and ship verification (somebody is
  waiting), then scheduled ship checks, then work detail pages. Priority changes who goes next,
  never how far apart.
- Spreads each job's next run so followed ships stay spread across the interval instead of
  converging on one tick.
- Stretches a quiet ship's interval: after two checks in a row found nothing, the wait doubles per
  further quiet check up to 4× (a day, at the default 6 h), and snaps back the moment a check
  finds anything.
- Stops a monthly full pass on page 1 when the tag's own work count matches what the library
  holds under the ship. The full pass is the only one allowed to conclude a work has *left* a tag.
- Runs a full pass for one ship ahead of the schedule only when an admin queues it from the Ships
  page, and then at that ship's next check rather than straight away.
- Re-reads a work's own page after a revision no more than once a week.
- Accepts compressed responses.
- Aborts a run after `MaxConsecutiveFailures` (default 3), and caps each run at
  `MaxRequestsPerRun` (default 500) requests and `MaxRunDuration` (default 2 h).
- Caches successful responses for `Ao3HttpClient:CacheDuration` (default 15 min). Cache hits do
  not count against the per-run budget.

### How an instance identifies itself

Every request carries a `User-Agent` built from three parts:

```
ShipWatcharr/0.1 (+contact: you@example.com; instance/a3f9c2)
\_____________/    \______________________/  \_____________/
  the software        who runs this copy       which copy
```

- **Product token**: constant and public, not editable. This is what lets AO3 recognise the
  tool's traffic as a known client.
- **Operator contact**: how AO3 reaches whoever runs this deployment. Falls back to the first
  admin's optional email (**Settings → Account**); set explicitly under **System** or via
  `Ao3HttpClient:OperatorContact`. Deliberately per-deployment: the project's author must never
  be the contact for someone else's instance.
- **Instance id**: 3 random bytes generated on first run into the data directory. Not derived
  from hostname or MAC, so it reveals nothing about the operator's environment.

**With no usable contact, outbound requests are disabled.** The app still boots and serves its
UI, but the worker refuses to make requests and logs why. This is re-checked every poll, so a
fresh install starts working as soon as a contact exists, without a restart.

### The AO3 login is instance-level

There is one AO3 login per deployment, not one per user. Work metadata is shared by everyone on
the instance, so a per-user credential has no answer for whose session a shared check should sign
in with. An admin saves it under **System**; it is stored as a single `Ao3InstanceCredential`
row. Nothing reads it back out: every response from `/api/admin/scraping/ao3-credential` is
status only, so a stolen dashboard session cannot exfiltrate the login.

**With no login stored, due jobs are held**, left unrun rather than failed, and the Ships page
says so. Like the contact gate, this is re-checked every poll. The two gates are separate
concerns: the contact is how requests are *attributed*, the login is what they are *authorised*
as.

**A session lasts as long as AO3's own cookies say.** AO3 is served through Cloudflare, which sets
a bot cookie that expires after 30 minutes. The cookies Cloudflare documents as its own are left out
of the stored session, so they neither date it nor count as proof that a login worked. Before that,
every run longer than half an hour finished logged out.

**Works for registered users only need a logged-in walk.** AO3 leaves them out of any listing it
serves logged out, and the pass for new works only reads the newest end of a tag. So
`Ship.WholeListingReadLoggedInAt` records when a walk of the whole listing last finished with every
page read logged in, and the Ships page shows it. A backfill that read any page logged out does not
earn it; a full pass that did is abandoned instead.

**The app never writes to AO3.** It will not leave kudos, comments or bookmarks, subscribe to
anything, or post, even though it holds a session that could. Keep it that way.

## Data model

The model splits into **global** data, stored once and shared by everyone, and **per-user** data
keyed by `UserId`. That split is what stops two users following the same ship from producing
duplicate rows or duplicate requests.

Global (from AO3):

- `Works`: one row per AO3 work id, used directly as the primary key. Full blurb metadata plus
  `FirstSeenAt` / `LastSeenAt` / `LastScrapedAt`.
- `Tags` / `WorkTags`: fandoms, relationships, characters, freeforms, warnings.
- `Ao3Pseuds` / `WorkAuthors`: creators, in byline order.
- `Ao3Series` / `WorkSeries`: series membership and part number.
- `Ships`: a followed relationship tag, and where all sync state lives: incremental watermark,
  backfill cursor, full-pass timestamps, and when the whole listing was last read logged in.
- `ShipWorks`: "this work appeared in this ship's listing". Separate from tags because AO3 tag
  synonyms mean a work returned by the canonical tag may not carry it in its own blurb.
  `MissingSinceAt` is when a full pass first walked the whole listing without seeing it.
- `Ao3InstanceCredentials`: the one AO3 login, single row by check constraint.
  `EncryptedPassword` and `EncryptedSessionCookie` are Data-Protection-encrypted.
- `WorkDownloadFiles`: downloaded files on disk, keyed by (work, format, work version).

Per-user:

- `Users` / `AspNetUsers`: ASP.NET Identity, plus `IsAdmin` and reader preferences
  (`AutoDownloadFavorites`).
- `WatchedShips`: a user's subscription to a `Ship`. Owns no sync state, so adding or removing a
  follower never affects what has been fetched.
- `UserWorkStates`: reading status, half-star rating (1–10, check-constrained), note, and
  `FavoritedAt`. Kept strictly apart from `Works` so metadata updates never touch it.
- `Downloads`: a user's request for a file, pointing at a shared `WorkDownloadFile`.
- `Notifications`: one per follower per work a followed ship gained, with the read timestamp.
- `SavedWorkFilters` / `SavedWorkFilterTags` / `SavedWorkFilterAuthors`: a named, reusable set of
  criteria. Every criterion is its own typed column, because each maps 1:1 onto a column of
  `Works` the query compares against, which is what lets the same predicates serve both the list
  and the match count (`backend/Ao3Tracker.Api/Data/WorkQueries.cs`).

Scheduling:

- `ScrapeJobs`: schedule config, scoped to a **ship**, not a user.
- `ScrapeRuns`: one row per execution attempt, with pages/requests/works counters, stop reason,
  and a heartbeat so a crashed run is distinguishable from a slow one.

### Tags are verified after the fact

A tag is accepted on trust and checked against AO3 a moment later by `ShipVerificationWorker`,
so following never blocks on the shared request gate and works while the archive is unreachable.
`POST /api/ships` returns `verificationState: "Pending"` and the Ships view polls until it
settles:

- **Verified**: AO3 served the tag's works index. The numeric tag id is harvested from the page's
  feed link where possible, since an id survives a rename and a name does not.
- **NotFoundOnAo3**: AO3 returned 404. Terminal; the ship's schedule is switched off.
- Still **Pending**: nothing settled (archive down, no contact configured). Retried with a
  doubling backoff capped at six hours and never given up on.

Synonyms are folded into their canonical tag: AO3 answers a synonym with a redirect, the ship is
renamed to the canonical tag or merged into an existing one (followers move across, `ShipWork`
rows move without duplicating, the redundant schedule is deleted). `WatchedShip.RequestedTagName`
records what the user originally typed.

## Frontend notes

- **Navigation** is a collapsible left sidebar, Sonarr-style: the library under **Dashboard**,
  per-user preferences under **Settings**, instance administration under **System**. It collapses
  to an icon rail, and becomes an overlay drawer below 700 px.
- **Saved filters** are applied by passing `savedFilterId` to `GET /api/works`; an unqualified
  request picks up the default set, which `useDefaultFilter=false` opts out of. The filter
  editor's dropdowns come from `GET /api/lookups/*`.
- **A broken page never blanks the app.** `ErrorBoundary`
  (`frontend/src/components/ErrorBoundary.tsx`) wraps the routed page inside `AppLayout`, keyed
  on the path, and a second one wraps the whole app. `frontend/src/api/client.ts` checks the
  shape of responses whose arrays the UI indexes into, so a stale backend fails as an ordinary
  request error rather than a white screen.
- **Themes.** Every pixel is painted from Obsidian's CSS variables. Obsidian's defaults ship in
  `frontend/src/theme/obsidian-defaults.css`, the built-in palettes as `data-theme` overrides in
  `presets.css`, both inside `@layer obsidian-defaults`; component rules live in `@layer app`;
  a user's pasted theme is injected unlayered and therefore wins regardless of specificity.
  `theme-dark` / `theme-light` go on `<body>` and are mirrored onto `<html>`. The palette and any
  pasted CSS live in `localStorage`, applied by an inline script in `index.html` before React
  mounts. `?safemode` skips a pasted theme. Every built-in text/background pair was checked
  against WCAG 4.5:1.

## Two database providers

EF Core migrations are not portable across providers from a single history, so there are two
provider-specific `DbContext` subclasses (`SqliteAppDbContext`, `PostgresAppDbContext`) sharing
one entity model under `backend/Ao3Tracker.Api/Data/`. Everything else depends on the abstract
`AppDbContext`.

**Precedence.** `Database:Provider` and `Database:PostgresConnectionString` can come from
`appsettings.json`, environment variables (`Database__Provider`,
`Database__PostgresConnectionString`), or the `settings.json` the admin UI writes to the data
directory (`/app/data/settings.json` in Docker, `backend/Ao3Tracker.Api/appdata/settings.json`
locally). `settings.json` is read last and wins once it exists. To reset, stop the app and delete
just that file.

**Switching does not migrate data.** The newly selected database starts empty. The admin
endpoint validates that it can connect before persisting or restarting anything.

**Timestamps are `DateTime` (UTC), not `DateTimeOffset`.** The SQLite provider can only translate
equality on `DateTimeOffset` columns; ordering and range comparisons throw at query time.
`UtcDateTimeConverter` (`Data/UtcDateTimeConverter.cs`) forces `DateTimeKind.Utc` on every value
read back, so JSON still carries a `Z`. New timestamp properties should be `DateTime`.

## Local development

Prerequisites: .NET 10 SDK, Node 20+, and `dotnet-ef` (`dotnet tool install --global dotnet-ef`).
No database server needed.

1. Backend. Migrations apply on startup and the SQLite file is created under
   `backend/Ao3Tracker.Api/appdata/`:
   ```
   cd backend/Ao3Tracker.Api
   dotnet run
   ```
   It listens on `http://localhost:5110` (see `Properties/launchSettings.json`).
2. Frontend dev server, which proxies `/api` to the backend:
   ```
   cd frontend
   npm install
   npm run dev
   ```

To run against PostgreSQL locally, set before `dotnet run`:
```
Database__Provider=Postgres
Database__PostgresConnectionString="Host=localhost;Database=shipwatcharr;Username=shipwatcharr;Password=..."
```

To point the app at a local stub instead of the real archive, set `Ao3HttpClient__BaseUrl`. No
test in this repository has ever made a request to AO3, and none should.

### Migrations

After changing entities or `AppDbContext`, generate a migration for **both** providers:
```
cd backend/Ao3Tracker.Api
dotnet ef migrations add <Name> --context SqliteAppDbContext -o Data/Migrations/Sqlite
dotnet ef migrations add <Name> --context PostgresAppDbContext -o Data/Migrations/Postgres
```

### Tests

`backend/Ao3Tracker.Tests` runs with `dotnet test`. Parsers are tested against captured AO3
markup in `Fixtures/` (with the capturing account's identity replaced by placeholders); the
request gate, budget and backoff have unit tests; the library endpoints, tag verification, saved
filters and the account endpoints run against a real SQLite database rather than a mocked
context. Tag verification's redirect and 404 paths are exercised against a local stub.

### Docker

`docker compose up --build` builds the frontend, publishes the backend with the built frontend as
its `wwwroot`, and runs as the image's non-root `app` user (uid 1654). `/app/data` holds the
SQLite file, the Data Protection key ring, the instance id and `settings.json`; it is the
`app-data` named volume in compose. `.dockerignore` keeps a local `appdata/` out of the build
context.

The container also reads `Ao3HttpClient:MaxRetries`, `Ao3HttpClient:InitialBackoff` and
`Ao3HttpClient:CacheDuration`, overridable with the usual double-underscore environment form.

## Adding another source

`Ao3ShipIndexScraper` is the worked example. It claims `Ao3ScraperKeys.ShipIndex` and splits into
three pieces on purpose: `Ao3BlurbParser` (HTML in, records out; no HTTP, no database, no clock),
`WorkIngestor` (records in, rows out), and the walker itself, which owns only the walking and the
stopping rules. Keep that split; it is what makes the parser testable against captured markup.

1. Implement `IAo3Scraper` in `Services/Scraping/`, using `IRateLimitedHttpClient` for all HTTP.
2. Consult `ScrapeContext.Budget` before every fetch and record the outcome (`RecordSuccess` /
   `RecordFailure` / `RecordCacheHit`). That is what enforces the per-run caps and the breaker.
3. Register it: `builder.Services.AddScoped<IAo3Scraper, YourScraper>();` in `Program.cs`.
   `ScraperRegistry` picks it up automatically.
4. A `ScrapeJob` is created per ship when someone follows the tag, keyed by
   `Ao3ScraperKeys.ShipIndex` (`"ao3-ship-index"`). A key nothing claims makes the worker log an
   "unknown scraper key" warning per due tick and reschedule. Registered keys are exposed at
   `GET /api/scrape-jobs/scrapers`.

## Known issue

`Microsoft.EntityFrameworkCore.Sqlite` pulls in `SQLitePCLRaw.lib.e_sqlite3`, which has an open
NuGet advisory ([GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)) with no
patched version at the time of writing. This affects essentially every .NET project using SQLite.
