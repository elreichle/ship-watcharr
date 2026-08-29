# Ship Watcharr — dashboard completion spec

## Problem statement

Following a ship on AO3 means going back to the same tag index and re-reading it for anything
new, then doing that again for every other ship you follow. There is no way to see all of them
at once, no way to say "only completed works over 500 kudos between 1k and 30k words" and keep
that view, and nothing that remembers what you have already read or what you thought of it.

The person with this problem is someone who follows several relationship tags and wants one
page that answers "what is new across all of them", self-hosted on their own hardware so the
library and the reading history are theirs.

## Solution

A single-container, self-hostable server that keeps a local, filterable library of the works
under the relationship tags its users follow, and a web dashboard over it. Scraped work metadata
is shared instance-wide (one Ship is scraped once for everyone watching it); reading status,
ratings, notes, saved filters and downloads are per user. The aggregated feed is sortable and
filterable, filters are saveable and reusable, works can be rated / marked read / annotated /
downloaded as files, and the whole thing runs from `docker compose up`.

Much of this already exists — see "Starting state" below. This spec covers the remainder.

## Starting state (as of the branch point)

Working, verified, and not to be re-done:

- Follow/unfollow relationship tags, verified against AO3 after the fact, synonyms folded into
  their canonical tag (rename or merge).
- The AO3 ship-index scraper, running under the scheduling/budget/rate-limiting pipeline, in two
  passes: incremental (watermark-bounded) and resumable backfill.
- `GET /api/works` — the aggregated feed, scoped to the caller's watched ships, paged, sortable,
  narrowable to one ship.
- Saved filters: ~20 typed criteria, match counts produced by the same predicates as the list,
  one markable default, owner-scoped.
- Works / Filters / Ships / Schedules pages, Sonarr-style sidebar, Obsidian-themeable.
- The politeness layer: one global rate gate at 5–8s, honest User-Agent, per-run budgets,
  circuit breaker, operator-contact gate on scraping.
- SQLite (default) and PostgreSQL, two migration histories over one entity model.

Modelled but touched by no code at the branch point: `UserWorkState`, `Download` /
`WorkDownloadFile`, `Work.DetailFetchedAt` / `Work.PublishedAt`, `Ship`'s full-sweep timestamps,
`WatchedShip.NotificationsEnabled`, and `Ao3InstanceCredential` (storage and store class exist;
no API, no login flow, no worker gate).

## User stories

1. As an instance admin, I want to save one AO3 login for the whole deployment, so that shared
   scrapes have an unambiguous session to run as.
2. As an instance admin, I want scraping to visibly refuse to run until that login exists, so
   that a half-configured instance is never mistaken for a broken one.
3. As a user, I want the Ships page to tell me loudly when my followed ships are scheduled but
   held, so that an empty library has a stated cause.
4. As an instance admin, I want the retired per-user AO3 credential gone from the UI and schema,
   so that there is exactly one place a login can be entered.
5. As the scraper, I want to authenticate to AO3 once and reuse the session, so that logged-in-only
   pages are readable without re-authenticating every run.
6. As a user, I want to mark a work To read / Reading / Read / Dropped, so that I can tell at a
   glance what I have already been through.
7. As a user, I want to rate a work in half-stars and leave myself a private note, so that my own
   opinion lives beside the archive's kudos count.
8. As a user, I want my reading status and rating to survive re-scrapes, so that updated metadata
   never costs me my own data.
9. As a user, I want to filter and save filters on reading status and my own rating, so that
   "unread, completed, over 500 kudos" is one saved view.
10. As a user, I want a page per work showing everything known about it, so that I can decide to
    read it without leaving for AO3.
11. As a user, I want publication date and the complete tag list, which a listing blurb does not
    carry, so that the detail page is not a thinner copy of the row I clicked.
12. As a user, I want to request a work as EPUB/MOBI/PDF/HTML and have the server fetch it, so
    that I have a copy that does not depend on AO3 being up.
13. As a user, I want a download I request to be queued and reported on rather than blocking, so
    that a 5–8s rate gate never becomes a hung page.
14. As a user, I want a work I already have at its current version to be served instantly without
    touching AO3, so that re-downloading costs the archive nothing.
15. As a user, I want a Downloads view listing what I have asked for and its status, so that a
    failed fetch is visible rather than silent.
