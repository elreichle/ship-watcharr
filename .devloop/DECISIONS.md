# Decisions

Plan changes only — tasks added, split, re-scoped, or dropped, each with its reason.

## 2026-08-22 — initial plan

Twenty tasks, agreed with Emma during `/devloop-init`. Notes on the shape of the list:

- **T5, T10 and T13 start `blocked` on captured AO3 markup.** The loop's shell has no network, so
  a parser it wrote would be green against HTML invented by the model — which is precisely what
  this project's verify-don't-guess rule exists to prevent. They unblock when a human saves the
  named file under `backend/Ao3Tracker.Tests/Fixtures/`.
- **The AO3 login gate (T2) lands before the login flow (T5) works.** That is deliberate and was
  agreed: it is phase 3 before phase 4 of the instance-credential plan. The gate is on a
  credential *existing*, not on a session being live.
- **Auto-downloading on saved-filter match was considered and dropped.** Every download stays a
  request someone made; an unattended rule could generate a lot of AO3 traffic.
- **The frontend gets no test runner.** There is none today, and adding one is not in scope; UI
  tasks verify through `tsc -b` plus a live check against a throwaway instance.

## 2026-08-22 — T21 and T22 added, from T1's review

`/code-review` over T1's diff reported three defects, all of them in scraper code committed before
this loop started rather than in T1's own changes. They are not T1's to fix — a task's diff stays
inside its own `delivers` line — so they are queued as their own tasks, verified against the source
before being written down:

- **T21**: `WorkIngestor.ResolvePseudsAsync` queries by raw `Username` and keys by the normalized
  one, so a case difference means a duplicate insert and a failed page.
- **T22**: an unparseable blurb date reads as `DateTime.MinValue`, which the incremental stopping
  rule counts as "not new" and stops on — skipping works that a later pass can no longer reach,
  since the watermark has moved past them. Folded in with it: one write in the same class that uses
  `DateTime.UtcNow` instead of the injected `TimeProvider`.

Both are `blocked-by: none` and sit after the planned work; neither blocks anything.

## 2026-08-22 — T23 added, and more for T21, from T2's review

The same `/code-review` pass over T2 reported two further defects in pre-loop scraper code, both
verified against the source before being written down:

- Folded into **T21**: `ApplyTags` looks tags up by their untruncated name while the dictionary is
  keyed by the truncated one, so a tag over 200 characters is dropped and then reconciled away.
- New **T23**: a non-OK page response `continue`s without advancing `page` or `pagesFetched`, so a
  persistent 500 re-requests one URL until the run's budget is gone.

Worth knowing for the rest of the loop: every review pass sees the whole branch diff, not only the
task's own hunks, so these findings will keep coming back until T21–T23 are done. Re-reporting them
is not a signal that a new task introduced them.

## 2026-08-22 — T21, T22 and T23 pulled forward, to run next

They were queued at the end of the list; they now run before T6. Three reasons, in order of weight:

1. All three are live defects in shipped scraper code, and two lose data silently — a page whose
   ingest fails on a duplicate pseud, and an incremental pass that stops early and then moves its
   watermark past the works it never read. Every scrape between now and whenever the list reached
   them would keep paying that.
2. T23 is a politeness defect specifically: a persistent 500 makes this app re-request one URL
   until its whole budget is gone. That is the exact behaviour the rate limiting and budgets exist
   to prevent, and this project does not get to ship it while it advertises the opposite.
3. Every per-task `/code-review` sees the whole branch diff and re-reports all three, at roughly
   75k tokens a pass. Fixing them makes the remaining sixteen tasks' reviews cheaper and their
   findings meaningful.

Nothing depends on them, so nothing else moves.

## 2026-08-22 — what an undated blurb means to the walk, decided in T22

T22 left the rule open: "decide what an undated blurb means to the stopping rule". Decided, and
written into `Ao3ShipIndexScraper` as a comment beside the code:

- **An undated work abstains.** It is ingested — it is a real work, and storing it with an unknown
  revision time is a smaller loss than dropping it — but it casts no vote in the watermark stop and
  cannot propose a new watermark. This is the reading the task suggested and the one the backfill
  path already took.
- **A page on which *every* blurb abstains stops the pass**, with `ScrapeStopReason.Error` so the
  watermark is left where it was. Abstention on its own would otherwise open a new unbounded walk:
  with nothing on a page saying where in the listing the run is, an incremental pass would read the
  whole tag, every time — the exact cost the pass exists to avoid. Not part of T22's `delivers`, but
  a direct consequence of the abstain rule, so it belongs in the same change.
- **Unless it is the last page.** Added after `/code-review` found the case: a small tag on one page
  whose dates the parser cannot read has no page after it, so there is no runaway walk to prevent,
  and reporting an error there would leave the watermark null and log the same complaint on every
  pass forever.

Two further things settled in the same task rather than queued:

- `WorkIngestor.Apply` now sets `UpdatedAtIsApproximate = true` when the date is unreadable. Before
  T22 an undated blurb never reached the ingestor on the incremental path at all; now that it does,
  a work first seen that way would have stored year 1 as an *exact* revision date. The parser is
  careful to report an unreadable date as approximate; the row has to keep that.
- `LibraryTestHost` now registers a settable `FixedClock` as the container's `TimeProvider`,
  hand-rolled rather than pulled in from a testing package. It is what lets a test tell a timestamp
  written through the injected clock apart from one written by calling `DateTime.UtcNow` — the
  second half of T22, and a seam the rest of the loop can reuse.

## 2026-08-22 — what a refused page does, decided in T23; and a correction to its premise

