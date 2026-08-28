# The scraper's walking and stopping rules

T28. An audit, not a fix: every row is a rule the code **has** as of `6802bfb`, the code that
implements it, which passes it applies to, what it entitles that pass to conclude, and the test that
pins it. Gaps are listed as rows with the task queued for them; nothing here was changed by the
commit that added this file.

The question held throughout is the spec's: *which pass is entitled to conclude this, and has it
actually seen enough to be entitled?* Every defect this loop has found in the walk has been a pass
concluding something it had not earned — absence, an end of listing, a watermark, an empty byline.

Line numbers are `backend/Ao3Tracker.Api/Services/Scraping/…` at `6802bfb`. "Pinned by" names a test
method in `backend/Ao3Tracker.Tests/`; every name in this file was checked against the suite's own
discovery list and matches at least one test (see the verification note at the foot).

Three passes exist in the design, and since 2026-08-28 all three exist in the code: `FullSweep`
(T15) shipped as a third mode of the same walk, so it inherits every rule below rather than having
rows of its own. What is only the sweep's is in `Ao3ShipIndexScraper.RecordSweepProgressAsync` and
`ConcludeSweepAsync`, and in D12 and E7 here — it is the only pass permitted to conclude a work has
*left* a tag.

---

## A. Where a pass starts

| # | Rule | Where | Passes | Concludes | Pinned by |
|---|------|-------|--------|-----------|-----------|
| A1 | A ship still `NotStarted`/`InProgress` gets a backfill; everything else gets an incremental pass | `ScrapeWorker.cs:234` | both | which stopping rules apply at all | **nothing — gap, T53** |
| A2 | A tag AO3 has denied is never requested | `Ao3ShipIndexScraper.cs:90` | both | nothing: returns before `FinishAsync`, so no ship state is written | `Never_asks_AO3_about_a_tag_it_has_already_denied` |
| A3 | A backfill starts at `max(1, BackfillNextPage ?? 1)` | `:93` | backfill | where the last run stopped is where this one resumes | `Resumes_a_backfill_where_the_last_run_stopped` |
| A4 | An incremental pass always starts at page 1 | `:93` | incremental | it has read the newest end of the listing, or it has read nothing | `Asks_AO3_to_exclude_what_it_already_has` |
| A5 | `BeginBackfill` moves `NotStarted` → `InProgress`, stamps `BackfillStartedAt`, defaults the cursor to 1 | `:751` | backfill | the ship is now walking its back catalogue | `Walks_forward_through_the_back_catalogue` |
| A6 | The watermark is captured before the walk and never re-read inside it | `:97` | incremental | every page of the run is cut against the same instant | `Advances_the_watermark_to_the_newest_work_it_ingested` |
| A7 | `listingWasFiltered` is read from the same function that builds the URL | `:102`, `:744` | incremental with a watermark only — **a backfill is never filtered** | which evidence the stopping rules may use, and whether the heading counts the tag or the filter's results | `Leaves_the_tags_total_alone_when_the_listing_was_filtered` |
| A8 | The `revised_at` bound is day-granular with a day of slack | `:744` | incremental | it can only ever return *more* than needed; the exact cut is client-side | `Asks_AO3_to_exclude_what_it_already_has` |

**A1 is the top of this table and nothing pins it.** Three separate comments in the walk lean on
"a `Failed` ship falls back to its incremental pass and goes on collecting new works" — the sentence
that makes T37's give-up survivable and T46's stall a live defect — and that sentence is one ternary
with no test under it. Queued as **T53**.

**A2's stop reason is `LastPage`.** Harmless only because the early return skips `FinishAsync`: were
that return ever moved below it, a denied ship's backfill would be recorded `Complete` with the tag
never requested. The run history is still told a healthy "walked off the end of the listing" for a
run that made no request at all. Noted on **T46**, which owns the ships-pointed-at-dead-tags family.

## B. Where a pass stops

