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
