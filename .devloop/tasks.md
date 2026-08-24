# Tasks

Status vocabulary: `todo` (not started) · `in_progress` (claimed by an iteration, may be
half-finished) · `done` (verified green and committed) · `blocked` (3 failed attempts, or
a dependency cannot be met — needs a human).
`attempts` counts failed verification runs on this task.

## Run order

Empty, and deleted with T28: the audit that stood in this slot is done, every pre-loop scraper
defect this loop inherited is closed, and nothing left is more urgent than file order. The next task
is the first `todo` in file order whose `blocked-by` are all `done` — T6. `.devloop/scraper-audit.md`
is what that section's reasoning turned into; read it before touching the walk.

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
- status: done
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
- status: done
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
- status: done
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
- status: done
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
- status: done
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
  T6 left the wire ready: `WorkListItem.state` is on every row already (`frontend/src/api/types.ts`),
  so a page of rows needs no extra fetch, and `PUT /api/works/{id}/state` **replaces** — send status,
  rating and note together, because an omitted field means "cleared", not "unchanged". A row that
  sends only the field it changed will silently wipe the other two.

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
  T6 narrowed what "no row at all" can mean: a wholly empty state is *stored* as an absent row and
  the API refuses to distinguish the two, so the left join is the only correct shape — but a row
  saying `None` still exists whenever a status was cleared while a rating stands, and the predicate
  has to accept both. `WorkQueries.StatesOf` is the per-caller predicate to join through; it sits
  beside `Library` so this stays one shared clause rather than two that can drift.

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
- blocked-by: T9, T51, and **fixture** — needs `backend/Ao3Tracker.Tests/Fixtures/ao3-work-page.html`,
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
  **T51 is on this task's `blocked-by` because of T28's audit** (§E8): `WorkIngestor.ApplyTags`
  reconciles a work's tags against the listing blurb's list, deleting anything not in it, so the
  complete tag list this task fetches is erased by the next incremental pass over the same ship —
  silently, on a run recorded as a success. Do not work around it here; T51 decides which
  observation wins, and this task then writes through that rule.

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
- blocked-by: T29, T30, T38, T40, T44, T46, T47, T52
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
  **From T28's audit** (`.devloop/scraper-audit.md`, §D and §E — read both before starting):
  three of this task's `blocked-by` were added by it. T40 because a sweep concluding absence needs
  its own coverage accounting to be honest, and an unreadable page currently counts as a page that
  was read; T44 because `LastKnownTotalWasAuthenticated` is this pass's, and only this pass's,
  consumer; T52 because a sweep the circuit breaker stopped halfway must never be readable as a
  sweep that finished — today that run is recorded `Succeeded`.
  Two audit rows are addressed to this task and are not tasks of their own. **D12: nothing refreshes
  `LastKnownTotalWorks` once a ship has a watermark.** Every incremental pass on such a ship is
  filtered and a filtered heading may not write the total, so the stored number is whatever the last
  unfiltered run read — possibly months old — while the tag goes on growing. This pass is both the
  only thing that would refresh it and the only thing documented to check against it, so decide
  explicitly whether the sweep trusts the stored number or its own, and write the rule down. **E6/E7:
  `Work.IsDeleted` and `ShipWork.MissingSinceAt` are cleared by every ingest and set by nothing.**
  This task writes the first code in the app that ever concludes absence, with the clearing side
  already shipped and every test in the suite proving only that direction.

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

## T21 — Pseud lookup folds case the way the key does
- status: done
- attempts: 0
- blocked-by: none
- delivers: Re-seeing an author whose username differs only in case from the stored row reuses that
  row instead of trying to insert a second one.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Pseud`
- notes: Found by `/code-review` during T1, in code that predates this loop.
  `WorkIngestor.ResolvePseudsAsync` (~line 324) loads existing rows with
  `usernames.Contains(p.Username)` — raw, and both providers compare that case-sensitively — but
  keys the dictionary it builds with `Normalize(p.Username)`. A stored `Emma` and an incoming
  `emma` therefore miss each other, a duplicate `Ao3Pseud` is added, and the page's
  `SaveChangesAsync` fails on the unique index, losing the whole page's ingest. The tag path two
  methods up already avoids this by filtering on the persisted `NameNormalized` column; do the same
  here rather than fixing it with a client-side `ToUpperInvariant`, which would drag the table into
  memory. That likely means a normalized username column and a migration on **both** providers — if
  so, say why in `.devloop/DECISIONS.md`. Test it as an ingest of the same author twice with the
  case changed between passes.
  Second defect in the same method pair, found by the review of T2: `ApplyTags` looks a tag up by
  `Normalize(t.Name)` on the **untruncated** blurb name, while `ResolveTagsAsync` keys the
  dictionary by `Truncate(t.Name, 200)`. A tag longer than 200 characters therefore misses its own
  row, is dropped from the work, and — through `Reconcile` — is deleted if it was there before.
  Truncate once, in one place, and key everything off that. `ApplyAuthors` has the same mismatch
  against `Truncate(…, 100)` — fix both.

## T22 — An unreadable blurb date must not end an incremental pass
- status: done
- attempts: 0
- blocked-by: none
- delivers: A page whose works are all newer than the watermark keeps the walk going even when one
  blurb's date could not be parsed, and the total-works timestamp comes from the injected clock.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Incremental`
- notes: Found by `/code-review` during T1, in code that predates this loop. Two things, same file
  (`Services/Scraping/Ao3ShipIndexScraper.cs`), both about time:
  (1) `Ao3BlurbParser.ParseUpdatedAt` deliberately returns `DateTime.MinValue` when neither date
  form is readable. `MinValue > watermark` is false, so such a blurb is counted as stale, which
  makes `fresh.Count < listing.Works.Count` true (~line 227) and stops the pass at `Watermark` on
  the spot. Worse than a short run: `newestSeen` still advances, so the works on the pages never
  reached are older than the next watermark and no later incremental pass will ever see them.
  Decide what an undated blurb means to the stopping rule — ingesting it and not letting it vote is
  the obvious reading — and say so in a comment. The backfill path already excludes `MinValue`
  explicitly (~line 323), which is the precedent.
  (2) `RecordTotal` (~line 311) stamps `ship.LastKnownTotalWorksAt = DateTime.UtcNow` while every
  other write in the class uses the injected `_time`, so a fake clock cannot see it.

## T23 — A failing page must not be re-requested forever
- status: done
- attempts: 0
- blocked-by: none
- delivers: A page that answers with a non-OK status is either retried a bounded number of times or
  left behind, and either way the walk stops instead of spending its whole budget on one URL.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
- notes: Found by `/code-review` during T2, in code that predates this loop.
  `Ao3ShipIndexScraper.ExecuteAsync` (~line 170): the non-OK, non-404 branch logs, checks the budget
  and `continue`s — but `page++` is at the bottom of the loop (~line 248), so the same URL is
  requested again, and again, until the budget runs out. `pagesFetched` never increments either, so
  the `MaxPagesPerRun` ceiling cannot fire. Worst on a persistent 500: a whole run's allowance spent
  re-asking AO3 for a page it has already refused, which is exactly the load this project's
  politeness rules exist to avoid. Decide the rule — bounded retries with the existing spacing, or
  stop the run and record the status — and say which in a comment. The circuit breaker is the
  neighbouring concern; check whether it already covers repeated non-OK responses before adding
  anything new. Test it against the fake HTTP client answering 500 for one page.