| # | Rule | Where | Passes | Concludes | Pinned by |
|---|------|-------|--------|-----------|-----------|
| B1 | Request cap → `Cap` | `ScrapeBudget.cs:78`, walk `:155` | both | nothing about the listing; a backfill resumes from its cursor | `Saves_a_cursor_a_capped_run_can_resume_from`, `Allows_requests_until_the_cap_is_reached` |
| B2 | Wall-clock cap → `TimeCap` | `ScrapeBudget.cs:86` | both | as B1 | `Stops_at_the_wall_clock_cap` |
| B3 | Breaker after N consecutive failures → `Breaker` | `ScrapeBudget.cs:112` | both | "AO3 is failing", which is the more urgent diagnosis than the cap | `Breaker_opens_after_consecutive_failures`, `Breaker_takes_precedence_over_the_request_cap` |
| B4 | 200-page ceiling → `Cap` | `:161` | both | bounds a pagination *bug*, not cost | unpinned — see the foot of §F |
| B5 | A transport failure re-asks the same URL, bounded only by the breaker | `:177` | both | nothing; it is the one path that retries a URL, deliberately | `Survives_a_request_timeout_instead_of_letting_it_escape` |
| B6 | A non-OK status stops the run with `Error`, asking once | `:256` | both | nothing about the listing; the page is retried next run at the scheduler's spacing | `Asks_once_for_a_page_the_archive_refuses`, `Records_the_status_that_stopped_the_run` |
| B7 | A 404 concludes **nothing** — never `LastPage` | `:204` | both | the walk only advances past a page that offered a next link, so a 404 is a contradiction, not an ending (T43) | `Refuses_to_end_a_walk_on_a_404_the_page_before_it_said_would_answer`, `Reads_the_same_conclusion_off_a_404_whichever_run_read_the_page_before_it` |
| B8 | A backfill's **first** request, above page 1, that 404s or reads to nothing retreats one page — once per run | `:229`, `:298`, `:534` | backfill | nothing yet: it asks the listing, which is the only authority on its own length | `Completes_a_backfill_whose_cursor_the_shrunken_listing_404s_past`, `Completes_a_backfill_whose_cursor_the_shrunken_listing_serves_empty`, `Reads_the_same_run_off_both_of_AO3s_answers_to_a_page_that_is_gone` |
| B9 | Neither the cursor nor the page before it readable → halve the cursor, stop with `Error` | `:570` | backfill | the listing is shorter than the cursor by an unknown amount, not by one | `Halves_the_cursor_when_the_page_before_it_does_not_answer_either` |
| B10 | The retreat's page still offers the page that failed → stop with `Error`, cursor left on it | `:478` | backfill | the question is carried to the next run rather than re-asked inside this one | `Leaves_a_stale_looking_cursor_alone_when_the_page_before_it_still_offers_a_next_link` |
| B11 | Any other page that parsed to no works and is not plausibly the end → `Error` | `:320` | both | explicitly *not* the end of the listing (§C) | `Refuses_to_conclude_from_an_empty_page_reached_mid_walk`, `Refuses_to_conclude_from_an_empty_page_a_resumed_backfill_started_on` |
| B12 | A page with no works that **is** plausibly the end → `LastPage` | `:365` | both | an empty tag, as far as anything on the page can say | `Still_treats_an_empty_first_page_as_an_empty_tag` |
| B13 | One dated work no newer than the watermark ends the pass → `Watermark` | `:431` | incremental | the listing is `revised_at` desc, so everything after it is older still | `Stops_at_the_first_page_holding_anything_it_already_has`, `Still_stops_an_incremental_pass_on_a_page_holding_a_dated_work_it_already_has` |
| B14 | A page whose every blurb abstained, offering a next page → `Error` | `:446` | incremental | nothing on the page says where in the listing the walk is (T22) | `Stops_an_incremental_pass_when_no_blurb_on_a_page_carries_a_readable_date` |
| B15 | …the same page with no next link is **not** an error | `:446` | incremental | a small tag the parser is struggling with is not a runaway walk | `Does_not_call_a_last_page_an_error_when_an_incremental_pass_could_not_read_its_dates` |
| B16 | A page with works and no next link → `LastPage` | `:467` | both | the listing's own word on where it ends — the strongest evidence in the system | `Marks_the_backfill_complete_when_it_runs_out_of_pages` |
| B17 | An undated blurb is ingested but casts no vote and proposes no watermark | `:394`, `:421` | incremental | "no age at all" is not "very old" (T22) | `Keeps_an_incremental_pass_going_past_a_blurb_whose_date_it_could_not_read` |
| B18 | After `MinStuckIncrementalRuns` runs stopping short at the same page, the walk stops asking for the page *after* it → `Held`; lifted for one run every `ProbeHeldPageEveryNthRun` held runs | `:145`, `:576`, `:779` | incremental | **nothing about the listing** — it is a bound on requests, not a conclusion. The watermark may not move on a `Held` stop, exactly as on an `Error` (T45) | `Stops_asking_for_a_page_that_has_not_answered_for_the_last_few_runs`, `Holding_a_page_leaves_the_watermark_exactly_where_the_failures_left_it`, `Asks_a_held_page_again_once_enough_runs_have_held_it`, `Bounds_a_stuck_incremental_pass_over_consecutive_runs_through_the_worker`, `Holds_a_page_that_fails_at_the_transport_level_as_readily_as_one_it_404s`, `Holds_no_page_when_the_breaker_opened_before_a_page_was_read`, `Holds_nothing_for_a_run_the_budget_stopped_rather_than_the_archive`, `Bounds_the_transport_failure_route_over_consecutive_runs_through_the_worker` |

