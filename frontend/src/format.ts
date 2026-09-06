/*
 * Date formatting, in one place.
 *
 * Every timestamp in the interface used to go through `toLocaleString()`, which spells out the
 * seconds ("9/5/2026, 12:56:00 PM"). Nothing here happens to the second, and the extra width was
 * what wrapped the Downloads and Notifications rows on a phone. These read "Sep 5, 2026, 12:56 PM"
 * in an English locale and follow the browser's language everywhere else.
 */

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const dateOnly = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });

/** A moment, to the minute. */
export function formatDateTime(value: string | Date): string {
  return dateTime.format(new Date(value));
}

/** A day, for timestamps that were never more precise than one. */
export function formatDate(value: string | Date): string {
  return dateOnly.format(new Date(value));
}

/** A moment or, for a column where nothing has happened yet, a dash. */
export function formatDateTimeOrDash(value: string | null): string {
  return value ? formatDateTime(value) : '—';
}