16. As a user, I want works that have left a tag to leave my library for that ship, so that the
    library does not drift permanently away from AO3.
17. As a user, I want to be told when a ship I follow gains a new work, so that I do not have to
    poll the dashboard to find out.
18. As a user, I want statistics over a ship's corpus with my own reading laid over them, so that
    I can see how much of a ship I have read and how my ratings sit against its reception.
19. As a self-hoster, I want `docker compose up --build` to produce a working instance on a fresh
    machine, so that hosting it does not require reading the source.

## Implementation decisions

**The AO3 login is instance-level, and a hard gate.** One credential per deployment
(`Ao3InstanceCredential`, single row enforced by a check constraint), because scraped data is
shared — a per-user credential has no answer for whose session a shared scrape logs in with. The
password is the durable source of truth, Data-Protection encrypted; the AO3 session cookie beside
it is only a cache and must never be what holds the login. A missing credential holds due jobs
unrun rather than failing them, and is re-checked every poll so a fresh install starts scraping
without a restart — the same shape as the existing operator-contact gate, which is a separate
concern and stays. The per-user `Ao3Credential` is retired: its ciphertext was already carried
over by the `InstanceAo3Credential` migration, both stores sharing the protector purpose
`Ao3Tracker.Ao3Credentials.v1`.

**Per-user state hangs off `UserId` and is never written by a scrape.** Rating is an integer
1–10 in half-stars (7 = 3.5 stars) with a check constraint, null meaning unrated as distinct from
a deliberate lowest rating. The works list carries each row's state so the feed needs one request,
not one per row.

**Saved-filter criteria stay typed columns, not a blob.** Each criterion maps 1:1 onto something
the query compares against, which is what lets the same predicates serve both the list and the
match count. Adding reading-status and rating criteria therefore means a migration on both
providers, which is the accepted cost. Per-user criteria join `UserWorkStates` on the caller's id;
"unread" must include works with no state row at all, not merely rows saying None.

**Downloads are queued, shared, and version-keyed.** `Download` is one user's request;
`WorkDownloadFile` is the file on disk keyed by (work, format, `Work.UpdatedAt` at fetch time), so
two users wanting the same format of the same unchanged work cause one fetch. Paths stored
relative to the data directory, never absolute — the Docker volume can be mounted elsewhere. The
fetch goes through `IRateLimitedHttpClient` like every other outbound request, never a raw
`HttpClient`, and is driven by a `BackgroundService` alongside `ScrapeWorker`. A request for a
version already on disk completes without any AO3 request at all.

**A full sweep is the only pass permitted to conclude a work has left a tag.** The incremental
pass stops at the watermark and the backfill walks backwards, so neither ever observes the whole
index; concluding absence from either would delete works merely because the pass stopped early. A
sweep marks its start, walks every page, and only then acts on what it did not see.

**Notifications are in-app only.** No SMTP, no push. `WatchedShip.NotificationsEnabled` is the
existing switch. Produced where new works are ingested, per watcher, and read through an
unread-count endpoint the shell polls.

**Statistics are computed by query, not stored.** No new schema. Two lenses over the same works:
the corpus as it stands, and the caller's reading laid over it.

**Scraper HTTP rules are non-negotiable.** Every outbound request goes through
`IRateLimitedHttpClient`, consults `ScrapeContext.Budget` before fetching, and records the outcome.
The User-Agent identifies the software, the operator, and the instance honestly — no browser-UA
spoofing, no rotation, no fake headers. Randomization in this codebase exists to *lower* the
request rate, not to disguise it.

**Parsers are built against captured markup, never remembered selectors.** HTML fixtures live as
files under `backend/Ao3Tracker.Tests/Fixtures/` (the existing parser tests hold theirs as inline
constants; new ones use files). No test may send a request to the real archive — where a live
round trip is needed, tests stand up a local stub, as the tag-verification tests already do.

**Timestamps are `DateTime` (always UTC), never `DateTimeOffset`** — the SQLite provider can only
translate equality on `DateTimeOffset`, so ordering and range comparisons throw at query time.

**Every schema change ships two migrations**, one per provider, generated with
`--context SqliteAppDbContext -o Data/Migrations/Sqlite` and
`--context PostgresAppDbContext -o Data/Migrations/Postgres`.

