/*
 * The reader's own rating of a work, on the half-star scale `UserWorkState` stores.
 *
 * Ten steps, not five: 7 is three and a half stars, and `null` is unrated rather than the lowest
 * score — a distinction the whole column exists to keep, so the control has to show it too.
 *
 * The glyph is the text star rather than an entry in the Lucide set in `Icon.tsx`. A half-filled
 * star is a clipped copy of the *same* character laid over the empty one, so the two layers cannot
 * drift out of register the way two different glyphs could, and nothing here has to reproduce path
 * data from a package this project does not install.
 */

import type { KeyboardEvent, MouseEvent } from 'react';

/** Half-stars, matching `CK_UserWorkStates_Rating` and `WorksController`'s MinRating/MaxRating. */
const MIN_RATING = 1;
const MAX_RATING = 10;

const STAR_COUNT = MAX_RATING / 2;

/** How full one star is drawn, given a rating measured in half-stars. */
function fillOf(value: number | null, starIndex: number): 'none' | 'half' | 'full' {
  const halves = (value ?? 0) - starIndex * 2;
  if (halves >= 2) return 'full';
  return halves === 1 ? 'half' : 'none';
}

/** What the value is called out loud. Unrated is a word, not a zero. */
function ratingLabel(value: number | null): string {
  if (value === null) return 'Unrated';
  const stars = value / 2;
  return `${stars} ${stars === 1 ? 'star' : 'stars'}`;
}

/** One arrow-key step. Stepping below the lowest star lands on unrated, not on the lowest star. */
function step(value: number | null, delta: number): number | null {
  const stepped = (value ?? 0) + delta;
  return stepped < MIN_RATING ? null : Math.min(stepped, MAX_RATING);
}

interface RatingStarsProps {
  /** Half-stars, 1–10. Null is unrated. */
  value: number | null;
  onChange: (value: number | null) => void;
  /** The accessible name, since nothing visible labels the control inside a table row. */
  label: string;
}

export function RatingStars({ value, onChange, label }: RatingStarsProps) {
  const handleClick = (event: MouseEvent<HTMLSpanElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    if (rect.width === 0) return;

    // Which half-star the pointer landed on. The stars sit flush against each other precisely so
    // this stays a straight division of the row's width.
    const half = Math.ceil(((event.clientX - rect.left) / rect.width) * MAX_RATING);
    const clicked = Math.min(Math.max(half, MIN_RATING), MAX_RATING);

    // Clicking the rating you already gave clears it. Without that there is no pointer-only way
    // back to unrated, and a rating that cannot be taken back is worse than one that can.
    onChange(clicked === value ? null : clicked);
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLSpanElement>) => {
    const commit = (next: number | null) => {
      // Called even when the value does not move, so an arrow key never scrolls the page instead.
      event.preventDefault();
      if (next !== value) onChange(next);
    };

    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowUp':
        return commit(step(value, 1));
      case 'ArrowLeft':
      case 'ArrowDown':
        return commit(step(value, -1));
      case 'Home':
      case 'Delete':
      case 'Backspace':
      case '0':
        return commit(null);
      case 'End':
        return commit(MAX_RATING);
      default:
        return;
    }
  };

  return (
    <span className="rating" data-rated={value !== null}>
      {/* A slider rather than a row of buttons: one tab stop per work instead of ten, which is
          what keeps a page of 25 rows keyboard-navigable at all. */}
      <span
        className="rating-stars"
        role="slider"
        tabIndex={0}
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={MAX_RATING}
        aria-valuenow={value ?? 0}
        aria-valuetext={ratingLabel(value)}
        title="Click to rate in half-stars. Click the rating you gave to clear it."
        onClick={handleClick}
        onKeyDown={handleKeyDown}
      >
        {Array.from({ length: STAR_COUNT }, (_, index) => (
          // Hidden from assistive tech: the value is already on the slider, and five repeated
          // stars read as noise beside it.
          <span key={index} className="rating-star" data-fill={fillOf(value, index)} aria-hidden="true">
            ★<span className="rating-star-fill">★</span>
          </span>
        ))}
      </span>
      {/* Spelled out beside the stars, because "unrated" and "half a star" are one clipped glyph
          apart and the difference matters more than the pixels it costs. */}
      <span className="rating-value">{value === null ? 'Unrated' : value / 2}</span>
    </span>
  );
}
