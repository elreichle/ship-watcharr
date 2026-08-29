import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api } from '../api/client';
import { NotificationsContext } from '../hooks/useNotifications';

/**
 * How long between polls of the unread count.
 *
 * A notification is produced by a scrape run, and a ship's job runs on a six-hour interval
 * (`ScrapeJob.Interval`) with every request inside it behind the shared 5-8s rate gate. So a poll
 * faster than this is mostly asking a question whose answer cannot have changed. A minute keeps a
 * dashboard left open all day at sixty requests an hour against its own server, which is cheap,
 * and still lights the badge within a glance of a run finishing.
 */
const POLL_INTERVAL_MS = 60_000;

function isVisible(): boolean {
  return document.visibilityState === 'visible';
}

/**
 * Holds the unread notification count for the signed-in shell, and keeps it current.
 *
 * Mounted inside the layout rather than at the app root, so the polling starts when a reader signs
 * in and stops when they leave — an unauthenticated page polling an authorized endpoint would do
 * nothing but 401 once a minute.
 */
export function NotificationsProvider({ children }: { children: ReactNode }) {
  const [unread, setUnread] = useState<number | null>(null);

  // A backgrounded tab is nobody looking at the badge, so it should cost nothing. Tracked as state
  // rather than read inside the poll, so that coming back to the tab re-runs the effect below and
  // re-reads the count at once instead of after the rest of the interval.
  const [visible, setVisible] = useState(isVisible);

  // Checked before every write: a response can land after the shell has unmounted, and `refresh`
  // has no cleanup of its own to cancel the request it is waiting on.
  const mounted = useRef(true);

  /**
   * How many times a write has told us the count.
   *
   * A poll that went out before a mark-read must not apply its own answer: it asked the question
   * while the badge was still lit, so landing afterwards would relight it over a list the reader
   * has just read — and leave it wrong until the next poll a minute later.
   */
  const writes = useRef(0);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  useEffect(() => {
    const onChange = () => setVisible(isVisible());
    document.addEventListener('visibilitychange', onChange);
    return () => document.removeEventListener('visibilitychange', onChange);
  }, []);

  const applyUnread = useCallback((count: number) => {
    writes.current++;
    setUnread(count);
  }, []);

  const refresh = useCallback(async () => {
    const seen = writes.current;

    try {
      const { unread: count } = await api.getUnreadNotificationCount();
      if (mounted.current && seen === writes.current) setUnread(count);
    } catch {
      // A failed poll leaves the last known count standing. It is still the last thing the server
      // said, and blanking the badge would make a momentary blip look like "nothing new".
    }
  }, []);

  useEffect(() => {
    if (!visible) return;

    // A chain of timeouts rather than an interval, as the download queue does: each poll is
    // scheduled by the one before it, so a slow response cannot leave two in flight.
    let running = true;
    let timer: number | undefined;

    const poll = async () => {
      await refresh();
      if (!running) return;
      timer = window.setTimeout(() => void poll(), POLL_INTERVAL_MS);
    };

    void poll();

    return () => {
      running = false;
      window.clearTimeout(timer);
    };
  }, [refresh, visible]);

  const value = useMemo(
    () => ({ unread, refresh, applyUnread }),
    [unread, refresh, applyUnread],
  );

  return <NotificationsContext.Provider value={value}>{children}</NotificationsContext.Provider>;
}
