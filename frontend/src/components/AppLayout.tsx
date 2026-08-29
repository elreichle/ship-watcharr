import { useCallback, useEffect, useRef, useState } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { APP_NAME } from '../appInfo';
import { useMediaQuery } from '../hooks/useMediaQuery';
import { ErrorBoundary } from './ErrorBoundary';
import { Icon } from './Icon';
import { NotificationsProvider } from './NotificationsProvider';
import { Sidebar } from './Sidebar';

const SIDEBAR_KEY = 'shipwatcharr.sidebar';
const NARROW_QUERY = '(max-width: 1100px)';
const MOBILE_QUERY = '(max-width: 700px)';

function readCollapsed(): boolean {
  try {
    if (window.localStorage.getItem(SIDEBAR_KEY) === 'rail') return true;
  } catch {
    // Storage blocked — fall through to the viewport-derived default below.
  }
  return window.matchMedia(NARROW_QUERY).matches;
}

function writeCollapsed(collapsed: boolean): void {
  try {
    window.localStorage.setItem(SIDEBAR_KEY, collapsed ? 'rail' : 'expanded');
  } catch {
    // Not worth surfacing: the sidebar still works, it just won't remember its width next load.
  }
}

/**
 * The app shell for every signed-in page: sidebar plus routed content.
 *
 * Three layouts, by viewport:
 *   wide    — sidebar expanded or railed, user's choice, remembered
 *   narrow  — defaults to the rail when crossing the breakpoint, still overridable
 *   mobile  — sidebar leaves the flow entirely and becomes an overlay drawer
 */
export function AppLayout() {
  const isNarrow = useMediaQuery(NARROW_QUERY);
  const isMobile = useMediaQuery(MOBILE_QUERY);
  const location = useLocation();

  const [collapsed, setCollapsed] = useState(readCollapsed);
  const [drawerOpen, setDrawerOpen] = useState(false);

  // React to *crossing* the narrow breakpoint, not to its current value: reading it on every
  // render would overwrite the user's saved preference the moment the app loads on a wide screen.
  const wasNarrow = useRef(isNarrow);
  useEffect(() => {
    if (wasNarrow.current === isNarrow) return;
    wasNarrow.current = isNarrow;
    setCollapsed(isNarrow);
  }, [isNarrow]);

  useEffect(() => {
    if (!isMobile) setDrawerOpen(false);
  }, [isMobile]);

  // The drawer always shows the full sidebar — a rail inside an overlay would be pointless.
  const railed = !isMobile && collapsed;

  const onToggleRail = useCallback(() => {
    if (isMobile) {
      setDrawerOpen((open) => !open);
      return;
    }
    setCollapsed((current) => {
      writeCollapsed(!current);
      return !current;
    });
  }, [isMobile]);

  const closeDrawer = useCallback(() => setDrawerOpen(false), []);

  useEffect(() => {
    if (!drawerOpen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setDrawerOpen(false);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [drawerOpen]);

  return (
    // The provider wraps the whole shell rather than the sidebar alone: the badge and the
    // notifications page under `Outlet` read the same count, and marking a row read on the page is
    // what has to drop it in the sidebar.
    <NotificationsProvider>
      <div
        className="app-shell"
        data-railed={railed || undefined}
        data-drawer-open={drawerOpen || undefined}
      >
        <Sidebar railed={railed} onToggleRail={onToggleRail} onNavigate={closeDrawer} />

        {/* Scrim only exists in drawer mode; CSS hides the shell's mobile topbar above 700px. */}
        <div className="app-scrim" onClick={closeDrawer} aria-hidden="true" />

        <div className="app-body">
          <header className="app-topbar">
            <button
              type="button"
              className="clickable-icon"
              onClick={onToggleRail}
              aria-label="Open navigation"
              aria-expanded={drawerOpen}
            >
              <Icon name="menu" size="m" />
            </button>
            <span className="app-topbar-title">{APP_NAME}</span>
          </header>

          <main className="app-main">
            {/* Keyed by path so navigating away clears a tripped boundary: a boundary holds its
                error until it remounts, and without this one broken page would keep showing its
                error no matter where the sidebar took you. */}
            <ErrorBoundary scope="page" key={location.pathname}>
              <Outlet />
            </ErrorBoundary>
          </main>
        </div>
      </div>
    </NotificationsProvider>
  );
}
