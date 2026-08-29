# Lessons

Reusable, one line each, deduplicated. Read in full every iteration (keep it under ~25 lines).
A journal `next:` is for *this* handoff; a lesson is what would have saved a past iteration an hour.

- A task's `delivers` line is the contract; its `notes` are one reader's guess at the implementation.
- Verification filters in task entries may match zero tests, or not the ones the task just added — check both.
- Review only this task's diff (`git diff HEAD~1 --name-only`); a branch-wide review re-derives findings already on the list.
- A reviewer's claim is not a defect until reproduced; unverified claims go to BACKLOG.md, not tasks.md.
- Mutation scripts that restore whole files must restore in reverse order, or re-read the diff before review.
- `/usr/bin/dotnet` is runtime-only: non-interactive shells need `PATH="$HOME/.dotnet:$PATH"`.
- Never `pkill` on `Ao3Tracker.Api.dll` — it matches the systemd dev instance on :5110; kill by PID.
- `dotnet ef database update` ignores your connection string: the design-time factory sends it to `design-time.db` in the API directory, never `appdata/`.
- A count both polled and written by a click needs the poll to check a write counter before applying, or a slow poll reverts the click.
- The docker daemon has network even though the loop's shell does not: `docker pull` and in-image `npm ci`/`dotnet restore` all work.
- A logger provider that never renders hides CA2017: a template with too few args throws only when formatted, and the throw unwinds the code that logged it.
- `code-review <paths>` reads whole files, not the diff, when they are new in `main...HEAD` — expect branch-wide findings and ~10 minutes.
- A test that seeds rows the ingestor also creates must get-or-create both the row and its join, or it fails a unique key instead of the rule.
- The loop's shell had network on 2026-08-29 (AO3 included) though not on 2026-08-10 — test it, don't trust `spec.md`'s "no network" line; a fixture may not need a human.
- A browser-saved AO3 capture carries the account's username and CSRF token: scrub both before committing, but the greeting username is what `Ao3LoginPage` reads, so update its three assertions too.
- `ScrapeJob` is one row per ship (unique index, key always `ShipIndex`): a new `IAo3Scraper` key cannot be scheduled without a migration and both job writers.
- A queue drained in a fixed order needs a give-up counter, or one item that never completes starves every item behind it — `DownloadWorker._attempts` is the in-memory pattern.
- To sit between a controller's read and its write, arm `LibraryTestHost`'s interceptor on the *write* command text — armed on the read it fires too early and the test passes vacuously.
- No task entry carries a `verify: live` line, so UI work is verified by `npm run build` + `lint`: don't rebuild the browser harness (past scratchpads are gone anyway).
- A "did this field change" guard misses an editor closed and reopened mid-write; one counter bumped by every touch of it covers both, erring toward keeping text.
- A substring assertion on a query parameter's *name* can pass on the sort column's value — pin the whole URL.