**T23's premise was not quite right, and the correction matters.** The task said a persistent
non-OK response re-requests one URL "until the budget runs out". It does not: every pass through
that branch calls `budget.RecordFailure()`, so the circuit breaker opens after
`MaxConsecutiveFailures` (3) and the run stops. Measured before the fix, a page answering 500 was
requested exactly 3 times, not 500. The bug is real but smaller than written down, and the honest
statement of it is:

- Three round trips to a page AO3 has already refused — and each of those is itself up to
  `MaxRetries`+1 attempts inside `RateLimitedAo3HttpClient`, which retries 429 and 5xx with
  backoff. So up to twelve requests for one page that was never going to be served.
- The run is then recorded as stopping on `Breaker`, which reads as "the archive is down" when in
  fact one page was refusing. The wrong diagnosis in the run history is the part a human pays for.
- `pagesFetched` never incremented on that path, so the `MaxPagesPerRun` ceiling could not fire —
  harmless while the breaker holds, but it means the ceiling was not the bound anyone thought.

**Decided: the walk adds no retry of its own for a non-OK status.** It stops the run with
`ScrapeStopReason.Error` and records the status. The reasoning is that the retry already happened
one layer down — a response reaching the scraper has exhausted the client's own retries — and a
status the client did not consider retryable (403, 410, an unfollowed redirect) will not become OK
by asking again. Nothing is lost by stopping: `FinishAsync` only advances the watermark for a run
that reached the end of the listing, and a backfill's cursor still points at the failed page, so
that page is retried once per run at the scheduler's spacing instead of in a tight loop inside one.

**The transport-failure branch keeps its retry, deliberately.** It is the same `continue`-without-
advancing shape, so it was considered rather than missed. It stays because nothing retried it:
`SendWithRetryAsync` only retries *responses*, so a timeout or a connection failure has had exactly
one attempt, and it is the failure most likely to succeed on the next. It is bounded by the
breaker, which is documented for precisely that. The asymmetry is now stated in a comment beside
both branches so it does not read as an oversight.

**`ScrapeOutcome` gained `ErrorMessage`, copied onto `ScrapeRun.ErrorMessage` by `ScrapeWorker`.**
Not in T23's `delivers` line, but "record the status" needs somewhere to record it, and that column
previously only ever held the message of an exception that escaped the scraper — a run that stopped
on a refused page recorded nothing actionable. The other two `Error` stops in the class (a 404 on
the first page requested, and a page no blurb on which could be dated) now fill it too, so the
field is not half-populated. No migration: the column is unbounded `TEXT`/`text` on both providers.

## 2026-08-22 — T24–T27 added, from T23's review; T24 and T27 to run next

`/code-review` over T23's diff reported five defects. **None was in T23's own change** — all five
are in scraper code committed before this loop started, and all five were verified against the
source (including the scheduling that makes the first one reachable) before being written down:

- **T24**: a multi-run backfill sets the incremental watermark from the *oldest* pages it read.
- **T25**: `pagesFetched == 0` is used to mean "page 1", so a resumed backfill can never complete;
  and a zero-work page mid-walk is taken for the end of the listing.
- **T26**: an unreadable byline parses to no authors, raises no parse warning, and the ingestor's
  `Reconcile` then deletes the work's existing creators.
- **T27**: one `AppDbContext` is shared by every job in a poll, and a failed save is retried from
  the `finally` where it escapes — leaving a run `Running` and its job due every minute.

**T24 and T27 run next, before T6.** Same reasoning as the earlier pull-forward, and the same kind
of defect T23 just closed: both make this app hammer AO3 in a loop. T24 leaves a completed backfill
re-reading its entire tag on every incremental pass forever; T27 leaves a failed job re-requesting
the same page on every minute-poll. Neither is theoretical — T24 is reachable by any tag over about
4,000 works, which is most of the ones this app exists to watch. T25 and T26 sit after them: both
are real, neither spins.

Worth recording because it changes how the earlier note should be read: the "every review re-reports
the same findings" observation from T2 held for T21–T23, but this pass found five *new* defects in
the same pre-loop scraper. That code has now had four review passes over it and is still yielding
findings, which says the scraper predates the loop's standard of care rather than that the reviews
are noisy.

## 2026-08-22 — a run-order list, and T28: audit the stopping rules before building a third pass

Two structural changes, agreed with Emma after T23's review, both made because the plan was relying
on prose that only worked by luck.

**`tasks.md` now opens with a "Run order" section, and it overrides file order.** The pull-forwards
recorded here for T21–T23 and again for T24/T27 were never actually reachable by the rule the skill
follows — "the first `todo` whose `blocked-by` are all `done`" is T6 by file order, and has been
since T21 was added. Three iterations honored the prose anyway, which was luck rather than
mechanism, and the next one might not have. Priority is not a dependency, so expressing it as
`blocked-by` would have been a lie; an explicit ordered list at the top of the file is the honest
form. It is self-deleting — entries go as their tasks complete, and the section goes when empty.

**T28 audits the walking and stopping rules, and T15 is now `blocked-by: T28`.** This loop has found
ten defects in pre-loop scraper code across four review passes, and the rate is not falling. Eight
of the ten are one shape: *a pass concluding something it had not seen enough to conclude* — an end
of listing, a watermark, an absence, an empty byline. T15 adds a third pass over the same listing
and is the only one permitted to conclude a work has left a tag, which is the strongest conclusion
in the system and rests directly on the rules that keep turning out to be wrong. Building it first
gets three broken passes instead of two.

