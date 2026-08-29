import { Fragment, useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import type {
  PagedResult,
  ReadingStatus,
  SavedFilter,
  WatchedShip,
  WorkListItem,
  WorkSort,
  WorkState,
} from '../api/types';
import { RatingStars } from '../components/RatingStars';
import { READING_STATUS_LABELS } from '../readingStatus';

const SORT_LABELS: Record<WorkSort, string> = {
  updated: 'Last updated',
  kudos: 'Kudos',
  hits: 'Hits',
  bookmarks: 'Bookmarks',
  comments: 'Comments',
  words: 'Word count',
};

const PAGE_SIZES = [25, 50, 100];

/** Matches `MaxNoteLength` on `UserWorkState.Note`, so an over-long note is refused here first. */
const MAX_NOTE_LENGTH = 4000;

/** Columns in the works table, for the note editor's row to span all of them. */
const WORK_COLUMN_COUNT = 8;

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

/** What the `filter` query parameter means when it says "show me everything I follow". */
const NO_FILTER = 'none';

export function WorksPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [result, setResult] = useState<PagedResult<WorkListItem> | null>(null);
  // null means "we could not find out", which is not the same as "you follow none" — saying the
  // latter when the request failed sends someone off to re-add ships they already have.
  const [ships, setShips] = useState<WatchedShip[] | null>(null);
  const [savedFilters, setSavedFilters] = useState<SavedFilter[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Why a failed write to one row's state is not the page's `error`: the list itself loaded fine,
  // and blanking the works to say so would lose the very row the reader was editing.
  const [stateErrors, setStateErrors] = useState<Record<number, string>>({});
  const [openNoteId, setOpenNoteId] = useState<number | null>(null);
  // Drafts are held per work, and dropped only once the note is saved. Closing an editor — by
  // Cancel, by the toggle, or by opening another row's — leaves the text where the reader left it,
  // which is the same promise the row-editor layout was chosen to keep.
  const [noteDrafts, setNoteDrafts] = useState<Record<number, string>>({});
  const [savingNoteId, setSavingNoteId] = useState<number | null>(null);
  // One counter per work, bumped on every write. A response is only allowed to touch the row when
  // its own token is still the newest: two edits to one row in flight together otherwise let the
  // slower, older answer land last and undo the newer one.
  const stateWriteTokens = useRef(new Map<number, number>());
  // The tail of each row's write chain, so two edits to one row are never in flight together. The
  // token above only decides which *response* may repaint the row; the requests themselves are
  // whole-state replacements, so letting them overlap lets the older one land last and leaves the
  // database holding the edit the row has already stopped showing.
  const stateWriteChains = useRef(new Map<number, Promise<unknown>>());
  // Bumped by every touch of a note editor — typing in one, opening one, closing one. A save
  // captures it and refuses to reset anything if it has moved since: the write is not instant, and
  // what the reader did during it outranks what the request carried.
  const noteEditorTouches = useRef(0);

  /**
   * The only door onto `openNoteId`. Opening, closing and switching all go through here so that the
   * counter cannot be bumped in one of them and forgotten in the next.
   */
  const showNoteEditor = (workId: number | null) => {
    noteEditorTouches.current += 1;
    setOpenNoteId(workId);
  };

  // The URL is the source of truth for the query, so a filtered page can be linked and the back
  // button steps through filter changes rather than leaving the app.
  const page = Math.max(Number(searchParams.get('page') ?? 1) || 1, 1);
  const pageSize = Number(searchParams.get('pageSize') ?? DEFAULT_PAGE_SIZE) || DEFAULT_PAGE_SIZE;
  const sortParam = searchParams.get('sort');
  const ascendingParam = searchParams.get('ascending');
  const shipIdParam = searchParams.get('shipId');
  const shipId = shipIdParam === null ? null : Number(shipIdParam);

  // Three states, and the absent one is not the same as "none": no parameter at all means the
  // server applies whichever saved filter is marked default, which is the point of having one.
  const filterParam = searchParams.get('filter');
  const savedFilterId =
    filterParam === null || filterParam === NO_FILTER ? null : Number(filterParam);
  const useDefaultFilter = filterParam !== NO_FILTER;

  const activeFilter =
    savedFilters.find((filter) =>
      savedFilterId === null ? useDefaultFilter && filter.isDefault : filter.id === savedFilterId,
    ) ?? null;

  // Only sent when the URL says so, so that an applied filter's own sort is what stands otherwise.
  // The dropdown still has to show something: the filter's sort, or the library's own default.
  const sort = isSort(sortParam) ? sortParam : null;
  const shownSort = sort ?? activeFilter?.sort ?? DEFAULT_SORT;
  const ascending = ascendingParam === null ? null : ascendingParam === 'true';
  const shownAscending = ascending ?? activeFilter?.ascending ?? false;

  useEffect(() => {
    // Both are conveniences, not the page — if either can't load, the works list below still stands
    // on its own and reports its own failure.
    api.getWatchedShips().then((response) => setShips(response.ships)).catch(() => setShips(null));
    api.getSavedFilters().then(setSavedFilters).catch(() => setSavedFilters([]));
  }, []);

  useEffect(() => {
    let current = true;

    // A different page of works is a different set of rows: an open note editor and a failed write
    // both belong to rows that are about to be replaced.
    showNoteEditor(null);
    setNoteDrafts({});
    setSavingNoteId(null);
    setStateErrors({});

    setLoading(true);
    api
      .getWorks({
        page,
        pageSize,
        shipId,
        sort: sort ?? undefined,
        ascending: ascending ?? undefined,
        savedFilterId,
        useDefaultFilter,
      })
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
  }, [page, pageSize, shipId, sort, ascending, savedFilterId, useDefaultFilter]);

  /** Writes one row's state into the loaded page, leaving every other row's copy alone. */
  const applyState = (workId: number, state: WorkState) => {
    setResult((current) =>
      current === null
        ? current
        : {
            ...current,
            items: current.items.map((work) => (work.id === workId ? { ...work, state } : work)),
          },
    );
  };

  /**
   * Sends one row's whole state and reconciles the row against what came back. Resolves to whether
   * the write landed — never rejects, so a caller that only wants the row updated can ignore it.
   *
   * Whole, because `PUT /works/{id}/state` replaces: a request naming only the field that changed
   * would clear the other two. Optimistic, because a rating that waits on a round trip does not
   * feel like a click — but the response, not the guess, is what the row ends up showing, and a
   * rejected write puts the old value back *and says so*.
   */
  const saveState = (work: WorkListItem, next: WorkState): Promise<boolean> => {
    const previous = work.state;
    const token = (stateWriteTokens.current.get(work.id) ?? 0) + 1;
    stateWriteTokens.current.set(work.id, token);
    // Whether this write is still the newest for this row. It governs what the *row* shows, not
    // what this call reports: a superseded write still happened, and the write that superseded it
    // is the one whose outcome the reader should be looking at.
    const isCurrent = () => stateWriteTokens.current.get(work.id) === token;

    applyState(work.id, next);
    setStateErrors(({ [work.id]: _cleared, ...rest }) => rest);

    // Queued behind this row's last write rather than sent now, so the archive of record ends up
    // agreeing with the row: the reader's last edit is the last one the server sees. A failed
    // predecessor still lets this one go — the reader asked for it, and abandoning it silently
    // would be the same lost edit by another route.
    const previousWrite = stateWriteChains.current.get(work.id) ?? Promise.resolve();
    const write = previousWrite.then(() =>
      api
        .setWorkState(work.id, next)
        .then((saved) => {
          if (isCurrent()) applyState(work.id, saved);
          return true;
        })
        .catch((err) => {
          if (isCurrent()) {
            applyState(work.id, previous);
            setStateErrors((errors) => ({
              ...errors,
              [work.id]:
                err instanceof ApiError ? err.message : 'Could not save that — nothing changed.',
            }));
          }
          return false;
        }),
    );

    stateWriteChains.current.set(work.id, write);
    // Only the tail is worth keeping: once this write is the last one done for the row, the map
    // entry is a reference to a settled promise that nothing will ever chain onto again.
    void write.then(() => {
      if (stateWriteChains.current.get(work.id) === write) stateWriteChains.current.delete(work.id);
    });
    return write;
  };

  const openNoteEditor = (work: WorkListItem) => {
    showNoteEditor(work.id);
    setNoteDrafts((drafts) =>
      // Seeded from the stored note only when nothing is held for this row: a draft that is still
      // here is text the reader typed and never saved, and it outranks what the server has.
      work.id in drafts ? drafts : { ...drafts, [work.id]: work.state.note ?? '' },
    );
  };

  /**
   * Both ways out of the note editor. The note is a parameter rather than read from `noteDraft`,
   * because Delete would otherwise have to blank the draft first and then send a value this render
   * cannot see yet.
   *
   * A refused write leaves the editor open holding the text: the row below it already carries the
   * reason, and closing would throw away what the reader typed as well as the write.
   */
  const commitNote = (work: WorkListItem, note: string | null) => {
    const workId = work.id;
    // What the editor looked like when Save was pressed. The textarea stays editable through the
    // round trip and the editor can be closed and reopened during it, so resetting on success
    // unconditionally makes the reader watch their own characters vanish, or the box they just
    // reopened slam shut, with nothing said.
    const touchesAtSend = noteEditorTouches.current;

    setSavingNoteId(workId);
    saveState(work, { ...work.state, note })
      .then((saved) => {
        if (!saved) return;
        // Untouched since the click, and still the row this save was started for: only then is
        // closing the editor and dropping its draft the reader's own last instruction, rather than
        // an undo of whatever they went on to do.
        if (noteEditorTouches.current !== touchesAtSend) return;
        setOpenNoteId((open) => (open === workId ? null : open));
        setNoteDrafts(({ [workId]: _saved, ...rest }) => rest);
      })
      .finally(() => setSavingNoteId((saving) => (saving === workId ? null : saving)));
  };

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
          Filter
          {/* Falls back to "none" rather than to an empty value, which would match no option and
              leave the control blank whenever the reader has no default set. */}
          <select
            value={activeFilter?.id ?? NO_FILTER}
            onChange={(e) =>
              // The sort is cleared with it: the point of picking a saved view is to get the order
              // it was saved with, and one left over from the previous view would override it.
              updateQuery({ filter: e.target.value, sort: null, ascending: null })
            }
          >
            <option value={NO_FILTER}>Everything you follow</option>
            {savedFilters.map((filter) => (
              <option key={filter.id} value={filter.id}>
                {filter.name}
                {filter.isDefault ? ' (default)' : ''}
              </option>
            ))}
          </select>
        </label>

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
          <select value={shownSort} onChange={(e) => updateQuery({ sort: e.target.value })}>
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
            value={shownAscending ? 'true' : 'false'}
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
          ) : activeFilter ? (
            // Saying "nothing scraped yet" while a filter is narrowing the list would blame the
            // scraper for the reader's own criteria.
            <>
              No works match “{activeFilter.name}”. Loosen it on the{' '}
              <Link to="/filters">Filters</Link> tab, or switch this to “Everything you follow”.
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
                <th>Yours</th>
                {/* Named for whose rating it is, now that the column beside it holds the other. */}
                <th>AO3 rating</th>
                <th>Words</th>
                <th>Chapters</th>
                <th>Kudos</th>
                <th>Hits</th>
                <th>Updated</th>
              </tr>
            </thead>
            <tbody>
              {works.map((work) => (
                <Fragment key={work.id}>
                  <tr>
                    <td className="work-cell">
                      {/* The title opens this app's own page for the work — everything known about
                          it, and the reader's own marks — rather than leaving for the archive. AO3
                          is one click further on, in the byline. */}
                      <Link to={`/works/${work.id}`}>{work.title}</Link>
                      <span className="work-byline">
                        {work.isAnonymous
                          ? 'Anonymous'
                          : work.authors.length > 0
                            ? work.authors.join(', ')
                            : 'Unknown author'}
                        {work.fandoms.length > 0 && <> · {work.fandoms.join(', ')}</>}
                        {' · '}
                        <a href={ao3WorkUrl(work.id)} target="_blank" rel="noreferrer">
                          AO3
                        </a>
                      </span>
                      <span className="work-chips">
                        {work.ships.map((ship) => (
                          <span key={ship} className="chip chip-ship">
                            {ship}
                          </span>
                        ))}
                        {/* A row is here despite a tag having let go either because another
                            followed tag still carries it or because this reader marked it — see
                            WorkQueries.Library. Which of the two is visible from the chips beside
                            this one, so the chip says the fact and not the reason. */}
                        {work.leftShips.map((ship) => (
                          <span
                            key={ship}
                            className="chip chip-left"
                            title={`AO3 no longer lists this work under ${ship}. Everything you have marked on it is untouched, and it comes back if the tag lists it again.`}
                          >
                            Left {ship}
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
                    <td className="work-state-cell">
                      <select
                        aria-label={`Reading status for ${work.title}`}
                        value={work.state.status}
                        onChange={(e) => {
                          void saveState(work, {
                            ...work.state,
                            status: e.target.value as ReadingStatus,
                          });
                        }}
                      >
                        {Object.entries(READING_STATUS_LABELS).map(([value, label]) => (
                          <option key={value} value={value}>
                            {label}
                          </option>
                        ))}
                      </select>
                      <span className="work-state-row">
                        <RatingStars
                          label={`Your rating of ${work.title}`}
                          value={work.state.rating}
                          onChange={(rating) => {
                            void saveState(work, { ...work.state, rating });
                          }}
                        />
                        <button
                          type="button"
                          className="link"
                          aria-expanded={openNoteId === work.id}
                          onClick={() =>
                            openNoteId === work.id ? showNoteEditor(null) : openNoteEditor(work)
                          }
                        >
                          {work.state.note === null ? 'Add note' : 'Note'}
                        </button>
                      </span>
                      {stateErrors[work.id] && (
                        <span className="error work-state-error">{stateErrors[work.id]}</span>
                      )}
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
                  {openNoteId === work.id && (
                    // A row of its own rather than a popover: the table already scrolls sideways,
                    // and a floating editor over a scrolling table is where a half-typed note goes
                    // to get lost.
                    <tr className="work-note-row">
                      <td colSpan={WORK_COLUMN_COUNT}>
                        <label className="work-note-field">
                          Your note on “{work.title}”
                          <textarea
                            value={noteDrafts[work.id] ?? ''}
                            maxLength={MAX_NOTE_LENGTH}
                            rows={4}
                            autoFocus
                            onChange={(e) => {
                              noteEditorTouches.current += 1;
                              setNoteDrafts((drafts) => ({ ...drafts, [work.id]: e.target.value }));
                            }}
                          />
                        </label>
                        <div className="button-row">
                          <button
                            type="button"
                            disabled={savingNoteId === work.id}
                            onClick={() =>
                              commitNote(work, (noteDrafts[work.id] ?? '').trim() || null)
                            }
                          >
                            Save note
                          </button>
                          <button
                            type="button"
                            className="link"
                            onClick={() => showNoteEditor(null)}
                          >
                            Close
                          </button>
                          {work.state.note !== null && (
                            // Clearing is emptying the box and saving, which nobody guesses. The
                            // button says so rather than leaving a note that can only be rewritten.
                            <button
                              type="button"
                              className="link"
                              disabled={savingNoteId === work.id}
                              onClick={() => commitNote(work, null)}
                            >
                              Delete note
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  )}
                </Fragment>
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
