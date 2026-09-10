import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import type { BucketCount, LabelledCount, MonthCount, ReadingStatus, Stats, WatchedShip } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { SkeletonRows } from '../components/Skeleton';
import { READING_STATUS_LABELS } from '../readingStatus';

/**
 * Statistics over the reader's library: the corpus as it stands, and their own reading laid over it.
 *
 * Every chart here is CSS over numbers the server already bucketed — no charting library, which is
 * the same reason the stars in `RatingStars` are a text glyph: a dependency whose whole job is to
 * draw a rectangle proportional to a number is a dependency this page can do without. It also keeps
 * the bars inside the theme. Every colour is a token, so a chart drawn under someone's Obsidian
 * theme is drawn in *their* accent rather than in a palette shipped with the app.
 */

/** What the `shipId` query parameter means when it says "everything you follow". */
const ALL_SHIPS = 'all';

function formatNumber(value: number): string {
  return value.toLocaleString();
}

/** An average, to one decimal. Null is a library with nothing to average, and says so in words. */
function formatAverage(value: number | null): string {
  return value === null ? '—' : value.toLocaleString(undefined, { maximumFractionDigits: 1 });
}

/** A share as a percentage, and 0% for a ship with no works rather than a division by zero. */
function percent(part: number, whole: number): number {
  return whole === 0 ? 0 : Math.round((part / whole) * 100);
}

function monthLabel(month: MonthCount): string {
  // Day 1 of the month at noon: the series is a year and a month, and constructing a date at
  // midnight lets a timezone behind UTC roll the label back into the previous month.
  return new Date(month.year, month.month - 1, 1, 12).toLocaleDateString(undefined, {
    month: 'long',
    year: 'numeric',
  });
}

/**
 * How many months the chart will draw, however long the library's own history is.
 *
 * Twenty years, which is longer than AO3 has existed. The cap is not about width — it is what
 * stops one unreadable date from being a hang: `Ao3BlurbParser` leaves a work whose date it could
 * not read at `DateTime.MinValue`, the server groups by year and month and so returns that work
 * under year 1, and filling from year 1 to this year is twenty-four thousand columns of DOM and as
 * many locale-formatted tooltips. `StatsQueries.WorksByUpdatedMonth` refuses to zero-fill for
 * exactly this reason and hands the span to the client; the client has to be the one that bounds it.
 */
const MAX_MONTH_COLUMNS = 240;

interface MonthSeries {
  columns: MonthCount[];
  /** Works dated before the drawn window — said in words rather than silently dropped. */
  omittedWorkCount: number;
  /** The first month drawn, for naming what "before" means. Null when nothing was left out. */
  omittedBefore: MonthCount | null;
}

/**
 * The month series with its gaps filled in, ending at its last month and no longer than the cap.
 *
 * The API deliberately returns only the months that have works — see `StatsQueries` — because a
 * server cannot know what span a client means to draw. A bar chart does know: a quiet month is a
 * gap in the ship's history, and dropping it slides every later month left and draws a steady
 * stream where there was a burst and a silence.
 *
 * The window is anchored at the *end* because that is the half a reader is looking at: a library
 * whose history runs longer than the cap loses its oldest months, not its newest.
 */
function monthSeries(months: MonthCount[]): MonthSeries {
  if (months.length === 0) return { columns: [], omittedWorkCount: 0, omittedBefore: null };

  const key = (month: MonthCount) => month.year * 12 + month.month;
  const lastKey = key(months[months.length - 1]);
  const firstKey = Math.max(key(months[0]), lastKey - MAX_MONTH_COLUMNS + 1);

  const counts = new Map(months.map((m) => [key(m), m.workCount]));
  const columns: MonthCount[] = [];

  for (let at = firstKey; at <= lastKey; at += 1) {
    // Months are 1-12 on the wire, so the modulo has to land on 12 rather than on 0.
    const month = ((at - 1) % 12) + 1;
    columns.push({ year: (at - month) / 12, month, workCount: counts.get(at) ?? 0 });
  }

  const omitted = months.filter((m) => key(m) < firstKey);

  return {
    columns,
    omittedWorkCount: omitted.reduce((total, m) => total + m.workCount, 0),
    omittedBefore: omitted.length === 0 ? null : columns[0],
  };
}

interface StatTileProps {
  label: string;
  value: string;
  hint?: string;
}

function StatTile({ label, value, hint }: StatTileProps) {
  return (
    <div className="stat-tile">
      <span className="stat-tile-value">{value}</span>
      <span className="stat-tile-label">{label}</span>
      {hint !== undefined && <span className="stat-tile-hint">{hint}</span>}
    </div>
  );
}

interface BarRow {
  key: string;
  label: string;
  value: number;
  /** Said beside the count — what this bar is, where the label alone does not carry it. */
  detail?: string;
}

