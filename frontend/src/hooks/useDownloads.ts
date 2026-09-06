import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../api/client';
import type { Download, DownloadFormat } from '../api/types';

/**
 * How long between polls while anything is still moving. Long enough that a page left open is not
 * a load generator, short enough that a file arriving mid-glance shows up without a reload — the
 * fetch behind it waits on a 5-8s rate gate, so there is nothing to see faster than this.
 */
const POLL_INTERVAL_MS = 4000;

/** The two states a worker is still going to change. Anything else has settled. */
function isMoving(download: Download): boolean {
  return download.status === 'Pending' || download.status === 'Downloading';
}

export interface DownloadQueue {
  /** Every request this reader has made, newest first. Null until the first load lands. */
  downloads: Download[] | null;
  /** Why the last load or write failed, or null. */
  error: string | null;
  request: (workId: number, format: DownloadFormat) => Promise<void>;
  remove: (id: number) => Promise<void>;
}

/**
 * This reader's download queue, kept current for as long as something in it is still moving.
 *
 * One hook rather than two page-local copies, because the Downloads page and a work's own page
 * want the same three things — the list, a way to ask for a format, a way to drop a request — and
 * the interesting part is shared. The queue is drained by a background worker, so a page showing
 * it has to poll; a page that polled forever would be asking the server about a list that cannot
 * change any more. So the polling follows the data: it runs while something is Pending or
 * Downloading and stops on the load that finds nothing is.
 *
 * `revision` is for a caller that knows the queue may have changed behind this hook's back — a
 * favorite mark can queue a request the server made, not the reader's click. Bumping it reloads
 * the list and restarts polling, exactly as a click through `request` does.
 */
export function useDownloads(revision = 0): DownloadQueue {
  const [downloads, setDownloads] = useState<Download[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  /**
   * Bumped whenever a click queues something. The poll chain below hangs off it, so a request made
   * after everything had settled restarts polling — and restarting it this way replaces the chain
   * rather than adding a second one, because the effect's cleanup cancels the old one first.
   */
  const [queueGeneration, setQueueGeneration] = useState(0);

  // Checked by every write path before it touches state: a response can arrive after the page has
  // been left, and `request` and `remove` have no effect cleanup of their own to cancel them.
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const refresh = useCallback(async () => {
    try {
      const loaded = await api.getDownloads();
      if (!mounted.current) return loaded;

      setDownloads(loaded);
      setError(null);
      return loaded;
    } catch (err) {
      // A failed poll leaves the list standing: what it holds is still the last thing the server
      // said, and blanking it would make a momentary blip look like an empty queue.
      if (mounted.current) {
        setError(err instanceof ApiError ? err.message : 'Could not load your downloads.');
      }
      return null;
    }
  }, []);

  useEffect(() => {
    // A chain of timeouts rather than an interval: each poll is scheduled by the one before it, so
    // a slow response cannot leave two in flight, and the decision to stop is made against what
    // the response actually said rather than against state a render has yet to show.
    let running = true;
    let timer: number | undefined;

    const poll = async () => {
      const loaded = await refresh();
      if (!running) return;

      // A load that failed says nothing about whether anything is still moving, so the chain
      // carries on — the server being briefly unreachable is not a reason to stop watching.
      if (loaded !== null && !loaded.some(isMoving)) return;

      timer = window.setTimeout(() => void poll(), POLL_INTERVAL_MS);
    };

    void poll();

    return () => {
      running = false;
      window.clearTimeout(timer);
    };
  }, [refresh, queueGeneration, revision]);

  /**
   * Asks for a format, and puts the answer straight into the list.
   *
   * The answer is authoritative for that one row — it is what the server did with the click, which
   * for something already on disk is a request that is already Complete — so it lands without
   * waiting on a re-read. Restarting the poll is what covers the other case.
   */
  const request = useCallback(async (workId: number, format: DownloadFormat) => {
    try {
      const made = await api.requestDownload(workId, format);
      if (!mounted.current) return;

      setDownloads((current) => {
        if (current === null) return [made];

        // Replaced in place when it is already there, rather than moved to the front: asking again
        // for something already asked for answers with that same request, and re-arming one does
        // not change when it was requested — so a row that jumped to the top would jump back down
        // again on the next poll, which is the list reordering itself under a reader's cursor.
        const existing = current.findIndex((download) => download.id === made.id);
        if (existing === -1) return [made, ...current];

        return current.map((download, index) => (index === existing ? made : download));
      });
      setError(null);

      if (isMoving(made)) setQueueGeneration((generation) => generation + 1);
    } catch (err) {
      if (mounted.current) {
        setError(err instanceof ApiError ? err.message : 'Could not ask for that download.');
      }
    }
  }, []);

  const remove = useCallback(async (id: number) => {
    try {
      await api.deleteDownload(id);
      if (!mounted.current) return;

      setDownloads((current) => current?.filter((download) => download.id !== id) ?? null);
      setError(null);
    } catch (err) {
      if (mounted.current) {
        setError(err instanceof ApiError ? err.message : 'Could not drop that download.');
      }
    }
  }, []);

  return { downloads, error, request, remove };
}
