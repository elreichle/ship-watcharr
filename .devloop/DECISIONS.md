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

## 2026-08-23 — T42: what a filtered listing's empty page may conclude, and what the heading is for

`PlausiblyTheEndOfTheListing` required `page == 1`. The argument for it is about an **unfiltered**
listing: AO3 404s past the last page rather than serving an empty 200, so a walk only gets above
page 1 because a page advertised more. Under a `revised_at` bound that argument does not hold — the
Next link comes off a result count that can race the blurbs, and one work leaving the window between
two requests answers page 2 with a well-formed empty listing. The pass then stopped with `Error`,
which is neither `Watermark` nor `LastPage`, so the watermark could not move and the ship re-read the
same two pages on every tick forever, having read the newest end of the listing in full each time.
The heading condition one line below was already gated on `listingWasFiltered` for exactly this
reason; this one was not.

**The waiver as first written was wrong, and the review caught it in this diff.** `page == 1 ||
listingWasFiltered` leaves a filtered page past the first needing only a container and no Next link
to be taken for the end — and because the heading is waived for filtered listings on the line below,
a page reading "60 Works" over zero blurbs was accepted. `LastPage` satisfies `mayPropose`, so the
watermark moved to page 1's newest and every work between the old watermark and that reading fell
behind both the `revised_at` bound and the client-side cut, permanently, with no full sweep in
existence to recover it. The defect being fixed cost two requests a tick and self-healed; the fix
would have lost works silently. That is the wrong direction, and the one this loop has ruled on
repeatedly — T24's "refuses to conclude" cost, paid again.

**So the filtered case asks the heading instead of waiving it.** A filtered heading counts the
filter's result set, which is useless as the *tag's* total (`RecordTotal` refuses it for that) and is
exactly the right number for the *walk's* — and an incremental pass always starts at page 1, so the
run's own tally of blurbs served is what it is comparable with. `FilteredHeadingSaysMore` is the
same question the unfiltered condition beside it asks, with the denominator the filter demands. It
is deliberately silent when no heading parses: no heading is no evidence, and the container plus the
absent Next link are what the conclusion rests on there.

The race the waiver exists for is the case where the heading **agrees**: a work leaving the window
between the two requests shrinks the count page 2 is served under, down to what page 1 already held.
The case that costs works is the case where it disagrees. One number tells them apart, which is why
this rule is worth having rather than reverting to `Error` on both.

`blurbsRead` is a new counter rather than a use of `worksSeen`, which counts what reached the
ingestor — on an incremental pass, only the works newer than the watermark. The rule needs what the
listing served, not what the run kept.

**Confirmed rather than assumed, per T42's own instruction:** `listingWasFiltered` is
`RevisedAtBound(mode, watermark) is not null` and `RevisedAtBound` gates on
`ScrapeRunMode.Incremental`, so a backfill is never filtered and nothing the waiver reaches is a
walk that could conclude `ShipBackfillState.Complete`. The five committed tests that pin T34's and
T37's rules were run against a mutation dropping the page condition entirely: five fail, three of
them backfill tests. The waiver's two halves were mutated separately — removing it fails the two
tests that want the end concluded, removing the heading guard fails the one that wants it refused.

**T42 did not wait on T39, and this is where the two now touch.** The waiver leaves `HasListing`
carrying more weight on a filtered page than it carried before, and `HasListing` rests on the
unverified premise T39 exists to settle. A test pins the maintenance-page case as `Error` under a
filter, which is the behaviour either way; what T39 would settle is whether a genuinely empty
filtered listing renders the container at all. Unchanged by this task, and one premise further into
load-bearing.

## 2026-08-23 — T42's review: one folded in, two queued as T43 and T44

`/code-review high` reported five findings. One was this diff's own and is folded into it, above.
Two are new tasks; two were already owned.

**Queued as T43, and it leads the run order.** The 404 branch's `lastPage == page - 1` guard
concludes `LastPage` → `Complete` from precisely the evidence `CursorMayBeStale` two hundred lines
below refuses to conclude from: page N-1 read, offering a Next link, page N absent. The two disagree
only on whether this run happens to have read page N-1 itself, and the reviewer demonstrated the
crossing against a running server rather than arguing it from the source — one transient 5xx during
a retreat leaves the cursor at N-1, and the next healthy run walks into the guard and retires the
ship with page N onward unread. This is T37's question asked of the branch T37 did not rewrite, it
is the strongest wrong conclusion the system can reach, and it is reachable today. It goes into
T28's `blocked-by` and T15's, where every rule of this kind has gone.

**Queued as T44.** T30 moved `LastKnownTotalWasAuthenticated` from the run's filter state to
`wroteTotal`, settling which *run* may assign it and leaving which *request* it describes alone:
`readWhileLoggedIn` ORs across every page while `RecordTotal` writes per page, so a run whose total
came off an anonymous cached page 1 and whose page 2 was fetched live with a session stamps the flag
`true` over a number fetched without one. Latent until T5 — nothing sets `Authenticated` before it —
and a live wrong answer the day T5 lands. T30's own argument, one scope further in.

**Already owned, not re-queued:** the unreadable page still counted as read on the non-retreat path
is **T40**, filed from T26's review and unchanged since; `BackfillStalledRuns` not being reset when
a backfill is written off is **T38**, whose other half was added from T30's review. Both were
re-reported independently by this pass, which is worth recording — a finding arriving twice from two
reviews is the loop's only signal about which queued tasks are actually costing something.

## 2026-08-23 — T43: what a 404 may conclude, and the one branch T37 did not rewrite

**Decided: a 404 concludes nothing about the length of a listing, on any page, in either mode.**

The 404 branch ended a walk on `lastPage == page - 1` — the page before this one was read, and this
one is not there. What made that look like the end of a listing was the phrasing: a 404 *past a page
this run actually read*. What it actually says only becomes visible with the walk's advance rule
beside it. The forward walk advances past a page only when that page offered a next link
(`if (!listing.HasNextPage) { LastPage; break; }` sits directly above `page++`), so `lastPage` being
`page - 1` **means** a page this run read said page N exists. That is the same evidence
`CursorMayBeStale` two hundred lines below refuses to conclude from — page N-1 read, offering a next
link, page N absent — and the two branches disagreed on it only over whether this run happened to be
the one that read page N-1.

The crossing between them costs one transient 5xx. A run with the cursor at N retreats, asks N-1,
gets a 500, and stops leaving `BackfillNextPage = N-1`; the next run is healthy, reads N-1, asks N,
takes the 404 and marks the ship `Complete` with page N onward never read. T37 pinned the first run's
shape as `Error` + `InProgress` + stalled, and the second reached the strongest conclusion in the
system off the same server. So this is T37's question asked of the branch T37 did not rewrite, and it
gets T37's answer.

**Refusing to conclude does not lose the genuine case; it defers it one run.** A listing really can
shrink between two requests of a walk, and page N-1's next link is exactly what that leaves behind —
which is the argument the guard was written for. But the run stops with the cursor already sitting on
the page that did not answer, so the *next* run's first request lands there, `CursorMayBeStale`
recognises it, and the retreat re-reads page N-1 and takes the listing's own word: no next link any
more and the backfill completes on evidence; still a next link and the ship is stalled rather than
retired, bounded by `MaxStalledBackfillRuns`. The price is one run and two requests, which is the
same price T37 already accepted for this situation arriving one run earlier. `Completes_on_the_next_run_when_the_listing_really_did_shrink_under_the_walk` pins the deferral
end to end, and `Reads_the_same_conclusion_off_a_404_whichever_run_read_the_page_before_it` pins the
unification as a property over one server rather than as an instance of it.

**The guard is not narrowed, it is removed.** Stating it correctly — a 404 may end a walk past a page
that was read *and offered no next link* — makes it unreachable, because a page with no next link
ends the walk where it stands and is never followed by a request at all. There was no version of this
condition left to keep.

**The removed test was the defect.** `Treats_a_404_past_the_last_page_as_the_end_of_the_walk` built
page 1 *with* a next link and left page 2 missing, under a comment reading "AO3 404s rather than
serving an empty page past the end of a listing" — a healthy end of listing has no next link on its
last page, so the fixture and its stated premise were about different events. It is now
`Refuses_to_end_a_walk_on_a_404_the_page_before_it_said_would_answer`.

## 2026-08-23 — T43's review: one overruled and queued, three queued

`/code-review high` over the T43 diff and the branch reported four findings. One is in this diff and
is **not** folded in; three are in earlier commits and became tasks. Nothing was folded in, which is
a first for this loop and wants stating plainly rather than glossed.

**Overruled, and queued as T45 — the incremental pass has no cursor to carry the question.** The
finding is correct and was demonstrated on a running server: `CursorMayBeStale` requires
`ScrapeRunMode.Backfill`, an incremental pass always restarts at page 1, so the recovery this task
substitutes for the conclusion does not exist for it. A page 1 that is entirely fresh and offers a
next link followed by a page 2 that 404s now stops with `Error`, `Error` may not move the watermark,
the watermark stays frozen so page 1 stays entirely fresh, and the pass sends the same two requests
every tick forever. The review's suggested shape was to keep the old conclusion for
`context.Mode != ScrapeRunMode.Backfill`.

Declined, because that is the trade T24 and T42 have both already ruled on and it is the same ruling
each time. The works on page 2 are older than everything on page 1, so a watermark moved past them is
a watermark no later incremental pass looks back behind: concluding costs the works permanently and
silently, refusing costs two requests per scheduler interval on a 5-8s gate. That cost is not an
accident either — it is the shape T23 deliberately chose, "retried once per run at the scheduler's
spacing, rather than in a tight loop inside one" — and every one of those runs is recorded failed
with a message naming the page and what contradicted it, so an operator is told. The pass also goes
on ingesting page 1 on every tick, so what is at stake is a gap, not a stopped library.

What the finding is right about is that the *bound* is missing, and a backfill in the same position
has one. That is T45, and it is a task rather than a line in this diff because closing it needs a
decision this task has no business making: what a permanently stuck incremental pass should do
instead of asking again, given that "give up on new works" is not a terminal state this product can
have. Widening the `revised_at` bound and dropping the filter for one run are both live options and
either may be a schema change on both providers. A give-up threshold is a conclusion drawn from
failure and inherits every objection this loop has raised to those, so it belongs in T28's table.

**Queued as T47, and it leads the run order — the filtered waiver fires on a page saying nothing.**
In T42's committed code, one iteration old. `FilteredHeadingSaysMore` returns false when the heading
did not parse, deliberately and with a comment saying "no heading is no evidence" — and T42's waiver
reads that false as permission. A filtered page 2 carrying the listing container, no next link, no
works and no heading therefore concludes `LastPage`, and the watermark moves to page 1's newest,
past everything page 2 would have held. T42 answered "what may a waiver rest on" and this branch took
the answer for "what may a stop conclude", which are not the same question. It leads because it is
the only silent wrong conclusion left in the walk: T45, T46, T38 and T40 all announce themselves in
the run history, and this one reports success. T28's `blocked-by` is now this task alone — the fifth
consecutive time that list has held exactly one rule about what a stop may conclude.

