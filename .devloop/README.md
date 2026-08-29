# .devloop

Closed-loop development state for Ship Watcharr, written by the `devloop-init` skill and read by
`devloop-iterate`, which has no memory of the conversation that produced it: `spec.md` is the
whole brief, `tasks.md` the work and its ordering, `JOURNAL.md` an append-only record of what each
iteration did, and `DECISIONS.md` the reasons the plan changed, `LESSONS.md` reusable one-liners, `BACKLOG.md` findings
the loop does not work. `bin/status.sh` is how an iteration loads state; `bin/check-journal.sh` gates the commit. Start or resume the loop with
`/devloop-iterate` for a single task, or hand the whole list to `/ralph-loop` (see the command
printed at the end of init). Everything here is committed on purpose — it is the plan, not scratch.