T28 deliberately **fixes nothing** — it tabulates the rules the code actually has, and queues each
gap as a task added both to `tasks.md` and to T15's `blocked-by`. That keeps it to one bounded
iteration rather than an open-ended rewrite, and makes "the audit's findings land before the full
sweep" structural instead of something a future iteration has to remember. Its verification requires
every test it names to be confirmed to match more than zero tests — the trap T22 hit, where a filter
matching nothing looks exactly like a pass.

Sequenced fourth rather than last: the audit reads best while the T24–T27 fixes are fresh, and it
should be auditing the rules as repaired, not as found.

## 2026-08-22 — T24: which run may propose a watermark, and a rule that is wrong in both directions

**Decided: a run may propose an incremental watermark only if it read page 1, and no new column
carries a backfill's newest-seen across its resumes.** The listing is `revised_at desc`, so page 1
is the only place the newest work in the tag can be observed. That is every incremental pass and the
*first* run of a backfill; a backfill resuming at its cursor starts partway down the listing and its
newest reading is an old date. The rejected alternative was a `BackfillMaxUpdatedAtSeen` column
alongside the existing floor, written on every backfill run and read at completion. It was rejected
because the run that reads page 1 already holds exactly the reading that column would store, and no
resumed run can improve on it — schema for a value already known.

**And: a backfill that read page 1 may leave a watermark however it stopped, unlike an incremental
pass, which still needs a `Watermark` or `LastPage` stop.** The asymmetry is not a shortcut. An
incremental pass that stops early leaves works between its watermark and the newest thing it read
unvisited, and nothing looks that far back again. A backfill from page 1 is not exposed to that:
nothing it failed to reach is newer than what it read, and its cursor holds the skipped pages for a
later run.

**The finding worth carrying forward: a rule that refuses to conclude can cost exactly what a rule
that concludes too much costs.** Gating the watermark on "started at page 1" *alone* — the obvious
reading of T24's `delivers` line — would have left every large tag's watermark null forever, because
any tag needing several backfill runs ends its first run on the page cap, which is not a stop that
may propose. A null watermark makes the incremental pass fetch the whole catalogue, walk to the
200-page ceiling, stop on `Cap`, and do it again next tick: the same runaway T24 was filed to
prevent, reached from the other side. Eight of the ten pre-loop defects found so far were filed as
*a pass concluding something it had not seen enough to conclude*; this is the first evidence the
class is two-sided.

**T28 inherits that directly.** Its table needs a column for what happens when a rule declines to
conclude — what stays null, and what the next pass does with the null. A row that records only what
a rule may conclude would have rated the null-watermark fix as correct.

**Recorded as a gap: T24 shipped without its `/code-review` pass.** The review agent terminated on
the account's monthly spend limit (resets 3:10pm America/Chicago) after reading the diff and before
reporting anything. The task still meets the loop's bar for `done` — its own verification, the full
suite and the frontend build and lint were all run and read green in the iteration — but every other
task in this loop has had a review over its diff, and the four passes over this particular file have
found ten defects between them. The diff is three tests, a threaded parameter and a rewritten comment
block in `Ao3ShipIndexScraper.FinishAsync`; it is worth a review over the commit when limits reset,
and the next iteration should not assume it happened.

## 2026-08-22 — T27: where a run's completion is written, and one line folded in from its review

**Decided: a job's terminal write goes through a context that cannot be carrying the change set
that failed.** `RunJobAsync`'s `finally` used to call `SaveChangesAsync` on the same
`AppDbContext` the scrape wrote through. EF Core does not detach a change set the database
refused, so when a scrape failed *because* a save was refused — a page colliding on a tag or a
pseud, which is the failure T21 existed to reduce and cannot rule out — the `finally` asked the
database the identical rejected question and threw from a `finally`, escaping the job entirely.
The run stayed `Running`, `NextRunAt` was never advanced, and the job was due again on the very
next minute-poll: a tight retry loop against AO3, the third one this loop has found and the last
of the three that were live. The write now happens in `PersistCompletionAsync`, which tries the
job's own context, and on failure writes the same two rows through a fresh scope. It never throws;
the last resort is leaving the run for startup reconciliation, which is what this method exists to
make rare rather than routine.

**And: one scope per job, which is what `Program.cs` already claimed.** The poll now selects job
*ids* and each job gets its own scope, so the `AppDbContext`, the scraper and the ingestor a job
uses are its own. The job row and its ship are re-read inside that scope — an entity tracked by the
poll's context has no business being saved through the job's. A per-job `catch` around the call
contains anything that still escapes, so one ship's bad page costs one ship's run instead of every
job behind it in the tick. The comment in `Program.cs` was left alone: it is now true.

**Folded in from `/code-review`, one line in the same method: a returned error is a failed run.**
`Ao3ShipIndexScraper` reports most failures by *returning* an outcome with
`StopReason = ScrapeStopReason.Error` rather than throwing — a 404 on the first page, any non-OK
status, a page nothing on which could be dated. `RunJobAsync` copied that stop reason and error
message onto the run and then set `Status = Succeeded` unconditionally, so the run history said a
scrape that never read a page went fine. Folded rather than queued because it is one line inside
the method this task rewrites and it is the same question the task is about — whether a run that
failed is recorded as one. T23 added the `ErrorMessage` copy immediately above it and left the
status alone, so this is that change finished rather than a new direction.

## 2026-08-22 — T29–T33 added, from T27's review; T29 and T30 in front of the audit

`/code-review` over T27's diff reported six defects. One was in T27's own method and is folded in
above; the other five are in pre-loop scraper code and were each verified against the source before
being written down:

