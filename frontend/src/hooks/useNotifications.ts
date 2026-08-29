import { createContext, useContext } from 'react';

export interface NotificationsValue {
  /** How many the reader has not marked read, or null until the first poll lands. */
  unread: number | null;
  /** Re-reads the count now, outside the poll. */
  refresh: () => Promise<void>;
  /**
   * Takes the count straight from a mark-read response.
   *
   * Every write endpoint answers with the count that survived it, so the badge can drop the
   * instant a row is marked read instead of waiting up to a minute for the next poll — and the
   * number it drops to is the server's own, not one the page worked out by subtracting.
   */
  applyUnread: (unread: number) => void;
}

/**
 * Shared because two places need the same number: the sidebar badge shows it, and the
 * notifications page changes it. A page-local copy in each would leave the badge lit for a poll
 * interval after the list it counts had been read.
 *
 * The context and its hook live apart from the provider component so that neither file exports
 * both a component and something else, which is what Fast Refresh needs to reload either one.
 */
export const NotificationsContext = createContext<NotificationsValue | undefined>(undefined);

export function useNotifications(): NotificationsValue {
  const ctx = useContext(NotificationsContext);
  if (!ctx) throw new Error('useNotifications must be used within a NotificationsProvider');
  return ctx;
}