**B4 and the `Cap` stop reason.** The page ceiling and the request budget report the same string, and
only `ScrapeRun.HitRequestCap` tells them apart — false on a ceiling stop. Reading the run history,
"cap" with the flag clear is the only trace a pagination bug leaves. Low consequence, deliberately
not queued; recorded here so the next reader of a `cap` run knows the two are distinct.

**B18 is the only rule here that decides what to ask from the *run history* rather than from the
page in hand.** It is deliberately not a conclusion: B6, B7 and B11 all leave a stuck incremental
pass rebuilding the same two requests every tick, correctly refusing to conclude anything from a
page that did not answer — and B18 spends fewer requests on that refusal without softening it. The
streak is read out of `ScrapeRuns` rather than counted into a column on the ship, so one healthy run
clears it with nothing to remember to reset. §G's question — *which pass is entitled to conclude
this* — has the answer "none, and it does not"; what B18 changes is only the cost of not concluding.

**B18 covers all three entrances since T81 (2026-08-28).** Its streak counts a run whose stop reason
is `Error`, `Held` or `Breaker`. `Error` is B7's 404 and B11's unreadable page; `Breaker` is B5 —
the transport failure, the one rule in the walk that re-asks a URL — which reaches the same stuck
state by a third road, leaving `LastPageFetched` on the same page but stopping for a different
reason. That variant costs 1 + `MaxConsecutiveFailures` requests a tick against the `Error` route's
two, so it was the dearest of the three and the only one the bound could not see. It shipped with
**T52** (§F1), the same fact one column over: the walk counting a `Breaker` run as stuck while the
run history called it a success would have been the two disagreeing about one row.

`Breaker` is the **only** budget stop the streak counts. `Cap` and `TimeCap` are runs that spent an
allowance, and a run that stopped before page N says nothing about page N — counting them would hold
a page over runs that never asked for it.

**B5 is the only rule in the walk that re-asks a URL**, and it is the one the breaker exists for. Its
neighbour B6 does not, by T23's ruling; the difference is that a response is AO3's answer and a
timeout is not.

## C. What a page carrying no works may conclude — `PlausiblyTheEndOfTheListing`

The conclusion this gates is the strongest one either pass can reach: `LastPage`, which for a
backfill is `Complete` and which nothing later revisits.