- **T29**: `RecordTotal` writes AO3's heading count on every page, but an incremental pass with a
  watermark asks for a `revised_at`-filtered listing, so the heading counts the filter's result set.
  A ship that backfilled to 4,317 works reads `LastKnownTotalWorks = 2` after one quiet pass.
- **T30**: `LastKnownTotalWasAuthenticated` is only ever set true, so it can outlive the total it
  describes; and the `sawRestricted` it is set from is computed over newly ingested works only.
- **T31**: `ParseTotalWorks` matches "Works" and falls back to the last digits in the whole heading,
  so a tag with exactly one work — heading "1 Work in …" — can report digits out of its own name.
- **T32**: the non-monotonic-boundary warning logs `BackfillNextPage`, set one line earlier, so it
  names the page after the one where the shift was seen.
- **T33**: `LoadExistingWorksAsync` chains three collection `Include`s with no `AsSplitQuery`, and
  nothing in this project sets `QuerySplittingBehavior` — a cartesian product per page, per pass.

**T29 and T30 are now in T28's `blocked-by`, and T15's.** Both decide what a pass writes back to the
ship, which is exactly what the audit tabulates, and both feed `LastKnownTotalWorks` — the number
T15's full sweep is supposed to check itself against before concluding works have left a tag. A
sweep reading a total that a routine incremental pass silently replaced with 2 would conclude that
almost the entire tag had disappeared. Expressed as `blocked-by` rather than as prose in the run
order, per the lesson recorded when that section was created: priority is not a dependency, but
*this* is one.

Worth recording: this is the fifth review pass over this scraper and the count of pre-loop defects
it has found is now fifteen. The rate is not falling, and T27's five continue the pattern — four of
the five are a value written back to the ship that nothing later can tell is wrong. T28 was already
sequenced before T15 for that reason; nothing here changes the plan beyond adding to it.

## 2026-08-22 — T29: which listing's heading is the tag's total, and the flag that travels with it

`RecordTotal` is gated on the request having carried no `work_search[revised_at]` bound, not on the
run's mode. The bound now comes from one function, `RevisedAtBound(mode, watermark)`, which
`BuildUrl` and the gate both read — so a change to *when* the filter applies cannot leave the gate
believing something else. Gating on the mode would have been equivalent today and wrong the moment a
backfill or a sweep grew a date window, which the `BackfillBeforeUpdatedAt` column is already
reserved for.

**A consequence worth naming: the total now goes stale rather than going wrong.** Only an unfiltered
pass writes it — the first incremental pass of a ship (no watermark yet), every backfill, and in
future every full sweep. A ship past its backfill and ticking over incrementally will keep the same
`LastKnownTotalWorks` indefinitely. That is the correct trade — a stale figure with an honest
`LastKnownTotalWorksAt` beside it is checkable, and a fresh figure that is silently the size of a
date filter is not — but **T15 must read the timestamp, not just the number**, before letting a
total license a conclusion about works having disappeared. Nothing outside the scraper reads either
field today, so there is no UI to correct.

**Folded in from T29's review: the authenticated-total flag is written only where the total is.**
`LastKnownTotalWasAuthenticated` is documented on `Ship` as whether the run that produced *the
stored total* was logged in. Skipping the total on a filtered pass while still latching the flag
would let the two come from different runs — a logged-out backfill's undercount wearing a later
authenticated pass's flag — which is a sharper version of exactly the defect T30 exists to fix, and
it would have been introduced *by this diff*. One line, one test, folded in rather than queued,
because the diff created it. T30 keeps the two halves that pairing does not fix: the flag is still a
one-way latch no anonymous run can clear, and `sawRestricted` is still computed over newly ingested
works only. T30's entry now says so.

## 2026-08-22 — T34–T36 added, from T29's review

Four findings, one folded in above; the other three verified against the source and queued:

- **T34**: any page parsing to zero works stops the walk with `LastPage`, and a backfill's
  `LastPage` becomes `BackfillState = Complete`. A 200 maintenance page or a markup change therefore
  retires a ship from backfilling having read nothing — the same failure the 404 branch directly
  above it was hardened against, reached by another route. `listing.TotalWorks` is already parsed and
  already distinguishes the two cases.
- **T35**: the `NormalizedPseudIdentity` dedup handles two capitalisation variants and collides on
  three, failing the upgrade on `PK_WorkAuthors`. Latent — nothing populated `Ao3Pseuds` before this
  branch.
- **T36**: `AdminScrapingPage` renders `ScrapingGateState.Problem` — *every* blocker — inside the
  section explaining the User-Agent, while the callout that should report a missing AO3 login is
  suppressed in precisely that case.

**T34 is in T28's `blocked-by` and leads the run order.** It is the same species as T29 and T24: a
pass writing the ship a conclusion it did not earn, which nothing downstream can tell is wrong.

Worth recording against the count kept in the previous entries: this is the sixth review pass over
this code and the pre-loop defect total is now eighteen. Three of T29's four were outside the
scraper's walk — the migration and the admin page — which is the first time a pass has found more
outside it than in. Read as the walk's density falling rather than the codebase's; T28 is still the
right next structural move, and it is now four tasks away rather than three.

## 2026-08-22 — T34: what an empty page is entitled to conclude, and T25 re-scoped

**Decided: a page with no readable works ends the walk only when everything on it agrees the tag
ended.** Three pieces of evidence, all already parsed by `Ao3BlurbParser.ParseListing`, say it did
not: the request was for a page above 1 (AO3 404s past the end rather than serving an empty 200, so
the walk only got above page 1 because something advertised more — a Next link this run read, or one
a previous run read before leaving the cursor here); the page carries a Next link of its own; or the
heading counts works the blurbs do not contain. None of the three, on page 1, is an empty tag and
concludes `LastPage` as before. Anything else is `ScrapeStopReason.Error`, which leaves
`BackfillNextPage` where it is, so the page is re-requested once per run at the scheduler's spacing
rather than being written off.

