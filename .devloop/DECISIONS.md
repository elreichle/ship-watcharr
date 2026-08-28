# Decisions

Plan changes only — tasks added, split, re-scoped, or dropped, each with its reason.

> The 60 entries before this point live in [`DECISIONS-archive.md`](DECISIONS-archive.md) (2026-08-22 — initial plan → 2026-08-26 — T77: the download-path pass is filed rather than observed a sixth time). Do not read it end to end; `grep` it for a task id when a live entry points into it.

## 2026-08-27 — T36: the gate grew a field rather than the page growing a parser

T36's notes offered two fixes and called the second smaller: render only the identity blocker in
"What AO3 currently sees", *or* drop the `identityConfigured &&` guard on the login callout. Its
`delivers` needs both — dropping the guard alone leaves the identity section still printing "No AO3
login is stored for this instance" under a heading about the User-Agent, and the section is half the
task. Both were done.

Rendering only the identity blocker needed a value the frontend did not have. `ScrapingGateState`
carried `Blockers` (a list the page never saw) and `Problem` (all of them joined by `\n\n`), so the
page's options were to split `Problem` on the separator and take the first entry, or to be handed
the identity blocker on its own. **The gate grew `IdentityProblem`.** Splitting the joined string
would put knowledge of the join order and separator in the page, which is exactly the drift
`ScrapingGate`'s class doc exists to prevent — one place answers what is blocking scraping, and the
worker and the admin screen both read that answer rather than each deriving one. `Problem` is
unchanged and still says everything at once, which is what the worker's log wants.

The frontend's `problem` field is now read by nothing. Left in place: it is the honest "every
reason" view of the endpoint, and removing it from the DTO to match one consumer's current needs
would be the tail wagging the dog.

## 2026-08-27 — T36's review: nothing in the diff, and the one new-looking finding was already ruled on

`/code-review high` ran to completion this time (the previous two died on the spend limit) and read
the whole branch rather than only T36's diff. It found **nothing in T36's changes**, and stated the
invariant the change turns on in its own words: `IdentityProblem` is non-null exactly when
`IdentityConfigured` is false, it stays `Blockers[0]`, and `ScrapingGateState` has one construction
site. Four findings elsewhere, and **no new task came out of them**:

- **`RecordBackfillProgress` counts an unreadable page 1 as a stalled-cursor run** — this is **T40**,
  whose defect is `firstPage ??= page` running before the `if (unreadable) break`. The consequence is
  new and worse than T40 was filed with, and is now written into T40's notes: no retreat can run for
  this shape, so the halving that justifies the 12-run bound never converges, and twelve intervals of
  a markup change fail every in-progress backfill permanently.
- **The feed's note editor drops text typed while a save is in flight** — **T57**, and the reviewer
  found the guard already written in `WorkDetailPage`. Added to T57's notes, because "copy the twin"
  is most of that task.
- **Two workers can each perform the same AO3 login** — **T63**, now derived by six consecutive
  reviews. Absorbed by **T77**.
- **T35 rewrote a migration that has already been applied** — **not filed, and the reason is on the
  record.** The reviewer argued from "already at the merge base", which is checkable and false:
  `git log --diff-filter=A` puts the migration's introducing commit `78940c9` on
  `devloop/dashboard-completion` and nowhere else. More to the point it argues past T35's actual
  reasoning, which was never "it is a dev branch". The old SQL either produced the right answer or
  violated `PK_WorkAuthors` and rolled the migration back unrecorded — enumerate the cases and there
  is no third one — so no database can be holding the un-merged state a repair migration would have
  to find, and any database that failed re-runs the corrected SQL. **The property is what makes an
  in-place rewrite safe, not the branch name**; T35's entry says so, and a reader who checks only
  the branch will keep re-filing this.

Three of the four are tasks a review has now found more than once. The list is doing its job; what
it costs is that each review spends its budget re-deriving it, which is what T77 exists to stop.

## 2026-08-27 — T38: the recovery path is T38's; the counting half went to T40 on the evidence

T38's notes carried an instruction from T44's review: "Decide both halves here: narrow the increment
to `askedStaleCursor` to match the documented intent, *and* ship the recovery path." Only the second
half was built, and the reason is a fact the note could not have known.

Narrowing `RecordBackfillProgress`'s guard from `if (!askedStaleCursor && firstPage is null) return;`
to `askedStaleCursor` alone **removes the bound entirely for a ship sitting at page 1**. Trace
`Gives_up_on_a_backfill_that_spends_run_after_run_on_a_cursor_nothing_answers`: the cursor halves its
way 10 → 5 → 2 → 1, and at page 1 `CursorMayBeStale` is false by construction (`page > 1`), so no
retreat runs and `askedStaleCursor` is false from that run onward. Under the narrowing the counter
freezes at 3 and the ship re-requests an unanswerable page once a run for ever. That test's own
comment states the current behaviour as intended — "the cursor halves its way down to page 1 on the
way and **goes on counting there**, so a ship that has run out of listing to retreat into is written
off rather than left asking" — so the narrowing is not a small correction, it is re-deciding a rule a
test was written to hold.

Meanwhile T36's review moved the whole argument onto **T40**: `firstPage ??= page` runs above the
`if (unreadable) break`, and moving those four lines below it — T40's entire diff — *is* the
narrowing, made at its root rather than by editing the guard. One task owns the line. Two tasks
editing the same three lines while disagreeing about the page-1 bound is how a run produces a
conflict it then has to unpick.

So T38 is its `delivers` line: the way out. Consistent with the convention T36's entry recorded —
**`delivers` is the contract, the notes are one reader's guess at the implementation** — which cut
the other way there (the notes offered less than `delivers`) and cuts this way here.

## 2026-08-27 — T38: only a `Failed` backfill may be restarted, and a restart does not make the ship due

Two scope decisions in the endpoint, both about what it costs AO3 rather than what it costs us.

**`Failed` only.** `POST /api/admin/ships/{id}/backfill/restart` returns 409 for a Complete,
InProgress or NotStarted backfill. Re-arming a Complete one would walk a back catalogue already read,
at 5-8 seconds a page, off one mis-click; re-arming an InProgress one would move the cursor out from
under a walk that is working. Neither is what "an outage should not cost a back catalogue" asks for,
and a narrower endpoint can be widened later on a real request.

