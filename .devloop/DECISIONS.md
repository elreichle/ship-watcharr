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