**The heading is only evidence on an unfiltered listing.** T29 established that a
`work_search[revised_at]` request's heading counts the filter's result set, not the tag — which is
why `RecordTotal` ignores it. Held against the blurbs, that same heading would make *every* quiet
incremental pass a parse failure on any tag whose filtered heading still prints a count: nothing new
since the watermark is the healthy, common case for that pass, and it reads as zero blurbs. So the
heading check reuses `listingWasFiltered`, the flag T29 introduced, and
`Reads_a_quiet_filtered_pass_with_a_populated_heading_as_nothing_new` pins it.

**Named rather than solved: a page 1 from which nothing at all parsed still concludes.** No heading,
no blurbs, no Next link, requested as page 1 — a maintenance page and an empty tag are
indistinguishable from that page alone, so the walk still calls it the end. `ship.LastKnownTotalWorks`
was considered as a fourth signal and rejected: T29 made that figure deliberately stale on a ticking
ship, and a tag whose works were genuinely all deleted would then never be concludable — the
"refuses to conclude" cost recorded under T24, paid to close a narrower hole than the three signals
already close. **This is a row for T28's table**, and the kind it says is most valuable: a rule that
concludes because nothing on the page contradicts it.

**T25 is re-scoped to its half (1).** Its half (2) — an empty page mid-walk taken for the end of the
listing — is the same `if (listing.Works.Count == 0)` block and the same question as T34, so it is
done here rather than left for a later iteration to rewrite the rule this one just wrote. What
remains of T25 is the 404 branch's `pagesFetched == 0` guard, a different branch and the opposite
failure: a resumed backfill that can never *reach* `Complete`, where T34's could reach it wrongly.
Both halves of T25's `delivers` line were kept in scope by the re-scope; only the sentence about
mistaking a page for the end has moved.

## 2026-08-22 — T34's review: three folded in, one queued as T37

`/code-review` over T34's diff reported five findings. Three were consequences of the diff itself
and are folded into it; one is a design contradiction that spans two branches and became T37; one
was about the task's own verification line.

**Folded in — a page declared unreadable must not have had its heading trusted first.**
`RecordTotal` ran *before* the readability guard, so an unfiltered page that parsed to zero works
wrote `LastKnownTotalWorks` from its heading and only then bailed. That matters because
`ParseTotalWorks` falls back to the trailing digits of any `h2.heading` when it finds no "Works"
(the same fallback T31 is filed against): an AO3 soft-error page served as 200 with
`<h2 class="heading">Error 404</h2>` put **404** over a real 4,317 and stamped it as freshly read.
The order is now guard, then record. This is the fourth time this loop has found the same shape —
a value written back to the ship that nothing downstream can tell is wrong — and the first time the
diff under review created the opportunity for it rather than inheriting it.

**Folded in — the listing container is the signal the rejected fourth one should have been.** The
T34 entry above records rejecting `ship.LastKnownTotalWorks` as a fourth piece of evidence, and
names the residual hole: a page 1 with no heading, no blurbs and no Next link still concludes "empty
tag", which is exactly the 200 maintenance page T34 was filed over. The review's answer is better
than the one that was rejected and better than the response-length heuristic that was not
considered: **an empty tag still renders `ol.work.index.group`; a maintenance page, a truncated body
or a proxy's substitute does not.** `Ao3ListingPage` gained `HasListing`, and a document without the
container can no longer be the end of anything. Worth recording that the test fake's own default
response is `("", OK)` — that shape was one stray test away from asserting `Complete`.

**Folded in — the run-history message now names the condition that actually tripped.** It read "the
listing says there are more" for every failing condition, including the one case where the listing
said nothing of the kind: a page reached only because an earlier page offered a next link carries
neither heading nor Next link, so the message contradicted the evidence printed beside it.
`SchedulesPage.tsx` renders that string verbatim to an operator diagnosing a stuck backfill.

**Queued as T37 — the 404 branch and the empty-200 branch now conclude opposite things about one
situation.** A resumed backfill's cursor points at page N+1; the tag shrinks; N+1 no longer exists.
If AO3 404s, T25 plans to call that the end of the listing and complete the backfill. If AO3 answers
200 with an empty listing, T34's rule calls it a parse failure and the ship re-requests that page on
every scheduled run forever. Both are defensible alone; they cannot both be right about the same
event, and neither bounds the retrying. **This is the two-sidedness recorded under T24 arriving a
third time**, and it is now the clearest argument yet for T28: two branches of one method, written
four iterations apart, each locally reasonable and jointly incoherent, is precisely what a table of
"which pass may conclude what, and what happens when it declines to" is for. T25's notes now refuse
to let its `page == 1` fix be written without settling T37 first, and T37 is in T28's `blocked-by`.

**And the verification line: a filter can also run *part* of a task.** T34 declared
`--filter ~Backfill`, which matched 2 of its 8 tests — and neither of the two guarding against the
new rule firing on a healthy pass, which are the ones whose regression would be worst. T22 filed the
lesson that a filter matching nothing looks like a pass; this is its second form, and the more
dangerous one, because it does not look empty. Corrected to `~Ao3ShipIndexScraper`. Checked while
the tooling was out: `~TotalWorks` (T31) and `~Monotonic` (T32) match **zero** tests today.

