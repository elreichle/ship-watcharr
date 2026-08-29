# Decisions

Plan changes only — tasks added, split, re-scoped, or dropped, each with its reason.

> The 71 entries before this point live in [`DECISIONS-archive.md`](DECISIONS-archive.md) (2026-08-22 — initial plan → 2026-08-27 — T46's review: two findings, both about what the diff claimed rather than what it did). Do not read it end to end; `grep` it for a task id when a live entry points into it.

## 2026-08-29 — T58: the date bound is the listing's own filter parameter, read off the capture

**The tag listing filters on `work_search[date_from]`, and that is the only evidence this rests
on.** `ao3-empty-listing.html`'s `form#work-filters` offers `work_search[date_from]` and
`work_search[date_to]` under the heading "Date Updated" — the same `revised_at` the pass sorts by —
and has no `revised_at` field anywhere. The capture is a date-bounded request in its own right: its
`date_from` carries a future date and its heading reads `0 Works in`, over a tag that plainly holds
more than none. So the parameter is applied, and a filtered heading counts the filter's result set
rather than the tag — which is what `RecordTotal`'s skip and
`HeadingCountsMoreThanTheRunWasServed`'s denominator have always assumed and can now assume for a
reason. What went out before was `work_search[revised_at]={"> date"}`, the advanced search's syntax
at `/works/search`; Rails discards the unknown nested key here without a word.

**No test can prove AO3 honours it, so the tests pin the URL and nothing more.** A substring
assertion on `revised_at` passed for the whole life of the bug — the sort column carries that name
too — so the incremental URL is now asserted whole, and the unfiltered first pass beside it.

**The inclusive bound keeps its day of slack rather than being tightened by one.** `date_from`
includes the day it names where `> date` excluded it, so the same `AddDays(-1)` now asks for a day
more. Left as it is: this bound decides no boundary — the cut is made client-side against the
watermark — and wider is the only direction it is allowed to be wrong in.

**`listingWasFiltered` was true while every incremental listing was unfiltered, and the two errors
cancelled.** The flag meant "we sent a bound"; the bound was discarded; so a heading counting the
whole tag was compared against the run's own blurbs. `RecordTotal` refused figures it could have
trusted (T30, T44) and `PlausiblyTheEndOfTheListing` refused pages — both conservative, neither
wrong in a way that lost works, which is why nothing ever failed over it. With the parameter
applied the flag is true of the request it sits beside, and the logic under it is unchanged
because it was written for the case that now actually happens.

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

## 2026-08-28 — T16: what counts as news is three conditions, and two of them are the caller's

T16's notes asked for the rule to be decided and stated: new to the ship, new to the instance, or
only from the incremental pass. **All three of those, taken alone, are wrong.** New-to-the-instance
would silence a work already held under another followed tag, which is news to this ship's watchers.
New-to-the-ship alone lets a backfill announce four thousand works. Incremental-only alone still
announces the whole back catalogue, because the first incremental pass over a newly followed tag has
no watermark and so reads every work in it as fresh — the case the notes name and the one the
obvious rule misses. The rule shipped is the conjunction: **new to the ship, incremental, and the
ship already had a watermark when the run started.**

**Rejected: passing the run mode down.** `IngestAsync` took a bool, not a `ScrapeRunMode`. The
ingestor has no other use for the mode and would then hold a copy of the rule that the scraper also
holds; the bool makes the split explicit — one condition is the page's, two are the pass's.

**Rejected: a unique index on (user, ship, work).** Nothing can produce a duplicate, and the
constraint's failure mode is losing a whole ingested page to one row — the same trade the truncation
rules in this file already decided the other way. The per-user cap bounds the table regardless.

## 2026-08-28 — T50: the filtered heading is re-based, not skipped

A filtered page 1 counting 4,317 matches over zero blurbs now stops with `Error`. The heading
condition was gated on the listing being *unfiltered*; it is now compared against the denominator
the heading counts — zero unfiltered, the run's blurb tally filtered — on every page, which is C3's
existing rule for page 2 applied one page earlier.

**Rejected: leaving page 1 to conclude.** T42's argument — holding a heading against zero blurbs
fails a quiet pass every tick — is about the *unfiltered* heading. A filtered request matching
nothing is served `0 Works` (T39's capture), so the quiet pass still concludes; only the
contradiction is refused. And refusing spends no extra request: the run asked for page 1 either way,
so the whole cost is a run recorded failed instead of a success over an empty library.

## 2026-08-29 — T79: the restricted-work warning stays, now as a measured invariant

T79's `delivers` offered two branches, and the captures chose the first: an anonymous listing is not
shown restricted works at all — 12,285 works against 13,736 for the same URL and sort, the same
twenty on page 1, every filter facet up by the same tenth. So the premise holds and the warning at
`Ao3ShipIndexScraper`'s `!response.Authenticated && …IsRestricted` is unreachable in practice.

**Rejected: retiring it as dead code.** It is unreachable *because* AO3 withholds those works, which
is the assumption `LastKnownTotalWasAuthenticated` is built on — so the branch is precisely the
alarm for the day that stops being true, and the day it fires is the day the flag goes wrong.

## T10 — the detail fetch is a worker of its own, not an `IAo3Scraper`

The task's notes asked for a new `IAo3Scraper` with a key in `Ao3ScraperKeys`, "registered in
`Program.cs`, no scheduling changes". Unrunnable as written: `ScrapeJob` has a unique index on
`ShipId`, and the only two writers create one job per ship always carrying `ShipIndex`, so a second
key is never scheduled. Rejected the alternative — a composite unique key, two migrations, both job
writers and ~10 tests — because it is also the wrong scope: a work's published date and tag list are
per-work facts shared by every ship carrying the work, and a ship-scoped pass re-asks per tag.
`WorkDetailWorker` is a sibling of `ScrapeWorker`/`DownloadWorker` behind the same gate, session and
rate gate; no schema change.

## T10 — a page this pass cannot read writes nothing, and is written off after three

Selection is deterministic and only a read removes a work from the backlog, so an unreadable page
would be re-asked every pass for ever — ten of them starve the feature, three answering non-OK trip
the breaker and stop every pass. `WorkDetailAttempts` (in-memory, forgotten on restart, as
`DownloadWorker`'s counter is) writes a work off after three unreadable *answers*; transport failures
are the breaker's business and never counted. A tagless work page counts as unreadable rather than
being stamped: `DetailFetchedAt` also puts the listing pass into add-only mode, so stamping one would
leave no source that may ever drop a tag. A 404 is recorded as a success against the budget — it is
the conclusive answer the pass exists to record.


## 2026-08-29 — T56: the races are pinnable after all, and the clear path had to move with them

T6 recorded that neither state race could be tested — one SQLite connection, no seam. There is one:
`LibraryTestHost`'s `ClaimingInterceptor`, armed on the *write* command rather than a read, runs a
second request's write between the controller's read and its own. All four collisions are now
pinned, and `WasRaced` makes a fragment that matches nothing a failure instead of a green vacuum.
Rejected: leaving the clear path alone. Re-inserting after a lost update means a losing clear can
now find a row where it assumed absence, so it retries too rather than reporting a false absence.


## 2026-08-29 — T59: the request's answer and the reader's copy are two columns, not one

T11's rule stands — a row saying Complete may never name bytes of a version the work has moved past
— but it was enforced by nulling `WorkDownloadFileId` on a re-arm, which also let go of a file still
sitting on disk. Split instead: `WorkDownloadFileId` remains the request's own answer, still refused
by `GET /downloads/{id}/file` for anything but Complete (T14's
`Will_not_serve_a_request_that_is_queued_while_still_naming_a_copy` is unchanged and still passes);
`PreviousWorkDownloadFileId` is the copy the reader is holding, served while the request is queued or
failed, cleared by `CompleteAsync` the moment a replacement lands. **Rejected: keeping one column and
gating serving on Failed only** — no migration, but the reader would then be locked out of their own
file for the whole time the request sat in the queue, which a held breaker or a missing login can make
indefinite. Both download views label it "Save earlier copy"; a non-Complete row with a held copy
reports its size as `previousSizeBytes` rather than reading as having nothing. The reference is
written only for bytes checked to be on disk at that moment — `UsableFileAsync` answers null for a
vanished file as well as for a stale version, so an unchecked carry-over offered a download link over
a file just established to be gone. **Kept, not dodged: the SQLite migration is the first here EF
implements as a table rebuild** (verified: it drops and renames `Downloads` outside a transaction).
Dropping the constraint on SQLite alone would avoid that, and was rejected — it would leave the
migrated schema differing from the one `EnsureCreated` builds for the tests, which is the drift the
two-context arrangement exists to prevent. In BACKLOG as a general question about startup `Migrate()`.

## 2026-08-29 — T60: the page's age, not the address it carried

A download tells a stale work page by comparing when this instance saw the revision
(`Work.UpdatedAtObservedAt`, our clock, stamped by the ingestor only on a move) against when the
page came off the wire (`ScrapeHttpResponse.FetchedAt`). Rejected: remembering the address a
`WorkDownloadFile` was fetched from, the shape T60's notes weighed — it only sees a work some format
of which is already stored, and misses the first download after a revision entirely. Also rejected:
bypassing the cache on every download fetch, which costs a rate-gated request per format for the
common case the cache exists for.

## 2026-08-29 — T78: a column, not a MAX over the run history

Where a restart resumes is `Ship.BackfillResumePage`, a nullable column on both providers, raised
beside the cursor once a page's works are committed and cleared when a backfill begins. Rejected:
`MAX(ScrapeRun.LastPageFetched)` over this ship's runs since `BackfillStartedAt`, the no-schema
variant T78's notes weighed — the run history is prunable, so the number would vanish exactly on the
long-stalled ships this exists for, and a MAX has nowhere to put the half of this that no run
fetched. The restart resumes *on* that page rather than after it, spending one re-read for the page
whose next link points into unwalked listing. **Named "ResumePage", not "DeepestPageRead"**: a page
an admin names replaces it in either direction, because a restarted walk that stalls again without
reading anything must not have the *next* restart discard the admin's choice — deeper, that is the
pages between the two re-requested at the shared gate, which is the cost this task removes. Not
conditioned on the page differing from the default: the Ships page pre-fills that default and posts
it, so the one-click path arrives as an explicit page and writes back what was already there.

## 2026-08-29 — T80 requires the heading on *both* halves of the page 1 short-circuit

T80 names the unfiltered page. The short-circuit is one clause serving both, and the rejected
option — waiving it for filtered page 1 only — would have left a filtered page 1 concluding on no
evidence one line after T50 refused exactly that on the evidence it had. Requiring the heading of
both costs the filtered path nothing: a quiet pass is served `0 Works in <tag>`, which T50's own
fixtures already carry, so both its pinned tests stay green and neither became vacuous.

## 2026-08-29 — T84: what a missing mark means to a reader

**A departed work leaves the listings and stays reachable, and a work the reader marked stays
everywhere.** `WorkQueries` splits: `Reachable` (membership, missing or not) scopes the detail page,
the state write and the download request; `Library` — `Reachable` minus works whose every watched
membership is marked missing — scopes the feed, saved-filter counts, statistics and vocabularies. A
`UserWorkState` row keeps the work, since an emptied state is stored as no row, so a sweep's wrong
mark can only ever drop works nobody here touched.

**Rejected: the saved-filter criterion and the feed toggle.** A criterion costs two migrations and
leaves the default wrong; a toggle desynchronises a saved set's match count from the list it links
to, which is the one thing `WorkQueries` exists to prevent. The retention clause needs neither.

**`StatsQueries.PerShip` restates the rule per ship** (review finding): counting a work under a tag
it left contradicted that tag's own feed. Applied to the flattened rows — inside the `SelectMany` a
correlated EXISTS makes the query need SQL APPLY, which SQLite does not have.

## 2026-08-29 — T85/T86: the sweep, from the clock to the page

**`ScrapeWorker` reads the injected `TimeProvider`, and `ScrapeRun.StartedAt` with it.** The rule
that forced it is T15's sweep interval, which compared the wall clock against a date the scraper
had stamped from the fixture's — untestable, and wrong in the direction that re-sweeps for ever.
Stamping `StartedAt` from the same clock (review finding) keeps one row on one clock; the run
history orders by it. `FullSweepIsDue`'s `Ship.CreatedAt` fallback is deliberately left outside the
seam rather than putting a clock through every writer of a Ship row, and says so.

**The sweep is its own line on the Ships page, not part of the status label.** The two are
independent — a ship whose back catalogue was given up on can be mid-sweep — and one slot would
have dropped one to say the other.