## T24 — A resumed backfill must not set the watermark from its oldest pages
- status: done
- attempts: 0
- blocked-by: none
- delivers: Only a pass that actually saw the newest end of a listing may propose an incremental
  watermark, so a multi-run backfill cannot leave the ship re-reading its whole tag forever.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Watermark`
- notes: Found by `/code-review` during T23, in code that predates this loop. Verified against the
  source, including the scheduling that makes it reachable. `FinishAsync` (~line 425) advances
  `IncrementalWatermarkUtc` on any `LastPage` stop, and the "refuses to move the watermark
  backwards" guard the comment leans on only bites when a watermark already exists
  (`ship.IncrementalWatermarkUtc is null || newest > …`). It is always null here:
  `ScrapeWorker.RunJobAsync` (~line 199) picks Backfill whenever `BackfillState` is `NotStarted` or
  `InProgress`, so a ship being backfilled never runs an incremental pass and never gets a
  watermark. `MaxPagesPerRun = 200` and `MaxRequestsPerRun = 500` mean any tag over ~4,000 works
  takes several runs, and the run that finally reaches `LastPage` has read only the *oldest* pages
  of a `revised_at desc` listing — so `newestSeen` is the revision date of some of the oldest works
  in the tag, and that becomes the watermark. Every later incremental pass then asks for
  `revised_at > (that old date - 1d)`, gets essentially the whole tag, walks to the 200-page
  ceiling and stops with `Cap` — which is not `reachedTheEnd`, so the watermark never advances and
  the next pass does it again, re-ingesting thousands of works on every tick, forever. This is the
  same class of defect as T23 but far more expensive. Fix by only letting a pass that began at
  page 1 propose a watermark, or by carrying the backfill's own newest-seen across resumes — say
  which and why in a comment. `Sets_a_watermark_when_a_backfill_finishes` covers only the
  single-run case; the test this needs is a backfill resumed at a page above 1.

## T25 — What actually ends a backfill
- status: done
- attempts: 0
- blocked-by: none
- **Re-scoped by T34**, which took half (2), then **closed by T37**, which took what was left. See
  the struck-through notes below and `DECISIONS.md` under T37. Nothing of T25 remained once the 404
  branch was rewritten: its guard is now `lastPage == page - 1` — "a 404 is the end of a listing only
  past a page this run actually read" — rather than the `page == 1` T25 proposed, which would have
  been right about a resumed cursor and wrong about T37's retreat. Verified by T37's run of
  `--filter FullyQualifiedName~Ao3ShipIndexScraper` (53 tests).
- delivers: A resumed backfill whose first request 404s because the listing shrank reaches
  `Complete`, instead of re-requesting the missing page on every scheduled run forever.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Backfill`
- notes: Found by `/code-review` during T23, in code that predates this loop. Two halves of one
  question — which page ends a walk — both in `Ao3ShipIndexScraper.ExecuteAsync`:
  (1) The 404 branch (~line 163) tests `pagesFetched == 0` to mean "the first page", but `startPage`
  (~line 79) is `ship.BackfillNextPage` for a backfill, so a run's first request is routinely page
  57 rather than page 1. If the listing shrank between runs, that request 404s with
  `pagesFetched == 0` and is recorded as `Error` instead of `LastPage` — so `BackfillState` never
  becomes `Complete`, the cursor never advances, and the ship re-requests the same missing page on
  every scheduled run indefinitely. The guard wants `page == 1`.
  ~~**Do not write this fix without reading T37 first.**~~ **T37 is done, and took this half with
  it.** T34's review found that `page == 1` on the
  404 branch and T34's rule on the empty-200 branch conclude *opposite* things about the same
  real-world situation — a cursor left pointing past an end the listing has since shrunk to. One
  would call it the end of the listing, the other a parse failure. T37 is where that is settled;
  this task must not quietly pick a side for the 404 branch alone.
  ~~(2) A 200-OK page that parses to zero works mid-walk takes the `LastPage` branch.~~
  **Done by T34.** T34 and this half were the same `if (listing.Works.Count == 0)` block and the
  same question, so answering one without the other would have meant writing a rule and rewriting
  it an iteration later. The block now concludes the listing has ended only on page 1, with no Next
  link and no populated unfiltered heading; anything else is an `Error` that leaves the cursor
  where it is. See `DECISIONS.md` under T34. **What is left of T25 is half (1) only** — the 404
  branch's `pagesFetched == 0`, which is a different branch and a different failure (a resumed
  backfill that can never *reach* `Complete`, where T34's was one that reached it wrongly).

## T26 — An unreadable byline must not erase authorship
- status: done
- attempts: 0
- blocked-by: none
- delivers: A blurb whose creators cannot be read leaves the work's existing authors alone and
  counts a parse warning, rather than quietly rewriting it as anonymous with no creators.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Author`
- notes: Found by `/code-review` during T23, in code that predates this loop.
  `Ao3BlurbParser.ParseAuthors` (~line 247) returns `[]` for any heading it cannot read, and
  `TryParseBlurb` (~line 116) then sets `IsAnonymous: true` and raises **no** `ParseWarnings` —
  unlike the title, which both falls back to `Unknown work {id}` *and* increments the counter.
  `WorkIngestor.ApplyAuthors` hands that empty set to `Reconcile`, which deletes every existing
  `WorkAuthor` row for the work. So a change to `rel="author"` or the `h4.heading` shape means the
  next incremental pass over an existing library rewrites every re-seen work as anonymous with zero
  creators, and the run record shows zero parse warnings to explain it. The parser's own contract
  says a shortfall is counted into `ParseWarnings`; this is the field that does not. Note the real
  ambiguity to resolve: a genuinely anonymous work also has no `rel="author"` anchors, so "no
  authors" and "could not read the authors" have to be told apart by something else — AO3 renders
  "Anonymous" as plain text in the byline. Decide the rule and say so in a comment.

## T27 — One scope per job, and a failed save that does not escape the finally
- status: done
- attempts: 0
- blocked-by: none
- delivers: A job whose save fails is recorded as failed and reschedules, instead of leaving a
  `Running` run forever, retrying the same page against AO3 every minute, and skipping every other
  due job in the tick.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~ScrapeWorker`
- notes: Found by `/code-review` during T23, in code that predates this loop, and verified.
  `ScrapeWorker.RunDueJobsAsync` (~line 121) creates **one** scope for the whole poll and passes
  `scope.ServiceProvider` to every `RunJobAsync` in the tick, so the `AppDbContext`, the scraper and
  the ingestor are one instance shared across all due jobs — which also contradicts the comment in
  `Program.cs` saying "the worker resolves one per job inside that job's own scope". Consequence: if
  a `SaveChangesAsync` inside `WorkIngestor` fails, the offending entities stay tracked on that
  shared context; the `catch` sets `Status = Failed`, and then the `finally`'s
  `await db.SaveChangesAsync(ct)` (~line 260) retries the same broken change set and throws again —
  from the `finally`, so it escapes `RunJobAsync` entirely. The `ScrapeRun` stays `Running` with no
  `CompletedAt`, `job.NextRunAt` is never advanced so the job is due again on the very next
  minute-poll (a tight retry loop against AO3, which is the load this project's politeness rules
  exist to prevent), and every remaining due job in that tick is skipped. Stale `Running` rows are
  only reconciled at startup, so it never resolves on its own. A scope per job, and saving the
  run/job rows in the `finally` on a context that cannot be carrying a poisoned change set, is the
  shape the fix wants. Make the `Program.cs` comment true rather than deleting it.