## 2026-08-23 — T37: what a cursor pointing past a shrunken listing may conclude, and the bound

**Decided: neither branch concludes. Both ask the page before the cursor.**

The situation T37 was filed over: a run stops with `BackfillNextPage = N+1` because page N offered a
next link; works are then deleted or hidden, the listing shrinks, and page N+1 no longer exists. AO3
answers that with a 404 or with a 200 carrying an empty listing, and which one it picks is AO3's
choice, not a difference in what happened. The two branches nonetheless read it as opposite things —
T25 planned the 404 as the end of the listing, so the backfill completes with its back catalogue
unread; T34's rule reads the empty 200 as a parse failure, so the ship re-requests that page on every
scheduled run forever. Both readings were less wrong than *unentitled*: a page that did not answer
cannot say why it did not answer.

So `CursorMayBeStale` recognises the situation — a backfill run's **first** answered request, on a
page above 1 — from either branch, and the answer is the same for both: step back one page and ask
the listing, which is the only authority on how long it is and which *can* answer. A Next link on
page N means the cursor's page is supposed to exist and this run's failure was passing, so the run
stops with `Error` and the cursor stays put. No Next link means the listing really does end before
the cursor, and `LastPage` → `Complete` is reached on the listing's own evidence rather than on a
guess about what a 404 meant. This is the third option T37 listed, chosen over the other two because
it *answers* the question rather than deferring it (option 3, leaving it for T15's sweep) or deciding
it by exhaustion (option 1 alone, a failure count converting into completed-with-gaps — a conclusion
drawn from failure, which is the shape this loop has now found wrong five times).

**The retreat is once per run, and the cursor moves with it.** Retreating twice inside one run is a
backwards walk of unknown length; a listing that lost several pages instead walks back a page per
run. That works because `RetreatFromStaleCursor` writes `BackfillNextPage = page - 1` *before* the
re-read: if the page reads, the backfill's own advance puts the cursor straight back where it was,
and if it does not, the next run starts a page lower and asks a **new** question rather than the same
one again. Retreating can never skip anything — it only re-reads pages, and ingestion is idempotent.

**The 404 branch's guard is now `lastPage == page - 1`, not `pagesFetched == 0` and not T25's
`page == 1`.** A 404 is the end of a listing only past a page *this run actually read*, which is what
`lastPage` holds. That is the guard T25's half (1) was reaching for, stated in terms of the evidence
rather than in terms of a page number: `page == 1` would have been right about a resumed cursor and
wrong about a retreat, and `pagesFetched == 0` was wrong about a resumed cursor. **T25 is closed by
this task** — nothing of it remained once the branch was rewritten, exactly as T34 took its half (2).

**The bound: `Ship.BackfillStalledRuns`, and `Failed` rather than `Complete`.** T37 asked for a bound
on how many runs a ship may spend re-asking, and the retreat alone does not give one — a maintenance
page answers nothing at any depth. A run that had to ask about its cursor and came away without an
end to the listing increments the counter; forward progress (`BackfillNextPage > startPage`) and
nothing else clears it. At `MaxStalledBackfillRuns` (5, so a listing that lost a few pages still
converges inside the allowance) the backfill is `ShipBackfillState.Failed` — the enum's existing
fourth value, which nothing had ever set. Not `Complete`, which would claim a back catalogue that was
never read, and which is the mis-conclusion T34 and T25 were both filed over. `ScrapeWorker` backfills
a `NotStarted` or `InProgress` ship only, so a `Failed` ship keeps its incremental pass and goes on
collecting new works; what it stops doing is spending two requests a run on a question nothing is
answering. Closing the gap it leaves is a full sweep's job, which is T15.

**Reading *a* page is not progress.** The first draft cleared the streak whenever any page parsed,
which a run that retreated to page 1 and found that unreadable too satisfies — it read a page, learned
nothing, and reset the counter that exists to count precisely that. Hence the cursor-advanced test.

**Two rows for T28's table, and one gap left open.** The rows: what a 404 is entitled to conclude now
depends on whether the run read the page before it, and a stalled backfill has a terminal state that
is neither complete nor still walking. The gap: **`Failed` has no operator exit** — nothing in the
product can put a ship back to `InProgress` once the count runs out, so a backfill written off during
an AO3 outage stays written off until a full sweep or a hand-edited database. Queued as **T38**, and
added to T15's `blocked-by`, since the sweep is what the state hands the gap to.

## 2026-08-23 — T37's review: three folded in, one queued as T39

`/code-review high` over the T37 diff reported four findings. Three were the diff's own and are
folded into it; one is about T34's committed code and became T39.

**Folded in — a cursor page the run sets aside must not be counted as a page it read.**
`pagesFetched++`, `firstPage ??= page` and `lastPage = page` ran *before* the empty-200 retreat, so
the unreadable cursor page was recorded as this run's first page. `FinishAsync` asks
`firstPage == 1` to decide whether a run saw the newest end of the listing, so a retreat from page 2
that went on to read page 1 and earn a watermark had that watermark thrown away — and the run
history stored `FirstPageFetched = 2` beside `LastPageFetched = 1`, a range that reads backwards.
The 404 route had no such bookkeeping, so the two routes T37 exists to unify still concluded
different things about one event; the review measured both. The readability test is now computed
before the counters and the retreat is taken above them.
`Reads_the_same_run_off_both_of_AO3s_answers_to_a_page_that_is_gone` asserts the two routes leave
the same outcome and the same ship, which is the property rather than an instance of it.

**Folded in — stepping back one page a run only converges for a listing that lost a few.**
`MaxStalledBackfillRuns` bounded the walk-back at one page per run, so a listing that shrank by
dozens exhausted the allowance and was written off `Failed` having never been broken — and the
review's scenario is a real one on this app, because the AO3 login is instance-level and a lapsed
one drops every restricted work out of every listing at once. `JumpCursorBackFrom` now halves the
cursor once a *second* consecutive page proves unanswerable: still backwards only, so it can no more
skip a page than the retreat can, but it finds a readable page in runs logarithmic in the cursor
instead of linear. The allowance went to 12 to match — two dozen requests, spread over twelve
scheduler intervals, and wide enough that anything exhausting it is a listing not answering at any
depth rather than one that merely lost pages. **This is what makes the give-up honest**: without it
the bound was as likely to fire on a big shrink as on a broken tag.

**Folded in — and the counter now catches a cursor that has run out of listing to retreat into.**
A consequence of the halving found while testing it: the cursor walks down to page 1, and page 1 is
never a stale cursor, so the streak stopped counting and the ship re-asked page 1 once a run forever
without ever being written off. The rule is now stated in terms of what the run learned rather than
which branch it took: forward progress clears the streak, a run the archive answered and that got no
further counts against it, and a run stopped by the budget, the breaker, a transport failure or a
refused status counts for nothing either way — writing off a back catalogue because AO3 was down for
an afternoon is the mis-conclusion this counter must not make.

**Folded in — the post-retreat message named a 404 that may not have happened.** `retreatedFrom` is
set by both routes, and the message said "AO3 returned 404 for page N as well as page N+1" whichever
one had happened, so a maintenance page at the cursor was reported to the operator as a 404. Each
page now reports what it actually did. Third iteration running in which this exact species —
a run-history string asserting more than the evidence behind it — has come back; `SchedulesPage`
renders it verbatim.

**Queued as T39 — `HasListing` is load-bearing on a claim about AO3's markup that nothing verifies.**
T34's fix made `ol.work.index.group` the thing that tells an empty tag from a page that is not a
results page, on the stated premise that an empty tag still renders the container. No test pins
that: `Ao3BlurbParserTests` has no `HasListing` case, and every scraper test reaching the zero-work
path goes through the `Page(n, [])` helper, which emits the container unconditionally. So the tests
prove the premise by assuming it. If AO3 instead renders a "No results found" notice with no
container, **every quiet incremental pass errors** — a `revised_at` filter matching nothing is the
common case on a quiet ship, and `Error` is neither `Watermark` nor `LastPage`, so the watermark
freezes and every scheduled run is recorded failed, forever. That is a worse failure than the one
T34 fixed, reached by the same rule. It needs one real capture of a zero-result AO3 index, which
this loop cannot fetch, so T39 is `blocked` on a fixture like T5, T10 and T13.

## 2026-08-23 — T26: what a byline crediting nobody is entitled to conclude

**Decided: "Anonymous", and nothing else, may conclude a work has no creators.** The parser had one
value for two facts — `IsAnonymous: authors.Count == 0` — and the two facts are "AO3 credits this
work to nobody" and "we could not read who AO3 credits it to". They arrive as the same markup: a
heading with no `rel="author"` anchors. `Ao3WorkBlurb.IsAnonymous` is now `bool?`, null being the
second, and `WorkIngestor` declines to write either the column or the author rows on null. That
matters because `ApplyAuthors` *reconciles*: an empty set is not "no news", it is a deletion, so the
old reading meant a change to AO3's heading shape would rewrite every re-seen work in the library as
anonymous with zero creators — and, since nothing counted a warning, the run record would say the
pass went fine. Same shape as the `UpdatedAt` rule two fields above it: an unreadable field leaves
what a working pass wrote.

**The asymmetry is deliberate and is the whole safety argument.** Mistaking a genuinely anonymous
work for an unreadable byline costs a parse warning and a stale author row. Mistaking an unreadable
byline for an anonymous work costs the credits of every work a pass touches. So every doubtful case
resolves to null, and the positive conclusion needs the word.

**Where the word may be read from is a second decision, and the review's finding.** The first
version excluded only the title from the search, which left the gift clause in it — and AO3 renders
a gift to someone who asked not to be named exactly like an anonymous creator. Under the very
markup change this task defends against (AO3 dropping `rel="author"`), every gifted work would then
have been read as anonymous and lost its authors: the defect, reintroduced through the fix. The scan
now takes the heading's words in order, skips the text of any link that is not `rel="author"`, and
**stops at the first standalone "for"**. Everything past that belongs to the recipient.

**Partial readability is not unreadability.** A byline where one anchor of two parses yields the
creator it read and reports a warning, rather than discarding both. A heading whose shape has moved
fails wholesale; a single odd anchor is a single odd anchor.

`Keeps_a_work_whose_title_is_unreadable` now expects two warnings where it expected one. That is the
new rule reading correctly, not a concession: an empty `h4.heading` yields neither a title nor a
byline, and both were always missing — only one of them used to say so.

Queued from the same review, none of it in T26's diff: **T40** (T37's own fix for counting an
unread page as read landed on the retreat path only), **T41** (the retreat's log line has six
placeholders and five arguments), and a line added to **T38** naming `BeginBackfill` as the place
`BackfillStalledRuns` is not cleared. The review also re-reported T39's unverified `HasListing`
premise independently — second pass, same finding, still waiting on a human with a browser.


