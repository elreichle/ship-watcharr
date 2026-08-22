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