**The schedule is left alone.** The obvious alternative — `NextRunAt = null` plus a `ScrapeWake`
signal, which is what following a new tag does — would put a walk that has been failing for days at
the front of the queue the moment somebody pressed a button. A back catalogue that has waited twelve
intervals can wait one more, and the decision about request spacing stays in the scheduler rather
than being made twice. The button says so, so nothing looks broken while nothing happens.

Two writes beyond the state and the cursor. `BackfillStalledRuns = 0` is not a nicety: both of the
scraper's own reset sites sit on paths a `Failed` ship no longer reaches and giving up does not clear
it either, so a restart that left the counter at twelve would hand back a ship that fails again on
its very next stalled run — indistinguishable, from outside, from a restart that did nothing. Three
reviews (T26's, T30's, T8's) had each derived that independently. And `BackfillMinUpdatedAtSeen` is
cleared **when the cursor moves backwards only**: the floor is the oldest work a contiguous walk has
reached, and `TrackBackfillFloor` reports anything newer as the listing shifting underneath, so
carried into a walk restarting nearer the top it makes every page of the redo warn about a listing
that never moved. A restart at the ship's own cursor is the same walk, so it keeps its floor.

`BeginBackfill` also zeroes the counter now, which is the fix T26's, T30's and T8's reviews reported.
Nothing on today's paths sets a `NotStarted` ship's counter above zero, so it is defence rather than
a live bug — asserted through a run that *stalls*, because a run that got anywhere clears the counter
on its own and the test would pass with the reset deleted.

## 2026-08-27 — T38's review: three defects in its own diff, and the cursor is not what it looked like

`/code-review high` ran to completion (the third in a row to survive) and read the working tree
rather than the branch, since `@{upstream}...HEAD` was 59 already-reviewed commits behind. Four
findings, three of them in T38's own changes and all three fixed in it.

**The floor guard was backwards in the common case.** T38 cleared `BackfillMinUpdatedAtSeen` only
`if (fromPage < ship.BackfillNextPage)`, on the assumption that the stored cursor is how deep the
walk got. It is not: `JumpCursorBackFrom` halves the cursor on every run where the cursor page and
the page before it are both unreadable, which is exactly the path to `Failed`. A walk that reached
page 40 and read its floor from page 39 ends up parked at page 1, and a default restart then finds
`1 < 1` false and keeps a floor no page of the re-walk can be newer than — so `TrackBackfillFloor`
logs "a full sweep will be needed" on every page and never updates again. **The floor is now dropped
unconditionally.** One page of shift detection is lost at the restart point; the next page
re-establishes it. That is strictly cheaper than a comparison that is wrong whenever the halving has
run, and the halving has run by definition on every ship this endpoint can be used on.

**"Where the walk gave up" was not where the walk gave up**, in four places — the controller comment,
`RestartBackfillRequest.FromPage`, the `restartBackfill` JSDoc, and the Ships page's detail line,
which named the cursor as the page AO3 would not answer for. All four now say what the cursor is:
where the last run *landed*. The improvement the reviewer suggested — record the deepest page
actually read and default to that — is **T78**, filed rather than folded in because it wants a
column and two migrations, and because re-walking pages is a politeness cost rather than a
correctness one.

**The restart was offered, and accepted, for ships that cannot scrape.** A `Failed` ship whose tag
AO3 has denied, or whose schedule is off, showed the form under a status line saying "AO3 has no such
tag" or "Paused" — and the endpoint took it, because it checked only `BackfillState`. Neither ship
would ever run: `Ao3ShipIndexScraper` returns before its first request for a `NotFoundOnAo3` tag, and
the worker only takes enabled jobs. So a "restarted" ship sat at `InProgress` for ever, which is
worse than the written-off state it replaced — that one at least said so. The endpoint now returns
409 with the reason for both, and the page has a `canRestartBackfill` predicate carrying the same two
conditions `describeStatus` returns early on.

**The fourth finding is T40's**, and the reviewer derived both halves of the reasoning already on the
record above — including, unprompted, that narrowing to `askedStaleCursor` alone would make `Failed`
unreachable for a ship parked at page 1. Two of its sub-points were acted on here: `Ship.cs`'s
summary line now describes what the counter actually counts and names T40 as the owner of the
discrepancy, rather than stating an intent the code does not implement; and
`Clears_a_stalled_streak_when_a_backfill_begins` asserts `InRange(0, 1)` rather than `== 1`, so a
test about the *reset* stops pinning T40's decision about the *increment*.

## 2026-08-27 — T39: the capture answered three questions and a fourth was still pointing at it

**T39 shipped as tests, not as a fix, and that was settled before this iteration started.** The
2026-08-24 entry above recorded that the capture confirms every premise T39 was written to doubt:
the container is present on a zero-result page, its class is `work index group` rather than the bare
`index group` the fallback selector guesses at, and a genuinely empty listing carries a `0 Works in
<tag>` heading. No code changed. What this iteration added is the tests that hold those three facts,
so the next re-capture diffs against an assertion instead of against a paragraph of prose.

**The scraper tests are part of the task, not surplus.** T39's `delivers` line says "parser tests",
and the convention T36 and T38 put on the record is that `delivers` is the contract. But its purpose
clause names `PlausiblyTheEndOfTheListing`, which is not in the parser — and a parser test pins only
that method's *inputs*. The gap T39 was written against is precisely that every scraper test reaching
the zero-work path goes through the `Page(n, [])` helper, which emits the container unconditionally
and therefore agrees with the premise whatever AO3 does; leaving that helper as the only route to
that path would have closed the task without closing the hole it names. So two tests in
`Ao3ShipIndexScraperTests` feed the real capture through a whole run: the filtered/incremental side
stops `LastPage` rather than `Error`, and the unfiltered/backfill side concludes `Complete` off the
`0 Works` heading. Mutation-checked apart: breaking `FindListing` reds both, and removing
`PlausiblyTheEndOfTheListing`'s `page == 1` short-circuit reds only the backfill one — the filtered
one survives on the heading waiver, which is the design saying so out loud.

**T28's §C7 is unblocked, not closed — and this iteration first wrote down that it was closed.**
The fear recorded there was that an unfiltered page 1 with the container, no works, no Next link and
no heading concludes `LastPage` — for a backfill, `Complete`, a whole back catalogue written off from
the absence of every piece of evidence. The capture says a real empty listing *does* carry a heading,
and it reads `0 Works`, and the first draft of this entry and of the audit's C7 row both read that as
the finding being answered. It is not. `PlausiblyTheEndOfTheListing` short-circuits on `page == 1`
**before any heading is consulted**, so the no-heading page 1 still concludes exactly as it did
before; nothing in the diff changed it. What the capture actually buys is the freedom to fix it — a
heading requirement on page 1 can no longer strand a genuinely empty tag, because a genuinely empty
tag has a heading. That fix is **T80**, and C7 stays open until it lands.

The claim was caught by T39's own review, and it is the more useful kind of finding: not a defect in
the code, but a document asserting a guarantee the code does not make. `Still_treats_an_empty_first_
page_as_an_empty_tag` had been sitting in the suite the whole time, green, with `Page(1, [])` — which
emits no heading — concluding `Complete`. The disproof of the claim was already a passing test.
**A note that says "X is now safe" is worth grepping the code for before it is written down.**

**T39's fourth question was never in T39's notes, and is now T79.**
`Ao3ShipIndexScraper.cs:335` says "that premise is T39's business, and this line is how it would
first announce itself" about a different premise entirely: that AO3 does not show restricted works to
a request carrying no session. T39's notes were rewritten on 2026-08-24 around the three questions
the capture answers and this one was dropped on the floor. The capture cannot answer it — it is a
zero-result page, so it carries no blurb at all, and it was taken *logged in*, so it is not an
anonymous request either. Closing T39 silently would have left a comment in shipped code pointing at
a `done` task, which is how a premise stops being anyone's business. Filed as **T79**, `blocked` on a
capture only Emma can take, per the loop policy on fixture tasks. It does not touch
`LastKnownTotalWasAuthenticated`: T30 and T44 settled that the transport, not the markup, says what a
request carried, and that holds whichever way T79 lands. What it decides is whether the warning
beside it can ever fire.

**A lesson about tasks whose scope a capture rewrites.** T39's notes were revised in place when the
fixture landed, and the revision was written as an answer sheet for the three questions in front of
it. A fourth obligation that lived in a code comment rather than in the task body did not survive the
edit. Where a task is rewritten around new evidence, the check worth making is `grep` for the task id
across the source tree, not only across `.devloop/`.

## 2026-08-27 — T40: an unreadable page is not a page read, but it is still an answer

**The four-line move was the easy half.** `pagesFetched++`, `parseWarnings +=`, `firstPage ??= page`
and `lastPage = page` sat three lines above the `if (unreadable) break`, so a fresh backfill whose
page 1 was a 200 maintenance page filed `PagesFetched = 1, FirstPageFetched = 1,
LastPageFetched = 1, WorksSeen = 0` — a run claiming the one page it could not read, under a counter
whose own summary reads "listing pages successfully parsed". T37 fixed exactly this on the retreat
path by `continue`ing above the counters; the non-retreat route to the same page was missed. The
move is now below the break and both routes agree.

`lastPage`'s arithmetic survives untouched, which was the thing T40's notes said to check. The 404
branch tests `lastPage == page - 1` to mean "a page this run read said page N exists", and the walk
only advances past a page that offered a next link — which an unreadable page never does, because it
breaks. So the page before a 404 is still a page that read, and the guard means what it meant.

**`parseWarnings` moved with the other three, and the number is kept in the log instead.** It is a
counter, `delivers` says an unreadable page advances no counter, and the retreat path had already
been dropping it since T37 — leaving it above the break would have re-created the inconsistency one
field over, with a run reporting warnings from a page its `PagesFetched` says it never read. But it
is also the only quantitative signal that tells a *markup change* apart from an *empty page*: blurbs
present and unnameable versus no blurbs at all. So the parse-failure `LogError` now carries
`{Warnings}`, and `Keeps_an_unreadable_pages_blurb_warnings_out_of_PagesFetched_but_not_out_of_the_log`
pins both halves.

**The decision T38 handed over: a run whose only page was unreadable counts as a stalled run.**
`RecordBackfillProgress` guarded with `if (!askedStaleCursor && firstPage is null) return;`, and
`firstPage` was standing in for "AO3 answered this run at all" only because it was set before the
unreadable break — "AO3 served a body" and "the parser read it" were the same fact, so either
reading of the guard gave the same answer. T40 separates them, and the guard has to pick.

Reading it as "the parser read a page" is the narrowing T38's notes originally asked for, and it is
wrong. Once `JumpCursorBackFrom` has halved a cursor down to page 1, `CursorMayBeStale` is false by
construction (it requires `page > 1`), so `askedStaleCursor` is false from that run on; with
`firstPage` null too, the guard returns early every run, the streak freezes, and
`ShipBackfillState.Failed` becomes unreachable. The ship re-requests one unanswerable page once a
run, for ever — precisely the load the counter exists to bound. T38 traced this, `/code-review`
derived it independently during T38, and running the mutation this iteration reds
`Gives_up_on_a_backfill_that_spends_run_after_run_on_a_cursor_nothing_answers` exactly as predicted.

So the guard reads "did AO3 serve this run a page body", and that needed its own name rather than a
side effect of a counter about something else: **`pagesServed`**, incremented once the response is a
200 and before the parser sees it. Narrower than `pagesRequested`, which counts a 404 and a refused
status; wider than `pagesFetched`, which counts only what read. The middle is exactly what the guard
wants — "AO3 told this run nothing" (down, refusing, cut off by the budget) is not the ship's
problem, "AO3 answered with something this run could not use" is.

**Counting it is only defensible because T38 shipped the way back.** Writing a backfill off used to
be permanent, and twelve runs against an unreadable page 1 retiring a back catalogue would have been
too strong a conclusion to draw from "the parser could not read this". `POST
/api/admin/ships/{id}/backfill/restart` puts a `Failed` backfill back to `InProgress`, so the bound
now ends a pointless request-a-run loop rather than retiring anything. Deciding the other way would
have needed a different bound, as T40's notes said; this way needed T38 first, which is why the two
tasks were split rather than merged.

**Two comments that named T40 as an open owner are now answers.** `Ship.BackfillStalledRuns`'s
summary said the "any unreadable page" half was "wider than intended … and is T40's to settle"; it
now states the rule and why the narrow one is unavailable. `Clears_a_stalled_streak_when_a_backfill_
begins` explained its `InRange(0, 1)` as refusing to pin T40's open decision; it now points at the
test that does pin it, and stays loose on purpose, because a test about the *reset* should not
assert the *increment*.

**T40's review did not run** — the account's monthly spend limit, for the third time on this branch
after T14 and T35. Reviewed by reading, with three mutations standing in for the coverage argument:
see the journal entry.

## 2026-08-27 — T45: the bound on a stuck incremental pass is a page it stops asking for

**Three answers were on the table and `delivers` picked one.** T45's notes weighed widening the
`revised_at` bound, dropping the filter for a run so the walk reaches page 2 by a different address,
and a give-up threshold; they also ruled the third out ("give up on new works is not a terminal state
this product can have"). The `delivers` line settles between the other two, and it reads *"stops
spending a request **on it** every tick, without that ever being written as a moved watermark"* —
"on it" is the page. The bound is on the depth of the walk.

**Dropping the filter was the notes' own preference and is not available.** It rests on the two
addresses being different requests, and **T58 is a filed, browser-verified finding that AO3 discards
`work_search[revised_at]` on this endpoint entirely** — the tag listing's filter form offers
`date_from`/`date_to` and has no `revised_at` field. So today the "unfiltered" retry would be the
identical request under a parameter Rails throws away: a second round trip to the same shared 5–8s
gate for a guaranteed-identical answer. That is exactly the load this project refuses to spend. The
idea is not wrong, it is *blocked on T58*, and it is worth revisiting there rather than building it
now against a parameter that does nothing.

**Widening the bound has the same problem and one more**: it is still a conclusion about how much of
the listing to ask for, drawn from a failure that says nothing about the listing.

**So: after three consecutive incremental runs that stopped short at the same page, the walk reads
that page and stops, without asking for the one after it.** Recorded as `ScrapeStopReason.Held` and
as a `Failed` run carrying a message that names the page and says it was not requested.

Three properties are what make this the cheap answer rather than a compromise:

- **It gives up nothing the erroring runs were achieving.** They were not getting past page N either.
  What the held run keeps is the request that was doing the work — page 1 is where new works appear
  in a `revised_at desc` listing, and every held run still reads and ingests it. A bound on how
  *often* the ship is scraped (backing `NextRunAt` off) would have cost exactly that, and would have
  been paid by the reader waiting longer for new works to appear.
- **It concludes nothing.** `Held` is not in `FinishAsync`'s `mayPropose` set, so the watermark does
  not move, which is the whole reason B6/B7/B11 leave the pass stuck in the first place. The hold
  sits *below* the `LastPage` stop in the walk, so a listing that has since shrunk to end at page N
  still ends the run healthily and still moves the watermark on the listing's own word.
- **It is reversible without an operator.** Held runs are counted too, and
  `ProbeHeldPageEveryNthRun` of them in a row lifts the hold for one run. Steady state is one
  request a run plus one extra every eight, against two every run — and a listing that heals is
  found by the next probe rather than by someone noticing.

**The streak is derived from `ScrapeRuns`, not counted into a column on the ship.** The run history
already records what each run read (`LastPageFetched`) and why it stopped (`StopReason`), so a
counter would be a second copy of a fact the database already holds, with an increment site and a
reset site to keep in step — and the reset half of that exact pairing is what `BackfillStalledRuns`
took T38 and T40 to get right. A streak read from history cannot drift: one healthy run and it is
gone, with nothing to remember to clear. It also means **no schema change and no migrations**, which
the notes had budgeted for.

**Two things only the end-to-end test could catch, and one of them was real.** The walk reads a
streak the *worker* writes, and it reads it while its own `ScrapeRun` row is already open and still
says nothing about where it got to — so without `CompletedAt != null` the most recent row is always
the run in flight, always reads back as "no page", and **the hold would never fire on a real
instance while every direct-call test passed**. `Bounds_a_stuck_incremental_pass_over_consecutive_
runs_through_the_worker` drives four real runs through `RunDueJobsAsync` and is the only test that
reds when that filter is removed. It needed `IAo3Scraper` registered for this test class: the host
registers the real scraper as itself, which is all `ScrapeAsync` needs, but `ScraperRegistry`
resolves the interface — registered per-class rather than in the host because the registry's
`ToDictionary` throws on the duplicate key the worker tests' stub would create.

**`Held` is a `Failed` run.** It read and ingested what it reached, but it did not get through the
listing, and the run history is the only place a headless worker reports itself — F1's finding, one
row over. It is a distinct stop reason rather than a reused `Error` because the walk has to tell its
own held runs from the failures that caused them; that is what times the probe. The Schedules page
renders both `stopReason` and `errorMessage` verbatim, so it needs no change to show this.

**Recorded as B18 and F8 in `.devloop/scraper-audit.md`**, per T45's note that whatever came out
belongs in T28's table. B18 is the only rule in §B that decides what to ask from the run history
rather than from the page in hand, and §G's question — *which pass is entitled to conclude this* —
has the answer "none, and it does not".

## 2026-08-27 — T45's review: three fixes in the diff, and the bound has a third entrance it misses

`/code-review high` ran to completion — the first review to do so on this branch since T38, and the
fourth attempt after T14, T35 and T40 all died on the monthly spend limit. It returned five findings,
all in T45's own diff. Three are fixed here; one is the reason **T81** exists; one was already
addressed by an edit made before the review returned.

**Fixed — the streak window silently coupled two constants that are documented as independent.**
`stuck` and `heldInARow` are both `TakeWhile`s over one `Take(ProbeHeldPageEveryNthRun)`, so `stuck`
could never exceed 8. Raising `MinStuckIncrementalRuns` to 8 or above — a plausible response to a
false hold — would make `stuck < MinStuckIncrementalRuns` permanently true and **turn the bound off
entirely, with no error and no failing test**. The window is now `StreakWindow`, derived as the max
of the two. Demonstrated rather than argued: with the threshold raised to 9,
`Stops_asking_for_a_page_that_has_not_answered_for_the_last_few_runs` reds under the coupled window
and passes under the derived one. That test now seeds `MinStuckIncrementalRuns` rows rather than a
literal three, which is what makes it follow the constant instead of agreeing with today's value.

**Fixed — the doc line claiming an exhaustive stop-reason vocabulary was not exhaustive.**
`ScrapeRun.StopReason`'s summary was edited in this task to list the values, and
`ReconcileInterruptedRunsAsync` writes a bare `"interrupted"` literal that is in neither the list nor
`ScrapeStopReason`. That mattered more after this task than before it, because `HeldAfterPageAsync`
is the first reader to treat the column as a closed vocabulary. Promoted to
`ScrapeStopReason.Interrupted` and used at its one write site — same string, no behaviour change, and
the vocabulary is now genuinely closed.

**Already fixed before the review returned — `held.Runs` saturates at the window.** Finding 3 read a
version in which the log and the run-history message said "for the last {Runs} runs" over a number
capped at 8. Both now say "for **at least** the last N runs", and `HeldPage`'s own doc states that
`Runs` is a floor and why widening the query to make it exact buys nothing either decision needs.

**Not fixed, and filed as T81 — a transport failure reaches the same stuck state and escapes the
bound.** The streak counts `Error` and `Held`. B5 — the transport-failure branch, the one rule in
the walk that deliberately re-asks a URL — re-requests a timing-out page until the breaker opens,
and the run stops with `Breaker` on the same `LastPageFetched`. Neither arm matches, the streak never
accumulates, and **the variant that escapes is the expensive one**: 1 + `MaxConsecutiveFailures`
requests a tick against the `Error` route's two.

It is filed rather than folded in for the reason T38 set the precedent for: `delivers` is the
contract, and T45's says *"stops with `Error` on the same page every tick"*. But the stronger reason
is that **folding it in would half-ship it.** The review's second finding is that a `Breaker` run is
still recorded `Succeeded` — which is already queued as **T52**, one of the two remaining blockers on
T15. Widening the streak to count `Breaker` while the history still calls those runs successes would
leave the walk and the run history disagreeing about the same row, which is the split this codebase
has spent T38, T40 and T44 closing elsewhere. T81 and T52 are the same fact one column over and the
task notes on both now say to take them in one diff. Recorded in the audit as a gap against B18 and
F1.

**What the review checked and found sound, recorded so it is not re-derived a seventh time:**
`FinishAsync`'s `mayPropose` is an allowlist, so `Held` cannot move the watermark by construction;
`CompletedAt != null` does exclude the run's own open row; the hold and the stale-cursor retreat
cannot interact, being incremental- and backfill-gated respectively; and the probe cadence works out
to one request per nine runs as designed.

## 2026-08-27 — T46: a 404 is the archive answering, and the stalled counter now hears it

**The guard needed a third fact, not a wider reading of an existing one.** `RecordBackfillProgress`
split runs into "the archive told this run nothing" (budget, breaker, transport failure, refused
status — must not count) and "the archive answered with something unusable" (`pagesServed > 0` —
counts). A 404 is neither: no body was served, and yet the archive answered definitively. Rather
than loosen `pagesServed`, which T40 had just given a precise meaning, the walk now carries
`pagesNotFound` and the guard reads all three. It changes the outcome at exactly one cursor
position — page 1, where `CursorMayBeStale` cannot fire — because every deeper cursor already
retreats and is counted through `askedStaleCursor`.

**Rejected: disabling the ship's scrape job when the tag 404s.** T46's notes offered it as the
cheaper alternative. It is a state only an operator can undo, and T38's rule is that a write-off has
to be reversible; `BackfillState.Failed` already has `POST /api/admin/ships/{id}/backfill/restart`
behind it. Filed on **T82** as the option to price first if the streak logic proves expensive.

**The A2 sibling shipped in the same diff: a denied tag reports `denied`, not `lastPage`.** A run
that returns before making a single request was recording the stop reason that means "walked off the
end of the listing", and the only thing keeping that from marking a backfill `Complete` was that the
early return sits above `FinishAsync`. `ScrapeStopReason.Denied` is a new value in a vocabulary
`HeldAfterPageAsync` reads as closed, so it went in that class beside the others, and
`ScrapeWorker` records it `Failed` — the run could not do its job and the ship needs an operator.

## 2026-08-27 — T46's review: two findings, both about what the diff claimed rather than what it did

`/code-review high` ran to completion on an explicitly named target and reported reading exactly the
working-tree diff — the second review in a row to survive, after three consecutive losses to the
monthly spend limit. Neither finding was a defect in the code; both were sentences that overstated it.

**The `delivers` line was wrong, and the comment repeated it.** "Stops backfilling instead of asking
forever" is two claims, and only the first is true. `ScrapeWorker` hands a `Failed` backfill an
incremental pass, which starts at page 1 with no watermark, emits the identical URL, and takes the
same 404 every tick. T45's held-page bound cannot catch it: the streak is keyed on
`LastPageFetched`, which is null for a run that read nothing. The comment now says what the bound
does end (the backfill, and the ship reading as InProgress for ever) and points at **T82** for what
it does not.

**An operator message named a remedy that does not exist.** T46's own notes say `ShipVerifier`
returns early for anything not `Pending`; writing "re-verification is what can clear this" into
every denied run's `ErrorMessage` turned that into advice. Confirmed by reading: nothing anywhere
moves a settled verification back to `Pending`, and re-following reuses the row. The message now
states the fact and stops, and the missing route is **T83**.

**Both findings are the same failure.** The code was checked against the tests; the prose was
checked against nothing. Where a diff writes a sentence an operator or a later iteration will act on
— a `delivers` line, a comment, an `ErrorMessage` — the sentence is part of the diff and wants the
same verification the code got.

## 2026-08-28 — T52 + T81: the breaker is a failed run, and the walk counts it

Shipped as one diff, which both tasks' notes had already settled: T52 makes a `Breaker` run record
`Failed`, T81 makes `HeldAfterPageAsync`'s streak count one. Either alone leaves the walk and the run
history disagreeing about the same row — a run the walk treats as stuck while the history calls it a
success, or the reverse.

**Rejected: a `BreakerOpen` column on `ScrapeRun`.** T52's notes offered it, on the symmetry with
`HitRequestCap` and `HitTimeCap`, and priced it honestly as a migration on both providers. It buys
nothing: `StopReason` already carries the fact, `CanContinue` returns `Breaker` ahead of every other
reason so a run with the breaker open stops for that reason, and the one case where the two could
differ — an exception overwriting `StopReason` with `Error` while the breaker was open — is a case
where the exception's own message is the more informative thing to have kept. A column would be a
second copy of a value already on the row, and the reset half of exactly that pairing is what
`MaxStalledBackfillRuns` needed two tasks to get right. The narrow fix was the status, plus the error
message `delivers` asked for.

**`Cap` and `TimeCap` do not join the streak, and T81 asked for this to be said either way.** They
are runs that spent an allowance they were given. A run that stopped before reaching page N says
nothing whatever about page N, so counting one would hold a page over runs that never asked for it —
the same mistake the null-page guard above the streak exists to avoid, one column over. `Breaker` is
the only budget stop that means the archive was failing. Pinned by
`Holds_nothing_for_a_run_the_budget_stopped_rather_than_the_archive`.

**The `Breaker`-with-no-page split is kept by the guard, and now has its own test.** T81 asked
whether the widened predicate keeps "the archive is down" (page 1 itself timing out,
`LastPageFetched` null, nothing to hold at) apart from "this ship is stuck on a page". It does, by
the `recent[0].LastPageFetched is not { } page` guard above the streak rather than by the streak
itself — so it is inherited rather than restated, which is why it wanted a test of its own now that
`Breaker` is counted. `Holds_no_page_when_the_breaker_opened_before_a_page_was_read` reds against
the realistic wrong implementation (an `int? page` letting null rows match each other), alongside
the `Error` test that already covered the same guard.

**The worker's failure rule moved to `ScrapeStopReason.RecordsAsFailure`.** Not a refactor for its
own sake: the test fixture that arranges a run history was carrying a second copy of the same list,
and this diff would have deepened the duplication by adding `Breaker` to both. The run history is
read back as well as written — `HeldAfterPageAsync` decides what to ask for from these rows — so a
fixture disagreeing with the worker about which stops are failures arranges histories no instance
can produce. The explanatory comment stays at the worker, which is where the *why* for each value
belongs.

**The breaker's message names the page the failures were for, not the page the loop was holding.**
Written first as the loop's own `page`, which is wrong on one reachable path: a 404 at a backfill's
cursor both trips the breaker and fires the stale-cursor retreat, which decrements `page` and carries
on into the budget check — so the message would name a page nothing had failed on. The walk now
tracks `lastFailedPage` at its two `RecordFailure` sites. Found by reading the diff for T46's rule
that a sentence a diff writes for an operator is part of the diff; pinned by
`Names_the_page_the_failures_were_for_rather_than_the_one_the_retreat_moved_to`.

## 2026-08-28 — T52+T81's review: zero findings, and one thing the comment did not say

`/code-review high` on an explicitly named five-file target ran to completion — the third in a row —
reported reading exactly the working-tree diff, and returned **no findings**. The first clean review
on this branch. Worth recording what it cleared, so it is not re-derived: no `errorMessage` write can
clobber an earlier one (all seven are followed by `break`); `FinishAsync`'s `mayPropose` still refuses
to move the watermark on a `Breaker` stop, so a false hold cannot lose works; `ScrapeRun.Status` has
three readers and nothing schedules, backs off or retires a ship off it, so `Breaker` becoming
`Failed` has no blast radius; and no duplicate of the old inline failure predicate survives anywhere.

**Its one note was about the comment, not the code, and is now in the diff.** Counting `Breaker` in
the streak accepts a case the comment did not name: an archive-wide incident spanning three
consecutive runs that leaves page 1 answering while page 2 times out is indistinguishable, from
inside `HeldAfterPageAsync`, from a page that is genuinely gone — so page 2 is held for up to
`ProbeHeldPageEveryNthRun` runs. The null-page guard only catches an outage that takes page 1 down
too. Accepted rather than fixed: bounded, healed by the probe without an operator, and losing no
works, against an every-tick cost of `MaxConsecutiveFailures` timed-out requests. The comment now
says so.

**A test that passed for the wrong reason, caught by a mutation rather than by the review.**
`Writes_no_message_for_a_run_that_merely_spent_its_request_budget` was first written as a plain
capped run — which has no failed request, so no page for a message to name, so it came back silent
whatever `BudgetStopMessage`'s branch said. Deleting the `stopReason == Breaker` condition left it
green. Rewritten so the run takes one transport failure before reaching its cap, where it reds. This
is G3 again from the same direction as T45: **the mutation that survives is the one worth having
run.**

**Launching the review before the diff was finished cost it a pass.** Two edits landed while it was
reading — the `lastFailedPage` fix and two de-hardcoded constants — and it re-took the diff and
re-reviewed rather than reporting on a tree that no longer existed. It recovered, and independently
confirmed both fixes were correct, but the honest rule is to freeze the diff before launching, since
the recovery was the reviewer's to make and might not have been made.

## 2026-08-28 — T15: the full sweep is a mode of the walk, and what it may conclude

**The sweep is a third `ScrapeRunMode` inside `Ao3ShipIndexScraper`, not a scraper of its own.**
That class's own comment said the opposite — a different stopping rule deserves a different
implementation — and it was written before the walk grew everything T22–T52 put in it. The sweep
needs the cursor, the stale-cursor retreat, the halving jump, the breaker bound, the unreadable-page
rules and the end-of-listing evidence; a second copy would be a second place to fix each of them,
and the download path's nine open tasks are the standing demonstration of what that costs. What is
genuinely the sweep's own is small — a sort order, a start it measures absence from, and the
conclusion — and the class comment now says so instead.

**It asks AO3 for `work_search[sort_column]=created_at`.** A sweep of a tag longer than one run's
budget spans several runs and possibly several days, and under `revised_at` the listing re-sorts
underneath it: a work deeper than the cursor that anyone edits jumps to page 1, which the sweep has
already walked past, so the sweep never sees it and would conclude it had left the tag. Posting date
does not change. The value is not a remembered parameter — `ao3-empty-listing.html` carries the sort
dropdown and labels `created_at` "Date Posted" — and nothing depends on the direction AO3 applies,
only on the order being stable. The price is that a sweep may never propose a watermark, since its
page 1 holds the newest work *posted*; `FinishAsync` now names the two revised_at-ordered passes
explicitly rather than excluding the incremental pass's early stops.

**D12, answered: the sweep trusts its own walk and no count at all.** Its evidence for having seen
the whole listing is the walk — page 1 to a page with no next link, every page read, since an
unreadable page stops the run and a stopped run concludes nothing. It does not compare what it saw
against `LastKnownTotalWorks`: the sweep's pages are unfiltered, so it *writes* that number as it
goes (D11), and a number the sweep just wrote cannot also check it. Nor would the comparison be one
— works are posted and deleted while a multi-run sweep walks, and any tolerance wide enough for that
is a guess with a library behind it. So D12's field is refreshed by the only pass entitled to
refresh it and read as a count by nothing.

**T44's field gets its consumer, and it is a narrow one.** The sweep concludes only against a
heading it read *itself* (`LastKnownTotalWorksAt >= LastFullSweepStartedAt`) and read *logged in*
(`LastKnownTotalWasAuthenticated`). Restricted works are invisible to a logged-out request, so an
anonymous sweep is about to declare every restricted work in the library gone at once. What it
catches is a session that lapsed under a sweep already walking; what it does not catch is a single
anonymous page in the middle of an otherwise logged-in sweep. Accepted rather than fixed with a
second column: the mark is reversible, the next pass that sees the work clears it, and nothing is
deleted.

**A sweep that gets no further than the page it started on is abandoned on the spot** — against the
backfill's twelve-run allowance one screen away. Two reasons, and both are about what is at stake: a
backfill is the only way its back catalogue can ever be read, while a sweep's evidence is
re-obtainable by definition and a fresh walk an interval later is no less likely to finish; and a
sweep run *displaces* the ship's incremental pass for that tick, so a sweep that spins costs the
ship its new works rather than merely costing requests. The interval is therefore measured from the
last sweep's **start**, not its completion — an abandoned sweep leaves no completion, and measuring
from one it never reached would make it due again on the next tick for ever.

**Rejected: reusing `BackfillNextPage` for the sweep's cursor.** They can both be owed — a Failed
backfill keeps its cursor as the record of where it gave up, and that ship is exactly the one a
sweep is for — so one column would have the two walks resuming at each other's pages. The new
`Ship.FullSweepNextPage` is also what says a sweep is in flight at all, which is why both a
completed and an abandoned sweep clear it. Two migrations, one per provider, as the spec requires.

**Rejected for now: hiding a missing work from `WorkQueries.Library`.** It is one clause, and it is
what user story 16 asks for, but that query also scopes the work detail page, the per-work state
write and the download request — so the clause would 404 a work its reader had rated, noted and
downloaded, on the strength of a soft mark a sweep can get wrong. Filed as **T84** with the three
products it could be instead. T15 therefore ships the write and the one reader that was already
there (`ShipsController`'s work count), and says so rather than half-taking the clause.

## 2026-08-28 — T15's review: six findings taken into the diff, two filed

`/code-review high` on an explicitly named eight-file target ran to completion — the fourth in a row
— and returned **eight findings**, all in this diff or created by it. Six were fixed here; the
distinction that decided each was whether the defect was T15's to have, not whether the file was.

**The session guard did not do what its own comment claimed** (finding 3), and this was the one
worth the review on its own. `LastKnownTotalWasAuthenticated` is a plain assignment from whichever
page last carried a heading, so a sweep whose session died in run 1 and came back in run 2 read the
flag `true` and would have marked every restricted work on run 1's pages as having left the tag —
while the docstring said it caught exactly that case. Fixed by making the rule compose: **a run that
reads any page without a session abandons the sweep**, so no sweep surviving to conclude has read
one in any of its runs, and the stored-flag check is left as the half that catches a sweep whose
pages carried no heading at all. Both halves are now stated as halves.

**An outage was being charged to the sweep** (finding 2). `RecordSweepProgressAsync` abandoned on any
run that failed to advance the cursor, so AO3 being unreachable for one tick abandoned the sweep
*and* — the interval being measured from the start — cost the ship a whole interval of absence
detection on a run that read nothing. It now takes the same three counters `RecordBackfillProgress`
takes and makes the same distinction: told nothing, stay in flight; answered and got nowhere,
abandon.

**Every ship would have swept on the same tick** (finding 1). Every already-followed ship has a
backfill completion or a follow date well over an interval old, so the first poll after this
deployed would have put all of them into a sweep at once — and a sweep in flight beats the
incremental pass, so the instance would have stopped collecting new works everywhere until the
backlog drained sequentially behind the shared gate. `FullSweepIsDue` now adds a per-ship offset
derived from the ship id, which makes a ship's sweeps 30–60 days apart. Derived rather than random
because the check runs every poll: a random offset re-rolls every minute and spreads nothing.

**Two smaller ones, both about what a column or a message claims.** A sweep that declined to
conclude was still writing `LastFullSweepCompletedAt`, so the column an operator reads as "the last
time this listing was walked" would name a walk that established nothing — it now writes the date
only alongside a conclusion. And `JumpCursorBackFrom`'s run-history line promised "retrying from page
N/2", which is true of a backfill and never of a sweep: the halved cursor is precisely what puts it
below the run's start page, which is the abandon condition. It now says which will happen.

**`AdminShipsController.RestartBackfill` clears the sweep cursor** (finding 7) — a file this task did
not otherwise touch, taken because the hazard is one this task created. A `Failed` ship is eligible
for both a sweep and a restart, so a sweep could sit at page 40 through however many ticks the
re-run backfill needed and then resume, claiming to have walked one listing from pages read weeks
and a whole re-walk apart. The start date is deliberately left, so the next sweep is spaced from the
last attempt rather than beginning the moment the backfill finishes.

**Filed rather than fixed: T85** (the worker schedules off `DateTime.UtcNow` while everything it
drives reads the injected `TimeProvider` — pre-existing, and a seam rather than a defect) and
**T86** (no DTO or page shows sweep state, so a ship sweeping for several ticks looks like a ship
doing nothing). Both are real; neither is in `delivers`, and both are cheaper as their own diff.

## 2026-08-28 — T77: eleven defects over one subsystem, and the five decisions they needed

The pass itself was scheduled by the run order and is not re-argued here. What follows is the five
questions the eleven tasks left open, and one thing that did not happen.

**No schema change, against two tasks that had budgeted for one.** T64 offered a concurrency token on
`Download` and priced it as two migrations. What it is actually about is one specific overwrite — a
re-arm landing on a row a worker has claimed — so the guard is the same one T11 chose, moved from
before the write to inside it: `ExecuteUpdateAsync` with `Status != Downloading` in the `WHERE`, and a
re-read when it matches nothing. A row version would also have made every unrelated concurrent write
to a `Download` a 409 for callers that have no such problem. T11's "checked, not locked" stands.

**A `Retry-After` longer than the ceiling stops the retrying rather than shortening it.** T73 named two
candidate answers and said they were not exclusive; both are taken, but the cap is not "come back in
two minutes instead of an hour" — that would be asking again sooner than AO3 said, which is the one
thing honouring the header is supposed to prevent. Past `MaxRetryAfter` (2 minutes) the response is
read as the failure it is, its caller records it, and the run's own circuit breaker ends the pass.
**Rejected: an instance-wide "send nothing before T" hold**, which would honour a long ask exactly —
and is precisely the defect T73 is named for, one request's wait becoming every request's. The waits
that are made now happen outside the gate, so a retry parks one request and nothing else.

**The download deadline starts at the transfer, and the message stays.** T67 asked which of the two to
change. Moving the start makes the existing message true rather than merely likelier: "AO3 stopped
sending the file before it was complete" is now only ever said about a transfer that began and did not
end. What used to spend the deadline — the gate wait, the 5–8s spacing, the retry backoffs — is
bounded by the ceiling above instead. `Ao3HttpClientOptions.DownloadTimeout`'s own docstring said
"gate wait included" and now says the opposite; it was documentation of the defect.

**A stored file is re-checked on disk by both readers, and a re-fetch repairs the row rather than
adding one.** T69's unverified half is confirmed: `DeleteDownload` removes only the request, so the
orphaned `WorkDownloadFile` row survives and a fresh `POST` finds it again — delete-and-ask-again could
not recover it, and nothing else in the app deletes a file row. Both readers of that row now stat the
file (`DownloadFetcher.FindUsableFileAsync`, `DownloadsController.UsableFileAsync`). The orphan is not
deleted: the path is derived from (work, format, version), so the re-fetch writes to exactly where the
row already points, and `StoreFileAsync`'s existing unique-index branch updates the size and checksum
it comes back with. One row, repaired, by the fetch the reader asked for.

**A transport failure re-queues; the breaker is what stops it cycling.** T70 asked what bounds it. The
fetcher records every failure against the drain's budget before it throws, so `MaxConsecutiveFailures`
of them opens the breaker and the rest of the queue is held rather than attempted — the same bound the
ship walk relies on when it re-asks a page. Said in a test (`Stops_re_queueing_once_the_archive_is_
plainly_down`) rather than left implied.

**And the smaller one: the orphan delete belongs where "no row survived" is known.** T71 offered the
caller as the place to delete `destination`. It went into `StoreFileAsync`'s catch instead, at the
`winner is null` branch — the one point that has established no row names those bytes. In the caller it
would also fire when the lookup itself threw, which is the case where a row may well exist.

## 2026-08-28 — T77: what the test fixture had to grow, and what it had been asserting

**Both `SeedFileAsync` helpers now write bytes.** They wrote a `WorkDownloadFile` row and nothing on
disk, under a docstring that said "bytes on disk, as the download worker will leave them" — so nine
tests asserting "already on disk" were asserting a row. T69's fix turned every one of them red, which
is the correct signal and not a regression: the helper was the thing that was wrong.

**`LibraryTestHost` grew one seam, `ClaimDownloadsWhileTheFileIsRead`.** T64's window — between the
controller reading a request row and writing it back — is inside one controller call and reachable by
nothing else; a test that raced a real worker against it would assert a timing rather than a rule. It
is a `DbCommandInterceptor` attached to the app's own contexts only (`NewContext` builds its own
options), disarmed unless a test arms it, firing once on the lookup of the stored file. Registering it
as an `IInterceptor` in the container did **not** work — EF did not discover it — so it is passed to
`AddInterceptors` explicitly.

## 2026-08-28 — T77's review: eight findings, six taken, and the one bound that was wrong

`/code-review high` on an explicitly named twelve-file target ran to completion — the fifth in a row —
and reported reading exactly the working-tree diff. **All eight findings were in it.** Six were fixed
here; one was a mutation run's litter; one is declined.

**The re-queue had no bound across polls, and that is the finding worth the review.** T70's release
was justified by the drain's circuit breaker — and the breaker is per drain, rebuilt by the next
poll. So an instance whose archive does not resolve would spend `MaxConsecutiveFailures` requests
every sixty seconds for ever, ~4,300 a day at an endpoint that is not answering, while the reader was
shown a request saying only "queued". That is the traffic shape `Ao3LoginBackoff` exists to prevent,
reproduced one subsystem over. The reviewer also named the other side of it: widening
`MarkFailedAsync` to `Pending` for T65 meant a store that was briefly locked — SQLite's "database is
locked", which this app can produce, the controller and the worker writing the same file — settled a
request that used to be retried.

**Both are now one mechanism: `DownloadWorker` counts consecutive failed polls per request, releases
under three and records a failure at three.** Everything escaping the fetcher is infrastructure — the
archive unreachable, the database locked, the disk refusing — and all of it may work next poll, so
none of it settles a request on one attempt and none of it retries for ever. It replaces
`NeverReachedAo3`, whose exception-type test was drawing a line that turns out not to be the useful
one. **Rejected: a column on `Download`.** The count is consecutive, per request, and read by nothing
outside this loop; two migrations would buy only surviving a restart, and a restart already re-queues
every claimed row. What it costs is that an instance restarted repeatedly re-attempts a hopeless
request, which is said in the code.

**Four smaller ones, each about a claim the diff made and did not keep.** `MaxRetryAfter` read only
`Retry-After`'s delta form, so the same header written as an HTTP-date fell through the new ceiling to
this instance's ten-second backoff — the opposite of honouring it — while the new docstring said the
ceiling governed `Retry-After`. The winner-row repair had no failure path, so a save that threw left
correct bytes under a row describing the ones that had gone. `NeverReachedAo3`'s remarks cited
`SendWithRetryAsync`, which this diff deleted; the two other references to it (`Program.cs`,
`Ao3ShipIndexScraper`) were stale for the same reason and fixed with it. And `NewContext`'s summary
had been left attached to the method inserted above it.

**Declined: making `Ao3SessionProvider`'s login gate injectable.** It is static because one deployment
per process is exactly what it is for, and the coupling the reviewer names is real but test-only —
`Ao3SessionProviderTests` parks inside the gate for about 250ms and any parallel class calling
`EnsureSessionAsync` waits that out. Production surface added for a test's timing is the wrong trade;
if that hold ever becomes a problem the test is what should change.

**One finding was not the diff's: `var before` in `Ao3SessionProvider` was litter from this
iteration's own mutation run**, whose restore order put the mutated file back. It changed nothing —
the line was assigned and never read — and it is gone. The lesson is the review's rather than the
code's: a mutation script that restores by rewriting whole files must restore in the order it
mutated, and the diff must be re-read before it is reviewed.