## T28 — Audit the scraper's walking and stopping rules
- status: done
- attempts: 0
- blocked-by: none
- delivers: `.devloop/scraper-audit.md` — one table row per rule that decides where a pass starts,
  where it stops, what it may conclude from stopping, and what it writes back to the ship. Each row
  names the rule, the code that implements it, which passes it applies to, what it concludes, and
  the test that pins it. Every gap found becomes a new task, added to this file **and** to T15's
  `blocked-by`.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test` green, `.devloop/scraper-audit.md` exists,
  and every row's named test is confirmed to exist and to bite — run each named filter and check it
  matches more than zero tests. A row naming a test that matches nothing is a gap, not a row.
- notes: **This task fixes nothing.** It reads, tabulates, and queues; a fix found here is a task,
  not a diff in this commit. That is what keeps it to one bounded iteration instead of an open-ended
  rewrite. Scope: `Ao3ShipIndexScraper` first and hardest, then `ScrapeWorker`'s mode selection and
  `WorkIngestor`'s reconcile rules — those are where all ten defects this loop has found so far
  lived. The question to hold throughout is the spec's: *which pass is entitled to conclude this,
  and has it actually seen enough to be entitled?* Every defect so far has been a pass concluding
  something it had not earned — absence, an end of listing, a watermark, an empty byline.
  Existing rules worth a row each, as a starting list and not a complete one: where each mode starts;
  what advances `BackfillNextPage`; what may propose a watermark and what may commit one; what marks
  a backfill `Complete`; what an undated blurb does (T22); what a refused page does (T23); what an
  empty page does; what a parse shortfall may erase (T26). Write down the rules the code *has*,
  not the rules it should have — a row saying "no rule; nothing decides this" is the most valuable
  kind of row here.
  Why it comes before T15: the full sweep adds a **third** pass over the same listing and is the
  only one permitted to conclude a work has left a tag — the strongest conclusion in the system,
  built on the stopping rules this audit is checking. Building it on rules already known to be wrong
  gets three broken passes instead of two.
  Three generalisations the table should carry as rows in their own right, one from each of the last
  three iterations: a waiver is only as good as the evidence it substitutes, and may not fire on a
  page carrying neither piece (T47); when a rule is waived because its argument does not hold, ask
  what that rule was *carrying*, not only whether it was sound (T42); and check whether a rule's own
  test constructs the situation its comment claims — T42's and T43's tests both pinned the defect
  rather than the rule, and were the defect written down.

## T29 — An incremental pass must not overwrite a ship's total with a filtered count
- status: done
- attempts: 0
- blocked-by: none
- delivers: `Ship.LastKnownTotalWorks` holds the number of works in the tag, not the number matching
  the last request's date filter.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Total`
- notes: Found by `/code-review` during T27, in pre-loop code, and verified against the source.
  `BuildUrl` (~line 367) adds `work_search[revised_at]=> {watermark-1d}` on every incremental pass
  that has a watermark, so AO3's `h2.heading` then counts the *filtered* result set. `RecordTotal`
  (~line 388) is called for every fetched page (~line 214) with no mode check, so a ship that
  finished its backfill at 4,317 works has `LastKnownTotalWorks = 2` after the next quiet
  incremental pass. `Ship.cs` documents the field as the tag's total and names it as the input a
  full sweep checks itself against before concluding works have disappeared — which is T15, and why
  T15 is now blocked by this. Gate `RecordTotal` on the request having carried no `revised_at`
  filter rather than on the mode, so it stays true if the filter's conditions ever change. The
  existing test `Stamps_the_tags_total_from_the_injected_clock_on_an_incremental_pass` passes today
  only because its ship has no watermark, so the filter is never applied — check the new filter
  bites before trusting it (see T22).

## T30 — `LastKnownTotalWasAuthenticated` must describe the total it sits beside
- status: done
- attempts: 0
- blocked-by: none
- delivers: The flag says whether *this* ship's stored total was read while logged in, and can go
  back to false when a later run reads one anonymously.
- **Re-scoped by T29.** The flag is no longer written by a pass that writes no total: T29's review
  found that skipping the total on a filtered listing while still latching the flag let the two come
  from *different runs*, which is a sharper version of this same defect, so the one-line pairing was
  folded into T29 and is tested by
  `Does_not_claim_a_filtered_pass_authenticated_the_total_it_did_not_write`. What is left here is
  the two things that pairing does not fix, both still live and both described below: the one-way
  latch, and `sawRestricted` being computed over newly ingested works only.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
  — **corrected from `~Authenticated`**, which matched 2 tests, one of them in an unrelated
  controller, and missed `Records_that_an_unfiltered_pass_read_the_total_while_logged_in` — this
  field's own positive test. The third form of T22's lesson: a filter can name the subject and still
  miss the tests about it, because the tests are named for the behaviour, not the field.
- notes: Found by `/code-review` during T27, in pre-loop code, and verified.
  `Ao3ShipIndexScraper.FinishAsync` (~line 487) does `if (sawRestricted) ship.LastKnownTotalWasAuthenticated = true;`
  — a one-way latch. A logged-in run sets it; the session lapses; a later anonymous run overwrites
  `LastKnownTotalWorks` with a lower count through `RecordTotal` while the flag still claims the
  total came from an authenticated run. That is precisely the confusion the field exists to prevent,
  and it is what T15 would read before concluding works had vanished. Second half: `sawRestricted`
  is computed over `toIngest` only (~line 273), so an incremental page whose restricted works were
  all already held never sets it even on a genuinely authenticated run. Assign the flag per run,
  beside the total, from what the run actually observed. Latent until T5 makes the scraper log in —
  but T5 is the task that makes it bite, so it should not land after it.

## T31 — A singular listing heading must not be read as the tag's name
- status: todo
- attempts: 0
- blocked-by: none
- delivers: `ParseTotalWorks` reads "1 Work in <tag>" as 1, whatever digits the tag name contains.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~TotalWorks`
- notes: Found by `/code-review` during T27, in pre-loop code, and verified by reading.
  `Ao3BlurbParser.ParseTotalWorks` (~line 392) locates the count by `IndexOf("Works")` and, when
  that is -1, scans the *whole* heading backwards for the last run of digits. A tag with exactly one
  work renders "1 Work in …" — singular — so a tag name carrying digits (Star Wars clone
  designations, a disambiguating year) donates them to the total. Match the singular form too, or
  require the digits to be followed by whitespace and "Work". No live markup needed: this is a pure
  parser test over a heading string, so it does **not** wait on a fixture.

## T32 — The non-monotonic-boundary warning names the wrong page
- status: todo
- attempts: 0
- blocked-by: none
- delivers: The warning names the page where the shift was seen.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Monotonic`
- notes: Found by `/code-review` during T27, in pre-loop code, and verified.
  `ship.BackfillNextPage = page + 1` runs at ~line 319 and `TrackBackfillFloor` at ~line 320 logs
  `ship.BackfillNextPage` as `{Page}` (~line 412), so the message is off by one. Cosmetic in the
  database, not in use: this warning is how a human learns a listing shifted under a backfill, and
  T15 exists to clean up after exactly that. Pass `page` in explicitly.

