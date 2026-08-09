import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import type { PagedResult, WatchedShip, WorkListItem, WorkSort } from '../api/types';

const SORT_LABELS: Record<WorkSort, string> = {
  updated: 'Last updated',
  kudos: 'Kudos',
  hits: 'Hits',
  bookmarks: 'Bookmarks',
  comments: 'Comments',
  words: 'Word count',
};

const PAGE_SIZES = [25, 50, 100];

const DEFAULT_SORT: WorkSort = 'updated';
const DEFAULT_PAGE_SIZE = 25;

function isSort(value: string | null): value is WorkSort {
  return value !== null && value in SORT_LABELS;
}

/** AO3 is where the work actually lives; this app only ever holds a description of it. */
function ao3WorkUrl(id: number): string {
  return `https://archiveofourown.org/works/${id}`;
}

function formatUpdated(work: WorkListItem): string {
  const updated = new Date(work.updatedAt);
  // An approximate timestamp came from a day-granular date, so rendering a time would invent
  // precision the scrape never had.
  return work.updatedAtIsApproximate ? updated.toLocaleDateString() : updated.toLocaleString();
}

export function WorksPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [result, setResult] = useState<PagedResult<WorkListItem> | null>(null);
  // null means "we could not find out", which is not the same as "you follow none" — saying the
  // latter when the request failed sends someone off to re-add ships they already have.
  const [ships, setShips] = useState<WatchedShip[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  // The URL is the source of truth for the query, so a filtered page can be linked and the back
  // button steps through filter changes rather than leaving the app.
  const page = Math.max(Number(searchParams.get('page') ?? 1) || 1, 1);
  const pageSize = Number(searchParams.get('pageSize') ?? DEFAULT_PAGE_SIZE) || DEFAULT_PAGE_SIZE;
  const sortParam = searchParams.get('sort');
  const sort = isSort(sortParam) ? sortParam : DEFAULT_SORT;
  const ascending = searchParams.get('ascending') === 'true';
  const shipIdParam = searchParams.get('shipId');
  const shipId = shipIdParam === null ? null : Number(shipIdParam);

  useEffect(() => {
    // The filter is a convenience, not the page — if it can't load, the works list below still
    // stands on its own and reports its own failure.
    api.getWatchedShips().then((response) => setShips(response.ships)).catch(() => setShips(null));
  }, []);

  useEffect(() => {
    let current = true;

    setLoading(true);
    api
      .getWorks({ page, pageSize, shipId, sort, ascending })
      .then((next) => {
        // Guards against a slow first request landing after a faster second one and overwriting it.
        if (!current) return;
        setResult(next);
        setError(null);
      })
      .catch((err) => {
        if (!current) return;
        setError(err instanceof ApiError ? err.message : 'Failed to load works.');
      })
      .finally(() => {
        if (current) setLoading(false);
      });

    return () => {
      current = false;
    };
  }, [page, pageSize, shipId, sort, ascending]);

  /** Any change other than paging invalidates the page number, so it resets unless set explicitly. */
  const updateQuery = (changes: Record<string, string | null>) => {
    setSearchParams((params) => {
      const next = new URLSearchParams(params);
      if (!('page' in changes)) next.delete('page');
      for (const [key, value] of Object.entries(changes)) {
        if (value === null) next.delete(key);
        else next.set(key, value);
      }
      return next;
    });
  };

  const works = result?.items ?? [];
  const totalPages = result?.totalPages ?? 0;

  return (
    <div className="page">
      <h1>Works</h1>

      <div className="works-controls">
        <label>
          Ship
          <select
            value={shipId ?? ''}
            onChange={(e) => updateQuery({ shipId: e.target.value === '' ? null : e.target.value })}
          >
            <option value="">All ships you follow</option>
            {ships?.map((ship) => (
              <option key={ship.shipId} value={ship.shipId}>
                {ship.tagName}
              </option>
            ))}
          </select>
        </label>

        <label>
          Sort by
          <select value={sort} onChange={(e) => updateQuery({ sort: e.target.value })}>
            {Object.entries(SORT_LABELS).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </label>

        <label>
          Order
          <select
            value={ascending ? 'true' : 'false'}
            onChange={(e) => updateQuery({ ascending: e.target.value })}
          >
            <option value="false">Highest first</option>
            <option value="true">Lowest first</option>
          </select>
        </label>

        <label>
          Per page
          <select value={pageSize} onChange={(e) => updateQuery({ pageSize: e.target.value })}>
            {PAGE_SIZES.map((size) => (
              <option key={size} value={size}>
                {size}
              </option>
            ))}
          </select>
        </label>
      </div>

      {error && <p className="error">{error}</p>}

      {result === null ? (
        !error && <p>Loading…</p>
      ) : works.length === 0 ? (
        <p className="hint">
          {ships !== null && ships.length === 0 ? (
            <>
              Nothing here yet — you aren’t following any ships. Add a relationship tag on the{' '}
              <Link to="/ships">Ships</Link> tab and its works will show up here.
            </>
          ) : (
            <>No works scraped yet for the ships you follow. Check progress on the{' '}
            <Link to="/ships">Ships</Link> tab.</>
          )}
        </p>
      ) : (
        <>
          <table className="works-table" aria-busy={loading}>
            <thead>
              <tr>
                <th>Work</th>
                <th>Rating</th>
                <th>Words</th>
                <th>Chapters</th>
                <th>Kudos</th>
                <th>Hits</th>
                <th>Updated</th>
              </tr>
            </thead>
            <tbody>
              {works.map((work) => (
                <tr key={work.id}>
                  <td className="work-cell">
                    <a href={ao3WorkUrl(work.id)} target="_blank" rel="noreferrer">
                      {work.title}
                    </a>
                    <span className="work-byline">
                      {work.isAnonymous
                        ? 'Anonymous'
                        : work.authors.length > 0
                          ? work.authors.join(', ')
                          : 'Unknown author'}
                      {work.fandoms.length > 0 && <> · {work.fandoms.join(', ')}</>}
                    </span>
                    <span className="work-chips">
                      {work.ships.map((ship) => (
                        <span key={ship} className="chip chip-ship">
                          {ship}
                        </span>
                      ))}
                      {work.categories.map((category) => (
                        <span key={category} className="chip">
                          {category}
                        </span>
                      ))}
                      {work.warnings.map((warning) => (
                        <span key={warning} className="chip chip-warning">
                          {warning}
                        </span>
                      ))}
                      {work.isRestricted && <span className="chip">Registered users only</span>}
                    </span>
                  </td>
                  <td>{work.rating}</td>
                  <td className="numeric">{work.wordCount.toLocaleString()}</td>
                  <td className="numeric">
                    {work.chapterCount}/{work.plannedChapterCount ?? '?'}
                    {work.isComplete && <span className="work-complete"> complete</span>}
                  </td>
                  <td className="numeric">{work.kudos.toLocaleString()}</td>
                  <td className="numeric">{work.hits.toLocaleString()}</td>
                  <td>{formatUpdated(work)}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="pager">
            <button
              type="button"
              disabled={page <= 1 || loading}
              onClick={() => updateQuery({ page: String(page - 1) })}
            >
              Previous
            </button>
            <span className="hint">
              Page {result.page} of {totalPages} · {result.totalCount.toLocaleString()} works
            </span>
            <button
              type="button"
              disabled={page >= totalPages || loading}
              onClick={() => updateQuery({ page: String(page + 1) })}
            >
              Next
            </button>
          </div>
        </>
      )}
    </div>
  );
}
