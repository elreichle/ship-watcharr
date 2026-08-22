# Tasks

Status vocabulary: `todo` (not started) · `in_progress` (claimed by an iteration, may be
half-finished) · `done` (verified green and committed) · `blocked` (3 failed attempts, or
a dependency cannot be met — needs a human).
`attempts` counts failed verification runs on this task.

Read `.devloop/spec.md` before starting any task. Every task additionally has to leave
`cd backend && PATH="$HOME/.dotnet:$PATH" dotnet test`, `cd frontend && npm run build` and
`npm run lint` green — that is the floor, not the verification.

Schema changes need **two** migrations, one per provider:
```
cd backend/Ao3Tracker.Api
PATH="$HOME/.dotnet:$PATH" dotnet ef migrations add <Name> --context SqliteAppDbContext  -o Data/Migrations/Sqlite
PATH="$HOME/.dotnet:$PATH" dotnet ef migrations add <Name> --context PostgresAppDbContext -o Data/Migrations/Postgres
```

## T1 — Admin API for the instance AO3 credential
- status: todo
- attempts: 0
- blocked-by: none
- delivers: An admin can save, inspect and clear the deployment's single AO3 login over HTTP.
  `GET/PUT/DELETE /api/admin/scraping/ao3-credential`, admin-only, returning a status DTO
  (username, whether a password is stored, whether a session is cached, timestamps) and **never**
  the password or session cookie in any response.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~InstanceAo3Credential`
- notes: `IAo3InstanceCredentialStore` / `Ao3InstanceCredentialStore` already exist and are
  registered in `Program.cs`; this task writes the controller over them, not the storage. Follow
  `AdminScrapingController` for the admin-authorization shape and `AccountController`
  (`api/account/ao3-credential`) for the per-user version being replaced — same status-not-secrets
  discipline. Clearing must remove the row, not blank the fields. Saving a new password must
  invalidate any cached session cookie: the cookie is a cache of *that* password's session.
  Add controller tests over the real SQLite host (`LibraryTestHost`), covering non-admin → 403,
  round-tripping a save, that no response body ever contains the plaintext, and that a re-save
  drops the cached session.

## T2 — Scraping holds when no AO3 login is stored
- status: todo
- attempts: 0
- blocked-by: T1
- delivers: With no instance credential, `ScrapeWorker` leaves due jobs unrun and logs why; the
  moment one is saved, the next poll starts scraping — no restart.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~ScrapeWorker`
- notes: Mirror the existing operator-contact gate in
  `Services/Scraping/ScrapeWorker.cs` (~line 125): checked **every poll**, not once at startup, and
  logged on transition rather than every tick, so the log does not fill with the same line. Jobs are
  *held*, not failed — do not advance `NextRunAt`, do not write a failed `ScrapeRun`, do not touch
  the circuit breaker. The two gates are independent concerns and both must pass; report both
  reasons when both are missing rather than only the first. Also expose the gate state on
  `GET /api/admin/scraping/identity` (or a sibling) so T3 can render it without guessing.

## T3 — AO3 login in the UI, and a loud banner when it is missing
- status: todo
- attempts: 0
- blocked-by: T2
- delivers: An admin can enter the AO3 login under System → Scraping, and every user sees a red
  banner on the Ships page saying their followed ships are scheduled but held until one exists.
- verification: `cd frontend && npm run build && npm run lint`, plus a live check against a
  throwaway instance (free port + scratch `Storage__DataDirectory`) confirming save → banner clears
  → delete → banner returns.
- notes: `frontend/src/pages/AdminScrapingPage.tsx` is where the login form goes; `ShipsPage.tsx`
  gets the banner. The banner is for *all* users, not just admins — a non-admin needs to know why
  their library is empty, and should be told to ask an admin rather than shown a form they cannot
  use. Word it as a missing login, never as a missing *session*: the session cookie is a cache and
  its absence means nothing. The Ships page already has the "no scraper registered" message —
  follow it. Paint from Obsidian CSS variables only; no hardcoded colours.

## T4 — Retire the per-user AO3 credential
- status: todo
- attempts: 0
- blocked-by: T3
- delivers: Exactly one place in the product where an AO3 login can be entered. The per-user
  endpoints, the Account settings section, `Ao3Credential`, `Ao3CredentialStore` and the
  `Ao3Credentials` table are gone.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test` and
  `grep -rn "Ao3Credential\b" backend/Ao3Tracker.Api frontend/src` returning nothing outside
  migration history.
- notes: Delete `Controllers/AccountController.cs` (the ao3-credential one), the model, the store,
  the DTOs, and the section in `frontend/src/pages/AccountSettingsPage.tsx` — but **not** the
  account *email* controller, which is a separate, still-live concern feeding the operator contact.
  Then a migration on both providers dropping the table. Safe to drop: the `InstanceAo3Credential`
  migration already carried any existing row over as ciphertext (both stores share the protector
  purpose `Ao3Tracker.Ao3Credentials.v1`). Do not delete or rewrite that earlier migration.

## T5 — Authenticate to AO3 and reuse the session
- status: blocked
- attempts: 0
- blocked-by: **fixture** — needs `backend/Ao3Tracker.Tests/Fixtures/ao3-login-page.html`, a real
  capture of `https://archiveofourown.org/users/login`. A human must save this file; the loop has
  no network. Leave this task `blocked` until the file exists.