## 2026-08-23 — T30: who owns the authenticated-total flag, and what may answer "anonymous"

**The flag is assigned by the run that writes the total, and by no other run.** `wroteTotal` — a
`bool` `RecordTotal` now returns — replaced `!listingWasFiltered` as the gate. Those are not the
same question: a filtered pass writes no total *and* is unfiltered-false, but an **unfiltered** pass
whose `h2.heading` will not parse also writes no total, and under the old gate it still stamped the
flag. The number on the ship then belonged to one run and the flag beside it to another, which is
the same divergence T29 folded in a fix for, reached through the heading rather than through the
filter. Ownership expressed as "did *I* write this number" cannot come apart from the number.

**And it is an assignment, not a latch.** That was T30's stated half: a logged-in run set the flag,
the session lapsed, and a later anonymous run overwrote `LastKnownTotalWorks` with a count short by
however many restricted works the tag holds while the flag still claimed the total came from a
logged-in session. A run that replaces the total now replaces the flag, downwards included.

**Deciding "anonymous" needed a source the blurbs cannot be.** Assigning `flag = sawRestricted`
would have closed the latch and opened something worse. Seeing a restricted work proves a run was
logged in; *not* seeing one proves nothing — a tag may hold none, or hold them on pages this run did
not read. So `sawRestricted` alone writes `false` over a genuinely authenticated pass, and that
false *false* is the harmful direction: it tells T15's sweep the stored total was counted at the
sweep's own visibility, when the total is really the higher logged-in count, and the sweep concludes
the difference has left the tag. Exactly the mis-conclusion the field exists to prevent, arrived at
from the other side.