## Non-goals

- **It never writes to AO3.** No kudos, comments, bookmarks, subscriptions, or posting, even
  though the session it holds could do all of them. The login exists to read pages that require
  being logged in, and for nothing else.
- No email or push notifications; no SMTP configuration.
- No auto-downloading on filter match — every download is a request someone made.
- No data migration between SQLite and PostgreSQL. Switching providers still starts empty.
- No second source beyond AO3, and no renaming of the `Ao3*` seams in anticipation of one.
- No public/multi-tenant hosting concerns: rate limiting of the app's own API, quotas, abuse
  handling. This is software you run for yourself and people you know.
- No mobile app; the web UI being usable on a phone is enough.
- No browser-UA spoofing, header forgery, or any change that makes the scraper harder to
  attribute. Settled, and not to be re-litigated.

## Testing decisions

The seams, highest first:

- **Controller-level, against a real SQLite database** (`LibraryTestHost`) for everything with a
  query behind it — library scoping, paging under ties, filter predicates, ownership. Not a mocked
  `DbContext`: these behaviours are decided by what the database actually does.
- **A local HTTP stub** standing in for AO3 wherever a round trip is under test — redirects,
  status codes, headers, the User-Agent actually sent. Never the real archive.
- **Pure functions against captured markup** for parsing: HTML in, records out, no HTTP, no
  database, no clock.
- **Unit tests** for the politeness arithmetic — budgets, breaker, jitter bounds, including that
  a misconfiguration clamps toward *slower*, never toward zero delay.
- **The frontend is typechecked and linted, not unit-tested.** There is no test runner in
  `frontend/`, and this project does not add one. UI tasks are verified by `npm run build`
  (which runs `tsc -b`) plus a live check against a throwaway instance.

## Definition of done

Every task in `.devloop/tasks.md` is `done`, and on a clean checkout:

- `cd backend && dotnet test` passes with no failures.
- `cd frontend && npm run build` and `npm run lint` are clean.
- `docker compose up --build` produces an instance that boots, registers an admin, accepts an
  instance AO3 credential and an operator contact, follows a ship, and survives
  `docker compose down && docker compose up` with its data intact.
- A logged-in user can, from the dashboard: see one aggregated feed across all followed ships,
  narrow it by any saved filter including reading status and their own rating, open a work's
  detail page, rate it, mark it read, note it, request it as a file and receive that file, see
  notifications when a followed ship gains works, and read statistics over their library.
- `README.md` describes what the app does as of that state — in particular its "planned" list and
  its "docker compose has still not been run" note are corrected.

## Verification commands

- test: `cd backend && PATH="$HOME/.dotnet:$PATH" dotnet test`
- typecheck: `cd frontend && npm run build`
- lint: `cd frontend && npm run lint`

Note: `/usr/bin/dotnet` is runtime-only. Non-interactive shells must prefix `PATH="$HOME/.dotnet:$PATH"`.

## Loop policy

- branch: `devloop/dashboard-completion` (cut from `ao3-ship-index-scraper`, itself 2 ahead of `main`)
- commit per task: yes — one commit per verified-green task.
- pushing: allowed. The branch tracks `origin/devloop/dashboard-completion`, and an iteration may
  push it once its task and journal commits are in. Fast-forward only — never `--force`, never to
  `main`, and still never open a PR.
- off-limits paths:
  - `backend/Ao3Tracker.Api/appdata/` — the dev instance on :5110 holds that SQLite database
    open. Never read, write, delete, or point a test at it.
  - `.scratch/` — machine-local, gitignored, may hold credentials.
  - `frontend/dist/`, `**/bin/`, `**/obj/` — build output.
- Running the app for a live check: never on the defaults. Use a free port *and* a scratch data
  directory — `ASPNETCORE_URLS=http://localhost:<free port> Storage__DataDirectory=<scratch dir>`,
  plus `--no-launch-profile` under `dotnet run` (otherwise `launchSettings.json` wins). The dev
  instance is a systemd user service: `systemctl --user {status,restart} ship-watcharr`, and it
  runs a built DLL, so it does not pick up changes without `dotnet build` + restart.
- No network from the loop's shell. `curl` to the internet times out. Anything needing live AO3
  markup is a fixture task and stays `blocked` until a human supplies the file.
