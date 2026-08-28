# Journal

Append-only. One entry per iteration, newest last.

> The 29 entries before this point live in [`JOURNAL-archive.md`](JOURNAL-archive.md) (2026-08-22 — init → 2026-08-25 — T13 Confirm AO3's real download URLs — done). Do not read it end to end; `grep` it for a task id when a live entry points into it.

## 2026-08-25 — T61 The login POST must not send the password to whatever host the form names — done

- did: `Ao3SessionEstablisher` refuses to post the credential anywhere but the configured archive.
  The form action is resolved as before and then measured against `LoginPath` under
  `Ao3HttpClientOptions.BaseUrl` — whole origin, not host — and a mismatch is a `Failed` login that
  names the address it refused. The same origin comparison replaced the host comparison in
  `Ao3DownloadLinks.Resolve`, which the review found still permitted a scheme downgrade.
- files: `Api/Services/Scraping/{Ao3SessionEstablisher,Ao3DownloadLinks}.cs`,
  `Tests/{Ao3LoginEstablisherTests,Ao3DownloadLinksTests}.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3Login` → 100 passed (97 before, 99 with the two
  tests the previous iteration left in the tree, one of them red); `--filter ~Download` → 81 (80
  before); `dotnet test` → 645 passed (641 before); `npm run build` + `npm run lint` → clean, the
  two known fast-refresh warnings only. Two mutations: removing the establisher's check reds both
  refusal tests, and narrowing either comparison from `GetLeftPart(UriPartial.Authority)` back to
  `.Host` reds exactly the downgrade test on that side and nothing else.
- commit: a1e0e02
- next: **T14 is next in plain file order** — its blocker (T12) is done, and T12's journal entry
  lists what it inherits. T10 is earlier and still blocked by T51.
- **This iteration inherited a claimed task, which is the mechanism working.** T61 was
  `in_progress` with two tests in the working tree and no fix — a previous turn marked it and died
  after writing the red test. Nothing was lost: the mark said where to look, the diff said how far
  it got, and the first thing this turn ran was that red test. The tests were kept as written.
- **The comparand was the decision, not the check.** The obvious move is to copy T12's twin exactly
  and compare the action against the page it came from — but the login page is fetched by the
  transport that *does* follow redirects, so `page.FinalUrl` is a value the archive's own redirects
  can move, and `Absolute` already resolves a relative action against `BaseUrl` regardless of where
  the page ended up. Measuring against the configured archive is both stricter and more consistent
  with what a relative action already does. A twin is not always a copy.
- **The origin, not the host, and that difference turned out to be live.** The download check
  compares hosts, which is enough for a cookie already scoped to one; a plaintext password is not,
  so this one compares scheme, host and port together. The review then found the same gap in the
  download path — an `http://` link on an https page keeps the host and gets the session cookie in
  the clear — so the stricter rule went there too. **The check that was copied from the download
  path came back stricter and fixed it.**
- **The sweep the previous entry asked for was actually run.** T13's journal recorded the lesson
  "when a fix comes with a general rule, grep for the rule", and this task existed because nobody
  had. Every outbound call site in the API is now accounted for: `ShipVerifier` and
  `Ao3ShipIndexScraper` build URLs from `BaseUrl` and a tag name; `DownloadFetcher` builds the work
  page URL and fetches one checked link; the login POST is this task. Exactly two fetch targets in
  this codebase come out of markup and both are checked. Other hrefs the parsers read
  (`Ao3BlurbParser`'s work, pseud and series links, `Ao3LoginPage`'s greeting) are parsed for ids
  and names and never fetched — worth knowing so the next sweep is shorter.
- **A refused action is a refused login, and inherits the backoff.** `Ao3SessionProvider` records it
  through `Ao3LoginBackoff` (5 → 15 → 30 → 60 minutes, lifted the moment the operator saves the
  credential again), so a login page whose form has moved is not re-fetched every sixty seconds.
  Nothing had to be added for that; it is worth writing down because it is the difference between a
  security check and a security check that hammers the archive.
- **The review found five and four are queued** — see the T61 review entry in `DECISIONS.md`. Its
  finding 3 is T67, already on the list from T13's review and now verified rather than reported;
  T69, T70 and T71 are new and all in T12's download code. The one folded in was the one this
  task's own doc comment had already claimed to be true.
  Filters checked to bite, per T22's lesson: `~Ao3Login` matches 100 and covers all three new
  establisher tests; `~Ao3DownloadLinks` matches 19 and covers the downgrade test. Still zero and
  still suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.

## 2026-08-26 — T14 Downloads in the UI — done

- did: `GET /api/downloads/{id}/file` streams a completed request's bytes with a sanitized
  filename; a Downloads page lists the reader's queue with live status, size, the failure message
  and Save/Try again/Remove; five format buttons and this work's requests sit on a work's own page.
  One `useDownloads` hook behind both pages — the list, the ask, the drop, and polling that runs
  while anything is `Pending` or `Downloading` and stops on the load that finds nothing is.
- files: `Api/Controllers/DownloadsController.cs`, `Tests/{DownloadsControllerTests,LibraryTestHost}.cs`,
  `frontend/src/hooks/useDownloads.ts`, `frontend/src/pages/DownloadsPage.tsx`,
  `frontend/src/components/WorkDownloads.tsx`, `frontend/src/downloads.ts` (all new),
  `frontend/src/{App.tsx,index.css,api/client.ts,api/types.ts,components/navigation.ts,pages/WorkDetailPage.tsx}`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only;
  `dotnet test --filter FullyQualifiedName~DownloadsController` → 52 passed (26 before);
  `--filter ~Download` → 107 (81 before); `dotnet test` → 671 passed (645 before). Seven mutations,
  every one red on the test that names it: dropping the containment check, dropping the ownership
  scoping, dropping the missing-file check, using the raw title (nine cases red, and the Russian one
  green — which is what that case is for), taking the length cut without the surrogate check,
  judging the title one UTF-16 unit at a time, and naming HTML as `text/html`.
- commit: 34ca322
- next: **T18 (Statistics API) is next in plain file order** — its blocker (T6) is done. Everything
  between is blocked: T15 still waits on T38, T40, T46 and T52; T16 and T17 wait on T15. T10 is
  earlier still and still blocked by T51.
- **The live check ran end to end against a stub, and it is worth rebuilding rather than
  re-deriving.** `scratchpad/live/` holds `stub.js` (a node stand-in for AO3: the login fixture with
  its relative form action, the captured work page with its download hrefs rewritten to the stub's
  own origin, and an EPUB payload) and `browse.js` (chrome-headless-shell over CDP). The API ran on
  :5312 with a scratch data directory, `Ao3HttpClient__BaseUrl` at the stub and the delays at
  0.2–0.4s; vite on :5313 with `BACKEND_URL` pointed at it. **Two things it needed that are not
  obvious.** The captured work page names `archiveofourown.org` in its download menu and T12's
  origin check refuses a link that is not same-origin with the page it was read from, so the stub
  has to rewrite those hrefs — rewriting them is what *exercises* the check rather than dodging it.
  And the headless shell defaults to a window narrow enough that the sidebar rails and renders no
  child links at all; `--window-size=1400,900` is what made the Downloads entry visible.
- **What the live check proved, in the stub's own log:** login page, login POST, work page with
  `view_adult=true`, then the file — four requests, every one carrying
  `ShipWatcharr/0.1 (+contact: …; instance/…)`. The bytes that came back out of
  `/api/downloads/1/file` were byte-for-byte the stub's payload, under
  `Content-Disposition: attachment; filename="We Chose to Wait a live check test.epub";
  filename*=UTF-8''…` — from a title of `We Chose to Wait: a "live" check / test`. Clicking MOBI in
  the browser went `Fetching…` → `Failed · AO3 answered 404 for the file itself` **without a
  reload**, which is the polling and the per-half failure message both working in one go.
- **Seeding a library for a live check does not need the scraper.** Ship, WatchedShip, Work and
  ShipWork went into the scratch SQLite file with python's `sqlite3` while the API held it open.
  There is no `sqlite3` binary on this machine; python has the module built in. Use the work id the
  captured page is of (70441196) so the address `DownloadFetcher` builds is one the stub answers.
- **The review did not run, and this diff is the least-reviewed on the branch.** `/code-review high`
  was launched and died on the account's monthly spend limit before reading anything. The pass was
  done by reading the diff instead. It found three: `Html` falling through to the `_` arm rather than
  being named (an exception should read as a decision, and the fallback should stay what it says it
  is); the sanitiser judging chars rather than runes, which reduced any title outside the basic
  plane to `work-{id}`; and a re-requested row jumping to the top of the list and back down on the
  next poll, because re-arming does not change `RequestedAt`. All three are fixed here. A later
  branch-wide review should read this diff first.
- **One mutation survived and the fix is the same shape as T12's.** Dropping
  `Status != DownloadStatus.Complete` from the file endpoint's guard left the suite green, because
  `Arm` moves the status and the file reference together and no test could construct a queued
  request that still named bytes. That state is precisely what **T59** is deciding whether to
  create. `Will_not_serve_a_request_that_is_queued_while_still_naming_a_copy` constructs it directly
  and T59's notes now point at it — the guard is what stops "keep the reference" from meaning "serve
  the previous version as the answer to the refetch".
- **The path check nothing can currently trip.** `RelativePath` is written only by
  `DownloadPaths.Relative`, out of a work id and an enum, so it cannot escape the data directory.
  It is checked anyway: this endpoint is the one place in the app where a value read out of the
  database becomes a file handed to whoever asked, and a row naming `../../etc/passwd` would be
  served in full to any signed-in reader. **The branch's own lesson, applied one input over** — T61
  swept every URL read out of markup; this is the same rule for a path read out of storage.
- **Six leaked API processes from earlier iterations are still running** (pids 650339, 651962,
  663142, 783152, 993804, 1004997 at the time of writing), each a `dotnet run` throwaway a previous
  live check never killed. Left alone rather than cleaned up, because they are not this task's and
  killing by pattern is what the "never pkill" rule exists to prevent. Worth an operator's attention.
  This iteration's own three (API, vite, stub) were killed by pid; the systemd dev instance was not
  touched.
  Filters checked to bite, per T22's lesson: `~DownloadsController` matches 52 (26 before) and
  covers every new test; `~Download` matches 107. Still zero and still suspect: T31 `~TotalWorks`,
  T32 `~Monotonic`. T40's `~PagesFetched` is zero by design.

## 2026-08-26 — T18 Statistics API — done

- did: `GET /api/stats` answers with two lenses over the caller's library — `corpus` (totals, works
  per month of last revision, the AO3 rating mix, kudos and word-count histograms, the ten most
  prolific creators) and `reading` (status mix, words read, and the reader's half-stars laid against
  what the archive made of the same works) — plus a `ships` table where the two meet, one row per
  watched ship carrying both its corpus size and how much of it this reader has marked. `?shipId=`
  narrows every figure and 404s on a ship the caller does not watch. Query-only, no schema, no
  migration.