| # | Evidence that the listing did **not** end here | Where | Applies to | Pinned by |
|---|-----------------------------------------------|-------|------------|-----------|
| C1 | No listing container in the document — not a results page at all | `:645` | every page | `Refuses_to_conclude_from_a_response_that_carries_no_listing_at_all`, `Still_refuses_a_filtered_page_that_carries_no_listing_at_all` |
| C2 | `page > 1` on an unfiltered listing — the walk only got here because a page advertised more | `:646` | unfiltered | `Still_refuses_an_empty_page_past_the_first_when_the_pass_was_not_filtered`, `Refuses_to_conclude_from_an_empty_page_a_resumed_backfill_started_on` |
| C3 | …waived on a **filtered** page past the first, but only where the heading says the run was served everything (`matched <= blurbsRead`) | `:646`, `:670` | filtered, page > 1 | `Ends_a_filtered_pass_on_an_empty_page_whose_heading_agrees_it_was_served_everything`, `Refuses_an_empty_filtered_page_whose_heading_counts_more_than_the_run_was_served`, `Refuses_an_empty_filtered_page_that_carries_no_heading_at_all` |
| C4 | A Next link — the page says there is more after it | `:647` | every page | `Still_refuses_an_empty_filtered_page_that_offers_a_next_one` |
| C5 | An **unfiltered** heading counting works the blurbs do not contain | `:648` | unfiltered | `Refuses_to_call_a_backfill_complete_when_a_page_parses_to_no_works_under_a_populated_heading` |
| C6 | `WhyNotTheEnd` names which of the above failed, for the run history | `:680` | every page | `Says_which_evidence_made_a_page_unreadable_rather_than_one_sentence_for_all_of_them` |
| C7 | **Page 1 waives C2 and C3 outright** — the short-circuit, and the last unexamined clause | `:646` | every page 1 | `Still_treats_an_empty_first_page_as_an_empty_tag`, `Reads_a_quiet_filtered_pass_with_a_populated_heading_as_nothing_new` |

**C7 is where the two open questions in this method live.**

- *Filtered page 1 carrying a heading that counts more than the run was served* concludes `LastPage`
  with a null error message: the ship ingests nothing and the run is recorded a clean success, every
  tick. This is exactly the contradiction C3 now refuses one page later. Queued as **T50**, whose
  notes carry the argument on both sides.
- *Unfiltered page 1 with the container, no works, no Next link and **no heading at all*** concludes
  `LastPage` too — and for a backfill that is `Complete`, the whole back catalogue written off from
  the absence of every piece of evidence. C3's rule for filtered pages is that no evidence is not
  permission (T47); page 1 has the opposite rule, on the premise that AO3 renders the container for a
  genuinely empty tag. That premise was **T39's**, and **T39 settled the premise on 2026-08-27**
  without closing this row: the capture shows a zero-result index rendering the container *and* a
  `0 Works in <tag>` heading. What that buys is the *freedom to fix this* — requiring a heading on
  page 1 can no longer strand a genuinely empty tag, because a genuinely empty tag has one. The code
  is unchanged: `page == 1` still short-circuits before any heading is read, so an unfiltered page 1
  with no heading at all still concludes `LastPage` and, for a backfill, `Complete`. **C7 stays open
  and the fix is T80.**

## D. What is written back to the ship, and what a later pass may believe