## T33 — One page of known works should not read a cartesian product
- status: todo
- attempts: 0
- blocked-by: none
- delivers: `LoadExistingWorksAsync` reads its three collections as three flat queries.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ingest`
- notes: Found by `/code-review` during T27, in pre-loop code, and verified — nothing in this
  project sets `QuerySplittingBehavior`, globally or locally. `WorkIngestor.LoadExistingWorksAsync`
  (~line 253) chains `Include(Tags)`, `Include(Authors)` and `Include(Series)` in one query, so
  twenty known works with ~15 tags, ~2 authors and ~1 series each materialise several hundred
  duplicated rows — on every incremental pass, over works that have not changed. `.AsSplitQuery()`
  is the whole fix. Cheapest task on this list; it changes no behaviour, so its verification is
  that the existing ingest tests stay green.

## T34 — A backfill must not call itself complete off a page that parsed to nothing
- status: done
- attempts: 0
- blocked-by: none
- delivers: A backfill whose page yields zero works while AO3's heading says the tag has thousands
  stops as an error, not as the end of the listing — so the ship is not retired from backfilling.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
  — **corrected from `~Backfill`**, which matched only 2 of this task's 8 tests and neither of the
  two guarding against the rule firing on a healthy pass. Found by the review; the lesson T22 filed
  is that a filter can look like a pass while running nothing, and this is its second form: a filter
  that runs *some* of the task and misses the tests that matter most.
- notes: Found by `/code-review` during T29, in pre-loop code, and verified against the source.
  `Ao3ShipIndexScraper.ExecuteAsync` (~line 221) sets `stopReason = LastPage` for any page parsing
  to zero works, and `FinishAsync` (~line 467) turns a backfill's `LastPage` into
  `BackfillState = Complete` + `BackfillCompletedAt`. A 200 maintenance page or a listing markup
  change therefore marks a ship fully backfilled having read nothing, and nothing looks again. This
  is the same failure the 404 branch immediately above was deliberately hardened against — reached
  by a different route, which is why the existing guard does not catch it. **The signal to tell the
  two apart is already parsed and already on the page**: `listing.TotalWorks`. A heading saying
  "4,317 Works" over zero readable blurbs is a parse failure; no heading and no blurbs is a
  plausibly empty tag. `Refuses_to_call_a_backfill_complete_when_the_first_page_404s` is the shape
  to follow. Check the filter bites before trusting it — `~Backfill` matched 6 at T24.

## T35 — The pseud dedup migration collides on a third capitalisation
- status: todo
- attempts: 0
- blocked-by: none
- delivers: `NormalizedPseudIdentity` folds any number of capitalisation variants without failing
  the upgrade.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Pseud`
- notes: Found by `/code-review` during T29 and verified by reading the SQL. Both providers:
  `Data/Migrations/Sqlite/20260822182752_NormalizedPseudIdentity.cs:77` and the Postgres twin at
  `.../Postgres/20260822182800_NormalizedPseudIdentity.cs:77`. The `DELETE` drops a loser link only
  where the *canonical* (`MIN(Id)`) link is already on that work, so with three variants — ids 1/2/3
  normalizing alike, a work linked to 2 and 3 but not 1 — neither row is deleted, and the following
  `UPDATE` repoints both to 1 and violates `PK_WorkAuthors (WorkId, PseudId)` mid-upgrade.
  **Latent, not live**: nothing populated `Ao3Pseuds` before this branch, so a real deployment's
  table is empty — which is why this is a correctness fix on the upgrade path and not an incident.
  Do **not** rewrite the applied migration in place if it has shipped anywhere; prefer a guard that
  is idempotent (dedup the link set before repointing, or repoint with a conflict-tolerant form).
  Getting a test around a migration is the hard part here — say in the notes what seam was used.

## T36 — The scraping-identity page shows blockers that are not about identity
- status: todo
- attempts: 0
- blocked-by: none
- delivers: "What AO3 currently sees" explains the User-Agent and nothing else; a missing AO3 login
  is reported by the callout that exists for it.
- verification: `cd frontend && npm run build && npm run lint`, plus a live check on a throwaway
  instance with **both** the operator contact and the AO3 login missing.
- notes: Found by `/code-review` during T29 and verified against the source.
  `ScrapingGateState.Problem` (`Services/Scraping/ScrapingGate.cs:72`) is every blocker joined, by
  design — `ScrapeWorker` wants all of them at once. `AdminScrapingPage.tsx:134` renders it verbatim
  under "What AO3 currently sees", so an instance missing both blockers prints "No AO3 login is
  stored for this instance…" inside the block about the User-Agent, contradicting the comment two
  lines above it. Worse, the dedicated login callout at line 113 is gated on
  `identity.identityConfigured`, so it is suppressed in exactly that case. Either render only the
  identity blocker in that section, or drop the `identityConfigured &&` guard on the callout —
  the second is smaller and makes both sections honest. Obsidian CSS variables only.

## T37 — A backfill stuck on a page that is neither readable nor gone
- status: done
- attempts: 0
- blocked-by: none
- delivers: One rule covering both ways a resumed backfill's cursor can point past the end of a
  shrunken listing, and a bound on how many runs a ship may spend re-asking for the same page.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
- notes: Found by `/code-review` during T34, over T34's own diff, and it is the two-sidedness
  `DECISIONS.md` recorded under T24 arriving for the third time. The situation: a run stops with
  `BackfillNextPage = N+1` because page N offered a next link; before the next run, works are
  deleted or hidden and the listing shrinks so page N+1 no longer exists. AO3's answer decides
  which branch handles it, and the two branches now disagree:
  (1) a **404** — T25 plans to read this as the end of the listing, so the backfill completes;
  (2) a **200 with an empty listing** — T34 reads this as a parse failure, so the run errors,
  `BackfillNextPage` never advances, and the ship re-requests that page on every scheduled run
  forever, never reaching `Complete`.
  Both readings are defensible in isolation and they cannot both be right about one situation.
  Neither branch bounds the retrying, which is the part that costs AO3 something. Options worth
  weighing: a consecutive-failure count on the ship that converts repeated identical failures into
  a completed-with-gaps state; stepping the cursor back to re-read page N and letting *its* next
  link settle whether N+1 should exist (the listing itself is the authority, and this asks it);
  or leaving the backfill stuck deliberately and making T15's full sweep the thing that resolves it.
  Say which and why in `.devloop/DECISIONS.md` — and note that T15 already depends on this area
  through T28.
  **Add to T28's `blocked-by`** when taken, or resolve it before T28 runs: it is a rule about what a
  stop is entitled to conclude, which is exactly what that audit tabulates.

