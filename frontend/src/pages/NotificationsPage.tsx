import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import type { PagedResult, ShipNotification } from '../api/types';
import { EmptyState } from '../components/EmptyState';
import { SkeletonRows } from '../components/Skeleton';
import { formatDateTime } from '../format';
import { useNotifications } from '../hooks/useNotifications';

const PAGE_SIZE = 25;

export function NotificationsPage() {
  const { unread, applyUnread, refresh } = useNotifications();

  const [unreadOnly, setUnreadOnly] = useState(false);
  const [page, setPage] = useState(1);
  const [result, setResult] = useState<PagedResult<ShipNotification> | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  /**
   * Bumped by anything that has made the loaded list wrong. A token the load effect depends on,
   * rather than a function the click calls: it means there is exactly one place that fetches, so a
   * reload from a click and a reload from a page change cancel each other the same way.
   */
  const [reloadToken, setReloadToken] = useState(0);
  const reload = () => setReloadToken((token) => token + 1);

  useEffect(() => {
    let current = true;
    setLoading(true);

    api
      .getNotifications({ unreadOnly, page, pageSize: PAGE_SIZE })
      .then((loaded) => {
        // Guards against a slow request landing after a faster later one and overwriting it.
        if (!current) return;

        setResult(loaded);
        setError(null);

        // Reading the last unread row on a page leaves the caller past the end of a list that
        // just got shorter. Stepping back is what stops that showing as "nothing here" — and the
        // floor of 1 is for the list emptying completely, where the server answers 0 pages and
        // there is still a first page to be on.
        if (loaded.items.length === 0 && page > loaded.totalPages) {
          setPage(Math.max(loaded.totalPages, 1));
        }
      })
      .catch((err) => {
        if (!current) return;
        setError(err instanceof ApiError ? err.message : 'Could not load your notifications.');
      })
      .finally(() => {
        if (current) setLoading(false);
      });

    return () => {
      current = false;
    };
  }, [page, unreadOnly, reloadToken]);

  // The count is polled once a minute for a badge; arriving at the page it labels is worth one
  // read of its own. Without it a reader who opens this list in the gap after a poll sees unread
  // rows while the header still says none, and "Mark all read" stays greyed out over them.
  useEffect(() => {
    void refresh();
  }, [refresh]);

  /**
   * Marks rows read, then reloads.
   *
   * The count comes from the response rather than from subtracting here: a list loaded a minute ago
   * may name rows another tab has already read, and the server's answer is the only one that has
   * counted what actually changed.
   */
  const markRead = async (ids: number[]) => {
    try {
      const { unread: surviving } = await api.markNotificationsRead(ids);
      applyUnread(surviving);
      setError(null);
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not mark that read.');
      // The badge may now disagree with a write that half-happened; ask for the truth.
      void refresh();
    }
  };

  const markAllRead = async () => {
    try {
      const { unread: surviving } = await api.markAllNotificationsRead();
      applyUnread(surviving);
      setError(null);
      reload();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not mark everything read.');
      void refresh();
    }
  };

  const showUnread = (only: boolean) => {
    setUnreadOnly(only);
    setPage(1);
  };

  const totalPages = result?.totalPages ?? 0;

  return (
    <div className="page">
      <h1>Notifications</h1>

      <p className="hint">
        One line for each work a ship you follow has gained since you started following it. In-app
        only — nothing here is emailed or pushed anywhere, and unfollowing a ship takes its lines
        with it.
      </p>

      <div className="notifications-controls">
        <div className="segmented" role="group" aria-label="Which notifications to show">
          <button
            type="button"
            className={unreadOnly ? 'segmented-option' : 'segmented-option is-active'}
            aria-pressed={!unreadOnly}
            onClick={() => showUnread(false)}
          >
            All
          </button>
          <button
            type="button"
            className={unreadOnly ? 'segmented-option is-active' : 'segmented-option'}
            aria-pressed={unreadOnly}
            onClick={() => showUnread(true)}
          >
            Unread{unread ? ` (${unread})` : ''}
          </button>
        </div>

        <button type="button" disabled={!unread || loading} onClick={() => void markAllRead()}>
          Mark all read
        </button>
      </div>

      {error !== null && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      {result === null ? (
        // Guarded on the error, as the other lists are: a first load that failed has no result to
        // show and is not still trying, so placeholder rows under the message would be a second,
        // false account of the same thing.
        !error && <SkeletonRows rows={8} kind="line" />
      ) : result.items.length === 0 ? (
        unreadOnly ? (
          <EmptyState
            title="Nothing unread"
            action={
              <button type="button" onClick={() => showUnread(false)}>
                Show everything
              </button>
            }
          >
            Everything a followed ship has gained has been seen.
          </EmptyState>
        ) : (
          <EmptyState title="Nothing yet">
            A ship you follow gaining a work is what puts a line here — the first scrape of a newly
            followed ship fills the library rather than this list.
          </EmptyState>
        )
      ) : (
        <>
          <ul className="notification-list">
            {result.items.map((notification) => (
              <li
                key={notification.id}
                className="notification"
                data-unread={notification.readAt === null || undefined}
              >
                <div className="notification-body">
                  <Link className="title-link" to={`/works/${notification.workId}`}>
                    {notification.workTitle}
                  </Link>
                  <span className="notification-meta">
                    {notification.shipName} · {formatDateTime(notification.createdAt)}
                  </span>
                </div>

                {notification.readAt === null ? (
                  <button
                    type="button"
                    className="link"
                    disabled={loading}
                    onClick={() => void markRead([notification.id])}
                  >
                    Mark read
                  </button>
                ) : (
                  <span className="notification-read">
                    Read {formatDateTime(notification.readAt)}
                  </span>
                )}
              </li>
            ))}
          </ul>

          <div className="pager">
            <button type="button" disabled={page <= 1 || loading} onClick={() => setPage(page - 1)}>
              Previous
            </button>
            <span className="hint">
              Page {result.page} of {Math.max(totalPages, 1)} ·{' '}
              {result.totalCount.toLocaleString()} notifications
            </span>
            <button
              type="button"
              disabled={page >= totalPages || loading}
              onClick={() => setPage(page + 1)}
            >
              Next
            </button>
          </div>
        </>
      )}
    </div>
  );
}
