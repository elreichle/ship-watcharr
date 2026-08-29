import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import type { WatchedShip } from '../api/types';

const BACKFILL_LABELS: Record<WatchedShip['backfillState'], string> = {
  NotStarted: 'Not started',
  InProgress: 'Reading the back catalogue',
  Complete: 'Back catalogue read',
  Failed: 'Back catalogue given up on',
};

function formatDate(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—';
}

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
        Follow a relationship tag and this instance scrapes it for everyone who follows it — once,
        not once per person. Enter the tag exactly as AO3 writes it, e.g.{' '}
        <code>Clarke Griffin/Lexa</code> for a romantic pairing or <code>Sam Winchester &amp; Dean
        Winchester</code> for a platonic one.
      </p>

      <form className="inline-form" onSubmit={(e) => void onSubmit(e)}>
        <label className="ship-tag-field">
          Relationship tag
          <input
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

      {error && <p className="error">{error}</p>}

      {ships === null ? (
        <p>Loading…</p>
      ) : ships.length === 0 ? (
        <p className="hint">You aren’t following any ships yet.</p>
      ) : (
        <>
          {/* Every user, not only admins: whoever is looking at an empty library is owed the
              reason, and a non-admin is told who can fix it rather than shown a form they cannot
              use. Said as a missing *login* — the session cookie is a cache, and its absence
              means nothing. */}
          {!ao3LoginConfigured && (
            <p className="callout callout-error">
              <strong>Nothing is being fetched.</strong> The ships you follow are scheduled, but
              this instance has no AO3 login saved, so every scrape is held. Your library stays
              empty until one is saved.{' '}
              {user?.isAdmin
                ? 'Add it under System → Scraping.'
                : 'Ask an admin of this instance to add one under System → Scraping.'}
            </p>
          )}

          {!verificationEnabled && (
            <p className="callout callout-warning">
              Tags can’t be checked right now: this instance has no operator contact, so it isn’t
              allowed to contact AO3 at all. An admin can set one under System → Scraping.
            </p>
          )}

          {ships.every((ship) => !ship.scraperAvailable) && (
            <p className="callout callout-warning">
              Nothing is being fetched yet. This build schedules the ships you follow but has no AO3
              scraper registered to run them, so work counts stay at zero until one lands.
            </p>
          )}

          <table className="ships-table">
            <thead>
              <tr>
                <th>Tag</th>
                <th>Works</th>
                <th>Status</th>
                <th>Last scraped</th>
                <th>Next scrape</th>
                <th>Also followed by</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {ships.map((ship) => {
                const status = describeStatus(ship, verificationEnabled);
                return (
                  <tr key={ship.shipId}>
                    <td className="ship-tag">
                      {ship.tagName}
                      {/* Only set when AO3 disagreed with what was typed. Saying so is the whole
                          point — otherwise the tag someone entered silently becomes another one. */}
                      {ship.requestedTagName && (
                        <span className="ship-renamed">
                          You followed “{ship.requestedTagName}”; AO3 files it under this tag.
                        </span>
                      )}
                    </td>
                    <td>{ship.workCount.toLocaleString()}</td>
                    <td>
                      <span className={status.tone}>{status.label}</span>
                      {status.detail && <span className="ship-status-detail">{status.detail}</span>}
                      {/* Admin-only, because a restart spends requests on behalf of everyone
                          watching the tag — and because it is the only thing in the product that
                          moves a ship out of Failed. */}
                      {user?.isAdmin && canRestartBackfill(ship) && (
                        <BackfillRestart ship={ship} onRestarted={() => void load().catch(() => {})} />
                      )}
                      {/* Admin-only for the same two reasons, and the only thing in the product
                          that moves a ship out of NotFoundOnAo3. */}
                      {user?.isAdmin && ship.verificationState === 'NotFoundOnAo3' && (
                        <VerificationRecheck
                          ship={ship}
                          onRechecked={() => void load().catch(() => {})}
                        />
                      )}
                    </td>
                    <td>{formatDate(ship.lastScrapedAt)}</td>
                    <td>{formatDate(ship.nextScrapeAt)}</td>
                    {/* Minus the reader, so "0" reads as "only you" rather than needing subtraction. */}
                    <td>{ship.watcherCount - 1}</td>
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
                );
              })}
            </tbody>
          </table>

          <p className="hint">
            Unfollowing only removes your subscription. Scraped works stay, so following the tag
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
        Picks up on this ship’s next scheduled scrape, not straight away.
      </span>
      {error && <span className="error">{error}</span>}
    </form>
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
        For a tag that was renamed or briefly gone. Nothing is scraped until AO3 confirms it.
      </span>
      {error && <span className="error">{error}</span>}
    </div>
  );
}