## T38 — An operator way back out of a failed backfill
- status: todo
- attempts: 0
- blocked-by: none
- delivers: An admin can put a ship whose backfill was written off back to `InProgress`, choosing
  where its cursor restarts, so an outage does not cost a back catalogue permanently.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Backfill` plus
  `cd frontend && npm run build && npm run lint`
- notes: Found while writing T37, which made `ShipBackfillState.Failed` reachable for the first time
  — the enum's fourth value, which nothing had ever set. T37's bound gives up on a backfill after
  `MaxStalledBackfillRuns` consecutive runs against a cursor the listing will not answer, which is
  right while AO3 is genuinely refusing and wrong the moment it stops: nothing in the product can
  move a ship out of `Failed`, so a backfill written off during an outage stays written off until a
  full sweep or a hand-edited database. `BackfillNextPage` is deliberately left pointing where the
  walk gave up, so a restart has somewhere honest to resume from; resetting `BackfillStalledRuns` to
  0 is part of the restart, or the ship gives up again on its very next run. The Ships page already
  renders `BackfillState` (`ShipDtos.cs`, `ShipsController.cs:110`) — a stalled ship should say so
  there rather than only in the run history. Consider whether a ship that starts answering again
  should recover on its own instead; if so, say why in a comment, because a self-clearing give-up is
  a give-up that can loop.
  T26's review adds the exact line: `BeginBackfill`'s `NotStarted` branch (`Ao3ShipIndexScraper.cs`
  ~line 633) already resets `BackfillStartedAt` and the cursor and is the restart path this task
  builds on, but it leaves `BackfillStalledRuns` alone — so a ship restarted at 12 gives up on its
  first stalled run rather than its twelfth.
  T30's review adds the same fact from the other end, and it is what makes the missing exit airtight
  rather than merely inconvenient: **both** sites that reset `BackfillStalledRuns` sit on paths a
  `Failed` ship no longer reaches (`ScrapeWorker` only backfills `NotStarted`/`InProgress`), and
  entering `Failed` does not reset it either. So the counter is frozen at its give-up value for as
  long as the state lasts, and an operator who edits `BackfillState` in the database without also
  zeroing the counter gets a ship that gives up again on its very next run. Whatever this task's
  re-arm path is, resetting the counter is not an extra nicety in it — it is the half that makes it
  work.

## T39 — Confirm what AO3 serves for a works index with no results
- status: blocked
- attempts: 0
- blocked-by: **fixture** — needs `backend/Ao3Tracker.Tests/Fixtures/ao3-empty-listing.html`, a real
  capture of a relationship-tag works index that matches nothing (either a tag with no works, or any
  tag under a `work_search[revised_at]` bound far in the future — the second is easy to produce and
  is the shape a quiet incremental pass actually sends). A human must save this file; the loop has no
  network. Leave this task `blocked` until it exists.
- delivers: A parser test over captured markup pinning whether AO3's zero-result index renders
  `ol.work.index.group`, and `Ao3ListingPage.HasListing` corrected if it does not.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Listing`
- notes: Found by `/code-review` during T37, against T34's committed code. `HasListing` is what
  `PlausiblyTheEndOfTheListing` uses to tell an empty tag from a page that is not a results page at
  all, on the premise — stated in that method's doc, verified nowhere — that an empty tag still
  renders the container. Nothing in the repo tests it: `Ao3BlurbParserTests.cs` has no `HasListing`
  case, and every scraper test reaching the zero-work path goes through the `Page(n, [])` helper in
  `Ao3ShipIndexScraperTests.cs`, which emits the container unconditionally. The tests prove the
  premise by assuming it. **If the premise is wrong the failure is worse than the one T34 fixed**: a
  quiet incremental pass — a `revised_at` filter matching nothing, the common case on a ship nobody
  is writing for — reads zero blurbs, `HasListing` is false, `PlausiblyTheEndOfTheListing` says no,
  and the run stops with `Error`. `Error` is neither `Watermark` nor `LastPage`, so `mayPropose` is
  false, the watermark never moves, and every scheduled run on every quiet ship is recorded failed
  forever. Check the filtered case specifically, not only an empty tag: the heading check beside it
  is already gated on `listingWasFiltered` for exactly this reason, and `HasListing` is not.
  While the capture is out, settle the sibling question `Ao3ListingPage` also guesses at: whether
  AO3 pages a zero-result listing with `ol.index.group` rather than `ol.work.index.group`.
  **T28's audit adds a third question to the same capture** (§C7): does a zero-result index render
  the "N Works in …" heading, and if so what number? It decides an open conclusion. `page == 1`
  short-circuits `PlausiblyTheEndOfTheListing`, so an **unfiltered** page 1 with the container, no
  works, no Next link and no heading at all concludes `LastPage` — for a backfill, `Complete`, the
  whole back catalogue written off from the absence of every piece of evidence. T47 established the
  opposite rule one page later: no heading is no evidence, and no evidence is not permission. Page 1
  can only be held to that standard once this capture says a genuinely empty listing carries a
  heading to be held to.

