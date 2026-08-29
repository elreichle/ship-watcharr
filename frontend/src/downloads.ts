import type { DownloadStatus } from './api/types';

/**
 * What each download status is called, for a reader who has never seen this queue before.
 *
 * Shared between the Downloads list and the format buttons on a work rather than declared beside
 * either: one request is shown in both places, and two copies would let them call the same row by
 * two different names.
 */
export const DOWNLOAD_STATUS_LABELS: Record<DownloadStatus, string> = {
  Pending: 'Queued',
  Downloading: 'Fetching…',
  Complete: 'Ready',
  Failed: 'Failed',
};

/**
 * A size a reader can read, from the byte count a request carries.
 *
 * Powers of 1024 with the units they are usually written as, because that is what every e-reader
 * and file manager a downloaded work lands in will say about the same file.
 */
export function formatSize(bytes: number | null): string {
  if (bytes === null) return '—';
  if (bytes < 1024) return `${bytes} B`;

  const units = ['KB', 'MB', 'GB'];
  let size = bytes / 1024;
  let unit = 0;

  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }

  // One decimal below ten and none above: "1.4 MB" is worth saying, "148.3 KB" is not.
  return `${size < 10 ? size.toFixed(1) : Math.round(size)} ${units[unit]}`;
}