- delivers: The scraper logs in with the stored credential, caches the resulting session cookie,
  attaches it to subsequent requests, and re-authenticates when it expires.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3Login`
- notes: Parse the `authenticity_token` hidden input out of the captured page — Rails rejects a
  login POST without it. Build it as three pieces the way `Ao3ShipIndexScraper` is built: a pure
  parser (HTML in, token out), a session establisher, and the caller. Store the cookie through
  `IAo3InstanceCredentialStore.SetSessionAsync` — encrypted, and treated as a **cache**: losing it
  must cost a re-login, never the credential. Go through `IRateLimitedHttpClient` like everything
  else, so the login POST is rate-gated and carries the instance User-Agent. Test the round trip
  against a **local stub**, never the real archive. **This code must never write to AO3** beyond
  the login POST itself: no kudos, comments, bookmarks or subscriptions, ever.

## T6 — Per-user work state: rate, mark read, annotate
- status: todo
- attempts: 0
- blocked-by: none
- delivers: `GET/PUT /api/works/{id}/state` — reading status, half-star rating, free-text note —
  and each row of `GET /api/works` carrying the caller's own state, so the feed needs one request
  rather than one per row.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~UserWorkState`
- notes: `UserWorkState` and its check constraint already exist; no migration needed. Rating is
  1–10 half-stars, and **null (unrated) must stay distinct from 1** — do not coerce. Writing state
  for a work outside the caller's watched ships is a 404, the same scoping rule
  `WorksController.GetWorks` already applies; and reading another user's state must be impossible
  by construction, not by filtering after the fact. The list join is per-caller — add it in
  `Data/WorkQueries.cs` beside the existing library query so the list and any future count keep
  sharing predicates. `Status = None` and no row at all mean the same thing to a reader; pick one
  as the canonical "cleared" representation and say which in a comment.

## T7 — Rating and status controls on the feed
- status: todo
- attempts: 0
- blocked-by: T6
- delivers: Each Works row shows and edits reading status and a half-star rating inline, with a
  note editor; changes persist and survive a reload.
- verification: `cd frontend && npm run build && npm run lint`, plus a live check that setting a
  rating, reloading, and re-reading the row shows the same value.
- notes: `frontend/src/pages/WorksPage.tsx`. Half-stars are ten steps, not five — the model is
  explicit that 7 means 3.5. An unrated work must look different from a 0.5-star one. Update
  optimistically but reconcile against the response, and surface a failed write rather than
  silently reverting: `WorksPage` already distinguishes real failure states carefully and this must
  not regress that. Obsidian CSS variables only.

## T8 — Reading status and rating as saved-filter criteria
- status: todo
- attempts: 0
- blocked-by: T6
- delivers: A saved filter can require a reading status (or its absence) and a minimum/maximum of
  the caller's own rating, in the editor, in the list, and in the match count.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~SavedFilter`
- notes: Typed columns, not a blob — see the spec for why. Migration on **both** providers.
  The predicates go in `Data/WorkQueries.ApplyFilter` so the list and `matchingWorkCount` cannot
  disagree; that shared-predicate property is load-bearing and there are existing tests asserting
  it. The per-user join is on the *caller's* id, which means a filter's meaning depends on who
  applies it — only its owner ever can, so this is consistent, but say so in a comment. **"Unread"
  must match works with no `UserWorkState` row at all**, not merely rows saying `None`; a left join
  is required. Extend `frontend/src/pages/FiltersPage.tsx` and `api/types.ts` to match.

## T9 — A page per work
- status: todo
- attempts: 0
- blocked-by: T6
- delivers: `GET /api/works/{id}` and a detail route rendering everything already held — title,
  authors, summary, full stats, tags by kind, series, language, which of your ships it appeared
  under, and your own state — reachable by clicking a row.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~WorkDetail`
  plus `cd frontend && npm run build`
- notes: Purely over data already in the database — this task fetches nothing from AO3. Same
  watched-ship scoping as the list: a work you cannot see is a 404. `SummaryHtml` is **untrusted
  HTML from AO3**; render it sanitized or as text, never `dangerouslySetInnerHTML` on the raw
  value. Leave a visible place for the fields T10 fills in (published date, complete tag list) and
  have it read as "not fetched yet" rather than as empty or absent.

## T10 — Per-work detail fetch
- status: blocked
- attempts: 0
- blocked-by: T9, and **fixture** — needs `backend/Ao3Tracker.Tests/Fixtures/ao3-work-page.html`,
  a real capture of an `https://archiveofourown.org/works/<id>` page (ideally one that is
  multi-chapter, in a series, and has a long freeform tag list). A human must save this file.
