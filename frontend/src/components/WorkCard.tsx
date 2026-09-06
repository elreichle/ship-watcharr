/*
 * One work in the list: a card rather than a table row.
 *
 * A row put the title, the reader's marks and six numbers on one line and asked the eye to track
 * across them; a card puts the work first — title, byline, tags, the reader's own marks in its
 * margin — and keeps the archive's numbers in a strip of their own where they can be read or
 * skipped. The card owns nothing: every mark is written through `onStateChange`, and the note
 * editor's draft and open state live on the page, so a page of cards behaves as one list.
 */

import { Link } from 'react-router-dom';
import type { ReadingStatus, WorkListItem, WorkState } from '../api/types';
import { formatDate, formatDateTime } from '../format';
import { READING_STATUS_LABELS } from '../readingStatus';
import { warningChipClass } from '../warnings';
import { FavoriteToggle } from './FavoriteToggle';
import { RatingStars } from './RatingStars';

/** Matches `MaxNoteLength` on `UserWorkState.Note`, so an over-long note is refused here first. */
const MAX_NOTE_LENGTH = 4000;

/** AO3 is where the work actually lives; this app only ever holds a description of it. */
function ao3WorkUrl(id: number): string {
  return `https://archiveofourown.org/works/${id}`;
}

function formatUpdated(work: WorkListItem): string {
  // An approximate timestamp came from a day-granular date, so rendering a time would invent
  // precision the scrape never had.
  return work.updatedAtIsApproximate ? formatDate(work.updatedAt) : formatDateTime(work.updatedAt);
}

interface WorkCardProps {
  work: WorkListItem;
  /** Sends the reader's whole state for this work; the page reconciles the card with the reply. */
  onStateChange: (next: WorkState) => void;
  /** A write to this work that was refused, shown under the marks it belongs to. */
  stateError?: string;
  /** The note editor, when it is this card's turn to show it. */
  noteEditor: {
    open: boolean;
    draft: string;
    saving: boolean;
    onToggle: () => void;
    onClose: () => void;
    onDraftChange: (draft: string) => void;
    onSave: () => void;
    onDelete: () => void;
  };
}

export function WorkCard({ work, onStateChange, stateError, noteEditor }: WorkCardProps) {
  return (
    // The status rides the card so the one the reader is in the middle of can carry the reading
    // spine — see .work-card[data-status='Reading'].
    <li className="work-card" data-status={work.state.status}>
      <div className="work-card-body">
        {/* The title opens this app's own page for the work — everything known about it, and the
            reader's own marks — rather than leaving for the archive. AO3 is one click further on,
            in the byline. */}
        <h2 className="work-card-title">
          <Link className="title-link" to={`/works/${work.id}`}>
            {work.title}
          </Link>
        </h2>
        <p className="work-byline">
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
        </p>
        <div className="work-chips">
          {work.ships.map((ship) => (
            <span key={ship} className="chip chip-ship">
              {ship}
            </span>
          ))}
          {/* A work is here despite a tag having let go either because another followed tag still
              carries it or because this reader marked it — see WorkQueries.Library. Which of the
              two is visible from the chips beside this one, so the chip says the fact and not the
              reason. */}
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
            <span key={warning} className={warningChipClass(warning)}>
              {warning}
            </span>
          ))}
          {work.isRestricted && <span className="chip">Registered users only</span>}
        </div>
      </div>

      {/* The archive's numbers, kept together and apart from the words: a strip the eye can run
          along or skip. Named for whose rating it is, since the reader's own sits below. */}
      <dl className="work-card-facts">
        <div className="work-card-fact work-card-fact-rating">
          <dt>AO3 rating</dt>
          <dd>{work.rating}</dd>
        </div>
        <div className="work-card-fact">
          <dt>Words</dt>
          <dd className="numeric">{work.wordCount.toLocaleString()}</dd>
        </div>
        <div className="work-card-fact">
          <dt>Chapters</dt>
          <dd className="numeric">
            {work.chapterCount}/{work.plannedChapterCount ?? '?'}
            {work.isComplete && <span className="work-complete"> complete</span>}
          </dd>
        </div>
        <div className="work-card-fact">
          <dt>Kudos</dt>
          <dd className="numeric">{work.kudos.toLocaleString()}</dd>
        </div>
        <div className="work-card-fact">
          <dt>Hits</dt>
          <dd className="numeric">{work.hits.toLocaleString()}</dd>
        </div>
        <div className="work-card-fact">
          <dt>Updated</dt>
          <dd className="numeric">{formatUpdated(work)}</dd>
        </div>
      </dl>

      {/* The reader's own marks, along the foot of the card: the work is the thing, the marks are
          notes in its margin. */}
      <div className="work-marks">
        <FavoriteToggle
          title={work.title}
          value={work.state.isFavorite}
          onChange={(isFavorite) => onStateChange({ ...work.state, isFavorite })}
        />
        <select
          aria-label={`Reading status for ${work.title}`}
          value={work.state.status}
          onChange={(e) =>
            onStateChange({ ...work.state, status: e.target.value as ReadingStatus })
          }
        >
          {Object.entries(READING_STATUS_LABELS).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </select>
        <RatingStars
          label={`Your rating of ${work.title}`}
          value={work.state.rating}
          onChange={(rating) => onStateChange({ ...work.state, rating })}
        />
        <button
          type="button"
          className="link"
          aria-expanded={noteEditor.open}
          onClick={noteEditor.onToggle}
        >
          {work.state.note === null ? 'Add note' : 'Note'}
        </button>
      </div>
      {stateError && (
        <p className="error work-state-error" role="alert">
          {stateError}
        </p>
      )}

      {noteEditor.open && (
        // Inside the card rather than a popover: a floating editor over a scrolling list is where
        // a half-typed note goes to get lost.
        <div className="work-note-editor">
          <label className="work-note-field">
            Your note on “{work.title}”
            <textarea
              value={noteEditor.draft}
              maxLength={MAX_NOTE_LENGTH}
              rows={4}
              autoFocus
              onChange={(e) => noteEditor.onDraftChange(e.target.value)}
            />
          </label>
          <div className="button-row">
            <button type="button" disabled={noteEditor.saving} onClick={noteEditor.onSave}>
              Save note
            </button>
            <button type="button" className="link" onClick={noteEditor.onClose}>
              Close
            </button>
            {work.state.note !== null && (
              // Clearing is emptying the box and saving, which nobody guesses. The button says so
              // rather than leaving a note that can only be rewritten.
              <button
                type="button"
                className="link"
                disabled={noteEditor.saving}
                onClick={noteEditor.onDelete}
              >
                Delete note
              </button>
            )}
          </div>
        </div>
      )}
    </li>
  );
}