**Queued as T46 — a 404 on a run's first request never counts as a stalled run.** In T37's committed
code. `RecordBackfillProgress` returns early on `!askedStaleCursor && firstPage is null`, meaning the
archive told this run nothing — right for the budget, the breaker, a transport failure and a refused
status, wrong for a 404, which is the archive answering. With the cursor at page 1 no retreat is
possible (`CursorMayBeStale` needs `page > 1`) and no page is read, so the counter never moves, the
ship stays `InProgress`, `ScrapeWorker` keeps choosing `Backfill`, and it never falls back to an
incremental pass — the one stalled ship that collects nothing at all and never reaches `Failed` to
say so. Reachable for a verified ship whose tag is later renamed or deleted, since `ShipVerifier`
returns early for anything not `Pending`. Same family as T38, and on T15's `blocked-by` beside it.

**Queued as T48 — `SaysAnonymous` relies on the title being an anchor.** In T26's committed code.
The title is kept out of the byline only because `IsBylineText` drops text inside anchors that are
not `rel="author"`, so under the exact markup change T26 exists to defend against a work titled
"Anonymous…" parses as having no creators and `ApplyAuthors` deletes them. T26's own lesson arriving
a third time: when a fix turns one signal into a conclusion, ask what else in the same document can
produce that signal.

## 2026-08-23 — T47: the filtered waiver rests on the heading, and an absent heading is not one

**Stated positively, not as a negation.** T42's waiver read
`listingWasFiltered && !FilteredHeadingSaysMore(...)`, and `FilteredHeadingSaysMore` returns false
for a page whose heading did not parse — deliberately, its own comment saying "no heading is no
evidence". The negation turned that false into permission, so a filtered page 2 carrying the listing
container, no Next link, no blurbs and no heading satisfied every condition and concluded
`LastPage`. The helper is now `FilteredHeadingSaysThisIsAll`, `TotalWorks is { } matched && matched
<= blurbsRead`, and the waiver fires on it directly: the heading is what the waiver rests on, not a
side condition it may skip when absent.

**The distinction T42 did not draw.** T42 answered "what may a *waiver* rest on" and this branch
took the answer for "what may a *stop* conclude". They are not the same question, and the second is
the one with works at stake: `LastPage` satisfies `mayPropose`, so the watermark moves to page 1's
newest reading — the listing being `revised_at` descending, everything page 2 would have held is
older than that and newer than the old watermark. `RevisedAtBound`'s day of slack recovers only what
sits within a day of the new watermark, so on a ship catching up over a long gap, where page 1 spans
weeks, the rest is unreachable by any incremental pass. Generalising, for T28's table: **a waiver
that replaces one piece of evidence with another may not fire on a page carrying neither.**

**The cost is T45's, knowingly.** Such a page now stops the run with `Error`, and an incremental
pass has no retreat — so it re-reads the same two pages every tick until the heading comes back,
which is exactly the every-tick repeat T45 owns and which this change makes one shape more reachable.
Recorded in T45's notes rather than fixed here. It is the same trade T24, T42 and T43 each ruled on
and the ruling has not changed: a refusal costs two requests per scheduler interval and files a
failed run naming the page, and a wrong conclusion costs works permanently on a run recorded as a
success.

**The test I deleted was the defect, for the second iteration running.**
`Lets_a_filtered_pass_end_on_an_empty_page_past_the_first` built page 2 as `Page(2, [])` — no
heading — so T42's own test asserted `LastPage` and a watermark of Jan 9 on precisely the page this
task refuses. The waiver's real case was already pinned by
`Ends_a_filtered_pass_on_an_empty_page_whose_heading_agrees_it_was_served_everything`, which builds
the heading the waiver asks for, so the deleted test was pinning the defect and nothing else. It is
now `Refuses_an_empty_filtered_page_that_carries_no_heading_at_all`, asserting `Error` and a frozen
watermark. T43's lesson arriving one iteration later: **check whether a rule's own test constructs
the situation its comment claims.**

## 2026-08-23 — T47's review: nothing folded in, one new task, three already queued

