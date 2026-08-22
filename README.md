# Ship Watcharr

A self-hosted dashboard for tracking fanwork about the ships you follow, scraping on behalf of
its users. Multiple people can register accounts on one instance; each user separately connects
their own account on the site being scraped, so scrapes run as them.

[Archive of Our Own](https://archiveofourown.org) is the first site supported, and the only one
today — but the seams are deliberately per-site rather than AO3-shaped, so a second source is an
`IAo3Scraper` sibling and a credential store, not a rewrite. See
[Key seams for future work](#key-seams-for-future-work).

**Status: early, but it ingests.** The scraping pipeline is wired end-to-end — scheduling, budgets,
rate limiting, persistence — and the AO3 ship-index scraper now runs under it, so following a tag
really does fill a library. Two passes exist: an incremental one bounded by the ship's watermark,
and a resumable backfill into the back catalogue. A full sweep — the only pass allowed to conclude a
work has *left* a tag — is still to come, so works that lose their relationship tag currently stay
listed. See [Extending the scaffold](#extending-the-scaffold).

## What this is for

Following a ship on AO3 means going back to the same tag index and re-reading it for anything new
— and doing that again for every other ship you follow. This does the checking for you: you name
the relationship tags you care about, and it keeps a local, filterable library of the works under
them.

Two things shape the design more than anything else:

- **Scraped data is shared; your data is yours.** Work metadata is stored once per AO3 work id
  and shared by everyone on the instance, so ten people watching the same ship cause one set of
  fetches, not ten. Reading status, ratings, notes and saved filters hang off your user id and
  are never touched by a re-scrape.
- **The archive is somebody else's volunteer-run infrastructure.** Every request goes through one
  global rate gate, and every request says who is making it. That is a constraint the rest of the
  app is built around rather than a setting to be tuned away — see
  [Respectful scraping](#respectful-scraping--read-this-before-adding-real-scrapers).

It is meant to be self-hosted, by you, on your own hardware. That is why the AO3 operator contact
is per-deployment and absent from this repo: whoever runs a copy is the one AO3 should be able to
reach about it.

## Goals and non-goals

**What it does today.** Follow and unfollow relationship tags, with each one verified against AO3
after the fact and synonyms folded into their canonical tag. Browse a paginated, sortable library
of everything scraped for the ships you follow. Save named filter sets — AO3's filter sidebar,
kept and reusable — and mark one as the default. See the scrape schedule behind each ship.

**Planned.** In no particular order:

- **Per-work detail fetches** — the data a blurb doesn't carry, such as publication date and the
  complete tag list, fetched from the work's own page and tracked by `Work.DetailFetchedAt` /
  `Work.PublishedAt`.
- **A Statistics tab** — one view with two lenses over the same works: the corpus as it stands
  (a ship's size over time, rating mix, kudos and hits distributions, most prolific authors) with
  your own reading laid over it (how much of a ship you've read, your ratings against the
  archive's reception). The only planned item with no schema behind it yet.
- **Notifications** when a watched ship gains new or updated works — `WatchedShip.NotificationsEnabled`
  is the switch already modelled for it.
- **Downloads** — works saved as files under the data directory, modelled by `Download` (your
  request) over `WorkDownloadFile` (the shared file on disk, keyed by work, format and work
  version, so a re-download after an update doesn't overwrite the copy you have).
- **A second source beyond AO3.** The seams are already per-site rather than AO3-shaped; see
  [Key seams for future work](#key-seams-for-future-work).

Every entity named above already exists, with its migration applied — what's missing is the
scraping that would fill those tables and the UI over them. Statistics is the exception, and
starts from nothing.

**Non-goal: it never writes to AO3.** It reads. It will not leave kudos, comments or bookmarks,
subscribe to anything, or post on your behalf, even though it holds a logged-in session that
could do all four. The account you connect is for reading pages that require being logged in,
and nothing else.

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

Adding a second site means implementing these alongside the AO3 versions rather than replacing
them — scheduling, budgets, persistence and the rate limiter are all site-agnostic already. The
interface *names* still carry `Ao3`, which is honest about what exists today; renaming them to
something site-neutral is worth doing at the point a second scraper actually lands, not before.

| Seam | Interface | Where |
|---|---|---|
| Scraper implementation | `IAo3Scraper` | `backend/Ao3Tracker.Api/Services/Scraping/IAo3Scraper.cs` |
| Tag existence / synonyms | `IShipVerifier` | `backend/Ao3Tracker.Api/Services/Scraping/ShipVerifier.cs` |
| Scraper HTTP access | `IRateLimitedHttpClient` | `backend/Ao3Tracker.Api/Services/Scraping/IRateLimitedHttpClient.cs` |
| AO3 credential storage | `IAo3CredentialStore` | `backend/Ao3Tracker.Api/Services/Credentials/IAo3CredentialStore.cs` |
| DB provider selection | `Database:Provider` branch | `backend/Ao3Tracker.Api/Program.cs` |

## Respectful scraping — read this before adding real scrapers

AO3 is volunteer-run infrastructure. Every scraper **must** go through
`IRateLimitedHttpClient`, never a raw `HttpClient`. That wrapper is the one thing that keeps
the whole system polite, and it is not optional:

- Serializes every outbound request through a single gate — no matter how many users/jobs run
  concurrently, requests never leave faster than the configured spacing apart.
- Spaces requests by a **random 5–8s** (`Ao3HttpClient:MinDelayBetweenRequests` /
  `MaxDelayBetweenRequests`). Randomizing raises the *average* delay above the floor, so this
  makes the scraper slower, not stealthier; it also stops several instances settling into
  lockstep and delivering synchronized load spikes. A `Max` below `Min` clamps up to `Min`, so
  a misconfiguration can only ever slow things down.
- Retries `429`/`5xx` with exponential backoff (jittered by ±20%), honoring `Retry-After`
  verbatim when present — an explicit instruction is not something to guess on top of.
- Spreads each job's next run by ±10%, so jobs sharing an interval don't converge onto one tick.
- Aborts a run after `MaxConsecutiveFailures` (default 3) consecutive failures, and caps each run
  at `MaxRequestsPerRun` (default 500) requests and `MaxRunDuration` (default 2h).
- Caches successful responses for `Ao3HttpClient:CacheDuration` (default 15 min) so unchanged
  pages aren't re-fetched. Cache hits don't count against the per-run budget — they cost AO3
  nothing.

### How this instance identifies itself

Every request carries a `User-Agent` built from three parts:

```
ShipWatcharr/0.1 (+contact: you@example.com; instance/a3f9c2)
\_____________/    \______________________/  \_____________/
  the software        who runs this copy       which copy
```

- **Product token** — constant and public. This is what lets AO3 recognise the tool's traffic
  as a known, well-behaved client. Not editable.
- **Operator contact** — how AO3 reaches *whoever runs this deployment*. Falls back to the first
  admin's email, which is optional and saved under **Settings → Account** — registration itself
  only asks for a username. Set it explicitly under **System → Scraping**, or via
  `Ao3HttpClient:OperatorContact` in configuration. Deliberately per-deployment: the project's
  author must never be the contact for someone else's instance.
- **Instance id** — 3 random bytes generated on first run into the data directory. Lets AO3
  distinguish two deployments in their logs without either revealing who runs them. Not derived
  from hostname or MAC, precisely so it leaks nothing about the operator's environment.

**With no usable contact, scraping is disabled** — the app still boots and serves its UI (that's
where you go to fix it), but the worker refuses to make requests and logs why. This is checked
every poll rather than once at startup, so a fresh install starts scraping as soon as a contact
exists, without a restart. On a new instance that means saving one: either an email under
**Settings → Account**, or a contact under **System → Scraping**. Registering an admin is not
enough on its own, because sign-up never asks for an address.

## Data model

The model splits into **global** scraped data, stored once and shared by everyone, and
**per-user** data keyed by `UserId`. That split is what stops two users watching the same ship
from producing duplicate rows or, more importantly, duplicate fetches.

Global (scraped from AO3):

- `Works` — one row per AO3 work id, which is used directly as the primary key. Carries the full
  blurb metadata plus `FirstSeenAt` / `LastSeenAt` / `LastScrapedAt`.
- `Tags` / `WorkTags` — fandoms, relationships, characters, freeforms, warnings.
- `Ao3Pseuds` / `WorkAuthors` — creators, in byline order.
- `Ao3Series` / `WorkSeries` — series membership and part number.
- `Ships` — a tracked relationship tag, and **where all scrape state lives**: incremental
  watermark, backfill cursor, full-sweep timestamps.
- `ShipWorks` — "this work appeared in this ship's listing". Separate from tags because AO3 tag
  synonyms mean a work returned by the canonical tag may not carry it in its own blurb.
- `WorkDownloadFiles` — downloaded files on disk, keyed by (work, format, work version).

Per-user:

- `Users` / `AspNetUsers` — ASP.NET Identity, plus `IsAdmin`.
- `Ao3Credentials` — one row per user; `EncryptedPassword` + `EncryptedSessionCookie` are
  Data-Protection-encrypted, never plaintext, never serialized back to the client.
- `WatchedShips` — a user's subscription to a `Ship`. Owns no scrape state, so adding or
  removing a watcher never affects what has been scraped.
- `UserWorkStates` — reading status, half-star rating (1–10, check-constrained), free-text note.
  Kept strictly apart from `Works` so re-scrapes can overwrite metadata without touching it.
- `Downloads` — a user's request for a file, pointing at a shared `WorkDownloadFile`.
- `SavedWorkFilters` / `SavedWorkFilterTags` / `SavedWorkFilterAuthors` — a named, reusable set of
  criteria, with its included/excluded tags and included authors. Every criterion is its own typed
  column rather than a serialized blob, because each one maps 1:1 onto a column of `Works` the
  query compares against — which is what lets the same predicates serve both the list and the
  match count. The cost is a migration per new criterion, acceptable while the vocabulary being
  modelled is AO3's, and therefore fixed.

Scheduling:

- `ScrapeJobs` — schedule config, scoped to a **ship**, not a user: scraped data is shared, so a
  per-user job would mean N users watching one ship producing N identical scrapes.
- `ScrapeRuns` — one row per execution attempt, with pages/requests/works counters, stop reason,
  and a heartbeat so a crashed run is distinguishable from a slow one.

## Interface: Sonarr-style navigation, Obsidian-compatible themes

Navigation is a collapsible left sidebar, following Sonarr/Radarr's split — the library under
**Dashboard** (Works, Filters, Ships, Schedules), per-user preferences under **Settings** (Account,
Appearance), instance-wide administration under **System** (Scraping, Database). The hamburger
collapses it to a 48px icon rail where groups open as flyouts; below 700px it becomes an overlay
drawer. The choice is remembered per browser.

The four Dashboard views are one story told in four places:

| View | What it is | Endpoint |
|---|---|---|
| **Works** | Paginated, sortable list of everything scraped for the ships you follow | `GET /api/works` |
| **Filters** | Named, reusable sets of criteria — AO3's filter sidebar, saved | `GET`/`POST`/`PUT`/`DELETE /api/saved-filters` |
| **Ships** | Follow and unfollow relationship tags | `GET`/`POST`/`DELETE /api/ships` |
| **Schedules** | Read-only view of the scrape schedule behind each ship | `GET /api/scrape-jobs` |

Following a tag creates the shared `Ship` if this instance has never seen it, subscribes you, and
enables one `ScrapeJob` for it. Unfollowing removes *only* your subscription — the ship, its works
and its run history stay for whoever else is watching, and the schedule switches off only when the
last watcher leaves. On this build nothing is registered under the job's scraper key, so the Ships
view says so rather than leaving you with a permanently empty library and no explanation.

### Saved filters apply to the works list, and count against it

A saved set is applied by passing `savedFilterId` to `GET /api/works`; an unqualified request
picks up whichever set is marked default, which `useDefaultFilter=false` opts out of. Asking for
a set that isn't yours is a `404`, not an empty list.

Each set is listed with a `matchingWorkCount`, and that number and the list it links to are
produced by the same code — the `Where` and `OrderBy` clauses live in
`backend/Ao3Tracker.Api/Data/WorkQueries.cs` and are shared by both callers, because a count
saying "412 works" next to a list showing a different set is worse than showing no count at all.
A set naming a ship you have since unfollowed returns nothing rather than reaching outside your
library: the ship criterion intersects with your subscriptions instead of replacing them.

The filter editor's dropdowns come from `GET /api/lookups/*` — ratings, categories and warnings in
AO3's own wording, and only the languages actually present in your library, so the list can't
offer a language that would match nothing.

### Tags are verified after the fact, not before

A tag is accepted on trust and checked against AO3 a moment later, by `ShipVerificationWorker`.
Checking first would mean blocking the request on a fetch queued behind the shared 5–8s rate gate,
which has no latency anyone can promise, and would make following a tag impossible whenever AO3 is
unreachable. So `POST /api/ships` returns immediately with `verificationState: "Pending"`, and the
Ships view polls until it settles into one of:

- **Verified** — AO3 served the tag's works index. The numeric tag id is harvested from the page's
  feed link where possible, since an id survives AO3 renaming the tag and a name does not.
- **NotFoundOnAo3** — AO3 returned 404, so it is almost certainly a typo. Terminal, and the ship's
  schedule is switched off; following the same misspelling again will not turn it back on.
- Still **Pending** — the check settled nothing (AO3 down, no operator contact configured, a
  redirect somewhere unexpected). Retried with a doubling backoff capped at six hours, and
  deliberately **never** given up on: an archive being unreachable, for however long, is not
  evidence that a tag is missing.

**Synonyms are folded into their canonical tag.** AO3 answers a synonym by redirecting to the
canonical tag's index, so a check that lands somewhere other than where it was aimed has found one.
The synonym is then renamed to the canonical tag, or — if that tag is already tracked — merged into
it: watchers move across, `ShipWork` rows move without duplicating, and the redundant schedule is
deleted. Without this, `Bellarke` and `Bellamy Blake/Clarke Griffin` would be two ships walking
identical works, which is the duplicate fetching the shared-`Ship` design exists to prevent.

Whichever way it resolves, `WatchedShip.RequestedTagName` records what that user originally typed,
so the UI can say why the tag on screen is not the one they entered.

### A broken page never blanks the app

React unmounts the whole tree when a render throws and nothing catches it, so any single bad field
could leave the interface as an empty `<body>` — no message, no navigation, indistinguishable from
the server being down. Two things prevent that:

- **`ErrorBoundary`** (`frontend/src/components/ErrorBoundary.tsx`) wraps the routed page inside
  `AppLayout`, so a failed page keeps the sidebar and says what went wrong; a second one wraps the
  whole app for anything above the shell. The page-level boundary is keyed on the path, because a
  boundary holds its error until it remounts — without that, one broken page would keep showing its
  error wherever you navigated next.
- **Response shape checks** in `frontend/src/api/client.ts`. `request<T>` only *asserts* `T`;
  nothing verifies the server sent it. A backend running different code than the page — a stale dev
  process, a half-applied deploy — can answer `200` with a body that typechecks and is `undefined`
  where the UI expects an array. The endpoints whose arrays the UI indexes into are checked at
  runtime and fail as ordinary request errors, which every page already renders.

Note that neither one is licence to swallow a failure: a page that cannot load its data says so.
`WorksPage` in particular distinguishes "you follow no ships" from "the ship list didn't load",
because claiming the former sends someone off to re-add ships they already have.

### Bring your own theme

Every pixel of the UI is painted from **Obsidian's CSS variables** — `--background-primary`,
`--text-normal`, `--interactive-accent`, `--nav-item-color` and the rest of the ~400-name
vocabulary documented at [docs.obsidian.md](https://docs.obsidian.md/Reference/CSS+variables/CSS+variables).
Paste an Obsidian community theme's `theme.css` into **Settings → Appearance** and it restyles the
app. Three things make that work:

- **Obsidian's defaults ship with the app** (`frontend/src/theme/obsidian-defaults.css`). Themes
  only override the subset they care about — one that sets nothing but `--accent-h/s/l` is
  perfectly normal — so without a full default layer underneath, a partial theme would leave half
  the interface unpainted.
- **Cascade layers, not specificity.** Our defaults live in `@layer obsidian-defaults` and our
  component rules in `@layer app`; the user's CSS is injected *unlayered*, and unlayered rules
  outrank every layered one regardless of specificity. That's what lets an arbitrary theme win
  without us knowing which selectors it uses.
- **`theme-dark` / `theme-light` on `<body>`**, the same switch Obsidian themes are written
  against, mirrored onto `<html>` for themes that reach for `:root.theme-dark`.

What does *not* carry over is a theme's structural rules — its styling for Obsidian's editor,
panes and ribbon has nothing to match here. A theme reads as its palette and typography rather
than as Obsidian.

Themes are stored in `localStorage`, never sent to the server. That keeps the login page themed
and avoids a flash of unstyled content (an inline script in `index.html` applies the theme before
React mounts), at the cost of the theme not following you to another browser. If a theme leaves
the interface unusable, load any page with **`?safemode`** to skip it and clear it from Appearance.

Note that pasted CSS can request remote images and fonts, so a theme from an untrusted source can
signal its author when you open the page — the Appearance page says so too.

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
`settings.json` file the admin UI writes to the data directory (`/app/data/settings.json` in
Docker; `backend/Ao3Tracker.Api/appdata/settings.json` in local dev). Whichever settings.json
says wins, once it exists — it's read after environment variables/command-line args, so it's
the actual source of truth from that point on. To fully reset, stop the app and delete just
that file (not the whole data directory, unless you also want to lose the SQLite db and Data
Protection keys).

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
   created under `backend/Ao3Tracker.Api/appdata/` on first run:
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
Database__PostgresConnectionString="Host=localhost;Database=shipwatcharr;Username=shipwatcharr;Password=..."
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
# edit .env — set AO3_OPERATOR_CONTACT at minimum
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
| `AO3_OPERATOR_CONTACT` | Email or project URL AO3 can reach you at. Without it scraping stays disabled. Overridable later under System → Scraping. |
| `AO3_MIN_DELAY` | Lower bound on spacing between scrape requests (`HH:MM:SS`), default `00:00:05`. |
| `AO3_MAX_DELAY` | Upper bound; each delay is drawn at random from the range. Default `00:00:08`. |
| `APP_PORT` | Host port the app is published on, default `8080`. |
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` | Only used with `docker-compose.postgres.yml`. |

The container also reads (with defaults baked into `appsettings.json`, overridable via the
same double-underscore env var convention EF/ASP.NET Core uses, e.g. `Ao3HttpClient__MaxRetries`):
`Ao3HttpClient:MaxRetries`, `Ao3HttpClient:InitialBackoff`, `Ao3HttpClient:CacheDuration`.

## Extending the scaffold

`Ao3ShipIndexScraper` is the worked example of everything below — it claims
`Ao3ScraperKeys.ShipIndex`, and splits into three pieces on purpose: `Ao3BlurbParser` (HTML in,
records out — no HTTP, no database, no clock), `WorkIngestor` (records in, rows out), and the
scraper itself, which owns only the walking and the stopping rules. A second source should keep
that split; it is what makes the parser testable against captured markup.

To add another scraper:
1. Implement `IAo3Scraper` in `Services/Scraping/`, using `IRateLimitedHttpClient` for all HTTP
   access — never a raw `HttpClient`, or the request goes out without rate limiting or the
   instance's User-Agent.
2. Consult `ScrapeContext.Budget` before every fetch and record the outcome
   (`RecordSuccess` / `RecordFailure` / `RecordCacheHit`). That is what enforces the per-run
   request cap, the wall-clock cap, and the circuit breaker.
3. Register it: `builder.Services.AddScoped<IAo3Scraper, YourScraper>();` in `Program.cs`.
   `ScraperRegistry` picks it up automatically — no scheduling/persistence code changes needed.
4. A `ScrapeJob` is created per ship the moment someone follows the tag, with its `ScraperKey`
   set to `Ao3ScraperKeys.ShipIndex` (`"ao3-ship-index"`). Return that same string from your
   scraper's `Key` and every job already sitting in the database starts running — no migration,
   no re-following. A key nothing claims makes the worker log one "unknown scraper key" warning
   per due tick and reschedule a whole interval out, which looks exactly like a job that is
   quietly doing nothing. Registered keys are exposed at `GET /api/scrape-jobs/scrapers`.

## Note on this scaffold's testing

`docker compose up` has still not been run — review the Dockerfile/compose files before relying
on them.

The entity model and both migration histories *have* been verified for real. The SQLite
migration applies and the app boots on it; the PostgreSQL migration was applied against an
actual `postgres:17-alpine` instance, confirming the 23 tables of the schema as it then stood,
that the half-star rating check constraint translates on both providers, and that every timestamp
lands as `timestamp with time zone`.

That check predates the saved-filter migration, which added three tables — the model is at 26
now, and the PostgreSQL history has not been re-applied against a real server since. The SQLite
one has.

The politeness layer has unit tests (`backend/Ao3Tracker.Tests`) covering the budget and
breaker, the jitter bounds — including that a `Max < Min` misconfiguration clamps to `Min`
rather than to zero delay — contact validation, and operator-contact precedence. The optional
account email has its own tests, over a real `UserManager` and database, covering that blank
clears the field rather than failing validation and that saving an address does not sign the
caller out. The scraping identity was also exercised end-to-end against a running instance: a
fresh install logs `Scraping is disabled`, saving an admin's email flips it to
`Scraping enabled. Identifying to AO3 as: …` on the next poll, overriding and clearing the
contact both work without a restart, and unreachable contacts (`nobody`) and header-injection
attempts (`a@b.com) Mozilla/5.0 (`) are rejected with a 400.

The library endpoints and tag verification are covered against a real SQLite database rather than
a mocked context — watched-ship scoping, paging returning each work exactly once under a sort full
of ties, and the synonym rename/merge paths all depend on what the database actually does. Tag
verification was additionally exercised end-to-end against a **local stub** standing in for AO3
(`redirect → renamed`, `404 → NotFoundOnAo3` with its schedule disabled, tag id harvested from the
feed link, User-Agent carrying the operator contact). No test has ever sent a request to the real
archive, and none should: the stub is what makes the redirect path testable without spending
somebody else's bandwidth on it.

Saved filters have the largest suite of the three, over the same real database: that each
criterion narrows the library the way it claims to (completion as genuinely three states, rating
bands inclusive at both ends, included tags required rather than merely any-of), that a set is
neither readable nor applicable by anyone but its owner, that promoting a default moves it off
the set that held it, that a set naming a ship its author no longer follows stays inside their
library, and that the reported match count counts only works its owner can see.

The admin database-settings endpoint was verified too, including that it rejects an unreachable
PostgreSQL connection string with a 400 *before* persisting or restarting anything.

One accepted, currently-unpatched issue: `Microsoft.EntityFrameworkCore.Sqlite` pulls in
`SQLitePCLRaw.lib.e_sqlite3`, which has an open NuGet advisory
([GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)) with no patched
version available at time of writing — this affects essentially every .NET project using
SQLite today, not something specific to this scaffold. Worth revisiting when a fix ships.
