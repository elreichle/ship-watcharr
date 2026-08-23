# Tasks

Status vocabulary: `todo` (not started) · `in_progress` (claimed by an iteration, may be
half-finished) · `done` (verified green and committed) · `blocked` (3 failed attempts, or
a dependency cannot be met — needs a human).
`attempts` counts failed verification runs on this task.

## Run order

Tasks are **not** taken in file order. Take the first `todo` listed here whose `blocked-by` are all
`done`; only when this list is exhausted does file order apply.

1. T37, then T25 — settle what a cursor pointing past a shrunken listing concludes, then fix the
   404 branch under that rule. T25 is now its 404 half only; T34 took the other.
2. T26 — the last of the pre-loop scraper defects
3. T30 — what is left of the authenticated-total flag, after T29 paired it with the total
4. T28 — the audit of the walking and stopping rules, while that code is still fresh
5. then file order, from T6

Why this list exists at all: every entry was found by review of pre-loop scraper code, so none of it
appears where the original plan put it — without this list, file order would send an iteration to T6
and leave the scraper's live defects in place. T23, T24 and T27 — the three that made this app
re-request AO3 in a loop, the one thing its politeness rules exist to prevent — are done. The
reasoning is in `DECISIONS.md` under the T23 and T27 reviews.

T30 stays in the audit's `blocked-by` — it and T29 both decide what a pass writes back to the ship,
which is what T28 tabulates — but it is now the smaller half of itself, T29 having paired the flag
with the total it describes. T31, T32, T33, T35 and T36 are real but slow-acting or latent and sit
in file order, after the planned work.

Delete an entry once its task is `done`. When this section is empty, delete the section.

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
- blocked-by: T28, T29, T30
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
- status: todo
- attempts: 0
- blocked-by: none
- **Re-scoped by T34**, which took half (2). See the struck-through note below.
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
  **Do not write this fix without reading T37 first.** T34's review found that `page == 1` on the
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
- status: todo
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
- status: todo
- attempts: 0
- blocked-by: T24, T25, T26, T27, T29, T30, T34, T37
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
- status: todo
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
- verification: `PATH="$HOME/.dotnet:$PATH" dotnet test --filter FullyQualifiedName~Authenticated`
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
- status: todo
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