/**
 * A row of labelled bars, scaled against the largest of them.
 *
 * Scaled to the biggest bar rather than to the total: these are counts across a vocabulary, and
 * against a total the common case is one bar at 90% and the rest invisible. The number is written
 * out beside every bar, so the drawing is a comparison and never the only way to read a value.
 */
function BarList({ rows }: { rows: BarRow[] }) {
  const largest = Math.max(...rows.map((row) => row.value), 0);

  return (
    <ul className="bar-list">
      {rows.map((row) => (
        <li key={row.key}>
          <span className="bar-label">{row.label}</span>
          {/* Decorative: the count is right there in text, and a screen reader reading the bar
              too would say every number twice. */}
          <span className="bar-track" aria-hidden="true">
            <span
              className="bar-fill"
              style={{ width: `${largest === 0 ? 0 : (row.value / largest) * 100}%` }}
            />
          </span>
          <span className="bar-value">
            {formatNumber(row.value)}
            {row.detail !== undefined && <span className="bar-detail">{row.detail}</span>}
          </span>
        </li>
      ))}
    </ul>
  );
}

function bucketRows(buckets: BucketCount[]): BarRow[] {
  return buckets.map((bucket) => ({ key: bucket.label, label: bucket.label, value: bucket.workCount }));
}

/**
 * Works per calendar month of their last revision, as columns.
 *
 * The one chart here that is not a bar list, because months are an axis: they have an order and
 * the distance between two of them means something, which a list of labelled rows throws away.
 * Wide libraries scroll rather than squeezing twenty years into the page width.
 */
function MonthChart({ months }: { months: MonthCount[] }) {
  const { columns, omittedWorkCount, omittedBefore } = monthSeries(months);
  const largest = Math.max(...columns.map((month) => month.workCount), 0);

  return (
    <>
      <div className="month-chart">
        {columns.map((month) => (
          <div
            key={`${month.year}-${month.month}`}
            className="month-column"
            // The whole figure in a tooltip, since only January carries a written label — with one
            // column per month a legible axis and a labelled one are not the same thing.
            title={`${monthLabel(month)}: ${formatNumber(month.workCount)} works`}
          >
            <span className="month-bar-track" aria-hidden="true">
              {/* A month with no works has no bar at all, rather than one rounded down to nothing.
                  The fill carries a minimum height precisely so a single work in a busy tag still
                  draws, and an empty column that also drew would be indistinguishable from it. */}
              {month.workCount > 0 && (
                <span
                  className="month-bar-fill"
                  style={{ height: `${largest === 0 ? 0 : (month.workCount / largest) * 100}%` }}
                />
              )}
            </span>
            <span className="month-tick">{month.month === 1 ? month.year : ''}</span>
          </div>
        ))}
      </div>

      {/* Said rather than silently dropped. In practice this is a work whose date AO3 listed in a
          form the scraper could not read, which lands two thousand years before the rest — the
          alternative to naming it is a chart that appears to be missing works. */}
      {omittedBefore !== null && (
        <p className="hint">
          {formatNumber(omittedWorkCount)}{' '}
          {omittedWorkCount === 1 ? 'work is' : 'works are'} dated before{' '}
          {monthLabel(omittedBefore)} and {omittedWorkCount === 1 ? 'is' : 'are'} not drawn.
        </p>
      )}
    </>
  );
}

/** The status mix, said in this app's own words rather than in the enum names the wire carries. */
function statusRows(mix: LabelledCount[]): BarRow[] {
  return mix.map((row) => ({
    key: row.label,
    label: READING_STATUS_LABELS[row.label as ReadingStatus] ?? row.label,
    value: row.workCount,
  }));
}