| # | Rule | Where | Passes | Concludes | Pinned by |
|---|------|-------|--------|-----------|-----------|
| D1 | The cursor advances to `page + 1` only after that page's works are committed | `:458` | backfill | a crash resumes on the page that was in flight, never after it | `Walks_forward_through_the_back_catalogue`, `Saves_a_cursor_a_capped_run_can_resume_from` |
| D2 | A retreat moves the **stored** cursor back one, carrying the question into the next run | `:545` | backfill | retreating can never skip anything — it only re-reads | `Completes_a_backfill_whose_cursor_the_shrunken_listing_404s_past` |
| D3 | Two unanswerable pages in a row halve the cursor | `:570` | backfill | converges logarithmically, so the stall allowance bounds a broken listing rather than a big one | `Halves_the_cursor_when_the_page_before_it_does_not_answer_either` |
| D4 | `LastPage` → `Complete` | `:830` | backfill | the back catalogue has been read — nothing revisits this | `Marks_the_backfill_complete_when_it_runs_out_of_pages` |
| D5 | The stalled streak is cleared by forward progress or `Complete`, and by nothing else | `:846` | backfill | reading *a* page is not progress; a retreat to page 1 that failed has learned nothing | `Clears_a_stalled_streak_as_soon_as_the_backfill_moves_forward_again` |
| D6 | A run the archive told nothing (`!askedStaleCursor && firstPage is null`) does not count as stalled | `:857` | backfill | an outage must not cost a back catalogue | `Does_not_retire_a_ship_because_a_retreat_was_cut_short_by_a_passing_failure` — **gap: T46**, a 404 at page 1 *is* the archive answering and still does not count |
| D7 | 12 stalled runs → `Failed`, incremental pass continues | `:861` | backfill | gives up on the back catalogue, never on the ship | `Gives_up_on_a_backfill_that_spends_run_after_run_on_a_cursor_nothing_answers` — **gap: T38**, nothing can leave `Failed` and the counter is frozen there |
| D8 | Only a run whose `firstPage == 1` may propose a watermark | `:906` | both | only page 1 holds the newest work in the tag | `Refuses_a_watermark_from_a_backfill_resumed_below_the_newest_page` — **gap: T40**, an unreadable page sets `firstPage` before the break |
| D9 | An incremental pass may propose only on `Watermark`/`LastPage`; a backfill from page 1 may propose however it stopped | `:920` | both | an early stop leaves works between the watermark and the newest reading unvisited, and nothing looks back | `Does_not_advance_the_watermark_on_a_run_that_ran_out_of_budget`, `Sets_a_watermark_from_page_1_even_when_the_run_stops_on_the_cap`, `Leaves_the_watermark_alone_when_a_page_fails` |
| D10 | The watermark never moves backwards | `:927` | both | a backstop for a rule proved wrong later | `Leaves_a_multi_run_backfill_with_the_watermark_its_first_page_proposed` |
| D11 | Only an **unfiltered** page may write `LastKnownTotalWorks`, and only a readable one | `:777`, `:320` | both | the heading counts whatever result set the request produced (T29) | `Records_the_tags_total_for_the_ship`, `Leaves_the_tags_total_alone_when_the_listing_was_filtered`, `Does_not_let_a_page_it_could_not_read_write_the_tags_total` |
| D12 | The full sweep refreshes the total, and **nothing reads it as a count** | `RecordTotal` via the sweep's unfiltered pages | sweep | the stored number is the last unfiltered pass's, and a sweep is the only later unfiltered pass a ship with a watermark gets | **rule written by T15** (2026-08-28): the sweep concludes from its own complete walk, never from a comparison against this number — which it has itself just written. What it does read beside it is `LastKnownTotalWasAuthenticated`; see E7 |
| D13 | `LastKnownTotalWasAuthenticated` is assigned by whichever **request** read the total, never latched | `RecordTotal` | both | the pair travel together or they say something no request made (T30, one scope further in by T44) | `Does_not_stamp_a_total_it_never_wrote_as_Authenticated`, `Clears_the_Authenticated_flag_when_a_later_run_reads_the_total_anonymously`, `Reads_the_transport_for_whether_the_total_was_Authenticated`, `Does_not_let_a_later_pages_session_stamp_a_total_read_anonymously`, `Takes_the_Authenticated_flag_from_the_last_page_that_wrote_the_total` — **gap closed by T44** (2026-08-25) |
| D14 | A restricted work on an unauthenticated response is **reported and never acted on** | `:353` | both | a proxy signal may not overrule the transport (T30) | `Does_not_let_a_restricted_blurb_claim_the_total_was_Authenticated` |
| D15 | `BackfillMinUpdatedAtSeen` tracks the oldest reading; a boundary that moves *up* is logged, not acted on | `:792` | backfill | the listing shifted under the walk and only a sweep can close it | non-monotonic branch unpinned — **T32** owns the message it prints |
| D16 | `LastIncrementalRunAt` is stamped on every incremental run whatever it concluded | `:887` | incremental | when the pass last ran, not what it achieved | unpinned — see the foot of §F |

