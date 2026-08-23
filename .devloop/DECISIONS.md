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