## T40 — An unreadable page must not be counted as a page that was read
- status: todo
- attempts: 0
- blocked-by: none
- delivers: A run's history counts the pages it actually read. A page that came back unreadable
  advances no counter and names no boundary, on every path — not only the retreat.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~PagesFetched`
  (**this filter matches zero tests today** — the task must name its new tests to match it, or
  verification runs nothing; see T22's lesson)
- notes: Found by `/code-review` during T26, in T37's committed code. `Ao3ShipIndexScraper.cs`
  ~line 281 runs `pagesFetched++`, `parseWarnings +=`, `firstPage ??= page` and `lastPage = page`
  *before* the `if (unreadable) break` three lines below, so a fresh backfill whose page 1 is a 200
  maintenance page records `PagesFetched = 1, FirstPageFetched = 1, LastPageFetched = 1,
  WorksSeen = 0` — a run claiming to have read a page it could not read. It also makes
  `readTheNewestEnd = firstPage == 1` true off that page, harmless today only because `newestSeen`
  is null there, which is the kind of accident T24 was. T37 fixed exactly this conflation on the
  retreat path and the hunk's own comment says so; the non-retreat path was missed. Moving the four
  lines below the break is the whole change — but check what else reads `lastPage`, because the
  404 guard is `lastPage == page - 1` and that arithmetic must still hold.

## T42 — An incremental pass must not stop with `Error` on a page it was told to expect
- status: done
- attempts: 0
- blocked-by: none
- delivers: A filtered incremental pass whose second page comes back empty stops in a way that lets
  its watermark move, so a ship cannot be frozen re-reading the same two pages on every tick.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
  — the filter that covers this file (58 tests today); `~Incremental` matched 6 at T22 and does not
  cover the stopping rules.
- notes: Found by `/code-review` during T30, in T34's committed code, and verified against the
  source. `PlausiblyTheEndOfTheListing` (~line 578) requires `page == 1`, so on an incremental pass
  any page past the first that parses to zero works is `unreadable` and the run stops with
  `ScrapeStopReason.Error`. `Error` is neither `Watermark` nor `LastPage`, so `mayPropose` is false
  and `IncrementalWatermarkUtc` does not move — even though page 1, the newest end, was read in
  full. The next scheduled run repeats both requests and ends the same way, indefinitely.
  Reachable whenever page 1 is entirely fresh and offers a Next link and page 2 then answers with a
  well-formed empty listing: a `revised_at`-filtered listing's Next link comes from a result count
  that can race the blurbs, and a single work leaving the window or being deleted between the two
  requests does it. The `page > 1` reasoning is sound about an **unfiltered** listing — AO3 404s past
  the last page — and the heading condition one line below it is already gated on
  `listingWasFiltered` for exactly this reason. This is that same gate, missing from the condition
  next to it. Waiving `page > 1` when `listingWasFiltered` is the small fix; check first whether it
  weakens the backfill guard T34 and T37 built, which is the same method (a backfill is unfiltered,
  so it should not be reachable — confirm rather than assume).
  Note what this shares with T39, which is `blocked` on a capture: both are about what a zero-blurb
  page means under a `revised_at` filter, and T39's fixture would inform this one. This task does
  **not** wait on it — the defect here is in the `page` condition, not in `HasListing`, and is
  demonstrable against the existing fake HTTP client. Do not let the fix depend on the unverified
  premise T39 exists to settle.

## T43 — A 404 must not conclude what the retreat beside it refuses to conclude
- status: done
- attempts: 0
- blocked-by: none
- delivers: A backfill reaches `ShipBackfillState.Complete` on the same evidence whatever the
  cursor's position, so a transient failure mid-retreat cannot retire a ship with its back
  catalogue unread.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
  — 64 tests today, and the filter that covers this file.
- notes: Found by `/code-review high` during T42, in T37's committed code, and demonstrated by the
  reviewer against a running server rather than argued from the source. The 404 branch's first
  guard is `lastPage == page - 1` (~line 209) → `LastPage` → `Complete`. That is the *same*
  evidence `CursorMayBeStale` refuses to conclude from — page N-1 read, offering a Next link, page
  N absent — and the two disagree only on whether this run happens to have read page N-1 itself.
  `Leaves_a_stale_looking_cursor_alone_when_the_page_before_it_still_offers_a_next_link` pins
  cursor=3 over pages 1(next)/2(next)/3→404 as `Error` + `InProgress` + stalled=1; the same server
  with cursor=2 gives `LastPage` + `Complete`.
  The route in is a retreat cut short, reproduced in two runs over pages 1(next)/2(next)/3→404:
  run 1 with cursor 3 and a transient 500 on page 2 retreats and leaves `BackfillNextPage = 2`;
  run 2 is healthy, reads page 2, asks page 3, gets the 404, and concludes `Complete` with page 3
  onward never read. One transient 5xx during a retreat is the whole cost of entry.
  The question to settle is the one T37 settled for the empty-200 branch and this branch was left
  out of: a Next link on the page before is the listing saying the next page *should* exist, and a
  404 for it contradicts that rather than confirming the end. Weigh that against what the guard was
  written for — a genuine walk off the end of a listing, where page N-1's Next link is exactly what
  a shrinking listing leaves behind. Whatever rule comes out has to answer for both, and it is a
  rule about what a stop may conclude, so it belongs in T28's table either way.

## T44 — The authenticated-total flag must come from the request that read the total
- status: todo
- attempts: 0
- blocked-by: none
- delivers: `Ship.LastKnownTotalWasAuthenticated` describes the request whose heading was stored,
  not whether anything in the run was authenticated.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
- notes: Found by `/code-review high` during T42, in T30's committed code. T30 moved the flag from
  the run's filter state to `wroteTotal`, which fixed *which run* may assign it and left *which
  request* it describes untouched: `readWhileLoggedIn |= response.Authenticated` accumulates over
  every page of the run, while `RecordTotal` writes `LastKnownTotalWorks` per page. An unfiltered
  multi-page run whose page 1 comes from an anonymous cache entry and writes the total, and whose
  page 2 is fetched live with a session but has no parseable heading, ends `wroteTotal = true` and
  `readWhileLoggedIn = true` — stamping "counted while logged in" on a number demonstrably fetched
  without one. Note `FromCache` responses preserve `Authenticated: false`, which is what makes the
  mixed run reachable rather than hypothetical.
  Latent until T5 teaches the client to authenticate — nothing sets `Authenticated` before then —
  so this is cheap now and a live wrong answer the day T5 lands. The fix is to capture the flag
  beside the write, in `RecordTotal`, rather than to OR it across the run; T30's own note that the
  pair "travel together or the pair says something neither run did" is the argument, one scope
  further in. Add it to T5's reading, since T5 is what makes it reachable.

## T45 — An incremental pass that cannot get past page 1 has no bound
- status: todo
- attempts: 0
- blocked-by: none
- delivers: A ship whose incremental pass stops with `Error` on the same page every tick stops
  spending a request on it every tick, without that ever being written as a moved watermark.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
  — name the new tests so this filter matches them; it is 67 today.
- notes: The consequence T43 accepted deliberately, and the only half of it left open. A backfill
  that cannot get past its cursor is bounded — `BackfillStalledRuns` at `MaxStalledBackfillRuns`
  writes the backfill off as `Failed` and the ship falls back to its incremental pass. An
  incremental pass has no equivalent: it always restarts at page 1, has no cursor to carry a
  question into the next run, and `Error` freezes `IncrementalWatermarkUtc`, so a page 1 that is
  entirely fresh and offers a next link followed by a page 2 that will not answer sends the same two
  requests on every tick forever. Demonstrated by `/code-review high` during T43 against a running
  server: page 1 fresh with a next link, page 2 → 404, watermark stays null.
  **Do not close this by concluding the end of the listing.** That is the trade T43 refused and T24
  ruled on: the works on page 2 are older than everything on page 1, so a watermark moved past them
  is a watermark no later incremental pass looks back behind. The cost being refused here is two
  requests per scheduler interval on a 5-8s gate, which is the same shape T23 chose deliberately —
  "retried once per run at the scheduler's spacing, rather than in a tight loop inside one" — and
  every one of those runs is recorded failed with a message naming the page, so the failure is
  visible rather than silent. What is missing is only the *bound*: something that notices a ship has
  spent N ticks failing on the same page and does something other than ask again.
  The design question, and the reason this is a task rather than a line in T43's diff: what a stuck
  incremental pass should *do*. A counter on the ship mirroring `BackfillStalledRuns` needs a
  terminal state, and "give up on new works" is not one this product can have. Widening the
  `revised_at` bound, or dropping the filter for one run so the walk can reach page 2 by a different
  address, are both worth weighing — and either is a schema change on both providers if it needs
  remembering across runs. A give-up threshold is a conclusion drawn from failure and inherits every
  objection this loop has raised to those (T37's note), so whatever comes out belongs in T28's table.
  **T47 widened the entrance.** A filtered page past the first that carries no readable heading now
  stops with `Error` rather than concluding the end, so a second shape reaches this stuck state — and
  unlike the 404 that motivated the task, this one is a page AO3 answered 200 with a listing
  container on it. Whatever bound this task settles on has to cover both.

## T46 — A backfill whose cursor sits at page 1 can never reach `Failed`
- status: todo
- attempts: 0
- blocked-by: none
- delivers: A run the archive answered with a 404 counts against `BackfillStalledRuns` wherever the
  cursor is, so a ship pointed at a tag that no longer exists stops backfilling instead of asking
  forever.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Backfill`
  — 13 today, and the filter that covers the stall counter.
- notes: Found by `/code-review high` during T43, in T37's committed code.
  `RecordBackfillProgress`'s `if (!askedStaleCursor && firstPage is null) return;` reads "the
  archive told this run nothing", which is right for the budget, the breaker, a transport failure
  and a refused status — and wrong for a 404, which is the archive answering definitively. With the
  cursor at page 1 `CursorMayBeStale` is false (`page > 1` fails), so no retreat happens,
  `firstPage` stays null, and the counter never moves. The ship stays `InProgress`, `ScrapeWorker`
  goes on choosing `Backfill` for it, and it never falls back to an incremental pass — so unlike
  every other stalled ship, this one collects nothing at all and never reaches `Failed` to say so.
  Reachable for an already-verified ship whose tag is later renamed or deleted: `ShipVerifier`
  returns early for anything not `Pending`, so nothing re-verifies it into `NotFoundOnAo3`, and the
  belt-and-braces check at the top of `ExecuteAsync` never fires. Same family as T38 — a ship stuck
  in a state the product cannot move it out of — and on T15's `blocked-by` for the same reason.
  Weigh it against what that guard was written for: writing off a back catalogue because AO3 was
  down for an afternoon. A 404 is not that, and telling the two apart is the whole task.
  **T28's audit re-derived this row independently** (§D6) and adds its sibling from §A2, which is
  the same family and belongs in the same diff if it is cheap: a ship whose tag AO3 has denied
  returns `ScrapeOutcome.Empty(ScrapeStopReason.LastPage)` at `Ao3ShipIndexScraper.cs:90`, so the
  run history records a healthy "walked off the end of the listing" for a run that made no request
  at all, on every tick, forever. It is harmless only because that early return skips `FinishAsync`
  — move the return below it in some later refactor and a denied ship's backfill is marked
  `Complete` with the tag never once requested. A stop reason that says why (or disabling the job)
  costs nothing here.