**D12 was the row this table existed to produce, and T15 answered it.** The sweep refreshes the
number because its pages are unfiltered, and reads it as a count for nothing — see the DECISIONS
entry of 2026-08-28 for why a comparison would not have been a check. The paragraph below is left as
the statement of the problem it was.

**D12 as it stood.** Nothing is wrong today; nothing decides it either.
A ship that finished its backfill and has a watermark sends only filtered requests, and a filtered
heading may not write the total (D11) — so `LastKnownTotalWorks` freezes at whatever the last
unfiltered run read, while the tag goes on growing. The field is documented as the figure a full
sweep checks itself against before concluding works have left the tag. T15 is therefore both the
only thing that would refresh it and the only thing that reads it; if the sweep is to trust the
stored number rather than its own, this is the rule that has to be written.

## E. What the ingestor may erase

An ingest is the write side of every rule above, and it is where a *partial* observation can
destroy a complete one.

| # | Rule | Where | Concludes | Pinned by |
|---|------|-------|-----------|-----------|
| E1 | `Reconcile` deletes every join row the blurb does not carry | `WorkIngestor.cs:235` | an author who removed a tag or dropped a co-creator must stop being recorded | `Drops_a_tag_the_author_has_removed` |
| E2 | A byline that could not be read (`IsAnonymous is null`) writes neither the column nor the author rows | `:142`, `:189` | an unread signal is not a claim (T26) | `An_unreadable_byline_leaves_the_authors_an_earlier_pass_read` |
| E3 | A byline that says "Anonymous" **does** reconcile to the empty set | `:189` | a work that really lost its creators must lose them here too | `A_work_that_becomes_anonymous_loses_the_authors_it_had` |
| E4 | An unreadable date leaves whatever a previous run stored, and marks the row approximate | `:124` | overwriting a real timestamp with `MinValue` would drag the watermark backwards | first-seen case only (`Marks_a_work_an_incremental_pass_first_saw_undated_as_having_an_approximate_date`); the **preservation** branch is unpinned — **gap, T54** |
| E5 | Statistics are written unconditionally, never gated on `UpdatedAt` having moved | `:110` | kudos move without a revision, so gating would freeze these columns | `Rewrites_the_statistics_of_a_work_it_has_seen_before` |
| E6 | Appearing in a listing clears `IsDeleted`/`DeletedAt` | `:147` | presence is proof; it undoes a deletion recorded earlier | — **still nothing sets them true, and it is not the sweep's to set.** T15 concluded absence from a *tag*, which is `ShipWork.MissingSinceAt` (E7); a work being gone from AO3 is a 404 on its own page, which is T10's fetch |
| E7 | Appearing in a listing clears `ShipWork.MissingSinceAt` | `:167` | the one direction safe on a partial pass | **closed by T15** (2026-08-28): `Ao3ShipIndexScraper.ConcludeSweepAsync` sets it, from a completed sweep only, for rows whose `LastSeenAt` predates the sweep's start. `Marks_a_work_the_completed_sweep_did_not_see_as_having_left_the_tag`, `Concludes_nothing_from_a_sweep_that_ran_out_of_budget_part_way`, `A_work_that_comes_back_stops_being_missing` |
| E8 | Tags are reconciled from **a listing blurb** | `:172` | the blurb's tag set is the work's whole tag set | `Drops_a_tag_the_author_has_removed` — **gap: T51.** The spec says a blurb does not carry the complete tag list; T10 fetches the rest from the work's own page, and the next incremental pass then deletes it |
| E9 | Two blurbs for one work on one page keep the last | `:53` | AO3 can render a work twice and the `(ShipId, WorkId)` key must stay satisfiable | unpinned — see the foot of §F |

**E7 is now built; E6 is not, and is not the sweep's.** Both directions of "a work left
this tag" exist as columns; only the clearing direction has a rule. That is correct as it stands —
neither pass below is entitled to set them — but it means T15 is writing the *first* code that ever
concludes absence, with the reading side of it already shipped and every test in the suite proving
only the clear.