So `ScrapeHttpResponse` gained **`bool Authenticated`** — whether the request that produced this
content carried a session cookie — and the flag is `response.Authenticated || sawRestricted`, one
run-level `readWhileLoggedIn`. It is on the *response*, not on `ScrapeContext`, because it describes
the content: a cached page is the page the caching request was given, so a logged-in run reading a
cached anonymous copy is reading anonymous content, and the field travelling with the body says so
for free. It is a constant `false` until T5, which is correct rather than a stub — nothing logs in
yet — and that is also why the false-negative window is empty at both ends: before T5 every run
really is anonymous, and after it the transport answers directly.

**The second half of T30 turned out to be no longer observable, and was fixed anyway.**
`sawRestricted` was computed over `toIngest`, the works handed to the ingestor, which T30 called out
as missing a page whose restricted works were all already held. That is only reachable on a pass
with a watermark — and since T29, such a pass is filtered, writes no total, and cannot touch the
flag. On an unfiltered pass `toIngest` *is* `listing.Works`: with no watermark every dated work is
fresh and the undated ones are appended. So the defect is presently dead. It is now computed over
`listing.Works` regardless, because "the two sets are equal" is an accident of where the watermark
filter is applied and not a rule anything states — the same species of accidental correctness T24
was, and the comment says so. No test pins it: there is no behaviour to pin.

## 2026-08-23 — T30's review: one folded in, one queued as T42, one added to T38

**Folded in: the flag reads the transport and nothing else.** The first version wrote
`readWhileLoggedIn |= response.Authenticated || listing.Works.Any(w => w.IsRestricted)`, keeping the
pre-loop premise that a restricted work is invisible to a logged-out request and therefore proves a
session. The review's objection is right and is the one this loop keeps arriving at: the disjunct's
only reachable effect is to **overrule the transport's "no" with a guess read off the page**. Before
T5 the client never authenticates, so the disjunct can only ever fire against a `false` the client
is certain of; after T5 it is redundant except in exactly that contradiction. And the premise it
rests on is an unverified claim about AO3's markup — the same species as the `HasListing` premise
T39 exists to settle, in the same file, found twice by two different reviews.

So the flag is `response.Authenticated`, and a restricted work on a response the client did not
authenticate is **logged as a contradiction rather than resolved as one**. If the premise is wrong,
that line is where it announces itself; if the client is wrong, likewise. Neither is a thing a walk
should conclude for itself, which is the standard the rest of this file is now held to.

Two tests changed shape with the rule. `Records_that_an_unfiltered_pass_read_the_total_while_logged_in`
took its authentication from a restricted blurb, so it became the counter-example:
`Does_not_let_a_restricted_blurb_claim_the_total_was_Authenticated`. And T29's
`Does_not_claim_a_filtered_pass_authenticated_the_total_it_did_not_write` took its authentication
from one too — which would have made it pass vacuously under the new rule — so it now gets it from
the transport. Both were confirmed to bite by restoring the disjunct and dropping the `wroteTotal`
gate: three of the six tests under the corrected filter fail on that mutation.

**Queued as T42, and added to T28's `blocked-by`.** `PlausiblyTheEndOfTheListing` requires
`page == 1`, so on an **incremental** pass any page past the first that parses to zero works is
classified unreadable and stops the run with `Error` — which is neither `Watermark` nor `LastPage`,
so the watermark cannot move and the next tick reads the same pages to the same end, forever. The
`page > 1` evidence is sound about an unfiltered listing (AO3 404s past the end) and unsound about a
`revised_at`-filtered one, whose Next link is driven by a count that can race the blurbs. The
neighbouring heading check is already gated on `listingWasFiltered` for precisely this reason and
this one is not — the same omission T34 fixed one condition to the right. It is a rule about what a
stop may conclude, so it goes where T37 went.

**Added to T38**, not a new task: entering `ShipBackfillState.Failed` does not reset
`BackfillStalledRuns`, and both reset sites are unreachable once the state is `Failed`. So even a
hand-edit of the state re-fails on the ship's first stalled run. T38 already owns the missing exit
from `Failed` and already names `BeginBackfill` as the restart path that leaves the counter alone;
this is the same fact from the other end and belongs in the same diff.