## T47 — A filtered page carrying no evidence at all must not end the pass
- status: done
- attempts: 0
- blocked-by: none
- delivers: `PlausiblyTheEndOfTheListing` waives `page > 1` for a filtered listing only on a page
  that actually says something, so an incremental pass cannot move its watermark past works it
  never saw.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
- notes: Found by `/code-review high` during T43, in T42's committed code, and demonstrated against
  a running server rather than argued from the source. `FilteredHeadingSaysMore` returns false when
  `TotalWorks` is null — deliberately, and T42's own doc comment says why: "no heading is no
  evidence". But the waiver reads that false as permission. So a filtered page 2 with the listing
  container, no next link, no works and **no heading** satisfies every condition and concludes
  `LastPage`, which lets `FinishAsync` move the watermark to the run's newest reading — page 1's
  newest, which is newer than everything page 2 would have held, the listing being `revised_at`
  descending. The reviewer's run: watermark Jan 1, page 1 carrying works dated Jan 20 and Jan 15
  with a next link, page 2 a well-formed empty listing with no heading → `LastPage`, and the
  watermark jumps to Jan 20. `RevisedAtBound`'s one-day slack recovers only what sits within a day
  of Jan 20, so on a ship catching up over a long gap — where page 1 spans weeks — everything that
  was on page 2 is permanently unreachable by any incremental pass.
  This is the only silent wrong conclusion left in the walk, which is why it leads the run order:
  T45, T46, T38 and T40 all announce themselves in the run history, and this one reports success.
  The rule to settle is what "no evidence" entitles a *stop* to, as against what it entitles a
  waiver to — T42 answered the second question and this branch took the answer for the first.
  Requiring a parsed heading before waiving `page > 1` is the obvious shape, but weigh it against
  what T42 was filed over: a filtered page 2 that will not parse a heading and gets `Error` instead
  is exactly the every-tick repeat T45 now owns, so the two tasks are the same trade seen from
  either side and whichever runs second should read the other. Not proposing a watermark while
  still stopping is the third option and probably the honest one. It is a rule about what a stop may
  conclude, so it belongs in T28's table either way; T28's `blocked-by` is this task alone.

## T48 — "Anonymous" in a title must not survive the markup change T26 defends against
- status: todo
- attempts: 0
- blocked-by: none
- delivers: `SaysAnonymous` reads the byline and not the heading it sits in, so no reshaping of
  `h4.heading` can let a work's title delete its creators.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Author` — 17 today.
- notes: Found by `/code-review high` during T43, in T26's committed code. `SaysAnonymous`
  (`Ao3BlurbParser.cs` ~line 309) keeps the title out of the byline only because `IsBylineText`
  drops text inside anchors that are not `rel="author"`, and the title happens to be such an anchor.
  Under exactly the failure T26 exists to defend against — AO3 reshaping the heading so no
  `rel="author"` anchors survive — the title's own words become byline words, and any work whose
  title contains "Anonymous" before any "for" parses as `IsAnonymous: true`.
  `WorkIngestor.ApplyAuthors` then reconciles it to an empty author set and deletes its creators,
  which is the destructive outcome T26's three-state design exists to prevent: the fix holds only
  while the markup it defends against has not changed.
  `Does_not_read_a_work_titled_Anonymous_as_having_no_author` covers the linked-title case only.
  The cheap guard the review suggests: ignore everything in the heading before the byline separator
  rather than relying on the title being an anchor. This is T26's own lesson arriving a third time —
  when a fix turns one signal into a conclusion, ask what else in the same document can produce that
  signal — and the answer here is the same heading, four people's names and a title.

## T49 — The stale-cursor retreat's warning renders a placeholder instead of a page number
- status: todo
- attempts: 0
- blocked-by: none
- delivers: `RetreatFromStaleCursor`'s log message names both page numbers it means, and the build
  stops emitting CA2017 for it.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Backfill` — 13
  today; name the new test so it matches — plus a build with no CA2017 in the output.
- notes: Noticed at T47's baseline, not caused by it; the warning has been in every build since
  T37. `Ao3ShipIndexScraper.cs:548` uses `{Page}` twice over five arguments, and structured logging
  binds positionally, so the second occurrence gets no argument and the operator reads "...to let
  the listing say whether page {Page} should exist." Trivial as a diff — repeat the argument, or
  name the second occurrence differently — but it is the one warning the build prints, and a build
  with a standing warning is a build where the next one arrives unnoticed. Worth checking whether
  any other template in the scraper reuses a name the same way while the file is open.
  **T41 was folded into this task by T28's audit and its entry deleted** — it described the same six
  placeholders over five arguments in the same log line, found at T26's baseline rather than T47's,
  and its verification (`dotnet build` reporting no CA2017) is already half of this one's. The number
  T41 is retired, not reusable; see `DECISIONS.md`.

## T50 — Page 1 accepts the same filtered contradiction page 2 now refuses
- status: todo
- attempts: 0
- blocked-by: none
- delivers: A decision, applied, about what a filtered listing's heading counting more works than
  the run was served entitles page 1 to conclude — and whichever way it goes, a test whose fixture
  is the situation its comment claims.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ao3ShipIndexScraper`
  — 67 today.
- notes: Found by `/code-review high` during T47, in the clause T47 did not touch.
  `PlausiblyTheEndOfTheListing` short-circuits on `page == 1`, and the unfiltered heading check is
  disabled for filtered listings, so a filtered page 1 carrying the listing container, no Next link,
  no blurbs and a heading counting N > 0 concludes `LastPage` with a null error message. The ship
  ingests nothing and the run is recorded a clean success, every tick, indefinitely. That is the
  same contradiction T47 just taught page 2 to refuse, and the same argument applies to it: a
  filtered heading counts the filter's result set, so counting more than the run was served is the
  listing saying there is more.
  **Read T42 and T45 before deciding, because this is their trade a third time.** T42's doc comment
  defends the current behaviour in as many words — "a quiet incremental pass reading zero blurbs
  under a heading is the healthy case and holding it against the page would fail every tick" — and
  that is true of the *unfiltered* heading it was written about, which counts the tag rather than
  the filter's results. Whether it stays true once the number is the filter's own count is the
  question this task settles. Refusing costs one request per tick with a failed run naming the page,
  which is T45's territory and is why that task's bound should probably land first.
  Either way `Reads_a_quiet_filtered_pass_with_a_populated_heading_as_nothing_new` needs work: it
  builds `Page(1, [], total: 4317)` under a watermark, so it asserts that a filtered heading counting
  4,317 matches over zero blurbs served is a healthy end of listing. If the answer here is that page
  1 keeps concluding, the fixture should be `total: 0` — the quiet pass it is named for — and not a
  contradiction standing in for one. Third iteration running that a rule's own test builds a
  different situation from the one its comment cites (T42, T43, and this); it belongs in T28's table
  as a row about tests, not only about rules.