**E8 is the one live cross-task hazard this audit found.** `ApplyTags` reconciles a work's tags
against `blurb.Tags`, which is the listing blurb's list. The spec's own user story 11 says the
listing blurb does not carry the complete tag list, and T10 exists to fetch what it lacks. Under
that premise, every detail fetch is undone by the next incremental pass over the same ship — the
work loses the tags only the detail page carried, silently, on a run recorded as a success. Whether
the premise holds is a markup question of T39's family; either way T10 cannot be built until this
rule says which observation wins. Queued as **T51** and added to T10.

## F. What the run history is told

The run history is the only place this worker reports itself. A wrong entry here is not cosmetic:
it is the difference between an operator seeing a stuck ship and seeing a green row.

| # | Rule | Where | Concludes | Pinned by |
|---|------|-------|-----------|-----------|
| F1 | `ScrapeStopReason.RecordsAsFailure` → `Failed`; every other stop → `Succeeded` | `ScrapeWorker.cs:339` | returning is how a scraper reports most failures, so those must not read as fine | `A_run_the_scraper_reported_an_error_for_is_recorded_as_failed`, `A_run_that_stopped_normally_is_still_recorded_as_succeeded`, `A_run_the_circuit_breaker_stopped_is_recorded_as_failed`, `A_run_that_spent_its_request_budget_is_still_recorded_as_succeeded` — **T52 closed 2026-08-28** |
| F2 | `NextRunAt` is advanced in a `finally`, however the run ended | `:302` | a job that failed must not be due again on the next minute-poll | `A_job_whose_save_fails_is_recorded_as_failed_and_rescheduled` |
| F3 | A held job records nothing at all — no run, no reschedule, no breaker | `:132` | it was not attempted, so it has not failed | `Holds_a_due_job_when_no_ao3_login_is_stored`, `Runs_the_held_job_on_the_next_poll_once_a_login_is_saved` |
| F4 | A run whose results cannot be saved is re-recorded `Failed` through a fresh context | `:322` | a run that could not be written is not a run that succeeded (T27) | `A_job_whose_save_fails_is_recorded_as_failed_and_rescheduled` |
| F5 | One scope per job | `:175` | one ship's rejected change set must not fail every ship behind it (T27) | `Each_job_in_a_poll_gets_its_own_scope`, `A_failed_job_does_not_skip_the_rest_of_the_poll` |
| F6 | Runs `Running` past 30 minutes are `Interrupted` at startup | `:88` | a backfill legitimately runs for hours, so no read-time timeout could tell the two apart | unpinned, below |
| F7 | `pagesFetched`, `firstPage` and `lastPage` are set **before** the unreadable break | `:309` | — | **gap: T40**, a run claims to have read a page it could not read — closed 2026-08-27, the four counters now sit below the break |
| F8 | A `Held` run is recorded `Failed`, like an `Error` | `ScrapeWorker.cs:339` | it read and ingested what it reached, but it did not get through the listing, and the run history is the only place a headless worker reports itself (T45) | `A_run_that_held_a_page_rather_than_asking_for_it_is_recorded_as_failed` |

**F1 was the gap worth a task, and T52 closed it on 2026-08-28.** `Breaker` is the one budget stop
that means *the archive was failing*: three consecutive refused or timed-out requests, spaced by the
5–8s gate, and the run stops. It was not `Error`, so the run was recorded **Succeeded** with no
error message beside it, and an operator watching the Schedules page saw green while a ship
collected nothing. It is now `Failed`, and the walk writes a message naming the page every one of
those requests was for. `BreakerOpen` still has no column of its own beside `HitRequestCap` and
`HitTimeCap`, deliberately — `StopReason` already carries the fact and a column would be a second
copy of it; see `DECISIONS.md`.

**Rules with no test, graded.** Queued: A1 (**T53** — the mode choice three comments depend on),
E4's preservation branch (**T54** — the rule that keeps a watermark from being dragged backwards).
Deliberately not queued, and listed here so the next reader knows the grading was done rather than
missed: B4 (the page ceiling, whose only consequence is an ambiguous `cap` in the run history), D16
(`LastIncrementalRunAt`, a timestamp nothing branches on), E9 (per-page dedup, which fails loudly on
a unique key if it breaks), F6 (a startup path with no clock seam — `ReconcileInterruptedRunsAsync`
reads `DateTime.UtcNow` directly rather than the injected `TimeProvider`, so pinning it means a
refactor, and its failure mode is a stale row in a history view).