- files: `Api/Data/StatsQueries.cs`, `Api/Dtos/StatsDtos.cs`, `Api/Controllers/StatsController.cs`,
  `Tests/StatsControllerTests.cs`, `Tests/StatsQueryTranslationTests.cs` (all new),
  `Api/Data/WorkQueries.cs`, `Api/Controllers/WorksController.cs`, `Tests/LibraryTestHost.cs`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Stats` → 62 passed (0 before — the filter matches
  nothing that existed, checked per T22's lesson); `dotnet test` → 733 passed (671 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Fourteen
  mutations, every one killed by the test that names it — dropping the 404 guard, reading every
  user's states instead of the caller's, turning the bucket comparison into `<`, restricting the
  status mix to works with a state row, an unweighted average rating, zero instead of null for an
  empty mean, dropping ships the aggregate returned nothing for, ordering ships by display text,
  scoring an unrated work as zero, ranking the least prolific first, counting a crossover once per
  ship in the corpus total, folding the per-ship read count over every status, and lifting the
  top-ten cap.
- commit: 119563f
- next: **T19 (the Statistics page) is next in plain file order** and its blocker is now done. Read
  T18's section in `tasks.md` for the response shape — the short version is that every fixed
  vocabulary arrives zero-filled in a stable order so a chart can index by position, buckets carry
  their bounds as well as their label, and averages are null rather than zero for an empty library.
  Everything between T14 and T18 is still blocked; T10 is earlier still and still blocked by T51.
- **One mutation survived on the first pass, and the surviving one named a real gap in the test
  rather than in the code.** Reading an unrated work as a zero left the suite green, because the
  test for it seeded a work with *no state row at all* — which the mutation also skips. The state
  that catches it is a row that exists with a status and a null rating, which is what marking
  something read without scoring it produces, and is the common case for any reader who marks more
  than they rate. The test now constructs it. Same shape as T14's surviving mutation: the guard was
  right and no test could reach the state it guards.
- **The per-ship figures were written correlated and had to be rewritten grouped.**
  `library.Count(x => x.Ships.Any(sw => sw.ShipId == w.ShipId))` per figure per watched ship reuses
  `WorkQueries.Library` verbatim, reads like a sentence, and compiled to **eighty-two lines of SQL**:
  five full subqueries over the works table, re-executed once per ship. Replaced with one grouped
  pass joining the library to `ShipWorks`, with the caller's status and rating looked up once per row
  and folded. Checking the generated SQL cost one throwaway test; it is worth doing for any query on
  this codebase that reads too well.
- **`ToQueryString()` under Npgsql is a new kind of test here and it earned its place twice.** No
  PostgreSQL server exists in this loop's shell, so "translates on both providers" was going to be an
  assertion rather than a check. `StatsQueryTranslationTests` builds a `PostgresAppDbContext` on a
  connection string it never opens and compiles each query through the real Npgsql translator. Its
  coverage list is read off `StatsQueries` by reflection rather than maintained by hand — and that
  caught a query I had added without a case, during this task, exactly as intended.
- **The live check ran against a throwaway instance and is worth repeating for any API task.** API
  on :5321 with a scratch data directory, registered through `/api/auth/register` with a cookie jar,
  library seeded straight into the scratch SQLite file with python's `sqlite3` (per T14's note —
  there is no `sqlite3` binary on this machine). It proved the three things controller tests cannot:
  the route is registered and 401s unauthenticated, the whole DTO tree serializes to camelCase JSON
  with no cycle, and the numbers are right end to end — a followed ship with no works listed at zero
  and sorted first by its normalized name, a month series with a real gap in it (January then March),
  a status mix summing to the corpus, `?shipId=` narrowing everything, and 404 for a ship the caller
  does not watch. **The seeding needs the schema's own NOT NULL columns**, which are fewer than the
  model suggests — `pragma table_info` first, do not guess from the entity class. The instance was
  killed by pid.
- **`/code-review high` ran this time**, and its three findings are in DECISIONS: two folded in (the
  bucket-array validation, and one shared predicate for "which ships does this reader watch"), one
  false. It also flagged that the diff was being rewritten under it, which is fair — the `PerShip`
  refactor landed mid-review. Launch the review after the diff has settled.
- **The six leaked API processes from earlier iterations are still running** (pids 650339, 651962,
  663142, 783152, 993804, 1004997 as of T14's entry). Still not this task's to clean up, still worth
  an operator's attention. This iteration's own throwaway was killed by pid; the systemd dev instance
  was not touched.
  Filters checked to bite, per T22's lesson: `~Stats` matches 62 and covers every new test, and
  matched zero before this task. Still zero and still suspect: T31 `~TotalWorks`, T32 `~Monotonic`.
  T40's `~PagesFetched` is zero by design.

## 2026-08-26 — T19 The Statistics page — done

- did: A Statistics view at `/stats`, under Dashboard between Downloads and Schedules. Two lenses
  over the caller's library — six tiles for the corpus, five for their own reading — a per-ship
  table where the two meet with a read-through bar, a column chart of works per month of last
  revision, bar lists for the AO3 rating mix, the kudos and length histograms, the status mix and
  the ten most prolific creators, and a table of the reader's half-stars against what the archive
  made of the same works. `?shipId=` narrows every figure and is in the URL, so a narrowed view is
  linkable. No charting library: every bar is a `width`/`height` percentage on a `<span>` coloured
  from Obsidian tokens.
- files: `frontend/src/pages/StatsPage.tsx` (new), `frontend/src/{App.tsx,index.css}`,
  `frontend/src/api/{client.ts,types.ts}`, `frontend/src/components/navigation.ts`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only;
  `dotnet test` → 733 passed (unchanged — this task touched no backend code); a live check against
  a throwaway instance on :5331 with vite on :5332, driven through chrome-headless-shell.
- commit: 2eba093
- next: **T20 is next in file order and is not selectable** — it waits on T17, which waits on T16,
  which waits on T15, which waits on T38/T40/T46/T52. Nothing between T10 and T20 is selectable
  either. **The first selectable task is T31**, and from there the run is the long tail of scraper
  and UI defects (T31–T71). Those are what unblock T15, and T15 is what unblocks the rest of the
  plan — so the tail is now the critical path, not a cleanup queue.
- **The live check is the reason three of the review's findings could be closed with evidence rather
  than argument, and the recipe is now cheap enough to be routine.** `scratchpad/live/` holds
  `browse.js` (chrome-headless-shell over CDP, using node 22's own `WebSocket` — the previous
  iteration's copy needed `ws`, which is not required). API on :5331 with a scratch data directory
  and `--no-launch-profile`, registered through `/api/auth/register` with a cookie jar, library
  seeded straight into the scratch SQLite file with python's `sqlite3`, vite on :5332 with
  `BACKEND_URL` pointed at the API. Three things it needed that are not obvious: the shell's default
  window is too narrow for the sidebar to render its child links (`--window-size=1400,1000` fixes
  it, as T14 found); `Page.captureScreenshot` shoots the viewport, so a tall page needs
  `Emulation.setDeviceMetricsOverride` first and a region needs `clip` with a `scale`; and reading
  *drawn geometry* rather than text is what makes a chart checkable — `getBoundingClientRect().width`
  per bar caught nothing, but counting columns that own a `.month-bar-fill` caught every empty month
  drawing a bar.
- **Seeding the defect is what verified the fix.** The review's first finding was that one work with
  an unreadable date puts `{year: 1, month: 1}` at the head of the month series, and the page filled
  from there — 24,300 columns. Inserting a work at `0001-01-01` into the scratch database reproduced
  it against the real endpoint, and the same insert then proved the cap: 240 columns, a note reading
  "1 work is dated before September 2006 and is not drawn", and the page still responsive. **The
  server's own comment said this would happen** — `WorksByUpdatedMonth` explains that it refuses to
  zero-fill because a misparsed year would materialise centuries of buckets. It hands the span to
  the client and the client filled it unbounded. A comment explaining why a hazard is *someone
  else's* problem is a comment worth reading as a task for whoever becomes someone else.
- **An empty library has four answers, not one.** No ships followed; ships followed but the AO3
  login is missing so every scrape is held; one ship narrowed to that has nothing in it; and the
  whole library empty with everything configured. The first three were checked live (the login one
  by starting without a credential, the ship one by saving a fake credential and reloading). The
  login wording is lifted from `ShipsPage` deliberately — the same cause said the same way in both
  places, and it names an *admin* to non-admin readers rather than showing them a form they cannot
  use.
- **Two mutations, both caught by the browser rather than the compiler.** Removing the
  `month.workCount > 0` guard makes every gap draw a 1px bar (32 columns, 32 bars, where 11 months
  have works); removing the `Number.isInteger` guard sends `shipId=NaN` and turns `?shipId=abc` into
  an error page under a picker that says "Everything you follow". There is no test runner in
  `frontend/` by decision, so for UI work the live check *is* the mutation test — which is worth
  saying out loud, because "verified by build and lint" would have caught neither.
- **The review's other two findings were older code and are on the list**: T72 is new
  (`Ao3DownloadLinks` measures against the post-redirect page URL, not the configured archive —
  verified here), and the session-login race was already T63 from T13's review as *reported, not
  verified*, so T63 is now marked verified and carries the extra consequence this reviewer named.
  Filing a twin for a defect already on the list is a real cost in a loop that reads the list every
  iteration; T73 was drafted and deleted for that reason.
- **One leaked API process from an earlier iteration is still running** (pid 1963836, `dotnet run
  --project Ao3Tracker.Api`), down from the six T14 and T18 recorded — so somebody has been
  clearing them. Not this task's to kill, and killing by pattern is what the "never pkill" rule
  exists to prevent. This iteration's own three (API, vite, chrome) were killed by pid; the systemd
  dev instance (pid 2033) was not touched.
  Filters checked to bite, per T22's lesson: this task added no tests, so no new filter to check —
  `dotnet test` matched the same 733 as the baseline, which is the assertion that the backend was
  not touched. Still zero and still suspect: T31 `~TotalWorks`, T32 `~Monotonic`. T40's
  `~PagesFetched` is zero by design.

## 2026-08-26 — T31 A singular listing heading must not be read as the tag's name — done

- did: `Ao3BlurbParser.ParseTotalWorks` finds the count with `(?<count>\d[\d,.]*)\s+Works?\b` —
  singular as well as plural, first match rather than last — and answers null when no such number is
  in the heading. The trailing-digits fallback is gone, so a tag name carrying digits can no longer
  donate them to the ship's size and an "Error 404" heading is no longer a tag of 404 works.
- files: `Api/Services/Scraping/{Ao3BlurbParser,Ao3ShipIndexScraper}.cs` (the second is a comment
  only), `Tests/{Ao3BlurbParserTests,Ao3ShipIndexScraperTests}.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ao3BlurbParser` → 46 passed (36 before, and 5 red
  when the tests were written before the fix); `dotnet test` → 743 passed (733 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Three
  mutations: narrowing `Works?` to `Works` reds both singular cases; restoring the trailing-digits
  fallback reds all three "not a work count" cases; taking the last match instead of the first
  survived until a case was written for it, and that case is now in the theory.
- commit: d6045a6
- next: **T32 is next in plain file order** and has no blockers. `dotnet build` emits **CA2017** on
  `Ao3ShipIndexScraper.cs:534` — a log template with more placeholders than arguments — which is
  plausibly T32's own defect wearing a compiler warning; read it before starting. Everything from
  T31 to T71 is the tail that unblocks T15, and T15 unblocks T16/T17/T20, so this tail is the
  critical path rather than a cleanup queue.
- **A surviving mutation named the missing case, again.** Taking the last regex match instead of the
  first left the suite green, because no heading under test had a second "N Works" in it. The one
  that catches it is a *tag name* containing the words — "1 - 20 of 4,317 Works in Prompt: 5 Works
  of Fiction" — which is exactly the class of input this task exists for: everything after "in" is a
  name somebody else chose. Third iteration running where the mutation that survived was a gap in
  the tests rather than in the code.
- **The task's own verification filter matched nothing, and had never matched anything.**
  `--filter FullyQualifiedName~TotalWorks` returns "No test matches" — the journal has been flagging
  it as "still zero and still suspect" every iteration since T22, and this is the first iteration to
  reach the task and act on it. The filter is now `~Ao3BlurbParser`. **Worth generalising: a
  verification command written at planning time is a guess about test names that do not exist yet**,
  and T22's ritual of checking that a filter bites is what turns the guess into a finding. T32's
  `~Monotonic` is the next one on that list, and it is next in file order — expect to have to fix it
  the same way.
- **Removing a fallback closed a second hole nobody was looking at.** The trailing-digits scan was
  what made an AO3 soft error served as 200 (`<h2 class="heading">Error 404</h2>`) offer 404 as the
  tag's size; `Ao3ShipIndexScraper`'s readability guard was the only thing between that number and
  `LastKnownTotalWorks`, and a test at `Ao3ShipIndexScraperTests` line ~1007 exists to pin exactly
  that. The guard stays and the test stays — two independent reasons on a field a full sweep checks
  itself against — but both comments were rewritten, because a comment that says "the parser will
  hand you rubbish, so I guard it" is wrong once the parser stops.
- **The review found nothing in this diff and five in the download code.** Three were already on the
  list (T67, T70, T64 — T64 promoted from *reported* to *verified*, with a worse consequence than it
  had been filed with), two are new (T73, T74). See DECISIONS. Three reviews running have now landed
  most of their findings in T12/T14's code, which is the newest and least-reviewed on the branch —
  T14's own entry predicted this. A dedicated pass over the download path would probably be cheaper
  than meeting it one finding at a time, and is worth considering as a task once the T31–T71 tail
  thins out.
- **One leaked API process is still running** (pid 1963836, `dotnet run --project Ao3Tracker.Api`).
  Not this task's, and killing by pattern is what the "never pkill" rule exists to prevent. This
  task started no processes of its own; the systemd dev instance (pid 2033) was not touched.
  Filters checked to bite, per T22's lesson: `~Ao3BlurbParser` matches 46 and covers all ten new
  cases. `~TotalWorks` matched zero and is retired — see above. Still zero and still suspect: T32
  `~Monotonic`, which is the next task. T40's `~PagesFetched` is zero by design.

## 2026-08-26 — T32 The non-monotonic-boundary warning names the wrong page — done

- did: `TrackBackfillFloor` takes the page it is looking at as a parameter and logs that, instead of
  `ship.BackfillNextPage` — which the line above it has already advanced to `page + 1`, so the
  message named the walk's next stop rather than the page the listing shifted under. Added
  `CapturingLoggerProvider`, the seam the test needed to see a log line at all.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Tests/CapturingLoggerProvider.cs` (new),
  `Tests/Ao3ShipIndexScraperTests.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Monotonic` → 1 passed (red first at "Expected: 2,
  Actual: 3", which is the defect itself); `dotnet test` → 744 passed (743 before); `dotnet build
  --no-incremental` → no new warnings; `npm run build` + `npm run lint` → clean, the two known
  fast-refresh warnings only. Mutation: logging `page + 1` reds the new test.
- commit: 2ab7766
- next: **T33 is next in plain file order** and has no blockers — `.AsSplitQuery()` on
  `WorkIngestor.LoadExistingWorksAsync`, the cheapest task on the list, verified by the existing
  ingest tests staying green. **Check `~Ingest` bites before trusting it.** The build's one code
  warning, CA2017 on `Ao3ShipIndexScraper.cs:536`, is **T49's** and was left alone on purpose.
- **The predicted dud filter was a dud, and that is now three in a row.** T31's entry said
  `~Monotonic` was "still zero and still suspect" and would need fixing the same way `~TotalWorks`
  did. It matched nothing, because no test existed — the task was filed as cosmetic, and a cosmetic
  task gets a verification command nobody expects to have to write code for. It bites now. Filters
  checked this iteration: `~Monotonic` 1, `~Backfill` 13, `~Download` untouched. The ritual is
  earning its keep; keep doing it first, not last.
- **The seam this task needed is the interesting part, not the one-argument fix.** `TrackBackfillFloor`
  writes nothing and returns nothing — the log line *is* the behaviour — so there was no way to pin
  it without capturing a record, and `LibraryTestHost` has never had a logging provider. The obvious
  move (a provider that formats, like the console one) would have thrown on
  `RetreatFromStaleCursor`'s six-placeholders-over-five-arguments template and made T49 a
  prerequisite of T32. `CapturingLoggerProvider` therefore **never calls the formatter**: it keeps
  the structured values and lets a test ask what `{Page}` was bound to. **T49 needs the opposite
  seam** — its defect only exists at format time — and must add a second, opt-in one rather than
  make this one render.
- **A stopping rule whose only output is a log line has no other test seam.** Worth remembering for
  the rest of the T33–T71 tail: several of those tasks are about what an operator gets told, and
  they can all use this provider now. Assert on the named value, not on the sentence.
- **The line is `LogInformation`, though every reference to it calls it a warning** — the task title,
  its notes, and the surrounding comments. Not changed: T32's delivers is the page number, and the
  level is a separate judgement about how loudly a shifted listing should announce itself. Someone
  deciding that should decide it for the whole file at once; the test's local variable was renamed
  from `warning` to `boundary` so the tests at least stop asserting something untrue in their names.
- **The review found nothing in this diff and four elsewhere; three were already filed.** T70, T64
  and T74 were all re-derived independently — T70 with a sharper consequence (`MaxConsecutiveFailures
  = 3` means a one-minute AO3 blip permanently fails exactly the first three queued downloads before
  the breaker starts holding the rest). One is new: **T75**, the login cooldown measured from before
  the round trip, verified by reading `Ao3SessionProvider`. **Four reviews running have landed nearly
  every finding in the download path** — T64/T67/T70/T71/T73/T74 are six open tasks over the same few
  hundred lines. A single dedicated pass over that subsystem is now clearly cheaper than six
  iterations; worth proposing as a task rather than repeating this observation a fifth time.
- **The leaked API process is still running** — pid 1963836 (`dotnet run --project Ao3Tracker.Api
  --no-launch-profile`) and its child 1964058, up 10h26m, first noted in T31's entry. Left alone
  again: it is not this task's, and killing by pattern is what the "never pkill" rule exists to
  prevent — the pattern matches the systemd dev instance (pid 2033, up 3d) too. If a future
  iteration wants the port back, kill **1963836 by pid**, not by name. This task started no
  processes of its own and did not touch the dev instance.

## 2026-08-26 — T33 One page of known works should not read a cartesian product — done

- did: `WorkIngestor.LoadExistingWorksAsync` says `.AsSplitQuery()`, so the three collection
  `Include`s stop multiplying out — a page of twenty known works with ~15 tags, ~2 authors and ~1
  series each was reading a three-way `LEFT JOIN` product with every `Works` column repeated in each
  row, on every incremental pass over works that had not changed. Added the second-pass test that
  covers all three joins, which is the invariant the `Include`s exist for and which `Work.Series` had
  nowhere.
- files: `Api/Services/Scraping/WorkIngestor.cs`, `Tests/WorkIngestorPseudTests.cs`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~Ingest` → 9 passed (8 before); `dotnet test` → 745
  passed (744 before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings
  only. Mutation: dropping `Include(w => w.Series)` reds the new test with a `DbUpdateException`.
- commit: 50f5941
- next: **T35 is next in plain file order** (`NormalizedPseudIdentity` collides on a third
  capitalisation) and has no blockers. Its own notes name the hard part: getting a test around a
  migration, and the seam is undecided — settle that before writing the fix, and do **not** rewrite
  the applied migration in place. Its filter is `~Pseud`, which currently matches this file's 9 (it
  bites, but on tests that say nothing about migrations — expect to add the test the filter is
  supposed to be about).
- **The verification filter bit, for the first time in four tasks.** `~Ingest` matched 8 before the
  change and 9 after. T31 and T32 both opened with a filter that had never matched anything; this one
  matched, and matched tests that actually walk the code path (three of the eight are second-pass
  re-ingests). The ritual costs one command and has now paid twice and cleared once — keep doing it
  first.
- **A behaviour-preserving task has no red-first test, so the evidence has to come from somewhere
  else.** Two places, both cheap. A throwaway probe printed `ToQueryString()` with and without the
  split against the **Postgres** provider: without, one statement with three stacked `LEFT JOIN`s;
  with, the root query plus EF's own note that more follow, and an `ORDER BY w."Id"` EF adds itself.
  That probe is also why no Npgsql translation test was added — the translator was watched accepting
  it. And the new test is what makes the diff worth committing: it pins the rule the query's comment
  states, on the leg nothing tested.
- **`Work.Series` was the untested third of a three-part invariant, and nothing said so.** Tags and
  authors each had a second-pass test; series had none in any file, and the failure mode is loud
  (`DbUpdateException` on the unique key) rather than silent, so it would have surfaced in
  production as a broken pass rather than as quiet data loss. Worth generalising: when a comment
  says "all three of these must X", check that three tests exist, not that the comment is true.
- **The class doc was widened instead of starting a second test file.** `WorkIngestorPseudTests` is
  named for pseuds and is really about join reconciliation across re-reads; a series-shaped twin
  would have duplicated its 30-line blurb helper. The name is now the wrong half of the truth and
  the doc is the right one — a rename is a task for whoever next touches the file.
- **The review found nothing in this diff and four elsewhere, and all four were already on the list**
  (T64, T63, T67/T73, T74) — the first review in five to add no task. It also re-derived a worse
  consequence for T64: two concurrent fetches mean two `File.Move`s onto one destination and two
  `WorkDownloadFile` inserts, only the second race-handled. **Five reviews running have landed in the
  download path**, which is now nine open tasks (T63/T64/T67/T69/T70/T71/T73/T74/T75) over a few
  hundred lines. Three iterations have observed that one dedicated pass would be cheaper; it is now
  written into the run order as something to *file*, because observing it again is the pattern.
- **Leaked processes, unchanged and growing older.** pid 1963836 (`dotnet run --project
  Ao3Tracker.Api --no-launch-profile`, 10h38m) and the chrome-headless-shell tree from T19's live
  check (1994238 and its children, plus 1996041, ~10h). None of them this task's; killing by pattern
  is what the "never pkill" rule exists to prevent, since the pattern also matches the systemd dev
  instance (pid 2033, up 3d08h). Kill **by pid** if a future iteration wants the port or the memory.
  This task started no processes of its own and did not touch the dev instance.
  Filters checked to bite, per T22's lesson: `~Ingest` 8 → 9, covering the new test. Still zero and
  still suspect: none known — T31 retired `~TotalWorks` and T32 fixed `~Monotonic`. T40's
  `~PagesFetched` is zero by design.

## 2026-08-26 — T35 The pseud dedup migration collides on a third capitalisation — done

- did: `NormalizedPseudIdentity`'s merge now thins each work's author links to one per creator —
  keeping the lowest pseud id present in the normalized group — before repointing what is left at
  the group's `MIN(Id)`. The old `DELETE` only dropped a link when the work was *already* linked to
  the canonical row, so three spellings with the work linked to the second and third dropped
  nothing and the repoint then moved two links onto one row, failing `PK_WorkAuthors` mid-upgrade.
  Corrected in both providers, in place.
- files: `Api/Data/Migrations/Sqlite/20260822182752_NormalizedPseudIdentity.cs`,
  `Api/Data/Migrations/Postgres/20260822182800_NormalizedPseudIdentity.cs`,
  `Tests/PseudMigrationTests.cs` (new), `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter FullyQualifiedName~PseudMigration` → 4 passed (2 red first, both with
  `SQLite Error 19: UNIQUE constraint failed: WorkAuthors.WorkId, WorkAuthors.PseudId` — the defect
  itself); `--filter ~Pseud` → 13 (9 before); `dotnet test` → 749 passed (745 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Mutation:
  replacing the normalized-group match with `1 = 1` reds
  `Two_different_creators_on_one_work_both_survive` and nothing else.
- commit: baf862d
- next: **T36 is next in plain file order**, `blocked-by: none`. Two things this iteration filed that
  change what the run looks like: **T76**, a verified defect in the same migration (below), and
  **T77**, the download-path pass the run order has been asking three iterations for — take T77
  **instead of T63** when the run reaches T63, not from its position at the end of the file.
- **The task's warning was conditional and the condition was false.** Its notes said not to rewrite
  an applied migration *if it has shipped anywhere*; `git branch --contains` says this one exists
  only on `devloop/dashboard-completion`. So it was corrected in place. What makes that safe is
  narrower than "it is a dev branch": the old merge either produced the right answer or threw and
  rolled back, so no database can be holding a wrong state that a repair migration would have to
  find. Check that property, not the branch name, before doing this again.
- **The suite could not reach migration SQL at all until this task.** Everything starts from
  `EnsureCreated`, which builds today's schema and executes no migration. `PseudMigrationTests`
  takes `IMigrator` off the context's infrastructure, migrates to the revision *before* the one
  under test, seeds with `ExecuteSqlRaw` (the entity model describes today's schema and cannot
  write a row to yesterday's), and migrates one step. It targets the migration by name rather than
  migrating to latest, so a future migration cannot red it for unrelated reasons. **Any of the
  remaining migration-shaped defects can use this now.**
- **The review died on the account's monthly spend limit — and its last thought was the best thing
  it produced.** Second review lost this way after T14's; the limit resets at 22:20 America/Chicago.
  The notification carried one sentence of the agent's reasoning: *"Let me empirically verify a
  suspicion about the migration's pseud deletion cascading."* Chased by hand, that is **T76**, and
  it is filed as verified rather than reported: `SavedWorkFilterAuthors` has a `PseudId` FK to
  `Ao3Pseuds` declared `onDelete: Cascade`, the migration repoints `WorkAuthors` only, and so the
  final `DELETE FROM "Ao3Pseuds"` silently takes a saved filter's author criterion with the loser.
  A throwaway probe through the new seam confirmed it — `PRAGMA foreign_keys` reads `1`, pseuds
  merged to `[1]`, `SavedWorkFilterAuthors` came out **empty**. **Read what a dead agent was doing
  when it died before concluding the diff went unreviewed.**
- **T76 was not folded into this task**, though the file was open and the pattern was already
  written. T35 delivers "the upgrade does not fail"; T76 is an upgrade that succeeds and loses data,
  in a different table, and it needs a judgement T35 does not — what `Exclude` means when an include
  and an exclude of one creator merge onto one row. The seam is what makes it cheap for whoever
  takes it.
- **Only SQLite was executed.** The Postgres twin's changed block is byte-identical (`diff`) and
  `dotnet ef migrations script --context PostgresAppDbContext` renders it, so the C# is right and
  the SQL is what was intended — but no PostgreSQL server ran it. Not new to this task; it is how
  every migration on this branch stands. The correlated references were moved out of the subquery's
  `JOIN ... ON` clauses into its `WHERE` so the form needs no argument about where PostgreSQL allows
  an outer reference without `LATERAL`.
- **`Position` comes out of the merge with gaps** — a work linked to spellings at positions 0 and 1
  keeps position 0. Harmless: `WorkIngestor` rewrites every position from blurb order on the next
  ingest (`WorkIngestor.cs:206`), and nothing reads Position expecting it to be contiguous.
- **Leaked processes, unchanged.** pid 1963836 (`dotnet run --project Ao3Tracker.Api
  --no-launch-profile`) and its child, plus T19's chrome-headless-shell tree (1994238 and children,
  1996041). None of them this task's; kill **by pid** if a future iteration wants the port. Killing
  by pattern is what the "never pkill" rule exists to prevent — the pattern matches the systemd dev
  instance (pid 2033) too. This task started no processes of its own and did not touch it.
  Filters checked to bite, per T22's lesson: `~Pseud` matched 9 before (all `WorkIngestorPseudTests`,
  none about migrations — as predicted) and 13 after; `~PseudMigration` matches the 4 new ones.
  Still zero and still suspect: none known. T40's `~PagesFetched` is zero by design.

## 2026-08-27 — T36 The scraping-identity page shows blockers that are not about identity — done

- did: "What AO3 currently sees" now renders `identityProblem` — the identity gate's blocker alone,
  a new field on `ScrapingGateState` and the DTO — instead of `problem`, which is every blocker
  joined; and the login callout above it lost its `identity.identityConfigured &&` guard. A fresh
  install missing both used to print "No AO3 login is stored for this instance" inside the block
  about the User-Agent while the callout that exists for the login was suppressed.
- files: `Api/Services/Scraping/ScrapingGate.cs`, `Api/Dtos/AdminDtos.cs`,
  `Api/Controllers/AdminScrapingController.cs`, `Tests/ScrapeWorkerGateTests.cs`,
  `frontend/src/api/types.ts`, `frontend/src/pages/AdminScrapingPage.tsx`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter ~ScrapeWorkerGate` → 9 passed (7 before); `dotnet test` → 751 passed
  (749 before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only;
  a live check on a throwaway instance (API :5341, vite :5342, chrome-headless-shell) through all
  three states. Mutations: propagating every blocker as `IdentityProblem` reds all three new backend
  assertions; putting both frontend defects back reproduces the exact symptom in the browser.
- commit: 9004c7d
- next: **T37 is `done` already; the next `todo` in plain file order is T38** (an operator way back
  out of a failed backfill), `blocked-by: none`. T10 is earlier and still blocked by T51. Remember
  **T77 is taken instead of T63** when the run reaches T63.
- **The task's two suggested fixes were not alternatives.** Its notes said "either render only the
  identity blocker in that section, or drop the `identityConfigured &&` guard — the second is
  smaller", but its `delivers` names both outcomes, and dropping the guard alone leaves the identity
  section still printing the login blocker. Worth generalising: **when a task's notes offer a
  cheaper option than its `delivers` describes, `delivers` is the contract** — the notes are one
  reader's guess at the implementation, written before anybody opened the file.
- **The page had no honest way to render half of `problem`.** `ScrapingGateState` exposed `Blockers`
  (never sent to the client) and `Problem` (joined by `\n\n`), so the alternative to a new field was
  splitting the joined string in the page — putting the separator and the blocker order into the
  frontend, which is the drift the gate's class doc exists to prevent. See DECISIONS.
- **The live check is the mutation test for UI work, again.** There is no test runner in `frontend/`
  by decision, so both frontend defects were put *back* and the page reloaded: the callout vanished
  and the login blocker reappeared under "What AO3 currently sees", which is the defect verbatim.
  Restoring the fix restored the correct render. The recipe is now three iterations old and cheap:
  `browse.js` from T19's scratchpad adapted in about ten minutes, node 22's own `WebSocket`, no
  `npm install`. **A fresh install is already the both-missing case** — registration takes no email,
  the contact falls back to the admin's email, so there is nothing to arrange: register and look.
- **`problem` is now read by no frontend code.** Left in the DTO and the TS interface deliberately:
  it is the endpoint's honest "every reason at once" view and the worker's log uses the same shape.
  Deleting a field because its one consumer stopped needing it would narrow the API to today's page.
- **The review ran to completion for the first time in three tasks, and found nothing in the diff.**
  It stated T36's invariant back in its own words — `IdentityProblem` non-null exactly when
  `IdentityConfigured` is false, still `Blockers[0]`, one construction site — which is the assertion
  worth having from a reviewer that read the whole branch. Its four other findings produced **no new
  task**: two are T40 and T57 with sharper notes now folded in, one is T63 (six reviews running,
  absorbed by T77), and one was a claim that T35 rewrote an already-applied migration. That last one
  is checkably wrong on its premise — `git log --diff-filter=A` puts the migration's commit on this
  branch only — and argues past T35's real reasoning, which was the *rollback* property rather than
  the branch name. See DECISIONS. **Worth generalising: a review that reads the whole branch will
  re-derive the list, and its findings need checking against the list and against the journal before
  being filed** — the second check is the one that caught this.
- **Leaked processes from earlier iterations, unchanged.** pid 1963836 (`dotnet run --project
  Ao3Tracker.Api --no-launch-profile`) and its child, plus T19's chrome-headless-shell tree
  (1994238 and children, 1996041). None of them this task's; kill **by pid** if a future iteration
  wants the port. This iteration's own three (API 2516941, vite 2517739/2517755, chrome 2518875)
  were killed by pid and :5341/:5342 confirmed free; the systemd dev instance (pid 2033) was not
  touched.
  Filters checked to bite, per T22's lesson: `~ScrapeWorkerGate` matched 7 before and 9 after, and
  the task's own written verification (`npm run build && npm run lint`) is not a filter at all —
  it cannot see this change, which is why the live check is the verification and the build is the
  floor. Still zero and still suspect: none known. T40's `~PagesFetched` is zero by design.

## 2026-08-27 — T38 An operator way back out of a failed backfill — done

- did: `POST /api/admin/ships/{id}/backfill/restart` (new `AdminShipsController`) puts a `Failed`
  backfill back to `InProgress` from a page the admin names or the ship's stored cursor, zeroing
  `BackfillStalledRuns`, clearing `BackfillCompletedAt` and dropping `BackfillMinUpdatedAtSeen`. It
  refuses a backfill that is not `Failed`, a tag AO3 has denied, a ship with no enabled schedule, and
  a page below 1. `BeginBackfill` zeroes the counter too. `WatchedShipDto` gained
  `BackfillNextPage`/`BackfillStalledRuns`, and the Ships page renders both a given-up and a merely
  stalled backfill with a `warning` tone and the numbers behind them, plus the restart form for
  admins.
- files: `Api/Controllers/AdminShipsController.cs` (new), `Api/Controllers/ShipsController.cs`,
  `Api/Dtos/ShipDtos.cs`, `Api/Models/Ship.cs`, `Api/Services/Scraping/Ao3ShipIndexScraper.cs`,
  `Tests/BackfillRestartTests.cs` (new), `Tests/LibraryTestHost.cs`,
  `Tests/Ao3ShipIndexScraperTests.cs`, `Tests/ShipsControllerTests.cs`,
  `frontend/src/{api/types.ts,api/client.ts,index.css,pages/ShipsPage.tsx}`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter ~Backfill` → 31 passed (13 before); `dotnet test` → 769 (751 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only; a live check on
  a throwaway instance (API :5351, vite :5352, chrome-headless-shell) through the given-up render,
  the restart click, the stalled-but-running render, and both new refusals. Mutations: deleting the
  controller's counter reset, deleting `BeginBackfill`'s, and deleting the floor clear each red a
  different test and nothing else.
- commit: a84cfaf
- next: **T39 is next in plain file order** (`blocked-by: none`) — its fixture landed on 2026-08-24
  and the task shrank to a test that pins `HasListing`, so read its own notes rather than its title.
  **T40 now owns the stalled-run increment guard** as well as the `firstPage` placement, and its
  notes carry the whole argument and name the test it has to re-decide. **T78 is new.** T77 is still
  taken instead of T63.
- **Only half of what the notes asked for was built, deliberately.** T38's notes said "decide both
  halves here": ship the recovery path *and* narrow `RecordBackfillProgress`'s increment guard to
  `askedStaleCursor`. Tracing the narrowing against
  `Gives_up_on_a_backfill_that_spends_run_after_run_on_a_cursor_nothing_answers` kills it: the cursor
  halves 10 → 5 → 2 → 1, and at page 1 `CursorMayBeStale` is false by construction (`page > 1`), so
  `askedStaleCursor` is false from that run on, the counter freezes at 3, and `Failed` becomes
  unreachable — a ship asking an unanswerable page once a run for ever. The test's own comment states
  the current behaviour as intended. Since T36's review that whole argument already lives on **T40**,
  whose one-line move of `firstPage ??= page` *is* the narrowing made at its root. **The review
  independently derived the same trap**, which is the strongest evidence available that the split is
  right rather than convenient. See DECISIONS.
- **`delivers` was the contract again, cutting the other way.** T36's entry generalised "when a
  task's notes offer a cheaper option than `delivers` describes, `delivers` is the contract". Here
  the notes offered *more*, and the same rule applies: build the `delivers` line, hand the surplus to
  the task that owns it, and write down why. Both halves of that convention are now on the record.
- **The review found three real defects in this diff and they are all fixed.** The floor guard
  compared `fromPage` against a cursor the halving retreat had already dragged below the pages the
  floor came from, so the common case kept a floor that blinds shift detection for the whole re-walk
  (now dropped unconditionally); four places claimed the cursor was "where the walk gave up" when it
  is where the last run *landed*; and the restart was offered and accepted for `NotFoundOnAo3` and
  unscheduled ships, which can never run, leaving a row saying "in progress" for ever. Its fourth
  finding is T40's, and two of its sub-points were acted on here — `Ship.cs`'s summary sentence now
  describes what the counter counts instead of what it was meant to, and the new `BeginBackfill` test
  asserts `InRange(0, 1)` rather than `== 1` so a test about the reset does not pin T40's decision
  about the increment.
- **The cursor is a worse default than it looks, and that is now T78.** `BackfillNextPage` is where
  the last run landed; `JumpCursorBackFrom` halves it once per stalled run, so a ship that read 39
  pages is stored on page 1 and the one-click restart re-walks all 39 at the shared gate. Idempotent,
  so it is politeness rather than correctness — filed rather than folded in, because the honest fix
  wants a "deepest page read" column and two migrations.
- **The live check caught nothing and was still worth running twice.** First pass verified the three
  renders and a real restart click end to end; second pass, after the review's copy and gating
  changes, verified the corrected wording and both new refusals in the browser as well as at the API.
  **A trap for the next iteration: the Bash tool's working directory persists between calls**, and
  launching vite from `backend/Ao3Tracker.Api` (left over from an earlier `cd backend`) served an
  empty document and had `npx` silently install a second vite. Symptom was a 39-character DOM with a
  correct `location.pathname`, which reads like a CDP problem and is not. Launch with an absolute
  `cd` in the same command.
- **Leaked processes from earlier iterations, unchanged.** pid 1963836 (`dotnet run --project
  Ao3Tracker.Api --no-launch-profile`) and its child, plus T19's chrome-headless-shell tree (1994238
  and children, 1996041). None of them this task's; kill **by pid**. This iteration's own six were
  killed by pid across two rounds and :5351/:5352/:9333 confirmed free; the systemd dev instance
  (pid 2033) was not touched.
  Filters checked to bite, per T22's lesson: `~Backfill` matched 13 before and 31 after — the new
  class is named `BackfillRestartTests` so every test in it matches whatever the method is called.
  Still zero and still suspect: none known. T40's `~PagesFetched` is zero by design.

## 2026-08-27 — T39 Confirm what AO3 serves for a works index with no results — done

- did: Wrote the tests that hold what the 2026-08-24 capture already showed. New
  `Ao3EmptyListingTests` pins the five markup facts a zero-result index carries — the container is
  present, it is classed `work index group` rather than the bare `index group` the parser's fallback
  guesses at, no works, no parse warnings, a `0 Works` heading, no pagination. Two tests in
  `Ao3ShipIndexScraperTests` walk the same capture through a whole run: the filtered/incremental side
  stops `LastPage` rather than `Error`, and the unfiltered/backfill side reaches `Complete`. No
  production code changed except two comments repointed from T39 to T79.
- files: `Tests/Ao3EmptyListingTests.cs` (new), `Tests/Ao3ShipIndexScraperTests.cs`,
  `Api/Services/Scraping/Ao3ShipIndexScraper.cs` (comment only),
  `.devloop/{tasks,DECISIONS,JOURNAL,scraper-audit}.md`
- ran: `dotnet test --filter ~Listing` → 24 passed (17 before); `dotnet test` → 776 (769 before);
  `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings only. Mutations:
  breaking `FindListing` reds `Renders_the_results_container...` and both scraper tests; making
  `ParseTotalWorks` return null for a zero count reds `Counts_its_empty_result_set_in_the_heading`
  and nothing else; changing `PlausiblyTheEndOfTheListing`'s `page == 1` to `page == 0` reds only the
  backfill test — the filtered one survives on the heading waiver, which is the design saying so.
- commit: e9f58c3
- next: **T40 is next in plain file order** and is on T15's `blocked-by`, so it is the load-bearing
  one; T38's journal entry, not T40's notes, carries the argument about the increment guard.
  **T79 and T80 are both new and both from this task.**
- **The review found one thing in this diff and it was in a document, not in the code.** I wrote
  "T28's §C7 is closed" into `scraper-audit.md` and `DECISIONS.md` because the capture answers the
  premise C7 was parked on. It does not close C7: `PlausiblyTheEndOfTheListing` short-circuits on
  `page == 1` *before any heading is read*, so an unfiltered page 1 with the container, no works, no
  Next link and no heading still concludes `Complete` — a ten-thousand-work back catalogue written
  off in one request. What the capture buys is the freedom to *fix* it without stranding genuinely
  empty tags. Now **T80**, and C7 is open again in the audit.
- **The disproof was a passing test I had already read.** `Still_treats_an_empty_first_page_as_an_empty
  _tag` uses `Page(1, [])`, whose helper emits no heading, and asserts `Complete`. I read that helper
  in this iteration, mutation-tested against it, and still wrote the claim. The lesson worth carrying:
  **a note saying "X is now safe" is a claim about code, and is worth grepping the code for before it
  is written down** — the capture answered a question about AO3, and I let that stand in for an answer
  about this repo.
- **A task rewritten around new evidence can lose an obligation that lives in a code comment.** T39's
  notes were revised in place when the fixture landed, as an answer sheet for the three questions the
  capture settles. A fourth — that AO3 does not show restricted works to an anonymous request — lived
  only in `Ao3ShipIndexScraper.cs:335` ("that premise is T39's business") and in one scraper test, and
  did not survive the edit. Closing T39 would have left two comments in shipped code pointing at a
  `done` task. Now T79, `blocked` on a capture, and both comments repointed. **`grep` the source tree
  for the task id before closing a task whose scope was rewritten, not only `.devloop/`.**
- **`git checkout <path>` to revert a throwaway probe also reverts the task's own work in that file.**
  I added a probe test to `Ao3ShipIndexScraperTests.cs`, then reverted it with `git checkout` on the
  path — which took both new tests and the T79 comment repoint with it, since the file was modified
  and not committed. Rebuilt from the tool call that wrote them. Revert a probe with the same
  targeted edit that added it, or commit first.
- **The probe was not needed anyway.** The question it was going to answer — what an unfiltered
  page 1 with no heading does — is asserted by an existing green test. Reaching for a scratch
  experiment before checking whether the suite already answers the question cost the file.
- **The review's other three findings are all already on the list**, each independently re-derived
  for the sixth consecutive review: `DownloadWorker`'s unguarded startup sweep is **T62**, a transport
  failure permanently failing a reader's download is **T70**, and two workers racing
  `EnsureSessionAsync` is **T63**. **T77 — one dedicated pass over the download path — is still the
  cheapest thing on the list**, and this is now the fourth iteration to write that down without acting
  on it.
- Filters checked to bite, per T22's lesson: `~Listing` matched 17 before and 24 after; the new class
  is `Ao3EmptyListingTests` so every method in it matches whatever it is called, and both scraper
  tests carry `Listing` in their own names. T79's `~Restricted` matches 1 today.
- Leaked processes from earlier iterations, unchanged and none of them this task's: pid 1963836
  (`dotnet run`) and child, and T19's chrome-headless-shell tree (1994238, 1996041). This task started
  no processes — it is test-only, and needed no live check. The systemd dev instance (pid 2033) was
  not touched.

## 2026-08-27 — T40 An unreadable page must not be counted as a page that was read — done

- did: Moved `pagesFetched++`, `parseWarnings +=`, `firstPage ??= page` and `lastPage = page` below
  the `if (unreadable) break` in the walk, so a page AO3 served but the parser could not read
  advances no counter and names no boundary on the non-retreat path either — T37 had fixed only the
  retreat. Kept the dropped warning count visible by adding `{Warnings}` to the parse-failure
  `LogError`. Then settled the increment question T38 handed over: `RecordBackfillProgress`'s
  stalled guard now reads a new `pagesServed` counter (200s whose body reached the parser) instead
  of `firstPage`, which had been standing in for "AO3 answered" only because it was set above the
  break.
- files: `Api/Services/Scraping/Ao3ShipIndexScraper.cs`, `Api/Models/Ship.cs`,
  `Tests/Ao3ShipIndexScraperTests.cs`, `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter ~PagesFetched` → 5 passed (**0 before**, by design); `dotnet test` →
  781 (776 before); `npm run build` + `npm run lint` → clean, the two known fast-refresh warnings
  only. No live check: backend-only, and the seam the spec names for this is the controller/unit
  tests.
- commit: db789ec
- next: **T45 is next in plain file order** and is the incremental twin of what T40 just decided for
  the backfill — a pass that cannot get past page 1 with no bound on how long it keeps asking. The
  shape of the answer here (find the fact the guard actually wants, give it its own name, do not
  re-purpose a counter that means something else) is the one to reach for. **T15 is down to two
  blockers, T46 and T52**, and nothing else stands between it and the rest of the plan.
- **The trap T38 wrote down was real, and the mutation proves it.** Reverting the guard to
  `!askedStaleCursor && firstPage is null` reds `Gives_up_on_a_backfill_that_spends_run_after_run_on_
  a_cursor_nothing_answers` and the new stalled-run test and nothing else — the cursor halves
  10 → 5 → 2 → 1, `CursorMayBeStale` requires `page > 1` so no retreat can run at page 1,
  `askedStaleCursor` goes false for ever, the streak freezes at 3 and `Failed` is unreachable. Two
  prior readers (T38 and its reviewer) derived this from the code without running it; running it
  cost one `sed` and confirmed it exactly. **Where a previous iteration hands over a predicted test
  failure, spend the two minutes to reproduce it before designing around it** — it is the cheapest
  possible confirmation that the design constraint is real rather than remembered.
- **Three mutations, each red in a different place.** (1) Guard deleted entirely → only
  `Counts_no_stalled_run_when_a_refused_status_left_PagesFetched_at_zero` reds, so the "AO3 told us
  nothing" half is pinned. (2) Guard back to `firstPage is null` → the two tests above red, so the
  "AO3 told us something unusable" half is pinned. (3) The four counter lines back above the break →
  four of the five new tests red. The two halves of the guard are therefore held by different tests,
  which is what makes the pair worth having rather than one test asserting both.
- **`delivers` was the contract again, and this time it cost something worth naming.** "advances no
  counter" covers `parseWarnings`, so it moved with the other three — but it is also the one number
  that tells "the markup changed and the blurbs are unreadable" apart from "the page is empty", and
  dropping it silently would have made the run history worse at the exact failure it exists for. The
  resolution was not to keep the counter but to move the fact to where it is still true: the error
  log line already fires for that page, so it now carries `{Warnings}`. **A counter you are about to
  stop recording is worth one look at what reads it before it goes.**
- **A helper the parser will not select produces no warning.** The test needed a page that is
  unreadable *and* carries parse warnings, and the obvious `<li class="blurb">` with no id produces
  neither: `SelectBlurbs` filters on `li.blurb` whose `Id` starts with `work_`, so an id-less blurb
  is never selected and never counted. `Nameless()` emits `id="work_"` — selected, then unnameable,
  which is the shape that increments `warnings`. Worth knowing before writing any future test about
  parse warnings.
- **The review died on the monthly spend limit for the third time on this branch** (T14, T35, now
  T40; it resets 20:30 America/Chicago). Reviewed by reading instead — the diff is 88 lines of
  production code, most of it comment. One thing that pass changed: `FinishAsync`'s call site had
  `askedStaleCursor: retreatedFrom is not null, pagesServed, ct`, a named argument followed by
  positional ones, which compiles only because the named one sits in its own position; `pagesServed:`
  is now named too. **Three lost reviews on one branch is a pattern, not an accident** — an iteration
  that wants one should check the reset time before launching.
- Filters checked to bite, per T22's lesson: `~PagesFetched` matched **0 before and 5 after**, which
  is what the task's own verification line warned about. The five are named individually rather than
  by class, because they belong in `Ao3ShipIndexScraperTests` beside the helpers they use — so a
  sixth test added to this rule has to carry `PagesFetched` in its own name or the filter will not
  see it.
- Leaked processes from earlier iterations, unchanged and none of them this task's: pid 1963836
  (`dotnet run`) and its child, and T19's chrome-headless-shell tree (1994238, 1996041). This task
  started none of its own — test-only, no live check. The systemd dev instance (pid 2033) was not
  touched.

## 2026-08-27 — T45 An incremental pass that cannot get past page 1 has no bound — done

- did: After `MinStuckIncrementalRuns` (3) consecutive incremental runs stop short at the same page,
  the walk reads that page and stops **without asking for the one after it** — a new
  `ScrapeStopReason.Held`, recorded as a `Failed` run whose message names the page and says it was
  not requested. The streak is derived from `ScrapeRuns` (`StopReason` + `LastPageFetched`) rather
  than counted into a column, so there is no schema change, no migration, and nothing to reset. Held
  runs count towards their own streak, and `ProbeHeldPageEveryNthRun` (8) of them in a row lifts the
  hold for one run so a listing that heals is found without an operator.
- files: `Api/Services/Scraping/{Ao3ShipIndexScraper,ScrapeBudget,ScrapeWorker}.cs`,
  `Api/Models/ScrapeRun.cs`, `Tests/{Ao3ShipIndexScraperTests,ScrapeWorkerRunStatusTests}.cs`,
  `.devloop/{tasks,DECISIONS,JOURNAL,scraper-audit}.md`
- ran: `dotnet test --filter ~Ao3ShipIndexScraper` → 91 passed (**78 before**, not the 67 the task's
  own verification line claimed); `dotnet test` → 795 (781 before); `npm run build` + `npm run lint`
  → clean, the two known fast-refresh warnings only. No live check: backend-only, and the seam the
  spec names for this is the controller/unit tests. Mutations below.
- commit: e407592
- next: **T46 is next in plain file order** (`blocked-by: none`), and with T45 closed **T46 and T52
  are the last two blockers on T15**, which unblocks T16 → T17 → T20 and the rest of the plan. T46's
  notes have been **corrected in place**: they quoted `if (!askedStaleCursor && firstPage is null)`,
  which T40 rewrote to `pagesServed == 0` — the premise survived the rewrite (a 404 leaves
  `pagesServed` at zero exactly as it left `firstPage` null), but the quote would have sent the next
  iteration looking for code that is not there. **T81 is new** and should be taken **with T52**, not
  in its own file position. T77 is still taken instead of T63.
- **`delivers` picked the design, and it picked against the notes' own preference.** T45's notes
  weighed widening the `revised_at` bound, dropping the filter for a run to reach the page by a
  different address, and a give-up threshold (which they also ruled out). The `delivers` line reads
  "stops spending a request **on it** every tick" — *on it* is the page, so the bound is on the
  **depth of the walk**. That turned out to be the cheapest of the three by a wide margin: no schema,
  no scheduler change, and nothing given up, because the held run still reads page 1 and ingests
  every new work on it. The erroring runs were not getting past page N either; the hold keeps the
  request that was doing the work and drops the one that was not.
- **The notes' preferred option is blocked on a task nobody had connected it to.** "Drop the filter
  for one run so the walk can reach page 2 by a different address" rests on the two addresses being
  different requests — and **T58** is a filed, browser-verified finding that AO3 discards
  `work_search[revised_at]` on this endpoint entirely. So today the retry would be the *identical*
  request under a parameter Rails throws away: a second round trip on the shared 5-8s gate for a
  guaranteed-identical answer. **Reading the sibling tasks a note points at, before building to it,
  is what cost fifteen minutes and saved a wrong design** — the note was written before T58 existed
  and nothing had gone back to reconcile them.
- **Six mutations, each red in a different place, and one survivor that was the most useful of them.**
  Hold disabled → 6 tests. Threshold 3→2 → the "only just started refusing" test. Held runs dropped
  from the streak → the test named for that. Probe removed → the probe test. Incremental gate removed
  → the backfill test. Hold moved above the `LastPage` stop → the healed-listing test.
  **The survivor: removing the `LastPageFetched is not { } page` guard changed nothing**, because
  `r.LastPageFetched == page` already excludes null rows when `page` is an `int`. The test I had
  written for that guard was passing for a reason that had nothing to do with it — its stub 404'd
  page 1, so the walk broke before ever reaching the hold. Rewritten to the situation the guard is
  actually about, and re-mutated against the realistic wrong implementation (`int? page`, letting
  nulls match each other), where it reds. **A mutation that survives is worth more than one that
  reds: it is the only thing that tells you a test is agreeing with the code rather than checking
  it** — G3 in the audit, arrived at from the other direction.
- **The review ran to completion, the first since T38** (T14, T35 and T40 all lost theirs to the
  monthly spend limit; checking the reset time before launching is what made the difference). Five
  findings, **all five in this diff**. Three fixed here, one already fixed before it returned, one
  filed as T81. The two worth carrying:
  - **A window that silently coupled two constants documented as independent.** `stuck` and
    `heldInARow` were both `TakeWhile`s over one `Take(ProbeHeldPageEveryNthRun)`, so `stuck` could
    never exceed 8 — and raising `MinStuckIncrementalRuns` to 8 or above, the obvious response to a
    false hold, would have **turned the whole bound off with no error and no failing test**. Now
    `StreakWindow`, derived from both. The test that pins it seeds `MinStuckIncrementalRuns` rows
    rather than a literal three, which is what makes it follow the constant instead of agreeing with
    today's value.
  - **A third entrance to the stuck state that the bound does not cover, and it is the expensive
    one.** The streak counts `Error` and `Held`; a page that fails at the *transport* level is
    re-asked by B5 until the breaker opens and stops the run with `Breaker`, on the same
    `LastPageFetched`. 1 + `MaxConsecutiveFailures` = 4 requests a tick against the `Error` route's
    two. **T81**, and it must ship with **T52** — T52 makes a `Breaker` run record as `Failed`, and
    either one alone leaves the walk and the run history disagreeing about the same row.
- **The build caught a CA2017 I had just written, of exactly the kind T49 exists for.** My hold's
  `LogWarning` used `{Next}` twice over five arguments. Structured logging counts *occurrences*, not
  distinct names. Reworded so every placeholder appears once; the one remaining CA2017, at
  `Ao3ShipIndexScraper.cs:638`, is still T49's in `RetreatFromStaleCursor` and was not touched.
  **Read the build's warnings on a diff that adds a log line**, not only its errors.
- **`git checkout <path>` ate the task's work, exactly as T39's entry warned it would.** I used it to
  revert a mutation between runs; the file was modified and uncommitted, so it restored from HEAD and
  took all 129 lines with it. Recovered from a scratchpad copy taken before the first mutation. **I
  had read that warning in this same iteration and walked into it anyway** — the durable fix is not
  to remember harder but to keep the copy: `cp` the file to the scratchpad before the first mutation
  and `cp` it back between them, which is what the rest of the run did.
- **Only half of one review finding was worth a fix, and the split is the convention.** Finding 2
  (`Breaker` still recorded `Succeeded`) is already **T52** and was left alone; finding 1 became T81
  rather than being folded in, on T38's precedent that `delivers` is the contract — but the stronger
  reason is that folding it in would have half-shipped it against T52.
- **`.devloop/scraper-audit.md` gained B18 and F8**, per T45's note that whatever came out belongs in
  T28's table, and the T81 gap is recorded against both. Also noticed while there: **F7 still read as
  an open gap for T40**, which closed on 2026-08-27 — corrected in passing. A `done` task's findings
  row is not self-updating, and nothing re-reads that table on the way past.
- Filters checked to bite, per T22's lesson: `~Ao3ShipIndexScraper` matched **78 before and 91
  after** — the task's own verification line said 67, which was three tasks stale, so the number in a
  `verification` line is a hint and not a baseline. Every new test lives in `Ao3ShipIndexScraperTests`
  so the class name carries them whatever they are called; the worker-side one is in
  `ScrapeWorkerRunStatusTests` (2 → 3, `~ScrapeWorkerRunStatus`) and is outside this task's filter by
  design. T81's `~Ao3ShipIndexScraper` is 91 today.
- **A registration this test class needed and the host cannot provide.** `LibraryTestHost` registers
  the real scraper as itself, which is all `ScrapeAsync` needs, but `ScraperRegistry` resolves
  `IAo3Scraper` — so a worker run inside this class found no scraper and recorded no run at all, and
  the end-to-end test returned an empty list. Registered per-class rather than in the host because
  the registry's `ToDictionary` throws on the duplicate key the worker tests' stub would create.
  Worth knowing for any future test that wants a real end-to-end scrape.
- Leaked processes from earlier iterations, unchanged and none of them this task's: pid 1963836
  (`dotnet run`) and its child, and T19's chrome-headless-shell tree (1994238, 1996041). This task
  started none of its own — backend-only, no live check. The systemd dev instance (pid 2033) was not
  touched.

## 2026-08-27 — T46 A backfill whose cursor sits at page 1 can never reach `Failed` — done

- did: The walk counts `pagesNotFound` (404s AO3 answered with) alongside `pagesServed`, and
  `RecordBackfillProgress`'s stalled guard reads all three facts instead of two — so a backfill
  whose only request 404s at page 1 counts as stalled, reaches `MaxStalledBackfillRuns` and is
  written off as `Failed` instead of asking for ever with `Failed` unreachable. Shipped T28 §A2's
  sibling in the same diff: a ship whose tag AO3 has denied returns the new
  `ScrapeStopReason.Denied` with a message, not `LastPage`, which meant "walked off the end of the
  listing" for a run that made no request; `ScrapeWorker` records it `Failed`.
- files: `Api/Services/Scraping/{Ao3ShipIndexScraper,ScrapeBudget,ScrapeWorker}.cs`,
  `Api/Models/ScrapeRun.cs`, `Tests/{Ao3ShipIndexScraperTests,ScrapeWorkerRunStatusTests}.cs`,
  `.devloop/{tasks,DECISIONS,JOURNAL}.md`
- ran: `dotnet test --filter ~Backfill` → 35 passed (33 before, **not the 13 the task's verification
  line claimed**); `dotnet test` → 799 (795 before); `npm run build` + `npm run lint` → clean, the
  two known fast-refresh warnings only. No live check: backend-only, and the seam the spec names for
  this is the controller/unit tests. Three mutations below.
- commit: 87c1167
- next: **T52 is the last blocker on T15**, and it is taken **with T81** — the two tasks' notes both
  say so, and they are the same fact one column over. That is the next task, ahead of T48–T51 in
  file order. T15 then unblocks T16 → T17 → T20. **T82 and T83 are new**, both filed from T46's
  review, and T82 is the honest half of what T46 did not finish.
- **Three mutations, each red in exactly its own place.** Guard back to `pagesServed == 0` → the two
  new stall tests red and nothing else, so the clause is pinned and nothing pre-existing was covering
  it. `Denied` back to `LastPage` → only the denied test. `Denied` dropped from `ScrapeWorker`'s
  failed-status list → only the worker test. The last one is why that test exists: the mapping is one
  token in an expression T52 and T81 are both going to edit, and without it their diff could silently
  drop it.
- **The review's findings were both prose, and both were mine.** `delivers` said the fix "stops
  backfilling instead of asking forever"; it stops the backfilling only — `ScrapeWorker` hands a
  `Failed` backfill an incremental pass that re-asks the same 404 every tick, and T45's hold cannot
  catch it because its streak is keyed on `LastPageFetched`, null for a run that read nothing (T82).
  And the `ErrorMessage` I wrote told an operator that re-verification clears a denied tag: nothing
  in the codebase moves a settled verification back to `Pending` (T83). **A sentence a diff writes
  for an operator is part of the diff** — check it against the code the way the code gets checked
  against the tests.
- **Naming a review's target explicitly is what made it cheap.** It reported reading exactly the
  working-tree diff, two findings, both in scope, no re-derivation of old tasks — the second review
  in a row to survive, against three earlier losses to the monthly spend limit.
- Leaked processes from earlier iterations, unchanged and none of them this task's: pid 1963836
  (`dotnet run`) and its child, and T19's chrome-headless-shell tree (1994238, 1996041). This task
  started none of its own. The systemd dev instance (pid 2033) was not touched.
