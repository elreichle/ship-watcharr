import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import type { WatchedShip } from '../api/types';

const BACKFILL_LABELS: Record<WatchedShip['backfillState'], string> = {
  NotStarted: 'Not started',
  InProgress: 'Reading the back catalogue',
  Complete: 'Back catalogue read',
  Failed: 'Backfill failed',
};

function formatDate(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—';
}

interface Status {
  label: string;
  /** Set when the status is the reason nothing will ever happen for this ship. */
  bad?: boolean;
  detail?: string;
}

/**
 * What is actually happening to this ship. Ordered by what blocks what: a tag AO3 has never
 * confirmed can't be scraped, so its verification state outranks anything the schedule says.
 */
function describeStatus(ship: WatchedShip, verificationEnabled: boolean): Status {
  if (ship.verificationState === 'NotFoundOnAo3') {
    return { label: 'AO3 has no such tag', bad: true, detail: 'Check the spelling and add it again.' };
  }

  if (ship.verificationState === 'Pending') {
    return verificationEnabled
      ? { label: 'Checking with AO3…', detail: ship.verificationError ?? undefined }
      : { label: 'Check paused', detail: 'This instance has no operator contact, so it cannot reach AO3.' };
  }

  if (!ship.scraperAvailable) return { label: 'Tag confirmed — waiting for the AO3 scraper' };
  if (!ship.isScheduled) return { label: 'Paused' };
  return { label: BACKFILL_LABELS[ship.backfillState] };
}

export function ShipsPage() {
  const [ships, setShips] = useState<WatchedShip[] | null>(null);
  const [verificationEnabled, setVerificationEnabled] = useState(true);
  const [tagName, setTagName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [removing, setRemoving] = useState<number | null>(null);

  const load = () =>
    api.getWatchedShips().then((response) => {
      setShips(response.ships);
      setVerificationEnabled(response.verificationEnabled);
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
                      <span className={status.bad ? 'error' : undefined}>{status.label}</span>
                      {status.detail && <span className="ship-status-detail">{status.detail}</span>}
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
