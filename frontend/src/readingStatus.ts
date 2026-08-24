import type { ReadingStatus } from './api/types';

/**
 * What each reading status is called, in the order a reader moves through them.
 *
 * Shared between the Works rows and the filter editor rather than declared beside either: they are
 * the same five marks, and two copies would let the feed and the filter that narrows it call one
 * status by two names.
 *
 * These are this app's own words, not AO3's, which is why they are here and not in
 * `/api/lookups/vocabulary` — that endpoint exists so AO3's wording has exactly one home, and it is
 * the server, where the parser reading those words off a blurb lives.
 */
export const READING_STATUS_LABELS: Record<ReadingStatus, string> = {
  None: 'Not set',
  ToRead: 'To read',
  Reading: 'Reading',
  Read: 'Read',
  Dropped: 'Dropped',
};

export const READING_STATUSES: ReadingStatus[] = ['None', 'ToRead', 'Reading', 'Read', 'Dropped'];

/**
 * Half-stars as a reader says them: 1 is half a star, 7 is three and a half. The same scale as
 * `UserWorkState.Rating` and the same wording as the stars on a Works row.
 */
export function describeHalfStars(half: number): string {
  const stars = half / 2;
  return `${stars} ${stars === 1 ? 'star' : 'stars'}`;
}

/** Every bound a rating criterion may take, lowest first. */
export const HALF_STAR_VALUES: number[] = Array.from({ length: 10 }, (_, index) => index + 1);