- delivers: A scraper that reads a work's own page for what a blurb does not carry — publication
  date and the complete tag list — writing `Work.PublishedAt` and `Work.DetailFetchedAt`, with the
  detail page showing them.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3WorkPage`
- notes: A new `IAo3Scraper` with its own key in `Ao3ScraperKeys`, registered in `Program.cs` —
  `ScraperRegistry` picks it up with no scheduling changes. Split it the way
  `Ao3ShipIndexScraper` is split: `Ao3WorkPageParser` (pure), an ingestor, and the walking/stopping
  rules. Consult `ScrapeContext.Budget` before every fetch and record every outcome. Prioritize
  works with a null `DetailFetchedAt`, and re-fetch only when `UpdatedAt` has moved past it —
  detail pages are the most expensive thing this app can ask AO3 for, one request per work.
  Selectors come from the fixture, never from memory.

## T11 — Requesting a download
- status: todo
- attempts: 0
- blocked-by: none
- delivers: `POST /api/works/{id}/downloads` queues a request for a format,
  `GET /api/downloads` lists the caller's with status, `DELETE` removes one. A request for a
  (work, format, current version) already on disk completes immediately with no AO3 request.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Download`
- notes: The model is already designed for this: `Download` is the per-user request,
  `WorkDownloadFile` the shared file keyed by (work, format, `Work.UpdatedAt` at fetch time). No
  migration should be needed — if one is, say why in `.devloop/DECISIONS.md`. Same watched-ship
  scoping as the rest of the library. Requesting the same thing twice must not queue it twice.
  Deleting a `Download` must not delete a `WorkDownloadFile` another user's request still points at.
  This task queues only; T12 does the fetching.

## T12 — The download worker
- status: todo
- attempts: 0
- blocked-by: T11
- delivers: A `BackgroundService` that drains the queue, fetches through the rate gate, writes
  under the data directory, records size and SHA-256, and moves the request to Ready or Failed with
  a message.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~DownloadWorker`
- notes: Through `IRateLimitedHttpClient` — never a raw `HttpClient`, or the request leaves without
  rate limiting or the instance User-Agent. Paths stored **relative** to the data directory (see
  `Services/Storage/StoragePaths.cs`); an absolute path breaks the moment the Docker volume mounts
  elsewhere. Write to a temp name and move into place, so a crash mid-fetch cannot leave a
  truncated file recorded as complete. Isolate the AO3 download URL construction in **one** small
  function with a comment saying it is unverified against real markup until T13 — that is the seam
  T13 replaces. Test against a local stub serving bytes, never the real archive. Downloads and
  scrapes share one global rate gate by design; do not add a second one.

## T13 — Confirm AO3's real download URLs
- status: blocked
- attempts: 0
- blocked-by: T12, and **fixture** — the same
  `backend/Ao3Tracker.Tests/Fixtures/ao3-work-page.html` T10 needs. The download menu on that page
  is what carries the real URLs.
- delivers: The URL construction from T12 replaced by what the captured page actually contains, for
  every format in `Ao3DownloadFormat`, with a test pinning each one.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~DownloadUrl`
- notes: Read the URLs off the fixture; do not reconstruct them from a remembered pattern. If AO3
  serves them from a different host or with a token, that is a finding to record in
  `.devloop/DECISIONS.md`, not something to work around silently. If some formats need a logged-in
  session, note the dependency on T5 rather than pretending they are anonymous.

## T14 — Downloads in the UI
- status: todo
- attempts: 0
- blocked-by: T12
- delivers: Format buttons on a work, a Downloads view listing yours with live status, and an
  endpoint that streams the file to the browser with a sensible filename.
- verification: `cd frontend && npm run build && npm run lint`, plus a live check requesting a
  download against a stubbed source and receiving the bytes.
- notes: New sidebar entry under Dashboard — `frontend/src/components/navigation.ts`. Stream the
  file rather than buffering it, and serve it only to a user whose own `Download` row points at it.
  The filename should be the work's title, sanitized — it is attacker-influenced text from AO3 and
  goes into a `Content-Disposition` header. Poll for status while anything is Pending, and stop
  when nothing is.