`/code-review high` over the T47 diff and the branch reported four findings. Three were already on
the list — `readWhileLoggedIn` ORing across a run whose total is written per page (T44, second
independent arrival), an unreadable page still advancing `pagesFetched` and naming the run's page
boundaries (T40, third arrival), and the `RetreatFromStaleCursor` log template binding five arguments
to six placeholders (queued as T49 at this task's baseline, hours before the review found it).
Nothing was folded in.

**Queued as T50 — page 1 accepts the contradiction page 2 now refuses.** `PlausiblyTheEndOfTheListing`
short-circuits on `page == 1`, and the unfiltered heading check is waived for filtered listings, so a
filtered page 1 with the container, no Next link, no blurbs and a heading counting N > 0 concludes
`LastPage` with a null error message: the ship ingests nothing and the run is recorded a success,
every tick. It is T47's own argument one page earlier, and the reviewer is right that it reads
oddly beside this diff — page 2 now refuses what page 1 accepts.

Not folded in for three reasons. It is not what T47 `delivers`, which is about the waiver of
`page > 1` and about a watermark moving past unseen works — and nothing moves here, because a page
with no blurbs leaves `newestSeen` null. It overturns a decision T42 stated in a doc comment and
pinned with a test, which is a thing to do deliberately in its own diff rather than as a rider on
someone else's. And the cost of refusing is a request per tick with no recovery, which is exactly
the bound T45 owes — so T45 probably has to land first for T50 to have anything to refuse *into*.
The asymmetry is real and is now written down in both tasks rather than left in the code for a
later reader to find.

## 2026-08-23 — T28: what the audit changed about the plan

The audit itself fixed nothing, by design. What it changed is the plan, in six places.

**Four new tasks, T51 to T54.** T51 (a listing blurb's tag list is reconciled as if it were
complete, so T10's detail fetch is undone by the next incremental pass) and T52 (a run the circuit
breaker stopped is recorded `Succeeded`) are defects with a stated consequence. T53 (nothing pins
`ScrapeWorker`'s mode choice) and T54 (nothing pins the ingestor's unreadable-date preservation) are
rules with no test under them. All four are in `.devloop/scraper-audit.md` with the row that found
them: §E8, §F1, §A1, §E4.

**Three edges added to T15's `blocked-by`: T40, T44, T52.** None is new work; each is a queued task
the audit found to be load-bearing for the sweep specifically. T40 because a pass that concludes
absence needs its coverage accounting to be honest and an unreadable page currently counts as read.
T44 because `LastKnownTotalWasAuthenticated` has exactly one consumer and it is T15. T52 because a
sweep the breaker stopped halfway must never be readable as a sweep that finished — which is the
same rule as "an interrupted sweep concludes nothing", one layer up in the run history.

**Not every gap became a task, and the grading is written down.** Four rules have no test and were
deliberately left unqueued: the 200-page ceiling (whose only consequence is an ambiguous `cap` in
the run history), `LastIncrementalRunAt` (a timestamp nothing branches on), the ingestor's per-page
work dedup (which fails loudly on a unique key if it breaks), and the startup reconciliation of
interrupted runs (a path with no clock seam, whose failure mode is a stale row in a history view).
They are listed at the foot of §F so the next reader can see the grading was done rather than
missed. The instruction in T28 was that every gap becomes a task; taken literally that would put
four entries with no stated cost onto a fifty-task list, which buys less than the sentence naming
them does.

**Two rows are addressed to T15 rather than to a task.** D12: nothing refreshes
`LastKnownTotalWorks` once a ship has a watermark, because every incremental pass on such a ship is
filtered and a filtered heading may not write the total — so T15 is both the only pass that would
refresh the number and the only one documented to check against it, and it has to decide which it
trusts. E6/E7: `Work.IsDeleted` and `ShipWork.MissingSinceAt` are cleared by every ingest and set by
nothing, so T15 writes the first code in this app that ever concludes absence. Both went into T15's
notes.

**Two open conclusions went to existing tasks instead of becoming new ones.** The `page == 1`
short-circuit in `PlausiblyTheEndOfTheListing` has two of them: the filtered one is T50, already
queued; the unfiltered one — page 1 with the container, no works, no Next link and *no heading at
all* completing a backfill — cannot be decided without knowing whether AO3's zero-result index
carries a heading, which is T39's capture. Added there rather than queued as a task that would
immediately block. Similarly, §A2's denied-tag `LastPage` went into T46's notes: same family (a ship
pointed at a tag that is not there), same diff if it is cheap.

**T41 folded into T49 and its entry deleted.** Both described the same six placeholders over five
arguments in `RetreatFromStaleCursor`'s log template — T41 found at T26's baseline, T49 at T47's,
neither noticing the other because each was written from a fresh context. That is this loop's
failure mode showing up in the task list rather than in the code, and it is the argument for the
audit having been worth an iteration: two independent readings of the same file produced two
entries for one line. The number T41 is retired, not reusable.

**The `Run order` section is deleted.** Its only entry was T28, and its own rule is to delete the
section when it empties. Five iterations running it held one task, and each time that task was a
rule about what a stop may conclude; the audit is what those five turned into. File order applies
from here, and the next task is T6 — the first planned work this loop has taken in fourteen
iterations.

**A verification detail worth keeping.** `dotnet test --filter FullyQualifiedName~X` matches
case-insensitively: `~Backfill` runs 13 tests although no test name contains a capital `Backfill`.
T22's ritual of checking that a task's filter bites is therefore safe, but checking it with a
case-sensitive `grep` over a test list is not — that under-reports, and would have made three
queued tasks look like they name a filter matching nothing.

**One finding folded out, not in.** `/code-review low` at this task's review step reported a real
defect in `AdminScrapingPage.tsx` — a rejected credential fetch leaves the login block rendering
"Loading…" forever, where the identity block thirty lines above it handles the same case correctly.
Verified by reading and queued as **T55**. Not fixed here: T28 fixes nothing by construction, and a
frontend fix riding along in a documentation commit is exactly the wandering diff the loop policy
exists to prevent. Worth noting that the review found nothing in the diff itself, which is what a
docs-only diff should produce — it reviewed the branch and reported the branch's oldest UI defect.

## 2026-08-23 — T6: what "no opinion" is stored as, and where the feed's copy of it comes from

**The canonical cleared state is the absence of a row.** T6's notes asked for a choice between
`Status = None` and no row at all, and this is it: `PUT /api/works/{id}/state` with nothing set
*deletes* the row rather than blanking it, and both forms read back as `WorkStateDto.Cleared`. The
argument is T8's, one task ahead: "unread" has to include works nobody has ever opened, so that
predicate is written over an absence no matter what — and keeping a second, equivalent
representation beside it would mean every such query covers two cases for one fact. Only a *wholly*
empty state is an absence; clearing a rating while a status stands keeps the row, because the row
still holds something. Callers cannot tell the two apart, which is the property that makes the
choice reversible if T8 or T18 wants the row kept.

**Enums cross the wire as names, so `ReadingStatus` does too.** `WorkStateDto.Status` is a string
and `SetWorkStateRequest.Status` is parsed with `Enum.TryParse` + `Enum.IsDefined`, matching
`SavedFiltersController.ParseValue` rather than inventing a numeric contract for one endpoint. The
`Enum.IsDefined` half is not decoration: `TryParse` accepts a *number* and hands back whatever byte
was asked for, so `"99"` parses to an undefined `ReadingStatus` and would have been written to the
column. That is what the mutation check caught — see the journal.

**The rating range is restated in the controller rather than left to the check constraint.**
`CK_UserWorkStates_Rating` is the source of truth and stays, but reaching it means a
`DbUpdateException` on the way out: a 500 for a client's typo, and on SQLite it takes the whole
`SaveChanges` with it. The controller answers 400 naming the field, and the constraint remains the
thing that makes a bug in this validation a failed write rather than bad data.

**The feed's copy is a correlated subquery, not a navigation.** `Work` has no navigation to
`UserWorkState` and does not get one — a navigation is loadable without saying whose state it is,
which is the mistake this whole table exists to prevent. `WorkQueries.StatesOf` is the per-caller
predicate, closed over and used as a subquery in the list projection, so the list and the state
endpoints narrow by the same clause. It sits beside `Library` for the reason stated there: a query
that forgets `UserId` does not return too much, it returns someone else's opinion labelled as the
caller's.

**One frontend change, and deliberately no UI.** `WorkListItem` in `frontend/src/api/types.ts`
gains `state: WorkState`, because that file is a hand-maintained mirror of the DTOs and leaving it
out would make the mirror describe a wire format the API no longer serves. No component reads it
yet; the controls are T7's, and this diff renders nothing.

**`LibraryTestHost` gained `NewWorksRequest`, and `Works` was left alone.** The state tests write
through one request and read back through the next, which a shared scope would answer out of the
change tracker the write warmed — the same reason `AdminAo3Credential` already builds a scope per
call. Changing `Works` itself would have been the tidier edit and was declined: thirty passing list
tests hang off it, and re-scoping them is not what T6 delivers.

## 2026-08-23 — T6's review: one folded in, two already queued

**The finding in T6's own diff was the unique-index race, and it is fixed here rather than queued.**
`SetWorkState` read then inserted with nothing between: two of one reader's requests for one work,
in flight together — which is precisely what T7's inline star and status controls on a single feed
row produce — both find no row, both insert, and the loser's `SaveChangesAsync` throws
`DbUpdateException` for an unhandled 500. Folded in rather than queued because it is a defect in
what T6 delivers and it needed no new decision: `ShipsController.ResolveShipAsync` already carries
this exact shape for the ship-insert race — catch, detach the losing insert, re-read the winner,
rethrow when there is no winner so an unrelated failure keeps its own cause. The one difference is
stated in the comment: a Ship has nothing to merge and this does, because PUT replaces, so the
request's state is written onto the winning row instead of being dropped with the losing insert.
The symmetric hole on the clear path — two concurrent clears, the second throwing
`DbUpdateConcurrencyException` on a row already gone — is closed the same way and for the same
reason: the caller asked for an absence and an absence is what holds, so that is a success.

**Neither race is pinned by a test, and its precedent is not either.** The fixture runs one SQLite
connection, so there is no seam that opens the window between the read and the write without an
interceptor the container is not built to take. `ShipsController`'s identical catch has been in the
tree unpinned since before this loop, which is the precedent this follows rather than an oversight
to copy quietly. Recorded here so a future reader does not read the absence of a test as the absence
of a decision.

**The review's other two findings are already-queued tasks, in code this diff does not touch.**
`Ao3ShipIndexScraper.cs:340`'s run-wide `readWhileLoggedIn |= response.Authenticated` is **T44**,
now arriving for the third time; `:229`'s backfill-only stale-cursor retreat, leaving an incremental
pass with no recovery for the 404 route that T42/T47 gave it for the empty-200 route, is **T45**,
arriving for the second. Both were reported against the branch rather than the diff, as T28's
docs-only review was. Nothing changed on either; the count is the point, since re-arrival is the
only signal this loop gets about which queued tasks are costing something.

## 2026-08-24 — T7: what the feed's own-state controls are, and what a click is allowed to send

**Every control on a row sends all three fields, and the one that builds the request is
`saveState`.** `PUT /works/{id}/state` replaces, so a star that posted only a rating would clear the
status and the note beside it — the gotcha T6 wrote into this task's notes. Rather than trust three
call sites to remember, `saveState(work, next)` takes a whole `WorkState` and every control spreads
the row's current one: `{ ...work.state, rating }`. `SetWorkStateInput` in `api/types.ts` is aliased
to `WorkState` for the same reason — a distinct partial type is exactly the shape that would let a
caller send one field.

**Optimistic, but the response is what the row keeps.** The click paints immediately, the answer
overwrites the guess, and a refused write puts the old value back *and* prints the reason in the
cell. Reverting silently is the failure mode T7's notes name; the revert is not the problem, the
silence is. The message is per row and not the page's `error`, because the list itself loaded fine
and blanking the works to report one failed write would lose the row being edited.

**A per-work token, not a per-work lock.** Two writes to one row in flight together (a rating and a
status, a half-second apart) are ordinary use of these controls, and the slower, older answer must
not land last and undo the newer one. Each write bumps a counter in a ref and only touches the row
while its own token is still the newest. The controls are deliberately *not* disabled during a
write: the token already makes a second edit safe, and disabling would drop the fast second click
rather than honour it. `saveState` resolves to whether the write landed and never rejects, so the
two callers that only want the row updated can ignore it without leaving an unhandled rejection —
whether the write landed is reported regardless of currency, because a superseded write still
happened.

**The rating is a slider, and the glyph is a text star.** `role="slider"` gives one tab stop per
row with arrow keys, Home/End and Delete over the ten half-steps; ten buttons per row would be 250
tab stops on a default page. A pointer click reads its position along the control as a fraction of
ten, which is why the stars sit flush with no gap between them. Clicking the rating you already gave
clears it — otherwise a pointer has no way back to unrated. The half-filled star is a *clipped copy
of the same character* laid over the empty one, not a second glyph and not a Lucide path: `Icon.tsx`
requires icon path data be copied verbatim out of `lucide-static`, which this project does not
install and this loop's shell cannot fetch, so inventing a star path would have been the guess that
rule exists to forbid. Fill is a `data-fill` attribute of `none`/`half`/`full` with the widths in
CSS, so there is no inline style for a theme to lose to.

**Unrated is spelled out, not merely drawn.** T7 requires an unrated work to look different from a
half-star one, and five faint stars against one half-filled faint star is a clipped glyph's worth of
difference. The value reads "Unrated" or the number beside the stars, `data-rated` carries it to CSS,
and `aria-valuetext` says it out loud.

**One column, named "Yours".** Status, stars and the note button share a cell rather than taking two
or three columns: they are one fact about one reader, and the table already scrolls sideways. The
existing `Rating` header became `AO3 rating`, which is a rename this diff owes — the column beside it
now holds the other kind of rating, and two columns called Rating is the ambiguity the task creates.

**The note editor is a row, not a popover.** It spans the table under the row it belongs to. A
floating editor over a table that scrolls horizontally is where a half-typed note goes to get lost.
Clearing a note is a `Delete note` button rather than "empty the box and save", which nobody guesses,
and both ways out go through one `commitNote(work, note)` so Delete never has to blank the draft and
then read a value the render cannot see yet.

**The frontend still has no test runner, and this task did not add one.** The spec says UI tasks are
verified by `npm run build` plus a live check; what that live check *was* is in the journal, and it
is more than the API-level curl its predecessor T3 used.

## 2026-08-24 — T7's review: three folded in, one queued

**The unhandled rejection was already gone before the review reported it.** `saveState` originally
rethrew from its `.catch` so the note editor could tell a failure from a success, which left the
star and the status select — the two callers that ignore the result — dropping a rejected promise on
every failed write. Found and fixed during my own read of the diff; `saveState` now resolves to
whether the write landed and never rejects. Noted because the review saw both versions and said so:
the working tree was being edited while it ran.

**Two note-editor losses folded in, both of them the row-editor layout's own promise broken.**
The editor was chosen over a popover because "a floating editor over a scrolling table is where a
half-typed note goes to get lost", and the first version lost text two other ways. `commitNote`
closed `openNoteId` unconditionally, so a save started on row A landed a moment later and closed row
B's editor mid-typing; and a single page-level `noteDraft` meant opening any other editor overwrote
whatever was in it. Both are fixed by scope: drafts are a `Record<number, string>` keyed by work,
seeded from the stored note only when nothing is held for that row, and dropped only on a *saved*
note; `commitNote` captures its own `work.id` and guards both setters on it. `savingNoteId` replaced
the page-level boolean for the same reason — one row's in-flight save was disabling every other
row's buttons. The Cancel button became **Close**, because a draft that survives is not cancelled.

**The works response check now requires `state` on every row.** `hasArray('items')` was written when
a row was scalars; T6 added `state` and T7 is the first code to read `work.state.status` two levels
deep on every row. A server deployed behind this page would have thrown during render — precisely
the crash `ResponseCheck` exists to convert into the version-mismatch message — so `isWorksPage`
checks the rows, not just the array.

**The one backend finding is queued as T56, not folded.** `SetWorkState`'s *update* path has no
handler for a row deleted under it, so one reader's two concurrent writes — the shape T7's controls
make ordinary — can answer 500 with the edit lost. Real, and in T6's code. Kept out of this diff on
the one-task rule: T7 delivers no backend code and verifies with `npm run build`, so a controller
change riding along would be unverified by this task's own command and unpinned by any test, which
is worse than a task naming it. T6 folded in the mirror-image *insert* race for the opposite reason
— it was a defect in what T6 delivered. T56 also has a decision to make that this diff must not make
for it: the clear path's "a losing clear is still a success" does not transfer to a caller whose
state no longer has a row to sit in.

**The live check grew two cases from this review** rather than the fixes being taken on trust: an
unsaved draft closed and reopened, and another row's write landing while a draft is open. Both are
in the driver, and both pass against the real page.

## 2026-08-24 — T8: what a per-user criterion is, and whose reading it reads

**Three typed columns, not a set of them.** `ReadingStatus`, `MinUserRating`, `MaxUserRating` on
`SavedWorkFilter`, migrated on both providers. The status is *one* value rather than a multi-select:
`ReadingStatus` is a plain byte enum with `None = 0`, so a flags mask would need a second, parallel
vocabulary for the same five marks and a bit standing in for the zero — two spellings of one concept,
which is the drift `Ao3Labels` exists to prevent. The cost is that "to read *or* unread" is
inexpressible in one set; the delivers line asks for "a reading status (or its absence)", and that is
what a single nullable column says exactly.

**`ApplyFilter` grew a `callerStates` parameter rather than a `userId`.** It takes
`IQueryable<UserWorkState>` — `WorkQueries.StatesOf(db, userId)` at both call sites — so every caller
has to name whose reading it is filtering by, and the same queryable that selects the rows in
`WorksController` is the one the projection reads each row's state from. A `userId` string would have
been a parameter a call site could get wrong silently; a defaulted one would have been a parameter a
call site could forget. The two per-user clauses are the only ones in this method whose answer
depends on who asks, which is written into the entity's remarks as well.

**"Unread" is a `NOT EXISTS`, not an equality.** The absence has two shapes — no state row at all,
and a row saying `None` left behind when a status was cleared while a rating or note stood — and T6
made the first one the common case by storing a wholly empty state as no row. `Status == None` would
have matched only the second and dropped every work nobody has ever touched, which is most of a fresh
library. The clause is `!callerStates.Any(s => s.WorkId == w.Id && s.Status != None)`.

**A rating bound drops unrated works, and that is the decision.** Null is not a score. A set asking
for "4 stars or better" that quietly kept everything unrated would answer a question nobody asked;
`Any(s => s.Rating >= min)` excludes them because a null fails the comparison, and both the entity
remarks and the editor's hint say so out loud. "Unrated only" is a criterion this task does not add —
it is a third state, not a bound, and nothing in the user stories asks for it.

**No check constraint on the new rating columns.** `CK_UserWorkStates_Rating` could be declared
because that table was new; SQLite cannot `ALTER TABLE ADD CONSTRAINT`, so adding one to
`SavedWorkFilters` would force a full table rebuild in the migration. `SavedFiltersController` bounds
them to 1-10 instead — the same numbers `WorksController` already restates — so an off-scale bound is
a 400 naming the field rather than a set that matches nothing for ever.

**The reading-status wording stays client-side, unlike every other vocabulary in the editor.**
`/api/lookups/vocabulary` exists so AO3's words have exactly one home, and that home is the server,
where the parser reading them off a blurb lives. These five marks are this app's own; the labels
moved out of `WorksPage.tsx` into `frontend/src/readingStatus.ts` so the feed and the filter that
narrows it cannot call one status by two names. The filter editor overrides exactly one of them —
`None` reads "Unread — not marked at all" as a criterion, where on a row it reads "Not set".

## 2026-08-24 — T8: a mutation run can be green because the build was stale

`shutil.move` restores a file with its **original mtime**, so MSBuild saw a source older than the DLL
built from the mutated copy and skipped the rebuild. The next `dotnet test` then ran the *last
mutation* — which is how a passing suite turned red without a source change and cost most of a debug
loop. Restores in a mutation harness have to rewrite the file (fresh mtime), not move it back.

Every mutation *result* survived the mistake: each mutation is written with a fresh mtime, so the
assembly it was tested against was rebuilt from correct sources plus that one change. The stale
binary only ever affects the run *after* a restore.


## 2026-08-24 — T9: what a work's own page shows, and what it is allowed to hand a browser

**The summary is sanitized on the server, and the DTO field is named for it.** `Work.SummaryHtml` is
markup an anonymous stranger typed into AO3 and this app stored verbatim; the detail endpoint is the
first thing that ever hands it to a browser. Sanitizing at render time in the client was the other
option and was rejected: the frontend has no sanitizer, the spec forbids adding a test runner and
this project adds no frontend dependency lightly, and "whichever client remembers to do it" is a rule
that holds until the second client. `WorkSummaryHtml.Sanitize` runs on the way out and the field is
`SummarySafeHtml`, so a client rendering it as HTML is doing the intended thing — which would not be
true of a field named after the column.

**The sanitizer's rule is element names only, and no attributes at all.** Not an attribute allowlist:
keeping `href` means being right about which URL schemes are safe, which encodings of `javascript:`
are equivalent, and which attribute AO3's markup starts carrying next. Keeping none means never
having to be right about that. Anchors are therefore unwrapped to their own words — a real loss of a
summary's links, and the price of a rule with nothing in it to get wrong. A short list of elements
(script, style, iframe, svg, form controls, the raw-text ones) is dropped whole rather than unwrapped,
because unwrapping a `<script>` leaves its source behind as text.

**The walk is iterative, with a depth cap.** A summary is untrusted input, so its nesting depth is
untrusted input too: a recursive walk over markup nested a few thousand deep would take the request
down with it. Past `MaxDepth` elements are unwrapped rather than emitted, and the words are kept
either way. The cap is belt-and-braces and the test over it asserts only that 5,000 nested elements
return and keep their text — removing the cap leaves that test green, which is stated here rather
than left as a silently unpinned constant.

**A markup-only summary is reported as no summary.** `Sanitize` returns null when nothing with words
in it survived, so a summary that was only a tracking pixel renders the page's "no summary" line
instead of an empty box.

**Tags are one list carrying each tag's kind, not a field per kind.** `Tags: [{ type, name }]`,
ordered by kind and then by name, so a tag type this app learns about later needs no new DTO field to
be shown, and two scrapes of the same work never order its freeforms differently. The page groups
them under headings from its own ordered list, which is what stops a work with no characters tagged
from moving its freeforms up into the place characters usually occupy.

**The detail route is `/works/:workId`, under the list it is reached from**, so the sidebar keeps
Works lit and the back link is one hop. The feed's title now opens this app's page for the work and
AO3 moved into the byline — the row used to leave for the archive on its only link, which is the
opposite of what a local library is for.

**The ship chips link back to the feed with `filter=none`.** Arriving at a ship's works and seeing
none of them because the reader's default saved filter was applied would read as the ship being empty.

**Published and the tag list say "not fetched yet" rather than rendering as absent.** Both are what
T10 fills, and a blank row is indistinguishable from "AO3 has no value for this". The row is rendered
either way, with `DetailFetchedAt` as what makes the null legible.

## 2026-08-24 — T9's review: three findings, three folded in, one queued for its twin

**A write for the previous work could land on the newly-loaded one.** React Router reuses
`WorkDetailPage` between two work pages rather than unmounting it, and `stateWriteToken` was bumped
only by writes — so a rating set on `/works/1`, followed by a navigation to `/works/2` that finished
loading first, stamped work 1's marks onto work 2's page. The load path already guarded itself with a
`current` flag; the write path had no equivalent. Fixed by bumping the token in the `workId` effect,
which is the same "a superseded write may not touch the page" rule already in the file, applied to
the case where what supersedes it is a different work rather than a later edit.

**Tag order within a kind now goes through `Tag.NameNormalized`.** Ordering on the display name is
collation-dependent — SQLite's binary collation puts a lowercase freeform after `Z` and PostgreSQL
does not — which is the exact hazard `WorkQueries.Order` cites when it refuses to offer a title sort,
and the reason the normalized column exists. The DTO comment promised a stable order; now it has one.

**Text typed while a note save is in flight is kept.** The textarea stays editable through the round
trip (only the buttons are disabled), and clearing the draft on success unconditionally replaced
whatever was typed after the click with the server's copy. `commitNote` now captures the draft it
sent and clears only if that is still what the box holds. **`WorksPage.tsx` has the same shape and is
not fixed here** — a frontend fix to the feed riding along in the detail page's commit is the
wandering diff the loop policy forbids. Queued as **T57**, which says to copy this shape rather than
invent a second one.

## 2026-08-24 — T11: what a second ask costs, and what a request is allowed to hold

**A download request is idempotent per (reader, work, format), so it answers 200 and never 201.**
The unique index on those three columns is the model's own decision, and it makes "ask again" and
"ask once" the same row by construction. A caller cannot act on the difference between the two, and
there is no per-download resource to point a `Location` header at — adding a `GET
/api/downloads/{id}` purely so a header had somewhere to aim would be a public endpoint nobody
asked for. What the response carries instead is `Status`, which is the thing that actually differs
between a request that queued and one that was already complete.

**Four things a request can find, and only two of them write.** Nothing already there, and no file
for this version: queued. Nothing there, but this exact version already on disk from another
reader's fetch: complete on the spot, no AO3 request at all — this is the whole point of
`WorkDownloadFile` being keyed by (work, format, `Work.UpdatedAt`) rather than living on the
per-user row. A fetch already `Downloading`: left alone, because that row belongs to the worker
until it finishes with it, and re-arming it is how one request becomes two fetches of identical
bytes. Anything else — failed, or holding a copy of a version the work has moved past — is
re-armed.

**A request still `Pending` is re-armed rather than left alone, which is deliberate.** Re-arming it
writes nothing unless the bytes turned up on disk meanwhile, in which case the request stops being
a fetch anybody has to perform. Leaving `Pending` in the "don't touch it" set would have cost that
and bought nothing, so only `Downloading` is untouchable.

**Staleness is decided by which file the row names, not by its status.** A request that says
`Complete` while the work has moved past the version it holds is not complete, and — the case that
makes the distinction load-bearing — the bytes for the version it has moved *to* may already be on
disk from another reader. Reading the status without reading `WorkDownloadFileId` would serve the
old copy as the new one. Re-arming drops the stale file reference rather than keeping it as a
fallback: a request that reports `Complete` beside bytes of a different version is worse than one
that reports `Pending`.

**The downloads list is the one list in this app not scoped to watched ships.** Every other query
answers "what can this reader see" out of their subscriptions. A download is not a view of the
library — it is something this reader asked for, and its file is on the instance's disk because
they asked. Hiding the row when they unfollow the ship would hide the only handle able to delete
it, stranding bytes nothing can reach. Scoped to `UserId` and nothing else, therefore.

**Deleting a request never deletes the file.** `Download` → `WorkDownloadFile` is many-to-one with
`OnDelete(SetNull)` on the FK, so dropping one reader's row cannot touch bytes another reader's row
names. Reclaiming files that nothing references any more is a real job and deliberately not this
one: it needs a rule about when an unreferenced file stops being worth keeping, and inventing one
inside a DELETE handler would decide it by accident.

**No migration.** `Download`, `WorkDownloadFile`, their unique indexes and their FK behaviours were
all in `InitialCreate` on both providers — the model was designed for this and only the code was
missing. T11 adds no column.

**The unique-index race is handled and unpinned, on T6's precedent.** Two clicks on one format
button land as two inserts and the loser catches `DbUpdateException`, re-reads the winner and
answers with it — the same shape as `WorksController.SetWorkState`, minus the merge, because two
requests for one format of one work cannot have asked for different things. As with T6, there is no
test: the fixture shares a single SQLite connection, so there is no seam to open the window. Said
out loud here so the gap reads as a decision rather than an oversight.

## 2026-08-24 — T11's review: nothing in the diff, two already-queued findings

`/code-review high` over the branch and the working tree found nothing in T11's code — it read the
per-caller scoping, the library 404, the `Enum.IsDefined` guard, the race handling and the
shared-file delete as sound. Its two findings are both against earlier commits and both already
have tasks, so neither is fixed here: fixing them in this diff is the wandering diff the loop policy
forbids.

**`LastKnownTotalWasAuthenticated` is T44, and this is its third independent arrival.** Same two
lines (`readWhileLoggedIn |=` accumulating per run against `RecordTotal` writing per page), same
fix, derived from scratch by a third reader. The task now says so.

**The feed's note editor is T57, and the review added a symptom the task did not have.** Beside the
draft being replaced, the success handler closes the editor unconditionally — so a reader who closes
mid-save and reopens has it slammed shut by the resolving write. `WorkDetailPage`'s captured-draft
guard does not cover that half, which T57's notes now record.

## 2026-08-24 — the three AO3 captures land, and two of them change the plan

Emma saved `ao3-login-page.html`, `ao3-work-page.html` and `ao3-empty-listing.html` under
`backend/Ao3Tracker.Tests/Fixtures/`, which is the human step T5, T10, T13 and T39 were parked on
since the initial plan. **No task is `blocked` any more** — the four that were are now `todo`, two
of them (T5, T39) with no dependency at all and two (T10 on T51, T13 on T12) waiting only on
ordinary loop work. Reading the captures before writing any parser against them settled three
questions, and two of the answers are not what the tasks assumed.

**T39's premise holds, and the failure it was written to catch does not exist.** A zero-result tag
index renders `<ol class="work index group">` — empty, but present — above a `0 Works in <tag>`
heading. `Ao3ListingPage.HasListing` is correct as written and gets no fix. The scenario T39
described, where a quiet incremental pass reads no blurbs, `HasListing` goes false, the run stops
`Error` and every scheduled run on every quiet ship is recorded failed forever, was a real risk
against a premise nothing tested; the premise turns out to be true. T39 shrinks from an
investigation to a test that pins it, which is worth keeping rather than dropping — the tests that
covered this path went through a helper emitting the container unconditionally, so they proved the
premise by assuming it.

The same capture closes **T28's §C7** in the direction that makes `PlausiblyTheEndOfTheListing`
sound: an empty listing *does* carry a heading, so an unfiltered page 1 with the container, no
works, no Next link and a `0 Works` heading is evidence of an empty result set rather than the
absence of evidence T47 refused to act on. A page 1 with no heading at all remains no evidence and
remains not permission — that rule is unchanged, it simply now has a case it can distinguish from.

**T13's answer arrived before T12 was built, and it invalidates T12's design.** The download menu
carries `/downloads/{workId}/{slug}.{ext}?updated_at={unix}`. The slug is an unstated truncation of
the title — "we chose to wait! marriage only after sex" becomes `we_chose_to_wait` — and
`updated_at` is AO3's own Unix timestamp, not `Work.UpdatedAt`. Neither is derivable from anything
the library stores, so **a download requires fetching the work's page first**: two rate-gated
requests, not one, and a second failure mode when the page fetch is the half that fails. T12's
instruction to isolate "URL construction from a work id" as a seam T13 would later replace is
therefore withdrawn — there is nothing to construct, the seam takes a fetched page, and T12 is
built that way from the start rather than shipping a guess for T13 to undo. It also merges work
with T10: both want `Ao3WorkPageParser`, and the download links come out of the same parse.

**A finding neither task was looking for: the incremental pass has never been filtered (T58, new).**
Emma applied the bound `BuildUrl` sends to a live tag index and AO3 returned the unfiltered
listing. The captured filter form says why — the tag-listing endpoint offers
`work_search[date_from]` and `work_search[date_to]` and has no `revised_at` key; `revised_at` there
is a *sort column value*, and `work_search[revised_at]` with a `>` prefix belongs to the advanced
search at `/works/search`. Rails drops the unknown nested key without complaint, so this failed
silently for the whole life of the scraper.

Filed as a new task rather than folded into an existing one, because it is nobody's defect in the
current list and because **it is not a correctness bug**: the exact cut is made client-side against
the watermark and always was, so the pass has read the right works throughout. What was lost is the
only thing the parameter was for — `RevisedAtBound`'s own comment calls it "what keeps a routine
pass to one request on a large tag" — meaning every scheduled run on every quiet ship has pulled
the full tag listing while a comment claimed otherwise. On this project's terms that is the serious
half, and it is exactly the kind of claim the verify-don't-guess rule exists to stop the codebase
making about itself. It also means `listingWasFiltered` has been describing an intent rather than a
fact, which entangles it with T30 and T44; T58 says to re-derive the total logic once the parameter
works rather than let two cancelling errors read as correct.

**One capture detail worth writing down because it will bite a parser.** `ao3-login-page.html`
holds *two* forms carrying an `authenticity_token`, the header dropdown `#new_user_session_small`
and the real `#new_user`, both posting to `/users/login`. "The first token on the page" is the
header's. T5's notes now say to select `#new_user` explicitly, so the fixture cannot be passed for
the wrong reason.

## 2026-08-25 — T5: the login is three seams, and the capture had a third token nobody counted

**The token trap was worse than the fixture note said, and the note's own advice would not have
caught it.** `ao3-login-page.html` carries the `authenticity_token` in *three* places, not two: a
`<meta name="csrf-token">` in the head at offset 2556, then the header dropdown's hidden input, then
the real form's. All three hold the same value in the capture, so no parser can be told from another
by reading it. The first draft of the guard test replaced "the first occurrence" believing that to be
the header dropdown's; it was the meta tag's, both forms kept the real token, and a parser mutated to
`QuerySelector("form")` passed the test that exists to catch precisely that. The test now gives all
three sites distinct values, and a sibling test asserts the capture really does hold three copies —
because the moment AO3 stops repeating it, the guard test silently stops distinguishing anything.
**This is the fifth arrival of the lesson the last four journal entries name**, and the first time it
landed on a test written specifically to construct the case it then failed to construct. Writing the
mutation first is the detector; running it is what actually catches this.

**Session attachment lives in the HTTP client, and the cycle that implies is broken by narrowing,
not by laziness.** The obvious wiring — client asks a session provider, provider logs in, login goes
through the client — is a DI cycle. It is broken by splitting the session into two interfaces with
opposite directions: `IAo3SessionCache` (read the cookie, discard it) is what the client depends on
and cannot log in, while `IAo3SessionProvider` → `IAo3SessionEstablisher` → `IRateLimitedHttpClient`
is the login path and nothing in it is reachable from the client. The consequence is deliberate: an
expired session is re-established *between* runs by the worker, never re-entrantly in the middle of a
request. The narrow cache also means the component that attaches cookies to outbound requests cannot
read the deployment's password, which is now a fact about the type rather than a habit.

**`Authenticated` is read off the page, not off "we sent a cookie".** AO3 answers a dead session with
a 200 and the anonymous view — never a 401 — so the transport cannot tell, and a flag set from the
request would be a lie a full sweep acts on. `Ao3LoginPage.ReadSessionState` reads AO3's own header:
`nav#greeting` for a signed-in reader, `#new_user_session_small` for everyone else. Both markers are
validated against real captures from both sides — the work page and the empty listing were saved
logged in, the login page logged out. A page carrying neither is `Unknown` and changes nothing: a 404
or a file body is not evidence, and treating "no evidence" as "logged out" would discard a working
session over every miss. This is also the whole expiry mechanism — an explicit `LoggedOut` reading is
what drops the cached cookie so the next poll logs in again.

**Two transports, because exactly one request must not follow redirects.** A successful login answers
with a 302 whose `Set-Cookie` *is* the session; following it spends that cookie on a page nobody
asked for and the caller never sees it. Rather than turn redirects off for the whole scraper — they
are how a synonym tag is recognised — the login POST gets a second `HttpClient` with
`AllowAutoRedirect = false`. Both share the one static rate gate and the one User-Agent, so this is a
differently-configured transport and never a second way out of the rate limit. Both handlers also run
with `UseCookies = false`: the session is a database row shared by every process reading this
deployment's data, so a per-handler cookie jar would be a second copy of it that quietly diverged.

**The cache key gained the session, and it had to.** Anonymous and logged-in views of one URL are
different pages — AO3 hides restricted works from nobody-in-particular — so a shared entry would hand
a logged-in run the anonymous copy for the whole 15-minute window. Keyed by *whether* a session was
sent rather than by which one: a re-login does not change what the archive will show this account.

**A bug the tests found that no amount of reading would have.** `Uri.TryCreate("/users/login",
UriKind.Absolute, …)` **succeeds on Linux**, producing `file:///users/login`. The form action read off
the page was therefore being resolved against the local disk rather than against the archive. The
scheme is now checked explicitly. Worth remembering anywhere else in this codebase resolves a path
read out of markup.

**The worker logs in only when something is due**, checked after the due-job query rather than
before. An idle instance re-authenticating on a timer would be two requests an hour that read nothing
at all, which is the load this scraper exists not to put on AO3. A login AO3 refuses holds the due
jobs exactly as a missing credential does — no run recorded, no `NextRunAt` advanced, no breaker
touched — because a wrong password is something a person has to fix, and burning the schedule against
it would turn one problem into a job history full of failures.

**A refused login must back off, and the backoff must not apply to the person fixing it.** Found by
`/code-review high` at this task's review step, and it is the politeness defect this project cares
most about: held jobs deliberately leave `NextRunAt` alone, so they stay due, so a refused login was
re-attempted on every sixty-second poll — around 2,880 AO3 requests a day, half of them failed POSTs
to `/users/login`, from an instance whose stored password will not become correct by being retried.
`Ao3LoginBackoff` (singleton, the `ScrapeWakeSignal` shape) climbs 5 → 15 → 30 → 60 minutes and stays
there. The design difficulty is that a cooldown on a *configuration error* is a cooldown on whoever is
correcting it, which would contradict the gate design's own rule that saving a setting starts scraping
on the next tick rather than at the next restart. Resolved by making the reset explicit rather than
temporal: `AdminAo3CredentialController` clears the backoff whenever the credential is saved or
cleared, so the wait only ever applies to a credential nobody has touched. The same schedule covers an
unreachable archive as well as a refusal — neither is helped by being asked again in a minute.

**The session's lifetime is read off the cookie jar, never off the `Set-Cookie` headers.** Also from
the review. Those are not the same set: a header can announce a *deletion*, whose date is in the past
by construction, so `Set-Cookie: banner=1; Max-Age=0` arriving beside the session dated the entire
session to that instant and sent the instance back to log in on every single poll. `Ao3Cookies` now
carries each cookie's expiry beside its value and `EarliestExpiry` takes the jar, so only cookies the
instance is actually holding can shorten the session. The same change fixes the milder version — an
unrelated short-lived cookie capping a fortnight-long login at a few minutes.

**"The login set no cookies" has to be asked of the POST, not of the merged jar.** Third from the
review. The form fetch already puts an anonymous `_otwarchive_session` in the jar, so the merged jar
is non-empty however little the POST did — meaning a response that set nothing at all was accepted and
that *anonymous* cookie encrypted and stored as the instance's session, with every later run believing
it was logged in until some page happened to read `LoggedOut`. Rails rotates the session on sign-in, so
a login that establishes no cookie of its own has signed nothing in, whatever its status line says.

**A page proved to be logged-out is not cached.** Fourth from the review, and the subtlest. The
request carried a cookie, so the response was keyed `session:` — but the session it named was dead.
Discarding the cookie and caching the page meant the next poll logged in successfully, computed the
same `session:` key, and was served the dead session's anonymous copy for the rest of the cache
window: fifteen minutes reading the logged-out archive immediately after re-authenticating to avoid
precisely that.

## 2026-08-25 — T44: the flag belongs to a request, and "the run" was never fine-grained enough

**Taken out of file order, as the run-order note asked.** T12 was next by position; T44 was taken
first because five readers had independently derived the same two-line fix and T5 had just turned it
from latent into live. No plan change beyond the ordering — T44 was always in the list.

**The fix is where the assignment lives, not what it computes.** T30 moved
`Ship.LastKnownTotalWasAuthenticated` from the run's filter state to `wroteTotal`, which settled
*which run* may assign it. It left *which request* it describes alone: `readWhileLoggedIn` ORed
`response.Authenticated` across every page while `RecordTotal` wrote `LastKnownTotalWorks` per page.
Both now happen in one place — `RecordTotal` takes the response's own reading and writes the number
and the flag in the same three lines. `wroteTotal`, `readWhileLoggedIn` and two `FinishAsync`
parameters are gone; there is no longer a value that can drift from the thing it describes because
there is no longer a value at all.

**Why a single run mixes both answers**, which is the part that makes this reachable rather than
theoretical, and it took T5 to make every clause true:

- A cached page preserves `Authenticated: false` regardless of the session the run holds.
- A page carrying no evidence either way — a 404, a file body, an AO3 soft error — reads false too.
  Not "anonymous", *unknown*, deliberately collapsed to false by T5 so that a 404 cannot throw away a
  working session.
- A session can die mid-run. `RateLimitedAo3HttpClient` discards a cookie AO3 has stopped honouring
  the moment a page comes back logged out, and every later `GetAsync` in that run goes out anonymous.

So an unfiltered multi-page pass can write its total from an anonymous page 1 and reach a
session on page 5, or the reverse. Under the OR, the first stamped "counted while logged in" on a
number short by however many restricted works the tag holds — and that number is precisely what
T15's sweep checks itself against before concluding works have left a tag.

**Within a run, the last unfiltered page with a readable heading owns the flag**, because it also
owns the total: `RecordTotal` rewrites both on every such page. That is not a new rule, it is the
old rule ("the flag belongs to the total beside it") applied one scope further in, which is exactly
what T30's own note predicted would be needed.

**`Ship.LastKnownTotalWasAuthenticated`'s own doc was one word wrong** and has been corrected: it
said "the run that produced the total". A run is not fine-grained enough to be the answer, and the
comment saying otherwise is how three of the five readers who re-derived this defect found it in the
first place. It now says *request*, and says why "run" is not.

**Not changed, and deliberately:** the restricted-work warning still refuses to overrule the
transport about what a request sent. A restricted blurb on a response the client says was anonymous
is logged and acted on by nothing. That premise is still T39's business.

## 2026-08-25 — T12: a download is two requests, and the seam takes a page rather than an id

**Built to T13's capture, not to T11's handoff.** The instruction T12 inherited was to isolate a URL
*construction* function taking a work id, as a placeholder seam T13 would later correct. The captured
work page withdrew that: `/downloads/{workId}/{slug}.{ext}?updated_at={unix}` has two parts nothing
in the library can produce, so the address is *read* rather than built. `Ao3DownloadLinks` therefore
takes a fetched page, and a download costs two rate-gated requests — the work's page, then the file.
Nothing was shipped for T13 to undo.

**Both halves fail independently, and a reader is told which.** The page fetch can 404 while the
file would have been fine, and the page can parse to a menu that offers no such format. Each is one
`Failed` request carrying a message naming the half that failed, never a retry: a work AO3 has taken
down would otherwise be asked for on every poll for ever, which is precisely the load this project
exists not to produce. The one thing that is *not* a failure is a drain running out of budget — that
releases the request back to `Pending`, because nothing is wrong with it.

**`view_adult=true` on the work page.** Without it AO3 answers an explicit work with an interstitial
that carries no download menu, and the request would fail saying "no EPUB offered" when what was
offered was a warning. It is the archive's own Proceed link, not a way around anything: the instance
is logged in and may read the page either way.

**Streamed, capped, and never cached.** `IRateLimitedHttpClient` grew `DownloadAsync(url, stream)`
rather than returning bytes, so an EPUB is never held in memory on its way to disk, and the send
path became generic over "how the response is read" so that a download and a page share one gate,
one retry policy and one User-Agent *by construction* rather than by being written twice. A new
`MaxDownloadBytes` (64 MB) bounds what one click costs the instance's disk — a chunked response has
no length until it has finished arriving — and a response past it is abandoned and reported, not
stored. Downloads are not put in the response cache: that cache exists to stop a *page* being
re-read within fifteen minutes, and the row keyed by (work, format, version) already de-duplicates
the bytes.

**Written to a temp name and moved into place, under a path carrying the version.** A crash or a
truncated response mid-fetch would otherwise leave a partial file at the exact path a row calls a
complete copy. The partial lives under `downloads/partial/` rather than the system temp directory,
so the move is a rename within one filesystem and therefore atomic. The stored path is
`downloads/{workId}/{workId}-{ticks}.{ext}` — relative, because the data directory is a Docker
volume, and carrying `Work.UpdatedAt` in ticks because two versions of one work are two files and a
path that collapsed them would serve the new bytes to every request still pointing at the old row.

**The download worker is a sibling of `ScrapeWorker`, not a mode of it.** What is due to be scraped
is decided by a schedule; what is due to be downloaded by someone having asked. They share the one
thing that matters — the global rate gate — so a download and a scrape queue behind each other
rather than doubling this instance's load. It applies the *same two gates*: no honest User-Agent and
no stored AO3 login mean the queue is held, nothing attempted and nothing failed, exactly as due
jobs are held. A download made without an identifying header is the thing this project refuses to
send, whoever clicked the button.

**One budget per drain, and the drain stops on the first held request.** A queue of two hundred
files is a real amount of load however it was asked for, so `ScrapeBudget` bounds a drain the way it
bounds a run; what it stops stays `Pending` for the next poll. Whatever held one request holds
everything behind it, so carrying on would claim and release each in turn for nothing.

**`Downloading` is re-queued on startup.** It means "a worker holds this row", which is what stops
the controller re-arming a fetch in flight — so after a crash nothing holds it and nothing will ever
touch it again. Every such row is stale by definition: one process, and the sweep runs before the
first drain claims anything.

**`WakeSignal` is now a base class.** `ScrapeWakeSignal` and the new `DownloadWakeSignal` are the
same semaphore-of-one mechanism with different policies, and one nudge must not spend the other's:
asking for a file has no business sweeping a schedule nothing changed. Subclasses rather than two
registrations of one type, so they stay separable by type in the container like everything else.

**No migration.** `Download`, `WorkDownloadFile`, their unique indexes and their FK behaviours were
all in `InitialCreate` on both providers. T12 adds no column, as T11 did not.

**The unique-index race on `WorkDownloadFile` is handled and unpinned, on T11's precedent.** Two
drains fetching the same (work, format, version) both find nothing on disk; the loser catches
`DbUpdateException` and keeps the winner's row. Its bytes are identical and were written to the same
deterministic path, so nothing is lost. As with T6 and T11 there is no test: the fixture shares one
SQLite connection and there is no seam to open the window. Said here so the gap reads as a decision.

**T13's verification filter changes.** It was `~DownloadUrl`, which matches nothing — the seam is
`Ao3DownloadLinks` and its tests are `Ao3DownloadLinksTests`. T13's filter is now
`~Ao3DownloadLinks`, and its remaining job is what the capture left open: a test per format, and the
two questions no capture can answer (whether a stale `updated_at` is rejected or redirected, and
whether these links work anonymously), decided against a local stub and recorded as assumptions.

## 2026-08-25 — T12's review: five folded in, one queued as T59, one already fixed, one already T49

`/code-review high` ran over the branch and the download slice and returned eight findings. Its two
highest were the same defect I had already found by reading my own diff (the stalled download
holding the global gate) and T49 — so of the eight, five were new and are fixed here, one is a
re-decision queued as T59, and two needed nothing.

**The gate-stall finding was already closed before the commit it reviewed.** The reviewer read the
working tree before `DownloadTimeout` went in and confirmed the mechanism empirically — with
`ResponseHeadersRead` and a 2s `HttpClient.Timeout`, a body taking 10s completes without throwing.
That is worth keeping written down: it is the reason the deadline exists, and it is not something
the type system or the timeout setting will remind anyone of. **Two readers derived this defect
independently**, which on this branch is now the third time that has happened.

**A 200 is not evidence that what arrived is the file.** This is the finding worth the most. The
download transport follows redirects, so an AO3 that declines a download — a restricted work whose
session dies in the seconds between reading the work's page and fetching the link, which
`DownloadAsync` cannot notice because it deliberately never reads session state off a file body —
answers by redirecting to the login form. That is a 200 carrying HTML. The old code stored it,
hashed it, moved it into place, gave it a `WorkDownloadFile` row and reported the request
`Complete`: a login page on disk under a name saying it is an EPUB, with a checksum agreeing.
`DownloadFetcher.LandedOnTheFile` now checks where the request *ended up*. Judged on the extension
rather than the whole address, so a redirect that still serves the file is not refused for moving
it, and judged not at all when the transport reported no final URL — "no evidence" is not evidence,
the same rule the session reading follows. A Content-Type check was considered and rejected: the
HTML download format really is `text/html`, so it cannot tell a login page from a legitimate
download.

**The instance's session cookie was being offered to whatever host the markup named.** `Resolve`
accepted any absolute http(s) URL and `DownloadAsync` attaches the session to whatever it is handed.
The `li.download` scoping made this unreachable in practice today, but a work page renders
author-supplied HTML, and one href surviving AO3's sanitiser inside the download menu would have
handed this deployment's AO3 login to whoever wrote it. A link must now name the same host as the
page it was read from, and `pageUrl` stopped being optional — a link with nothing to check it
against is not a link this app may fetch.

**Abandoned part-files are swept at startup.** The fetcher deletes its own when a fetch fails, but a
killed container leaves one behind with nothing to clean it up, each worth up to `MaxDownloadBytes`.
Nothing reads that directory and nothing resumes a part-file, so anything in it at startup is
rubbish by definition — the run that created it is gone and the row it belonged to has just been
re-queued. It sits in `ReleaseInterruptedFetchesAsync`'s `finally`, so a database failure there does
not also leak disk.

**Two documentation defects, both mine and both real.** Inserting `WakeTheWorker` above `Arm` left
two `<summary>` blocks stacked on one member, so `Arm`'s documentation was stranded on a method
describing something else. And a comment in `FailAsync` explained a guarantee about
`WorkDownloadFileId` that the controller does not actually provide — which is T59, and the comment
now points there instead of claiming it.

**T59 is a re-decision, not a bug fix.** `Arm` nulls `WorkDownloadFileId` when it re-arms a stale
request, which T11 chose on purpose. The review's objection is the half that choice did not weigh:
the file is not deleted, so a reader whose refetch fails is left with neither a working row nor a
reachable copy of bytes that are still on disk. Queued rather than changed here, because reverting
T11's rule would reintroduce the worse failure it was written against, and because T14 has to render
whatever state the answer invents.

**T49 arrives for the third time and is still not this diff's to fix.** The reviewer confirmed
`CA2017` empirically once more — six placeholders over five arguments, `string.Format` throws, and
the retreat never runs. It is the only analyzer warning in the build.

## 2026-08-25 — T13: five formats read off the capture, and two questions settled by not depending on the answer

**Every format is pinned to an address copied out of the capture, one case per enum member.** The
five hrefs in `ao3-work-page.html` share an obvious shape, and a table whose expectations were
generated from that shape would agree with a parser that generated the same shape — and go on
agreeing with it while it was wrong. So each `[InlineData]` carries the full URL as the fixture
spells it, and `Reads_every_format_this_library_fetches_and_nothing_else` compares the parsed keys
against `Enum.GetValues<Ao3DownloadFormat>()`, which is also the guard on the enum: a sixth member
added without a re-captured page fails there rather than quietly becoming a format no work offers.

**Neither open question was answered, and neither needed to be.** T13 existed to settle two
behaviours a capture cannot describe. The settlement in both cases is that this app's behaviour is
safe under every answer, pinned against a stub, rather than a guess at what AO3 does.

**A stale `updated_at`, if AO3 refuses it, is already handled and already pinned.** A refused
address is a refused file, which `Fails_a_request_whose_file_AO3_will_not_serve_and_stores_nothing`
covers — `Failed`, a message naming the file half, no row, nothing on disk — and
`Does_not_ask_again_for_something_it_has_already_failed` covers the request not then being made for
ever. Nothing new was written for this branch; a 410-shaped copy of an existing 404 test would have
been a duplicate wearing a different number.

**A stale `updated_at`, if AO3 redirects it to the current file, is accepted — and the row still
says which version this library thinks it holds.** `LandedOnTheFile` judges a redirect on the
extension, so being served the file from a fresher address is not a refusal. The claim worth pinning
is the one after it:
`Keys_a_redirected_download_to_the_version_the_library_holds` asserts that `WorkDownloadFile.
WorkUpdatedAt` and the stored path come from `Work.UpdatedAt`, not from the `updated_at` in whatever
address the request ended at. **They are different clocks and always were** — AO3 stamps its own,
the library records what its last scrape saw — so a row keyed off the address would claim to be a
copy of a version no scrape has ever seen, and the next request for the work as the library holds it
would fetch the same bytes again.

**Whether these links work anonymously is a question this deployment never puts to AO3.** The
capture was taken logged in, and it stays that way in practice: the download queue is held until an
instance login is stored, both halves of a fetch go through the authenticated transport, and a
session that lapses mid-fetch produces a login redirect the fetcher refuses rather than a login page
stored as an EPUB. `Never_asks_for_a_download_anonymously` pins the middle of that — one page
request, one file request, and no reach for `GetLoggedOutAsync`, which exists for the login page and
nothing else. The one thing that must hold when a session has lapsed anyway is at the transport:
`Identifies_the_instance_even_on_a_download_it_has_no_session_for` pins that no cookie to attach is
not an excuse for a request that does not say who is making it.

**The question mattered, and here is what it turned up: the response cache is how a stale address
arises at all.** A download address is read off the work's page, and that page goes through the
fifteen-minute response cache. So a work updated between one download and the next is fetched from
an address the cached page carried — and if AO3 simply *serves* the old version's file at the old
address, which is the third answer neither of the two above covers, those bytes are stored keyed to
`Work.UpdatedAt` as it now stands. The library would then report the previous version as a copy of
the current one: the exact failure the version-keyed path exists to prevent, arriving through the
cache rather than through the filename. **Queued as T60 rather than fixed here** — the cheap fix
(bypass the cache on every download page fetch) throws away the case the cache is actually for, a
second format of the same unchanged work, and the comparison that would be exact is unavailable
because the two timestamps are not the same clock.

## 2026-08-25 — T13's review: nothing in the diff, eight findings in the branch, two verified

T13's diff is tests only, and `/code-review high` found nothing wrong with it. What it did do was
read the whole branch and return eight findings in code earlier tasks shipped. None were folded in —
a test-only task that quietly grows a security fix and a `BackgroundService` fix is a diff nobody
can review — so all eight are queued as **T61–T68**.

**Two were verified here rather than taken on the reviewer's word**, because an agent's finding is a
claim until someone reads the code:

- **T61, the login POST.** `Ao3SessionEstablisher.Absolute` returns any absolute http(s) URL
  unchanged and `form.Action` comes off the fetched login page, so a form action naming another host
  is posted to — carrying the instance's AO3 username and its decrypted password. This is the twin
  of the host check T12's review added to `Ao3DownloadLinks.Resolve` one iteration ago, and the
  login flow is now the only place in the codebase without it. **That is the third time on this
  branch a defect has been found in the code path a previous fix's reasoning applied to but did not
  reach**; the rule the earlier entry stated — anywhere a URL read out of markup is then fetched
  with credentials, the host is the check that matters — was written down and still not swept for.
- **T62, the startup partials sweep.** `DiscardPartialFiles` runs in a `finally` outside its
  method's own `catch`, and that method is awaited outside `ExecuteAsync`'s loop `try`.
  `Directory.GetFiles` there is unguarded, so an unreadable partials directory throws out of
  `ExecuteAsync` and `BackgroundServiceExceptionBehavior.StopHost` takes the API down at boot — on a
  path that runs on every boot, in a class whose own comment says nothing may end this loop. It was
  added by T12's review, which is worth noting: a fix for a disk leak introduced a startup crash,
  and the review that produced it did not re-examine where its own code would run.

The other six (T63–T68) are recorded as reported-not-verified, each saying so in its own notes, so
whoever takes one checks the mechanism before building to it.

## 2026-08-25 — T61: the archive is the comparand, and the origin is what is compared

`Ao3SessionEstablisher` now refuses to post the login form to anything but the archive the
deployment is configured for. Three decisions inside a nine-line check:

**The comparand is the configured archive, not the page the form came from.** T12's twin in
`Ao3DownloadLinks.Resolve` compares a link against `page.FinalUrl ?? pageUrl`, because a work page is
fetched at an address this app built and a download link has no other authority to be measured
against. The login flow has a better one. `Absolute` already resolves a *relative* action against
`Ao3HttpClientOptions.BaseUrl`, so the configured root is what a relative action reaches whatever
redirects the login page followed — and comparing an absolute action against `page.FinalUrl` would
have compared it against a value the archive's own redirects can move. `LoginPath` under `BaseUrl`
is the one address in this flow no page can influence, and it is what the check uses.

**The comparison is the whole origin, not the host.** The download check compares hosts, which is
right for the value it protects — a session cookie already scoped to a host. What this request
carries is a plaintext password, and an action that keeps the name and drops to `http://` puts it on
the wire in the clear. `GetLeftPart(UriPartial.Authority)` compares scheme, host and port together;
AO3 serves its login form over HTTPS and posts it back to the same place, so nothing real is refused
by asking for that.

**The check sits at the call site, not inside `Absolute`.** `Absolute` also resolves `LoginPath`
itself, where there is nothing to compare against and no markup involved. A host rule pushed down
into the helper would have had to special-case its only other caller.

A refused action is a refused login, so it inherits `Ao3LoginBackoff` — 5 → 15 → 30 → 60 minutes,
lifted the moment the operator saves the credential again. A login page whose form has moved is not
a thing that becomes true by being asked for every sixty seconds.

**The sweep the rule asked for was run this time.** T13's entry recorded that T12's review stated a
general rule — anywhere a URL read out of markup is then fetched with credentials, the host is the
check that matters — fixed one site and never swept. Every outbound call site in the API is now
accounted for: `ShipVerifier` and `Ao3ShipIndexScraper` build their URLs from `BaseUrl` and a tag
name, `DownloadFetcher` builds the work-page URL and fetches one checked link, and the login POST is
this task. Two fetch targets in the codebase come out of markup and both are now checked. The other
hrefs the parsers read (`Ao3BlurbParser`'s work, pseud and series links, `Ao3LoginPage`'s greeting)
are parsed for ids and names and never fetched.

**T61's review found five, one of them in the file this task's own doc comment points at.**
`/code-review high` read the working tree and returned five findings, four of them in T12's download
code and none in the login change. The one that belongs to this diff is finding 2:
`Ao3DownloadLinks.Resolve` compares hosts, `IsWeb` accepts `http`, and the doc comment written *in
this task* claimed "the same check guards `Ao3DownloadLinks`" — which it did not, one scheme along.
An `http://archiveofourown.org/downloads/…` link inside `li.download` on an https page resolved,
and `DownloadAsync` attaches the instance's session cookie to whatever it is handed. Folded in
rather than queued: it is three characters, it is the same rule this task exists to apply, and the
alternative was shipping a comment that described the codebase as safer than it was.

The other four are queued. One of them, finding 3, is **T67 already** — reported by T13's review,
re-derived independently here, and verified this iteration by reading the code rather than left as
a claim. The remaining three are **T69** (a `WorkDownloadFile` row whose file is gone still answers
"you already have this"), **T70** (a transport failure settles a queued download as `Failed` on one
attempt, where the scrape walk deliberately re-asks) and **T71** (a file moved into place before its
row is written is orphaned when the write throws). All three were verified mechanically here; where
a reviewer's claim went further than what was read — T69's "and `DELETE` then `POST` cannot recover
it either" — the task says which half is verified and which is not.

## 2026-08-26 — T14: the file is a fourth answer, and the filename is the untrusted part

**Serving the bytes is its own endpoint, and it has four answers rather than two.**
`GET /api/downloads/{id}/file` streams through `PhysicalFile`, so a PDF never sits in memory on its
way to a reader. What took the thinking is what it says when it cannot serve: a request that is not
this reader's — or that never existed — is a bare 404 alike, the rule `DeleteDownload` already
follows, because answering differently reports whose a given id is. The other two are about the
caller's own row and therefore say what is wrong with it: not `Complete` is 409, and a row whose
file has left the disk under it is 410 rather than the unhandled exception that opening a missing
path would otherwise be. A reader can act on the difference — one means wait, the other means ask
again — and neither discloses anything, because both are about a row they own.

**The 409 is judged on the status, not on whether a file is named, and that half was unpinned.**
Dropping `request.Status != DownloadStatus.Complete` from the guard left the suite green: today
`Arm` moves the status and the file reference together, so no test could hand the endpoint a queued
request that still named bytes. That state is exactly what **T59** is deciding whether to create —
its whole question is whether a re-armed request should keep the copy it already had — so the test
constructs it directly (`Will_not_serve_a_request_that_is_queued_while_still_naming_a_copy`) rather
than leaving the guard resting on a coincidence in another method. T59's notes now point at it: the
guard is what stops "keep the reference" from meaning "serve the previous version as the answer to
the refetch". Same shape as T12's surviving mutation, and the same remedy.

**The filename is the untrusted input, and it is allowlisted rather than filtered.** A work title is
text an author wrote and AO3 carried, and it goes into a `Content-Disposition` header and then into
whatever filesystem receives it. So `FileNameFor` keeps letters, digits and a short list of
punctuation and turns *everything else* into a space — rather than removing the characters known to
be bad today, which is a list that has to be right for ever. A dot is kept only after a letter or
digit, which is what tells `Co.` apart from `../..`. Trailing dots and spaces go, because Windows
silently drops them and a name ending in one is not the name the reader was shown; a leading dot
goes because it hides the file on Unix. A title that survives as nothing is not an error — a work
titled entirely in punctuation is a work — so `work-{id}` stands in.

**Runes, not chars, and the difference is a whole script.** Judged one UTF-16 unit at a time, both
halves of a letter outside the basic plane fail every test a letter passes and become spaces: a
title written in Gothic, Deseret or a CJK extension would have come out as the work id. Safe, but
the fallback is for titles that are punctuation, not for titles this app declined to read. The
length cut is the same rule at the other end — 120 units, taken one short when the 120th would be
half of a letter, because a lone surrogate is not something a filesystem or a header encoder can do
anything with.

**HTML is served as bytes, deliberately.** AO3's HTML download is a whole document of
author-supplied markup. Served from this app's own origin under `text/html` it would be one slipped
`Content-Disposition` away from running as script inside a logged-in session, so it goes out as
`application/octet-stream` with `nosniff` beside it. The arm is named rather than left to the
fallback: the exception is a decision and should read as one, and the fallback stays what it says it
is — a format nobody has typed yet. Every other format is named as itself, which is what lets a
phone hand an EPUB to a reading app.

**The stored path is checked against the data directory even though nothing can make it wrong.**
`RelativePath` is written only by `DownloadPaths.Relative`, out of a work id and an enum. It is
checked anyway because this endpoint is the one place in the app where a value out of the database
becomes a file handed to whoever asked: a row naming `../../etc/passwd` — from a restored database,
a migration, a future writer with a different idea of that column — would otherwise be served in
full to any signed-in reader. This is the branch's own repeated lesson applied before it has to be:
the rule T61 swept for was about URLs read out of markup, and this is the same rule one input over.

**`Cache-Control: private, no-store`.** Private because the response is served against this
reader's request row; no-store because a request re-armed onto a newer version of the work answers
the same address with different bytes, and a cache holding the old ones would be a stale copy the
version-keyed path exists to prevent.

**Polling follows the data, in one hook rather than two pages.** `useDownloads` is the queue, the
ask and the drop, shared by the Downloads page and the format buttons on a work — they show the
same rows, and two copies would let them disagree. It polls while anything is `Pending` or
`Downloading` and stops on the load that finds nothing is; a click that queues something restarts
the chain by bumping a generation the effect depends on, so the restart *replaces* the chain rather
than adding a second one. A failed poll does not stop it — the server being briefly unreachable
says nothing about whether a fetch is still running — and does not blank the list, because what it
holds is still the last thing the server said.

**A re-requested row is replaced in place, not moved to the front.** Re-arming does not change
`RequestedAt`, and the list is ordered by it, so a row that jumped to the top on the click would
jump back down on the next poll — the list reordering itself under the reader's cursor.

**No per-work downloads endpoint.** The work page filters the reader's own queue rather than asking
for one work's slice: it is a handful of rows, and a second endpoint would be a second scoping rule
to keep honest for no gain.

**T14's review was read, not run.** `/code-review high` was launched and died on the account's
monthly spend limit before reading the diff. The pass was done by reading instead, and it found
three things worth changing — the unnamed `Html` arm, the surrogate handling, and the row-ordering
flicker — plus the containment check, which came out of asking what this endpoint does with a value
it did not write. Recorded so that the next branch-wide review knows this diff has had less
adversarial reading than its neighbours.

## 2026-08-26 — T18: the corpus and the reader meet in one table, and the second provider is checked by translation rather than by hope

**Two lenses, and one per-ship table where they meet.** `GET /api/stats` returns `corpus` (the
works as they stand, nobody's reading in them) and `reading` (the caller's own numbers), which is
the split the spec asks for. But "share of each ship read" is a fraction, and a fraction needs its
numerator and its denominator in the same row — split across a corpus list and a reading list, the
page would have to join them by ship id to say anything at all. So `ships` sits at the top level
carrying both, and is documented as the place the two lenses meet rather than as a third lens.

**"Works per ship over time" is the `shipId` parameter crossed with one series, not a series per
ship.** The task's delivers line names both, and asking for both at once produces a
(ship × month) grid whose rows do not sum to anything — a work carrying two watched relationship
tags is in both series. One series over whatever the request is scoped to answers the same question
exactly, and T19's own filter ("one ship or all") is already the control that picks the scope.

**The series is keyed on `UpdatedAt`, and named for it.** `PublishedAt` is null until somebody
opens a work's own page, so a publication curve would describe what has been clicked on rather than
what the ship's corpus looks like. The DTO field is `worksByUpdatedMonth` rather than anything
shorter, because "works per month" over a revision date is a real fact and a publication curve is
what a reader would otherwise assume they were being shown.

**Zeros are filled in everywhere the vocabulary is fixed, and nowhere it is data.** Bucket
histograms, the AO3 rating mix and the reading-status mix all carry every value including the ones
no work landed on: a grouped query returns only what is present, and a mix that silently omits
"Dropped" asks a reader who has never dropped anything a different question from one who has. The
month series is the exception and is deliberately gappy — its span comes from the data, so a single
work carrying a misparsed year would make the server materialize centuries of empty buckets. The
caller has the year and month on every row and can fill the axis exactly.

**The histogram's `CASE` is generated from the bucket array, which is the opposite of the rule
`WorkQueries.ApplyFilter` follows, and for that rule's own reason.** There, each bound is a user's
number and has to reach the database as a parameter, or every distinct bound earns its own query
plan. Here the boundaries are compile-time constants shared by every caller, so there is exactly
one plan whichever way it is written — and generating the comparisons buys the thing that actually
matters, which is that a bar's label and the comparison that fills it come from one array and
cannot drift. A theory pinning every boundary value to the label it must land under is what says
the generation is right.

**The cross-provider guarantee is `ToQueryString()` under Npgsql, and that is a new kind of test
here.** Every statistics figure is an aggregate, aggregates are where EF's two providers differ
most, and there is no PostgreSQL server in this loop's shell. `StatsQueryTranslationTests` builds a
`PostgresAppDbContext` on a connection string it never opens and asks each query to compile — EF
throws rather than falling back to client evaluation, so an untranslatable aggregate fails here
instead of 500ing on somebody else's instance months later. It cannot say the SQL is *right*; that
is what the SQLite controller tests are for. The list of queries it covers is read off `StatsQueries`
by reflection rather than maintained by hand, because a hand-maintained list of "things to check"
is exactly the list that stops being maintained — and it caught a missing case during this task.

**Thirteen small aggregates rather than one fused `GROUP BY`.** A single grouped-by-constant query
would fetch every total in one round trip, and PostgreSQL reads a bare `GROUP BY 1` as an ordinal
reference to the first output column — EF's wrapping usually avoids that, but "usually" is not a
property this repo can test for. Each figure is instead its own unambiguous statement against a
local database, on a page nobody polls.

**The subscriptions decide which ships have rows; the aggregate only fills them in.** A followed
ship with nothing in it cannot appear in an aggregate over the library, because it contributes no
works to group. Driving the list from `WatchedShips` and left-joining the counts is what makes a
fresh install — or one the AO3-login gate is holding — show its ships sitting at zero instead of
showing an empty page that explains nothing. That page's whole job is to explain the emptiness.

**The per-ship figures were written as counts correlated to each watched ship, and that was
wrong.** The correlated form reads beautifully — `library.Count(x => x.Ships.Any(...))` per figure
per ship, reusing `WorkQueries.Library` verbatim so the numbers could not drift from the feed — and
it compiled to five full subqueries over the works table, re-executed once per watched ship: eighty
lines of SQL and twenty scans per figure for a reader following twenty ships. Replaced with one
grouped pass that joins the library to `ShipWorks` and folds the caller's state per row. The lesson
is the one T33 is queued for: in this codebase a query that reads like a sentence is worth checking
against the SQL it actually became.

**An average over nothing is null, not zero.** An empty library has no mean kudos and a reader who
has rated nothing has no mean rating; zero is not even on the half-star scale, which starts at one.
Computed in C# from two totals the database already returned, which is also the form in which "no
works" has an answer rather than a division by zero.

**Unrated is absent from the rating lens, and unmarked is present in the status mix.** They look
like the same decision and are opposites. A null rating is "no opinion" and averaging it in as a
zero would drag every bar off the bottom of the scale, so rated works are selected for. A missing
*status* is a real answer — "nobody has touched this" — and it is most of a fresh library, so the
status mix counts it under `None` and therefore sums to the corpus. That is the same reading of
"unread" the saved-filter predicate takes, and the reason it has to cover both an absent row and a
row left saying `None`.

## 2026-08-26 — T18's review: three findings, two folded in, one false and turned into a test

`/code-review high` ran this time (T14's died on the spend limit) and returned three, all in this
diff. It also flagged, fairly, that the files were being rewritten under it: the `PerShip` refactor
below landed mid-review, so it waited, rebuilt and re-checked. Worth knowing for next time — launch
the review *after* the diff has settled, not alongside the last change to it.

**The medium finding was wrong, and checking it was still worth the trip.** The claim: grouping on
`UpdatedAt.Year` / `.Month` compiles to `date_part` over a `timestamp with time zone`, which
PostgreSQL evaluates in the session's `TimeZone` — nothing pins it, so a work revised at 03:00 UTC
on the first of a month would land in the previous month for a self-hoster whose server defaults to
Chicago. The reasoning is right about PostgreSQL and wrong about what Npgsql emits: the generated
SQL is `date_part('year', w."UpdatedAt" AT TIME ZONE 'UTC')`, and `AT TIME ZONE 'UTC'` over a
timestamptz is not session-dependent. But the reviewer's second sentence was the real finding —
`StatsQueryTranslationTests` only asserts that *some* SQL came out, so if that normalization ever
stopped happening nothing here would fail: the query would still compile, still run, and quietly
answer differently on two instances holding the same library. There is now a test asserting every
`date_part(` in that query is paired with an `AT TIME ZONE 'UTC'`.

**`Distribution` validated the mistake it could not make and not the two it could.** It threw on a
mid-array open-ended bucket, and said nothing about a *closed last* bucket or a gap between two —
both of which are silent, because the generated comparisons are a chain of `<= Max` and a bar
therefore catches everything above the previous bar's `Max` whatever its own `Min` says. Close the
top bar and a million-word work is still counted under a label that excludes it; leave a gap and the
values in it are filed under a bar the client is told starts higher. `Validate` now checks all
three, which is also what makes `StatsBucket.Min` load-bearing rather than decorative — it was
published to the client and read by nothing.

**One predicate for "which ships does this reader watch", in `WorkQueries`.** `PerShip` had its own
copy of `WatchedShips.Where(w => w.UserId == userId)`, which is the thing `StatsQueries`' own header
says it does not do — and it was not the only copy: `WorksController` had two more, plus a fourth in
the shape of an `AnyAsync` membership guard. All of them agree today. The failure they were set up
for is narrowing what "watched" means later — a pause flag, a soft delete — and having the library
stop including ships that the per-ship rows and the per-row ship names kept naming, with nothing
failing. Now `WorkQueries.WatchedShipsOf` / `WatchedShipIdsOf`, with `Library` itself derived from
them, and every site pointed at it. Touching `WorksController` widens this task's diff by four
lines; leaving one caller behind would have recreated exactly the drift the finding is about.
