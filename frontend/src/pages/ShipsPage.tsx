import { Fragment, useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import type { WatchedShip } from '../api/types';
import { EmptyState } from '../components/EmptyState';
import { Icon } from '../components/Icon';
import { SkeletonRows } from '../components/Skeleton';
import { formatDate as formatDay, formatDateTime, formatDateTimeOrDash } from '../format';

const BACKFILL_LABELS: Record<WatchedShip['backfillState'], string> = {
  NotStarted: 'Not started',
  InProgress: 'Reading the back catalogue',
  Complete: 'Back catalogue read',
  Failed: 'Back catalogue given up on',
};

const formatDate = formatDateTimeOrDash;

interface Status {
  label: string;
  /**
   * How loudly to say it. `error` is for a state in which nothing will ever happen for this ship;
   * `warning` for one that is degraded but still running — a given-up backfill leaves the
   * incremental pass collecting new works, so calling it an error would overstate it.
   */
  tone?: 'error' | 'warning';
  detail?: string;
}

/**
 * The page a restart should resume from.
 *
 * Not the cursor. A stalling walk steps its cursor backwards once a run looking for a page the
 * listing will answer for, so by the time a ship is written off the cursor is usually far below the
 * pages AO3 has already served — and resuming there would have this instance ask for every one of
 * them again at 5-8 seconds apiece. `backfillResumePage` is the number nothing but a restart lowers.
 * Re-reading that one page is deliberate: it is the page whose next link points into listing nobody
 * has walked.
 */
function restartPage(ship: WatchedShip): number {
  return ship.backfillResumePage ?? ship.backfillNextPage ?? 1;
}

/**
 * What is actually happening to this ship. Ordered by what blocks what: a tag AO3 has never
 * confirmed can't be scraped, so its verification state outranks anything the schedule says.
 */
/**
 * Whether an admin restarting this ship's backfill would achieve anything.
 *
 * The same two conditions `describeStatus` returns early on, and for the same reason: a tag AO3 has
 * denied is skipped before the scraper's first request, and an unscheduled ship is never picked up
 * at all — so a restart on either leaves a row reading "in progress" that no run will ever touch.
 * The endpoint refuses both; this keeps the button from being offered under a status line that
 * already says the ship is going nowhere.
 */
function canRestartBackfill(ship: WatchedShip): boolean {
  return (
    ship.backfillState === 'Failed' &&
    ship.verificationState !== 'NotFoundOnAo3' &&
    ship.isScheduled
  );
}

/**
 * Whether an admin asking for a full re-read of this ship would be accepted and would ever run.
 *
 * The endpoint's refusals, mirrored so the button is only offered where it does something: a denied
 * or unscheduled ship is never walked, a ship still reading its back catalogue is walking the whole
 * listing already, and one being re-read or already queued has nothing more to ask for.
 */
function canQueueSweep(ship: WatchedShip): boolean {
  return (
    ship.verificationState !== 'NotFoundOnAo3' &&
    ship.isScheduled &&
    (ship.backfillState === 'Complete' || ship.backfillState === 'Failed') &&
    ship.fullSweepNextPage === null &&
    ship.fullSweepRequestedAt === null
  );
}

/**
 * The full sweep and the monthly re-read of recent works, in one line — or null for a ship that has
 * had neither.
 *
 * Beside the status rather than inside it, because the two are independent: a ship whose back
 * catalogue was given up on can be mid-sweep, and a single status slot would have to drop one of
 * those to say the other. Either walk in flight beats the incremental pass on every tick until it
 * reaches the end of what it asked for, which can leave a ship collecting no new works for days —
 * and nothing outside the run history would otherwise say so.
 */
function describeSweep(ship: WatchedShip): string | null {
  if (ship.fullSweepNextPage !== null) {
    return (
      `Re-reading the whole listing to find works that have left the tag — next up is page ` +
      `${ship.fullSweepNextPage}. Its pass for new works waits until this finishes.`
    );
  }

  if (ship.fullSweepRequestedAt !== null) {
    return (
      'A full re-read of the listing is queued and starts at this ship’s next check. Its pass for ' +
      'new works waits until that finishes.'
    );
  }

  // A full sweep beginning puts a re-read in flight away, so the two never walk at once.
  if (ship.recentSweepNextPage !== null) {
    const since =
      ship.recentSweepFrom !== null ? `since ${formatUtcDay(ship.recentSweepFrom)}` : 'recently';
    return (
      `Re-reading works updated ${since} to find any that have left the tag — next up is page ` +
      `${ship.recentSweepNextPage}. Its pass for new works waits until this finishes.`
    );
  }

  const history = [describeLastFullSweep(ship), describeLastRecentSweep(ship)].filter(
    (line) => line !== null,
  );
  return history.length > 0 ? history.join(' ') : null;
}

function describeLastFullSweep(ship: WatchedShip): string | null {
  const started = ship.lastFullSweepStartedAt;
  if (started === null) return null;

  const completed = ship.lastFullSweepCompletedAt;

  // A sweep only writes its completion alongside a conclusion, and its start is left standing when
  // it is abandoned — so a start newer than the last completion is a walk that did not get there.
  if (completed !== null && new Date(completed) >= new Date(started)) {
    return `Listing last re-read in full on ${formatDateTime(completed)}.`;
  }

  // A ship whose every page has been read logged in is scheduled no sweep at all, so the interval
  // sentence below would promise it one that never comes.
  if (ship.wholeListingReadLoggedInAt !== null) {
    return (
      `A full re-read of the listing started ${formatDateTime(started)} and did not finish; ` +
      'none is scheduled for a ship already read in full, so another runs only if an admin queues one.'
    );
  }

  return (
    `A full re-read of the listing started ${formatDateTime(started)} and did not finish; ` +
    'the next one is due an interval after that, not after this ship next runs.'
  );
}

function describeLastRecentSweep(ship: WatchedShip): string | null {
  const started = ship.lastRecentSweepStartedAt;
  if (started === null) return null;

  const completed = ship.lastRecentSweepCompletedAt;
  if (completed !== null && new Date(completed) >= new Date(started)) {
    return `Recent works last re-read on ${formatDateTime(completed)}.`;
  }

  // The next re-read is spaced from the latest walk of any kind, which is never earlier than this one.
  return (
    `A re-read of recently updated works started ${formatDateTime(started)} and did not finish; ` +
    'the next is due no sooner than a month after that.'
  );
}

/**
 * A UTC midnight as the day it names. Through the browser's zone it would read as the day before
 * anywhere west of Greenwich.
 */
function formatUtcDay(value: string): string {
  const day = new Date(value);
  return formatDay(new Date(day.getUTCFullYear(), day.getUTCMonth(), day.getUTCDate()));
}

/**
 * Whether this ship's library can be trusted to hold the works AO3 shows only to registered users.
 *
 * AO3 leaves those works out of a listing it serves to a logged-out request, and the pass for new
 * works only ever reads the newest end of the listing, so a walk that read a page logged out leaves
 * a gap nothing else fills. Null while the first walk is still under way: saying "not yet" about a
 * walk that is happening adds nothing to a status that already says it is happening.
 */
function describeCoverage(ship: WatchedShip): string | null {
  if (ship.wholeListingReadLoggedInAt !== null) {
    return `Every page last read logged in on ${formatDateTime(ship.wholeListingReadLoggedInAt)}.`;
  }

  if (ship.backfillState === 'NotStarted' || ship.backfillState === 'InProgress') return null;

  return (
    'Not yet read in full while logged in, so works only registered users can see may be missing ' +
    'until a full re-read.'
  );
}

/**
 * The one thing about a ship worth seeing without opening its row, or null.
 *
 * A re-read in flight or queued comes first: it is what explains a ship collecting no new works,
 * and it is also the fix for the gap the coverage line would otherwise report. A ship that is going
 * nowhere gets nothing here — its status already says so, louder.
 */
function summarizeRow(ship: WatchedShip, status: Status): string | null {
  if (status.tone === 'error') return null;
  if (ship.fullSweepNextPage !== null) return `Re-reading, page ${ship.fullSweepNextPage}`;
  if (ship.fullSweepRequestedAt !== null) return 'Re-read queued';
  if (ship.recentSweepNextPage !== null) {
    return `Re-reading recent works, page ${ship.recentSweepNextPage}`;
  }
  if (ship.wholeListingReadLoggedInAt === null && describeCoverage(ship) !== null) {
    return 'Not fully read logged in';
  }
  return null;
}

function describeStatus(ship: WatchedShip, verificationEnabled: boolean): Status {
  if (ship.verificationState === 'NotFoundOnAo3') {
    // Not "add it again": following the same name resolves to this same denied ship, which leaves
    // its schedule off and is never re-checked — so the old advice sent everybody down a path that
    // provably does nothing. Following the *right* name is a different tag and does work.
    return {
      label: 'AO3 has no such tag',
      tone: 'error',
      detail:
        'Check the spelling — following the same name again lands back here. If the tag was ' +
        'renamed or briefly gone, an admin can have AO3 asked again from this page.',
    };
  }

  if (ship.verificationState === 'Pending') {
    return verificationEnabled
      ? { label: 'Checking with AO3…', detail: ship.verificationError ?? undefined }
      : { label: 'Check paused', detail: 'This instance has no operator contact, so it cannot reach AO3.' };
  }

  if (!ship.scraperAvailable) return { label: 'Tag confirmed — waiting for the AO3 scraper' };
  if (!ship.isScheduled) return { label: 'Paused' };

  const label = BACKFILL_LABELS[ship.backfillState];
  const page = ship.backfillNextPage ?? 1;

  // Said only where the cursor is *behind* what was read, which is exactly the retreat this line
  // exists to make visible. A forward walk leaves the cursor one past the deepest page and a retreat
  // can land it exactly on it; neither is a walk that lost ground, and saying the same page twice
  // would just make the ordinary row longer.
  const readTo =
    ship.backfillResumePage != null && ship.backfillResumePage > page
      ? ` It had read as far as page ${ship.backfillResumePage}.`
      : '';

  // Both of these lived only in the run history, which is a different page. From here a ship stuck
  // on a page AO3 will not answer has looked exactly like one quietly working through its listing.
  //
  // Neither says the cursor is the page AO3 refused, because it usually isn't: a stalling walk steps
  // its cursor backwards once a run looking for a page that answers, so by the time it gives up the
  // number below is where it ended up, not what went wrong.
  if (ship.backfillState === 'Failed') {
    return {
      label,
      tone: 'warning',
      detail:
        `${ship.backfillStalledRuns} runs in a row got nothing AO3 would answer, so this instance ` +
        `stopped asking; its cursor is at page ${page}.${readTo} New works still arrive; the older ` +
        'ones are on hold.',
    };
  }

  if (ship.backfillState === 'InProgress' && ship.backfillStalledRuns > 0) {
    return {
      label,
      tone: 'warning',
      detail:
        `${ship.backfillStalledRuns} ` +
        `${ship.backfillStalledRuns === 1 ? 'run has' : 'runs in a row have'} got no further ` +
        `through the listing; its cursor is at page ${page}.${readTo} It keeps trying for a while yet.`,
    };
  }

  return { label };
}

export function ShipsPage() {
  const { user } = useAuth();
  const [ships, setShips] = useState<WatchedShip[] | null>(null);
  const [verificationEnabled, setVerificationEnabled] = useState(true);
  // Assumed present until the server says otherwise, so a slow load never flashes a banner saying
  // scraping is broken at someone whose instance is fine.
  const [ao3LoginConfigured, setAo3LoginConfigured] = useState(true);
  const [tagName, setTagName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [removing, setRemoving] = useState<number | null>(null);
  // Several at once, unlike Schedules: nothing is fetched on opening, and an admin working through
  // a few written-off ships wants to compare them.
  const [openShipIds, setOpenShipIds] = useState<ReadonlySet<number>>(new Set());

  const toggleOpen = (shipId: number) =>
    setOpenShipIds((current) => {
      const next = new Set(current);
      if (!next.delete(shipId)) next.add(shipId);
      return next;
    });

  const load = () =>
    api.getWatchedShips().then((response) => {
      setShips(response.ships);
      setVerificationEnabled(response.verificationEnabled);
      setAo3LoginConfigured(response.ao3LoginConfigured);
    });

  useEffect(() => {
    load().catch(() => setError('Failed to load the ships you follow.'));
  }, []);

  // Verification happens in the background a moment after following, and a rename or merge changes
  // what the row says — so the list refreshes itself while anything is still unresolved, and stops
  // once nothing is.
  const anyPending = ships?.some((ship) => ship.verificationState === 'Pending') ?? false;
  useEffect(() => {
    if (!anyPending || !verificationEnabled) return;

    const timer = setInterval(() => void load().catch(() => {}), 5000);
    return () => clearInterval(timer);
  }, [anyPending, verificationEnabled]);

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();

    const trimmed = tagName.trim();
    if (trimmed === '') return;

    setError(null);
    setSubmitting(true);
    try {
      await api.watchShip(trimmed);
      await load();
      setTagName('');
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to follow that tag.');
    } finally {
      setSubmitting(false);
    }
  };

  const remove = async (ship: WatchedShip) => {
    setError(null);
    setRemoving(ship.shipId);
    try {
      await api.unwatchShip(ship.shipId);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to stop following that tag.');
    } finally {
      setRemoving(null);
    }
  };

  return (
    <div className="page">
      <h1>Ships</h1>

      <p className="hint">
        Follow a relationship tag and this instance checks it for everyone who follows it — once,
        not once per person. Enter the tag exactly as AO3 writes it, e.g.{' '}
        <code>Clarke Griffin/Lexa</code> for a romantic pairing or <code>Sam Winchester &amp; Dean
        Winchester</code> for a platonic one.
      </p>

      <form className="inline-form" onSubmit={(e) => void onSubmit(e)}>
        <label className="ship-tag-field">
          Relationship tag
          <input
            name="tag"
            autoComplete="off"
            spellCheck={false}
            value={tagName}
            onChange={(e) => setTagName(e.target.value)}
            placeholder="Clarke Griffin/Lexa"
            maxLength={200}
          />
        </label>
        <button type="submit" disabled={submitting || tagName.trim() === ''}>
          {submitting ? 'Adding…' : 'Follow'}
        </button>
      </form>

      <p className="hint">
        Tags are accepted straight away and checked against AO3 a moment later — the check needs a
        real request, and those are deliberately spaced out. If AO3 doesn’t have the tag, or knows
        it by another name, this page will say so.
      </p>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      {ships === null ? (
        // Guarded on the error: a failed load has no rows on the way, and placeholder rows under
        // the message would promise some.
        !error && <SkeletonRows rows={3} kind="line" />
      ) : ships.length === 0 ? (
        <EmptyState title="You aren’t following any ships yet">
          Follow a relationship tag above and this instance starts checking it for you.
        </EmptyState>
      ) : (
        <>
          {/* Every user, not only admins: whoever is looking at an empty library is owed the
              reason, and a non-admin is told who can fix it rather than shown a form they cannot
              use. Said as a missing *login* — the session cookie is a cache, and its absence
              means nothing. */}
          {!ao3LoginConfigured && (
            <p className="callout callout-error">
              <strong>Nothing is being fetched.</strong> The ships you follow are scheduled, but
              this instance has no AO3 login saved, so every check is paused. Your library stays
              empty until one is saved.{' '}
              {user?.isAdmin
                ? 'Add it under System → AO3.'
                : 'Ask an admin of this instance to add one under System → AO3.'}
            </p>
          )}

          {!verificationEnabled && (
            <p className="callout callout-warning">
              Tags can’t be checked right now: this instance has no operator contact, so it isn’t
              allowed to contact AO3 at all. An admin can set one under System → AO3.
            </p>
          )}

          {ships.every((ship) => !ship.scraperAvailable) && (
            <p className="callout callout-warning">
              Nothing is being fetched yet. This build schedules the ships you follow but has no AO3
              source registered to run them, so work counts stay at zero until one lands.
            </p>
          )}

          <table className="ships-table">
            <thead>
              <tr>
                <th>Tag</th>
                <th className="numeric">Works</th>
                <th>Status</th>
                <th>Last checked</th>
                <th>Next check</th>
                <th className="numeric">Also followed by</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {ships.map((ship) => {
                const status = describeStatus(ship, verificationEnabled);
                const sweep = describeSweep(ship);
                const coverage = describeCoverage(ship);
                const note = summarizeRow(ship, status);
                // Admin-only, because a restart spends requests on behalf of everyone watching the
                // tag — and because it is the only thing in the product that moves a ship out of
                // Failed.
                const restart = user?.isAdmin === true && canRestartBackfill(ship);
                // Admin-only for the same reason: the re-read walks every page of the listing on
                // behalf of everyone who follows the tag.
                const queueSweep = user?.isAdmin === true && canQueueSweep(ship);
                // Admin-only for the same two reasons, and the only thing in the product that moves
                // a ship out of NotFoundOnAo3.
                const recheck =
                  user?.isAdmin === true && ship.verificationState === 'NotFoundOnAo3';
                const hasDetails =
                  status.detail !== undefined ||
                  sweep !== null ||
                  coverage !== null ||
                  restart ||
                  queueSweep ||
                  recheck;
                const open = hasDetails && openShipIds.has(ship.shipId);
                return (
                  // Keyed on the Fragment, not the <tr>: the fragment is the array element.
                  <Fragment key={ship.shipId}>
                    <tr>
                      <td className="ship-tag">
                        {/* Opens the sentences and repairs underneath. Every row used to carry
                            all of them at once, which turned a few ships into a page of prose. */}
                        {hasDetails ? (
                          <button
                            type="button"
                            className="link expander"
                            aria-expanded={open}
                            onClick={() => toggleOpen(ship.shipId)}
                          >
                            <Icon name="chevron-right" size="xs" className="expander-icon" />
                            {ship.tagName}
                          </button>
                        ) : (
                          <span className="ship-tag-name">{ship.tagName}</span>
                        )}
                        {/* Only set when AO3 disagreed with what was typed. Saying so is the whole
                            point — otherwise the tag someone entered silently becomes another one —
                            so it stays in the row rather than behind the chevron. */}
                        {ship.requestedTagName && (
                          <span className="ship-renamed">
                            You followed “{ship.requestedTagName}”; AO3 files it under this tag.
                          </span>
                        )}
                      </td>
                      <td className="numeric">{ship.workCount.toLocaleString()}</td>
                      <td>
                        <span className={status.tone}>{status.label}</span>
                        {note !== null && (
                          <span className="ship-status-detail ship-note">{note}</span>
                        )}
                      </td>
                      <td>{formatDate(ship.lastScrapedAt)}</td>
                      <td>{formatDate(ship.nextScrapeAt)}</td>
                      {/* Minus the reader, so "0" reads as "only you" rather than needing
                          subtraction. */}
                      <td className="numeric">{ship.watcherCount - 1}</td>
                      <td>
                        <button
                          type="button"
                          className="link"
                          disabled={removing === ship.shipId}
                          onClick={() => void remove(ship)}
                        >
                          {removing === ship.shipId ? 'Removing…' : 'Unfollow'}
                        </button>
                      </td>
                    </tr>
                    {open && (
                      <tr className="ship-details-row">
                        <td colSpan={7}>
                          <div className="ship-details">
                            {status.detail && <p>{status.detail}</p>}
                            {sweep !== null && <p>{sweep}</p>}
                            {coverage !== null && <p>{coverage}</p>}
                            {restart && (
                              <BackfillRestart
                                ship={ship}
                                onRestarted={() => void load().catch(() => {})}
                              />
                            )}
                            {queueSweep && (
                              <FullSweepQueue
                                ship={ship}
                                onQueued={() => void load().catch(() => {})}
                              />
                            )}
                            {recheck && (
                              <VerificationRecheck
                                ship={ship}
                                onRechecked={() => void load().catch(() => {})}
                              />
                            )}
                          </div>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>

          <p className="hint">
            Unfollowing only removes your subscription. Works already in the library stay, so following the tag
            again picks up where it left off instead of re-fetching everything from AO3.
          </p>
        </>
      )}
    </div>
  );
}

/**
 * The way back out of a written-off backfill.
 *
 * The page is editable rather than fixed because the two reasons a backfill gives up want different
 * answers: after an outage the deepest page read is exactly where to resume, and after a listing
 * that shrank it is the one page that will fail again. `restartPage` is the default, so the common
 * case is one click.
 *
 * Restarting does not make the ship due — it resumes on its next scheduled run, and saying so here
 * is what stops the button looking broken when nothing happens for an hour.
 */
function BackfillRestart({ ship, onRestarted }: { ship: WatchedShip; onRestarted: () => void }) {
  const [page, setPage] = useState(String(restartPage(ship)));
  const [restarting, setRestarting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (event: FormEvent) => {
    event.preventDefault();

    const parsed = Number(page);
    if (!Number.isInteger(parsed) || parsed < 1) {
      setError('A listing page is numbered from 1.');
      return;
    }

    setError(null);
    setRestarting(true);
    try {
      await api.restartBackfill(ship.shipId, parsed);
      onRestarted();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to restart the backfill.');
    } finally {
      setRestarting(false);
    }
  };

  return (
    <form className="ship-action" onSubmit={(e) => void submit(e)}>
      <label>
        Restart at page
        <input
          type="number"
          min={1}
          value={page}
          onChange={(e) => setPage(e.target.value)}
          disabled={restarting}
        />
      </label>
      <button type="submit" disabled={restarting}>
        {restarting ? 'Restarting…' : 'Restart'}
      </button>
      <span className="ship-status-detail">
        Picks up on this ship’s next scheduled check, not straight away.
      </span>
      {error && (
        <span className="error" role="alert">
          {error}
        </span>
      )}
    </form>
  );
}

/**
 * The way to fill in a listing that was read logged out, without waiting for the scheduled re-read.
 *
 * One button, like the recheck below: there is nothing about a full re-read for an admin to choose.
 * Like the restart above, it waits for the ship's next check rather than jumping the queue, and the
 * note beside it says so, so nothing happening straight away does not look like a broken button.
 */
function FullSweepQueue({ ship, onQueued }: { ship: WatchedShip; onQueued: () => void }) {
  const [queuing, setQueuing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const queue = async () => {
    setError(null);
    setQueuing(true);
    try {
      await api.queueFullSweep(ship.shipId);
      onQueued();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to queue a full re-read.');
    } finally {
      setQueuing(false);
    }
  };

  return (
    <div className="ship-action">
      <button type="button" disabled={queuing} onClick={() => void queue()}>
        {queuing ? 'Queuing…' : 'Re-read whole listing'}
      </button>
      <span className="ship-status-detail">
        Logged in, one page at a time, starting at this ship’s next check. New works for this ship
        wait until it finishes.
      </span>
      {error && (
        <span className="error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}

/**
 * The way back from a denial.
 *
 * A 404 is usually the typo the status line assumes, but it is also what a renamed tag and a tag
 * briefly withdrawn look like — and nothing re-asks on its own: verification returns before its
 * first request for a tag already settled, and the scraper skips the ship before its own. Following
 * the tag again does not help either, because it lands on the same denied row.
 *
 * One button and no options: it is a single request against a tag AO3 has already refused, and
 * there is nothing about it for an admin to choose. The row polls itself while the answer is
 * pending, so no refresh is needed to see it.
 */
function VerificationRecheck({ ship, onRechecked }: { ship: WatchedShip; onRechecked: () => void }) {
  const [rechecking, setRechecking] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const recheck = async () => {
    setError(null);
    setRechecking(true);
    try {
      await api.recheckVerification(ship.shipId);
      onRechecked();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to send that tag back for checking.');
    } finally {
      setRechecking(false);
    }
  };

  return (
    <div className="ship-action">
      <button type="button" disabled={rechecking} onClick={() => void recheck()}>
        {rechecking ? 'Sending…' : 'Check with AO3 again'}
      </button>
      <span className="ship-status-detail">
        For a tag that was renamed or briefly gone. Nothing is fetched until AO3 confirms it.
      </span>
      {error && (
        <span className="error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}