## G. Rules about the rules

Five generalisations, each paid for by a defect this loop shipped and then fixed. They are rows in
their own right because every one of them was true of code that read as correct at the time.

| # | Rule | Paid for by | What it looks like in this file |
|---|------|-------------|--------------------------------|
| G1 | **A waiver is only as good as the evidence it substitutes, and may not fire on a page carrying neither piece.** No evidence is not permission | T47 | C3. The waiver replaces `page > 1` with a heading; a page with no heading keeps `page > 1` |
| G2 | **When a rule is waived because its argument does not hold, ask what that rule was *carrying*, not only whether it was sound.** | T42 | C2 was carrying "the listing says there is more", which a filtered listing says with a number instead of a Next link |
| G3 | **Read the fixture, not the test name.** A fix and its test are written in the same sitting by the same reasoning, so a wrong premise produces a test that agrees with it | T42, T43, T47, and open in T50 | Three tests in this suite have now been deleted for constructing a different situation from the one their comment cited. The fourth is `Reads_a_quiet_filtered_pass_with_a_populated_heading_as_nothing_new`, which builds `Page(1, [], total: 4317)` and is named for a quiet pass |
| G4 | **When a fix turns one signal into a conclusion, ask what else in the same document can produce that signal.** | T26, and open in T48 | E2/E3. One `h4.heading` holds a title, four people's names and a gift recipient |
| G5 | **A proxy signal keeps looking sound right up until the real signal arrives beside it — and then it is only ever a way to disagree with it.** | T30 | D14. The lock symbol was a proxy for a session; `response.Authenticated` is the session |

G3 is the only one of the five that is about tests rather than rules, and it is the one with an open
instance. Every remaining stopping defect in the queue — T38, T40, T45, T46, T50 — announces itself
in the run history when it fires. None of them is silent. That is the state this audit found the
walk in, and it is what makes T15 buildable next: the sweep will be the third pass over this
listing, and the rules it inherits are now written down rather than inferred.

## H. Gaps found, and where they went

| Gap | Row | Went to |
|-----|-----|---------|
| A listing blurb's tag list is reconciled as if it were complete, so T10's detail fetch is undone by the next pass | E8 | **T51** (new), and T10's `blocked-by` |
| A run stopped by the circuit breaker is recorded `Succeeded` | F1 | T52 — **done 2026-08-28**, with T81 in one diff |
| Which pass a ship gets is pinned by no test | A1 | **T53** (new) |
| The ingestor's unreadable-date preservation is pinned by no test | E4 | **T54** (new) |
| An unreadable page is counted as a page that was read, which also feeds D8 | D8, F7 | T40, added to T15's `blocked-by` |
| The authenticated-total flag ORs across a run whose total is written per page | D13 | T44 — **done 2026-08-25**; the flag is written inside `RecordTotal` from the response the heading came off |
| Nothing refreshes a watermarked ship's tag total | D12 | T15's notes — its sweep is both the only refresher and the only consumer |
| Filtered page 1 accepts the contradiction page 2 refuses | C7 | T50 (already queued) |
| Unfiltered page 1 with no heading may complete a backfill | C7 | **T80** (new) — T39's capture removed the reason not to fix it, but the code is unchanged and still concludes |
| A 404 at page 1 never counts as a stalled run; a denied tag reports `LastPage` | D6, A2 | T46's notes |
| A page failing at the transport level stops with `Breaker`, so T45's bound never sees it | B18, F1 | T81 — **done 2026-08-28**, with T52 in one diff |
| Two tasks describe the same CA2017 warning | — | T41 folded into T49; see `DECISIONS.md` |

---

**Verification.** Every test named above was checked against the suite's own discovery output
(`dotnet test --list-tests`, 391 tests) rather than against memory or the source, and each matches
at least one test. Rows whose "pinned by" column says *unpinned*, *no rule*, or *gap* name no test
on purpose — those are the findings.
