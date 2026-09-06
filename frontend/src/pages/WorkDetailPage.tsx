import { useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import type { Ao3TagType, ReadingStatus, WorkDetail, WorkState } from '../api/types';
import { EmptyState } from '../components/EmptyState';
import { FavoriteToggle } from '../components/FavoriteToggle';
import { Icon } from '../components/Icon';
import { RatingStars } from '../components/RatingStars';
import { SkeletonRows } from '../components/Skeleton';
import { WorkDownloads } from '../components/WorkDownloads';
import { formatDate, formatDateTime } from '../format';
import { READING_STATUS_LABELS } from '../readingStatus';
import { warningChipClass } from '../warnings';

/** Matches `MaxNoteLength` on `UserWorkState.Note`, so an over-long note is refused here first. */
const MAX_NOTE_LENGTH = 4000;

/**
 * Tag kinds in the order AO3 lists them on a work, each with the heading it is shown under.
 *
 * An array rather than the tag list's own order, because a work with no characters tagged should
 * not silently move its freeforms up into the place characters usually occupy — the headings are
 * how a reader finds the kind they are looking for.
 */
const TAG_GROUPS: { type: Ao3TagType; heading: string }[] = [
  { type: 'Fandom', heading: 'Fandoms' },
  { type: 'Relationship', heading: 'Relationships' },
  { type: 'Character', heading: 'Characters' },
  { type: 'Freeform', heading: 'Additional tags' },
  { type: 'Warning', heading: 'Warning tags' },
];

/** AO3 is where the work actually lives; this app only ever holds a description of it. */
function ao3WorkUrl(id: number): string {
  return `https://archiveofourown.org/works/${id}`;
}

function formatUpdated(work: WorkDetail): string {
  // An approximate timestamp came from a day-granular date, so rendering a time would invent
  // precision the scrape never had.
  return work.updatedAtIsApproximate ? formatDate(work.updatedAt) : formatDateTime(work.updatedAt);
}

/** The way back to the list, as chrome above the title rather than a footnote. */
function BackToWorks() {
  return (
    <Link className="back-link" to="/works">
      <Icon name="chevron-right" size="xs" className="back-link-icon" />
      Works
    </Link>
  );
}

export function WorkDetailPage() {
  const { workId: workIdParam } = useParams();
  const workId = Number(workIdParam);

  const [work, setWork] = useState<WorkDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Told apart from any other failure because it is not one: the work is simply not in this
  // reader's library, and the page that says so has nothing to retry.
  const [notFound, setNotFound] = useState(false);

  const [stateError, setStateError] = useState<string | null>(null);
  const [noteDraft, setNoteDraft] = useState<string | null>(null);
  const [savingNote, setSavingNote] = useState(false);

  // Bumped on every write. A response may only touch the page while its own token is the newest:
  // two edits in flight together otherwise let the slower, older answer land last and undo the
  // newer one. The same rule the feed's rows follow, for the same reason.
  const stateWriteToken = useRef(0);
  // The tail of this page's write chain, so two edits are never in flight together. The token above
  // only decides which *response* may repaint the page; the requests themselves are whole-state
  // replacements, so letting them overlap lets the older one land last and leaves the database
  // holding the edit the page has already stopped showing.
  const stateWriteChain = useRef<Promise<unknown> | null>(null);

  useEffect(() => {
    let current = true;

    setWork(null);
    setError(null);
    setNotFound(false);
    setStateError(null);
    setNoteDraft(null);
    setSavingNote(false);

    // A different work is a different page, and a write still in flight belongs to the last one.
    // Bumping the token here is what stops its response stamping one work's marks onto another:
    // React Router reuses this component between two work pages rather than unmounting it, so
    // without this the load path's own guard would be the only one and the write path would have
    // none.
    stateWriteToken.current += 1;
    // A different work's writes have no order to keep with this one's, and making them wait would
    // only hold this page's first edit behind a request that has left the page.
    stateWriteChain.current = null;

    if (!Number.isInteger(workId) || workId <= 0) {
      setNotFound(true);
      return;
    }

    api
      .getWork(workId)
      .then((loaded) => {
        if (current) setWork(loaded);
      })
      .catch((err) => {
        if (!current) return;
        if (err instanceof ApiError && err.status === 404) setNotFound(true);
        else setError(err instanceof ApiError ? err.message : 'Failed to load this work.');
      });

    return () => {
      current = false;
    };
  }, [workId]);

  /**
   * Sends the whole state and reconciles the page against what came back. Resolves to whether the
   * write landed, so the note editor can keep its text when it did not.
   *
   * Whole, because `PUT /works/{id}/state` replaces: a request naming only the field that changed
   * would clear the other two. Optimistic, because a rating that waits on a round trip does not
   * feel like a click — but the response, not the guess, is what the page ends up showing.
   */
  const saveState = (next: WorkState): Promise<boolean> => {
    if (work === null) return Promise.resolve(false);

    const previous = work.state;
    const token = stateWriteToken.current + 1;
    stateWriteToken.current = token;
    const isCurrent = () => stateWriteToken.current === token;

    const applyState = (state: WorkState) =>
      setWork((loaded) => (loaded === null ? loaded : { ...loaded, state }));

    applyState(next);
    setStateError(null);

    // Queued behind the last write rather than sent now, so the server ends up holding the
    // reader's last edit rather than whichever request happened to arrive last. A failed
    // predecessor still lets this one go — the reader asked for it, and abandoning it silently
    // would be the same lost edit by another route.
    const write = (stateWriteChain.current ?? Promise.resolve()).then(() =>
      api
        .setWorkState(work.id, next)
        .then((saved) => {
          if (isCurrent()) applyState(saved);
          return true;
        })
        .catch((err) => {
          if (isCurrent()) {
            applyState(previous);
            setStateError(
              err instanceof ApiError ? err.message : 'Could not save that — nothing changed.',
            );
          }
          return false;
        }),
    );

    stateWriteChain.current = write;
    return write;
  };

  const commitNote = (note: string | null) => {
    if (work === null) return;

    // What the box held when Save was pressed. The textarea stays editable through the round trip,
    // so dropping the draft on success unconditionally would throw away anything typed after the
    // click — the reader would watch their own characters vanish.
    const sent = noteDraft;

    setSavingNote(true);
    saveState({ ...work.state, note })
      .then((saved) => {
        // A refused write leaves the editor holding what was typed: the error line beside it says
        // what happened, and dropping the text would lose the writing as well as the write.
        if (saved) setNoteDraft((draft) => (draft === sent ? null : draft));
      })
      .finally(() => setSavingNote(false));
  };

  if (notFound) {
    return (
      <div className="page">
        <BackToWorks />
        <EmptyState
          title="Work not found"
          action={
            <Link className="button" to="/works">
              Back to Works
            </Link>
          }
        >
          Nothing you follow carries this work — either it was never scraped, or the ship it came
          from is one you have since unfollowed.
        </EmptyState>
      </div>
    );
  }

  if (error !== null) {
    return (
      <div className="page">
        <BackToWorks />
        <h1>Work</h1>
        <p className="error" role="alert">
          {error}
        </p>
      </div>
    );
  }

  if (work === null) {
    return (
      <div className="page">
        <BackToWorks />
        {/* A title-sized bar and a few lines where the summary will be. */}
        <SkeletonRows rows={1} kind="title" />
        <SkeletonRows rows={4} kind="text" />
      </div>
    );
  }

  const note = noteDraft ?? work.state.note ?? '';

  return (
    <div className="page">
      <BackToWorks />

      <h1 className="work-detail-title">{work.title}</h1>

      <p className="work-detail-byline">
        {work.isAnonymous
          ? 'Anonymous'
          : work.authors.length > 0
            ? work.authors.join(', ')
            : 'Unknown author'}
        {' · '}
        <a href={ao3WorkUrl(work.id)} target="_blank" rel="noreferrer">
          Read on AO3
        </a>
      </p>

      <div className="work-chips">
        {work.ships.map((ship) =>
          // A tag the work has left is not a link: that feed no longer holds this work, so the
          // chip would land the reader on a page their work is missing from. The others go back
          // into the feed narrowed to that ship, with the reader's default filter left off —
          // arriving at a ship's works and seeing none of them because a saved default was applied
          // would read as the ship being empty.
          ship.missingSinceAt === null ? (
            <Link
              key={ship.shipId}
              className="chip chip-ship"
              to={`/works?shipId=${ship.shipId}&filter=none`}
            >
              {ship.tagName}
            </Link>
          ) : (
            <span
              key={ship.shipId}
              className="chip chip-left"
              title={`Not found under this tag since ${new Date(ship.missingSinceAt).toLocaleDateString()}. Everything you have marked here is untouched, and it comes back if the tag lists it again.`}
            >
              Left {ship.tagName}
            </span>
          ),
        )}
        <span className="chip">{work.rating}</span>
        {work.categories.map((category) => (
          <span key={category} className="chip">
            {category}
          </span>
        ))}
        {work.warnings.map((warning) => (
          <span key={warning} className={warningChipClass(warning)}>
            {warning}
          </span>
        ))}
        {work.isRestricted && <span className="chip">Registered users only</span>}
      </div>

      <section className="work-detail-section">
        <h2>Summary</h2>
        {work.summarySafeHtml === null ? (
          <p className="hint">No summary — AO3’s listing carried none for this work.</p>
        ) : (
          // Sanitized by the server before it was ever sent: an allowlist of element names and no
          // attributes at all, so what arrives here has nowhere left to carry script. `WorkDetail`
          // names the field for that, and the raw column this is derived from reaches no client.
          <div
            className="work-detail-summary"
            dangerouslySetInnerHTML={{ __html: work.summarySafeHtml }}
          />
        )}
      </section>

      <section className="work-detail-section">
        <h2>Yours</h2>
        <div className="work-detail-state">
          <label>
            Reading status
            <select
              value={work.state.status}
              onChange={(e) => {
                void saveState({ ...work.state, status: e.target.value as ReadingStatus });
              }}
            >
              {Object.entries(READING_STATUS_LABELS).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </select>
          </label>

          <RatingStars
            label={`Your rating of ${work.title}`}
            value={work.state.rating}
            onChange={(rating) => {
              void saveState({ ...work.state, rating });
            }}
          />

          <span className="work-detail-favorite">
            <FavoriteToggle
              title={work.title}
              value={work.state.isFavorite}
              onChange={(isFavorite) => {
                void saveState({ ...work.state, isFavorite });
              }}
            />
            {/* Spelled out beside the heart, as the rating is beside its stars: the date is the
                one thing the mark knows that a filled glyph cannot say. Keyed on the flag, not the
                date — a click is shown before the server answers, and the date is the server's. */}
            <span className="work-detail-favorite-caption">
              {!work.state.isFavorite
                ? 'Not a favorite'
                : work.state.favoritedAt === null
                  ? 'Favorite'
                  : `Favorited ${formatDate(work.state.favoritedAt)}`}
            </span>
          </span>
        </div>

        <label className="work-note-field">
          Your note
          <textarea
            value={note}
            maxLength={MAX_NOTE_LENGTH}
            rows={4}
            onChange={(e) => setNoteDraft(e.target.value)}
          />
        </label>

        <div className="button-row">
          <button
            type="button"
            disabled={savingNote || noteDraft === null}
            onClick={() => commitNote(note.trim() || null)}
          >
            Save note
          </button>
          {noteDraft !== null && (
            <button type="button" className="link" onClick={() => setNoteDraft(null)}>
              Discard changes
            </button>
          )}
          {work.state.note !== null && (
            // Clearing is emptying the box and saving, which nobody guesses. The button says so
            // rather than leaving a note that can only ever be rewritten.
            <button
              type="button"
              className="link"
              disabled={savingNote}
              onClick={() => {
                setNoteDraft(null);
                commitNote(null);
              }}
            >
              Delete note
            </button>
          )}
        </div>

        {stateError !== null && (
          <p className="error" role="alert">
            {stateError}
          </p>
        )}
      </section>

      <WorkDownloads workId={work.id} />

      <section className="work-detail-section">
        <h2>Tags</h2>
        {work.detailFetchedAt === null && (
          <p className="hint">
            These are the tags AO3’s listing showed. The complete list lives on the work’s own page,
            which nothing has fetched yet.
          </p>
        )}
        {work.tags.length === 0 ? (
          <p className="hint">No tags were read for this work.</p>
        ) : (
          TAG_GROUPS.map(({ type, heading }) => {
            const tags = work.tags.filter((tag) => tag.type === type);
            if (tags.length === 0) return null;

            return (
              <div key={type} className="work-detail-tag-group">
                <h3>{heading}</h3>
                <div className="work-chips">
                  {tags.map((tag) => (
                    <span key={tag.name} className="chip">
                      {tag.name}
                    </span>
                  ))}
                </div>
              </div>
            );
          })
        )}
      </section>

      {work.series.length > 0 && (
        <section className="work-detail-section">
          <h2>Series</h2>
          <ul className="work-detail-series">
            {work.series.map((series) => (
              <li key={series.id}>
                {series.title}
                {series.part !== null && <span className="hint"> · part {series.part}</span>}
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="work-detail-section">
        <h2>Details</h2>
        <dl className="work-detail-facts">
          <dt>Words</dt>
          <dd>{work.wordCount.toLocaleString()}</dd>

          <dt>Chapters</dt>
          <dd>
            {work.chapterCount}/{work.plannedChapterCount ?? '?'}
            {work.isComplete ? ' · complete' : ' · in progress'}
          </dd>

          <dt>Kudos</dt>
          <dd>{work.kudos.toLocaleString()}</dd>

          <dt>Hits</dt>
          <dd>{work.hits.toLocaleString()}</dd>

          <dt>Bookmarks</dt>
          <dd>{work.bookmarks.toLocaleString()}</dd>

          <dt>Comments</dt>
          <dd>{work.commentCount.toLocaleString()}</dd>

          <dt>Collections</dt>
          <dd>{work.collectionCount.toLocaleString()}</dd>

          <dt>Language</dt>
          <dd>{work.languageName ?? 'Not read'}</dd>

          <dt>Updated</dt>
          <dd>{formatUpdated(work)}</dd>

          <dt>Published</dt>
          {/* A listing blurb does not carry the publication date at all, so null here is "nobody
              has fetched the work's own page", not "AO3 has no date". Saying which is the whole
              point of showing the row while it is still empty. */}
          <dd>
            {work.publishedAt !== null ? (
              new Date(work.publishedAt).toLocaleDateString()
            ) : (
              <span className="hint">Not fetched yet</span>
            )}
          </dd>

          <dt>First seen here</dt>
          <dd>{new Date(work.firstSeenAt).toLocaleDateString()}</dd>

          <dt>Last seen in a listing</dt>
          <dd>{new Date(work.lastSeenAt).toLocaleDateString()}</dd>
        </dl>
      </section>
    </div>
  );
}