## T15 — The full-sweep pass
- status: todo
- attempts: 0
- blocked-by: none
- delivers: A third pass over a ship's index that walks every page and, only afterwards, marks the
  `ShipWork` rows it did not see as having left the tag — so the library stops drifting from AO3.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~FullSweep`
- notes: `Ship` already carries the full-sweep timestamps. This is the **only** pass permitted to
  conclude absence: the incremental pass stops at the watermark and the backfill walks backwards,
  so neither ever sees the whole index. A sweep that is interrupted — budget exhausted, breaker
  tripped, run cancelled, app restarted — must conclude **nothing**; record the sweep start and act
  only on a sweep that reached the end. Leaving a tag is not deletion: the `Work` row stays (other
  ships may hold it), and a work that returns must come back rather than being resurrected as new.
  Sweeps are expensive — schedule them rarely relative to the incremental pass, and say what the
  interval is and why in a comment. Reuse the existing listing parser; no new markup needed.

## T16 — Notifications when a followed ship gains works
- status: todo
- attempts: 0
- blocked-by: T15
- delivers: A `Notification` entity (migration on both providers), rows produced per watcher when a
  ship they follow gains a new work, and `GET /api/notifications` + unread count + mark-read.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Notification`
- notes: Per-user rows keyed on `UserId`, honouring the existing `WatchedShip.NotificationsEnabled`
  switch. Produced where new works are ingested (`Services/Scraping/WorkIngestor.cs`), which is
  also where "new" is already distinguished from "seen again" — do not re-derive it. A backfill
  pass discovering 4,000 old works must not produce 4,000 notifications: decide the rule (new to
  the ship vs. new to the instance vs. only from the incremental pass), implement it, and state it
  in a comment. Following a ship for the first time must not notify about its entire back
  catalogue. In-app only — no email, no push. Cap or age out old rows so the table cannot grow
  without bound.

## T17 — The notification UI
- status: todo
- attempts: 0
- blocked-by: T16
- delivers: An unread count in the sidebar, a list of notifications linking to the works that
  caused them, and marking read individually or all at once.
- verification: `cd frontend && npm run build && npm run lint`, plus a live check that the count
  drops when a notification is read.
- notes: `frontend/src/components/Sidebar.tsx` and `navigation.ts`. Poll the count on an interval
  the app can afford — this is its own server, but a 1s poll is still wasteful; pick something like
  60s and say why. Stop polling when the tab is hidden. Obsidian CSS variables only.

## T18 — Statistics API
- status: todo
- attempts: 0
- blocked-by: T6
- delivers: `GET /api/stats` — two lenses over the caller's library: the corpus (works per ship
  over time, rating mix, kudos and word-count distributions, most prolific authors) and their own
  reading laid over it (share of each ship read, their ratings against the archive's reception).
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Stats`
- notes: Query-only, no new schema. Scoped to the caller's watched ships like everything else.
  Everything must translate on **both** providers — no client-side evaluation of a whole library,
  and no `DateTimeOffset` comparisons. Bucket in SQL, not in memory. A user watching one ship with
  three works must get a sane answer, not a division by zero; so must a user watching none.

## T19 — The Statistics page
- status: todo
- attempts: 0
- blocked-by: T18
- delivers: A Statistics view under Dashboard rendering both lenses, filterable to one ship or all.
- verification: `cd frontend && npm run build && npm run lint`, plus a live check against an
  instance with works in it.
- notes: No charting library is currently a dependency and the project has been deliberate about
  its dependencies — prefer plain SVG/CSS bars, or record the choice and its justification in
  `.devloop/DECISIONS.md` before adding one. Charts must be legible under an arbitrary Obsidian
  theme, so take every colour from the CSS variables rather than a fixed palette. An empty library
  gets an explanation, not an empty chart.

## T20 — Docker, actually run
- status: todo
- attempts: 0
- blocked-by: T4, T14, T17, T19
- delivers: `docker compose up --build` producing a working instance on a clean machine: it boots,
  serves the UI, registers an admin, accepts an operator contact and an AO3 login, follows a ship,
  and survives `docker compose down && docker compose up` with its data intact.
- verification: `docker compose up --build -d && curl -fsS localhost:8080` (or `$APP_PORT`),
  then a down/up cycle confirming the account and followed ship are still there.
- notes: The README says outright that `docker compose up` has never been run — this is the task
  that makes that sentence false, and it must then rewrite it. Requires Docker on the machine and
  network access for the base images; if either is unavailable, mark this `blocked` with the reason
  rather than claiming it passed. Likely breakage: the frontend build stage, the `wwwroot` copy,
  the data-directory permissions under `appuser`, and migrations running on first boot. The
  `app-data` volume must carry the SQLite database, the Data Protection key ring, and
  `settings.json` — verify all three by recreating the container, not by reading the compose file.
  Finish by correcting the README's "Planned" list, its testing note, and anything else this loop
  made untrue.