export function StatsPage() {
  const { user } = useAuth();
  const [searchParams, setSearchParams] = useSearchParams();
  const [stats, setStats] = useState<Stats | null>(null);
  // null means "we could not find out", which is not "you follow none" — the picker and the empty
  // library's explanation both hang off this, and both would otherwise lie about a failed request.
  const [ships, setShips] = useState<WatchedShip[] | null>(null);
  const [ao3LoginConfigured, setAo3LoginConfigured] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  // Anything that is not a positive whole number is read as "everything you follow", so the picker
  // and the figures agree about what was asked for. `?shipId=` alone parses to 0 and `?shipId=abc`
  // to NaN, and both would otherwise be sent — one to be 404ed, the other to be refused as a
  // malformed query — under a control still showing the whole library.
  const requestedShipId = Number(searchParams.get('shipId'));
  const shipId = Number.isInteger(requestedShipId) && requestedShipId > 0 ? requestedShipId : null;

  // The picker is fed from /api/ships rather than from the response's own `ships`, which narrows
  // with everything else: reading it would leave the control holding the one ship it is already on,
  // with no way back to the whole library.
  useEffect(() => {
    api
      .getWatchedShips()
      .then((response) => {
        setShips(response.ships);
        setAo3LoginConfigured(response.ao3LoginConfigured);
      })
      .catch(() => setShips(null));
  }, []);

  useEffect(() => {
    let current = true;
    setLoading(true);
    // Cleared before the request, not only when one fails: a load in flight is a page whose numbers
    // are about to be a different ship's, and leaving the old ones up under the new ship's name is
    // the thing the failure path already refuses to do.
    setStats(null);

    api
      .getStats(shipId)
      .then((response) => {
        if (!current) return;
        setStats(response);
        setError(null);
      })
      .catch((err: Error) => {
        if (!current) return;
        // The figures are cleared with the failure: leaving the previous ship's numbers on screen
        // under a new ship's name is worse than an empty page saying what went wrong.
        setStats(null);
        setError(err.message);
      })
      .finally(() => {
        if (current) setLoading(false);
      });

    return () => {
      current = false;
    };
  }, [shipId]);

  const selectedShip = ships?.find((ship) => ship.shipId === shipId) ?? null;

  const corpus = stats?.corpus ?? null;
  const reading = stats?.reading ?? null;

  return (
    <div className="page">
      <h1>Statistics</h1>

      <p className="hint">
        Everything below is counted over the works in your library at the moment you asked — the
        same works <Link to="/works">Works</Link> lists. Nothing here is stored, so it can never
        drift out of step with the library it describes.
      </p>

      <div className="works-controls">
        <label>
          Ships
          <select
            value={shipId === null ? ALL_SHIPS : String(shipId)}
            onChange={(e) => {
              const value = e.target.value;
              setSearchParams(value === ALL_SHIPS ? {} : { shipId: value });
            }}
          >
            <option value={ALL_SHIPS}>Everything you follow</option>
            {(ships ?? []).map((ship) => (
              <option key={ship.shipId} value={ship.shipId}>
                {ship.tagName}
              </option>
            ))}
          </select>
        </label>
      </div>

      {error !== null && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      {loading && !error && <SkeletonRows rows={2} kind="table" />}

      {stats !== null && corpus !== null && reading !== null && (
        corpus.workCount === 0 ? (
          // An empty library is explained rather than drawn. Which explanation depends on how far
          // the reader has got: no ships, a login the instance is waiting on, or a ship that is
          // followed and scheduled and simply has nothing in it yet.
          <div className="callout callout-warning">
            {ships !== null && ships.length === 0 ? (
              <>
                <strong>Nothing to count yet.</strong> You aren’t following any ships, so your
                library is empty. Follow one from <Link to="/ships">Ships</Link> and statistics
                appear as its works are fetched.
              </>
            ) : !ao3LoginConfigured ? (
              <>
                <strong>Nothing to count yet.</strong> The ships you follow are scheduled, but this
                instance has no AO3 login saved, so every check is paused and no works have arrived.{' '}
                {user?.isAdmin
                  ? 'Add it under System → AO3.'
                  : 'Ask an admin of this instance to add one under System → AO3.'}
              </>
            ) : selectedShip !== null ? (
              <>
                <strong>Nothing to count yet.</strong> No works have been fetched for{' '}
                {selectedShip.tagName}. <Link to="/ships">Ships</Link> says when it was last
                checked and when it is next due.
              </>
            ) : (
              <>
                <strong>Nothing to count yet.</strong> The ships you follow have no works in them
                so far. <Link to="/ships">Ships</Link> says when each was last checked and when it
                is next due.
              </>
            )}
          </div>
        ) : (
          <>
            <section>
              <h2>The library</h2>
              <div className="stat-tiles">
                <StatTile label="Works" value={formatNumber(corpus.workCount)} />
                <StatTile
                  label="Complete"
                  value={formatNumber(corpus.completeCount)}
                  hint={`${percent(corpus.completeCount, corpus.workCount)}% of the works here`}
                />
                <StatTile label="Words" value={formatNumber(corpus.wordCount)} />
                <StatTile label="Kudos" value={formatNumber(corpus.kudos)} />
                <StatTile label="Average kudos" value={formatAverage(corpus.averageKudos)} />
                <StatTile label="Average length" value={formatAverage(corpus.averageWordCount)} hint="words" />
              </div>
            </section>

            <section>
              <h2>Your reading</h2>
              <div className="stat-tiles">
                <StatTile
                  label="Marked"
                  value={formatNumber(reading.markedCount)}
                  hint={`${percent(reading.markedCount, corpus.workCount)}% of the library`}
                />
                <StatTile
                  label="Read"
                  value={formatNumber(reading.readCount)}
                  hint={`${percent(reading.readCount, corpus.workCount)}% of the library`}
                />
                <StatTile label="Words read" value={formatNumber(reading.readWordCount)} />
                <StatTile label="Rated" value={formatNumber(reading.ratedCount)} />
                <StatTile
                  label="Your average"
                  value={
                    // Half-stars on the wire, stars on the page — the same scale the rating control
                    // shows, said the way a reader says it.
                    reading.averageRating === null ? '—' : formatAverage(reading.averageRating / 2)
                  }
                  hint={reading.averageRating === null ? 'nothing rated yet' : 'stars'}
                />
              </div>

              <BarList rows={statusRows(reading.statusMix)} />
              <p className="hint">
                These sum to the whole library: a work you have never marked counts as “Not set”
                rather than dropping out, which is what “unread” means to a saved filter too.
              </p>
            </section>

            {stats.ships.length > 0 && (
              <section>
                <h2>Per ship</h2>
                <table className="stats-table">
                  <thead>
                    <tr>
                      <th>Ship</th>
                      <th className="numeric">Works</th>
                      <th className="numeric">Words</th>
                      <th className="numeric">Marked</th>
                      <th className="numeric">Read</th>
                      <th className="numeric">Rated</th>
                      <th>Read through</th>
                    </tr>
                  </thead>
                  <tbody>
                    {stats.ships.map((ship) => (
                      <tr key={ship.shipId}>
                        <td>
                          <Link to={`/works?shipId=${ship.shipId}`}>{ship.tagName}</Link>
                        </td>
                        <td className="numeric">{formatNumber(ship.workCount)}</td>
                        <td className="numeric">{formatNumber(ship.wordCount)}</td>
                        <td className="numeric">{formatNumber(ship.markedCount)}</td>
                        <td className="numeric">{formatNumber(ship.readCount)}</td>
                        <td className="numeric">{formatNumber(ship.ratedCount)}</td>
                        <td>
                          <span className="share">
                            <span className="bar-track" aria-hidden="true">
                              <span
                                className="bar-fill"
                                style={{ width: `${percent(ship.readCount, ship.workCount)}%` }}
                              />
                            </span>
                            <span className="bar-value">
                              {percent(ship.readCount, ship.workCount)}%
                            </span>
                          </span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </section>
            )}

            <section>
              <h2>When these works were last revised</h2>
              {corpus.worksByUpdatedMonth.length === 0 ? (
                <p className="hint">
                  Nothing here carries a date yet, so there is no history to draw.
                </p>
              ) : (
                <>
                  <MonthChart months={corpus.worksByUpdatedMonth} />
                  <p className="hint">
                    One column per month, by the date AO3 last showed the work as updated. Quiet
                    months are drawn as gaps rather than skipped. Hover a column for its figure.
                  </p>
                </>
              )}
            </section>

            <section>
              <h2>Content ratings</h2>
              <BarList
                rows={corpus.ratingMix.map((row) => ({
                  key: row.label,
                  label: row.label,
                  value: row.workCount,
                }))}
              />
            </section>

            <section>
              <h2>Kudos</h2>
              <BarList rows={bucketRows(corpus.kudosDistribution)} />
            </section>

            <section>
              <h2>Length</h2>
              <BarList rows={bucketRows(corpus.wordCountDistribution)} />
            </section>

            <section>
              <h2>Most prolific creators</h2>
              {corpus.topAuthors.length === 0 ? (
                <p className="hint">
                  Nothing in this library names a creator — anonymous works are counted everywhere
                  else here, but they have no author to rank.
                </p>
              ) : (
                <BarList
                  rows={corpus.topAuthors.map((author) => ({
                    key: String(author.pseudId),
                    label: author.name,
                    value: author.workCount,
                    detail: `${formatNumber(author.kudos)} kudos`,
                  }))}
                />
              )}
            </section>

            <section>
              <h2>Your ratings against the archive’s</h2>
              {reading.ratingsAgainstReception.length === 0 ? (
                <p className="hint">
                  You haven’t rated anything yet. Rate a work from <Link to="/works">Works</Link> or
                  its own page, and what the archive made of the works you score lands here beside
                  your own opinion of them.
                </p>
              ) : (
                <table className="stats-table">
                  <thead>
                    <tr>
                      <th>Your rating</th>
                      <th className="numeric">Works</th>
                      <th className="numeric">Average kudos</th>
                      <th className="numeric">Average length</th>
                    </tr>
                  </thead>
                  <tbody>
                    {reading.ratingsAgainstReception.map((row) => (
                      <tr key={row.rating}>
                        <td>{row.rating / 2} ★</td>
                        <td className="numeric">{formatNumber(row.workCount)}</td>
                        <td className="numeric">{formatAverage(row.averageKudos)}</td>
                        <td className="numeric">{formatAverage(row.averageWordCount)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </section>
          </>
        )
      )}
    </div>
  );
}