## T51 — A listing blurb's tag list must not delete what a detail fetch added
- status: todo
- attempts: 0
- blocked-by: none
- delivers: A rule, written down and applied, for which observation of a work's tags wins when a
  listing pass and a work's own page disagree — so a fuller tag list survives the next incremental
  pass over the same ship.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ingest` — 7
  today; name the new test so it matches.
- notes: Found by T28's audit (`.devloop/scraper-audit.md` §E8), and the one live cross-task hazard
  it turned up. `WorkIngestor.ApplyTags` (`WorkIngestor.cs:172`) builds `desired` from `blurb.Tags`
  and hands it to `Reconcile`, which deletes every `WorkTag` not in that set — correct while the
  blurb is the only source, and destructive the moment it is not. The spec's user story 11 says a
  listing blurb does **not** carry the complete tag list, which is the entire reason T10 exists; so
  under the spec's own premise every detail fetch is undone by the next incremental pass over that
  ship, silently, on a run recorded as a success. `ApplySeries` and `ApplyAuthors` share the
  reconcile and should be checked for the same exposure, though nothing planned writes them from a
  second source.
  This is T26's shape one layer out — a rule whose destructive half is safe only because of an
  assumption about its input, and the assumption is about to stop holding. Options worth weighing:
  reconcile only within the tag types a blurb is authoritative for; keep a per-`WorkTag` provenance
  column and let a listing pass reconcile only its own rows (a migration on both providers); or make
  the detail fetch the only writer of tags for a work it has fetched. Say which and why in a
  comment, because whichever it is, T10 writes through it.
  Whether a blurb really is short of tags is a markup question of T39's family — but the rule has to
  be decided either way, since T10 is blocked on it and the cost of being wrong is deletion.

## T52 — A run the circuit breaker stopped is recorded as a success
- status: todo
- attempts: 0
- blocked-by: none
- delivers: A run that stopped because the archive was failing is visible as such in the run
  history, rather than recorded `Succeeded` with no error message.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~ScrapeWorker` —
  12 today; name the new test so it matches.
- notes: Found by T28's audit (`.devloop/scraper-audit.md` §F1). `ScrapeWorker.cs:278` reads
  `run.Status = outcome.StopReason == ScrapeStopReason.Error ? Failed : Succeeded`, and
  `ScrapeStopReason.Breaker` is not `Error` — so a run that spent `MaxConsecutiveFailures`
  consecutive refused or timed-out requests, 5-8s apart, and tripped the breaker is recorded a
  success. `HitRequestCap` and `HitTimeCap` are persisted on `ScrapeRun`; `BreakerOpen` has no
  counterpart, so nothing in the history distinguishes it either. An operator watching the Schedules
  page sees green while a ship collects nothing — which is precisely what the comment three lines
  above that ternary says the rule exists to prevent.
  On T15's `blocked-by`: a sweep is the only pass permitted to conclude absence and must conclude
  nothing when interrupted, so "the breaker stopped it" reading as a completed run is the exact
  mis-read that pass cannot afford.
  The narrow fix is the status; consider also carrying `budget.BreakerOpen` onto the run beside the
  two cap flags, since the `finally` already reads the budget for those (that is a schema change on
  both providers, so weigh it). Do **not** widen this into a review of every stop reason — `cap` and
  `timeCap` are genuinely successful outcomes for a backfill, and `watermark`/`lastPage` are the
  healthy ends. `Breaker` is the one that means the archive was failing.

## T53 — Which pass a ship gets is pinned by no test
- status: todo
- attempts: 0
- blocked-by: none
- delivers: Tests over `ScrapeWorker`'s mode choice: a `NotStarted` or `InProgress` ship is
  backfilled, and a `Complete` or `Failed` one falls back to its incremental pass.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~ScrapeWorker` —
  12 today; name the new tests so they match.
- notes: Found by T28's audit (`.devloop/scraper-audit.md` §A1) — the top row of the whole table,
  and nothing pins it. `ScrapeWorker.cs:234` is one ternary, and three separate comments in
  `Ao3ShipIndexScraper` lean on its second half: "the ship keeps its incremental pass (see
  ScrapeWorker's mode choice, which backfills only a NotStarted or InProgress ship), so it goes on
  collecting new works" is what makes T37's give-up survivable and what makes T46's stall a defect
  rather than a nuisance. A sentence three fixes rest on should not be a claim in a comment.
  `ScrapeWorkerJobIsolationTests` already has the seam: a fake `IAo3Scraper` recording the
  `ScrapeContext` it was handed, and `LibraryTestHost` for the ship rows. Assert the mode on the
  context, and on the `ScrapeRun` row, for all four `ShipBackfillState` values — the fourth,
  `Failed`, is the one nothing has ever exercised end to end.

## T54 — An unreadable date must not erase the date a working pass read
- status: todo
- attempts: 0
- blocked-by: none
- delivers: A test that fails if `WorkIngestor` ever writes `DateTime.MinValue` over a revision
  timestamp an earlier pass stored.
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Ingest` — 7
  today; name the new test so it matches.
- notes: Found by T28's audit (`.devloop/scraper-audit.md` §E4). `WorkIngestor.Apply`
  (`WorkIngestor.cs:124`) writes `UpdatedAt` only when the blurb's reading is above `MinValue`, and
  its comment names the cost of getting it wrong: "overwriting a real timestamp with MinValue would
  drag the ship's watermark backwards and re-ingest the tag". Only the *first-seen* branch is pinned
  (`Marks_a_work_an_incremental_pass_first_saw_undated_as_having_an_approximate_date`); the
  preservation branch — ingest a work with a good date, ingest the same work again from a blurb
  whose date will not parse, assert the stored timestamp is untouched — is not.
  Test-only, no production change expected; if writing it turns up a real defect, that is a separate
  task and this one still lands. The reason it is queued rather than left as a note: this loop has
  now twice deleted a test that pinned a defect instead of the rule (T43, T47), and an untested
  protective branch is the same failure one step earlier — nothing would fail if a refactor dropped
  the guard. Assert `UpdatedAtIsApproximate` too, since the same branch sets it.

## T55 — A failed credential fetch leaves the AO3 login block loading forever
- status: todo
- attempts: 0
- blocked-by: none
- delivers: The instance-AO3-login block reports a failed load the way the identity block above it
  already does, instead of rendering "Loading…" permanently.
- verification: `cd frontend && npm run build && npm run lint`, plus a live check on a throwaway
  instance with the endpoint failing (stop the API after the page loads, or point the dev server at
  a port with nothing on it, and reload).
- notes: Found by `/code-review low` at T28's review step, in T3's committed code, and verified by
  reading. `AdminScrapingPage.tsx:31` is `const loadCredential = () => api.getInstanceAo3Credential().then(setCredential)`
  and its only caller (`:35`) catches into `setLoginError`. `credential` therefore stays `null`, and
  the render branch at `:212` is `credential === null ? <p>Loading…</p> : …` — so a rejected fetch
  shows an error *and* a spinner that never resolves, with nothing to retry it. The identity block
  thirty lines earlier has the same shape and gets it right: `:103` renders `error ? … : <p>Loading…</p>`,
  so the loaded-vs-failed distinction is made there and not here. Copy that shape, or give
  `credential` a third state; a retry button is worth considering since neither block has one.
  **Same file as T36** — which is about the gate blockers rendered under "What AO3 currently sees" —
  and a different defect. Not folded, but whoever takes either should read both and may land them
  together; they are two small edits to the same page's login and gate rendering. Obsidian CSS
  variables only.
