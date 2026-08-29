import { useEffect, useState, type FocusEvent, type KeyboardEvent } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { Icon, type IconName } from './Icon';
import type { NavChild } from './navigation';

function itemClass({ isActive }: { isActive: boolean }): string {
  return isActive ? 'nav-item is-active' : 'nav-item';
}

/** A count keyed by the route it belongs to, so the sidebar decides what a badge means. */
export type NavBadges = Record<string, number | undefined>;

/**
 * The count itself is hidden from assistive technology and spelled into the item's own label
 * instead — "Notifications, 3 unread" is one thing to hear, where a link followed by a loose
 * number is two, and the second of them does not say what it counts.
 */
function NavBadge({ count }: { count: number }) {
  return (
    <span className="nav-badge" aria-hidden="true">
      {count > 99 ? '99+' : count}
    </span>
  );
}

/** What a screen reader should hear instead of the bare label, or undefined for the label itself. */
function badgeLabel(label: string, count: number | undefined): string | undefined {
  return count ? `${label}, ${count} unread` : undefined;
}

interface NavLeafProps {
  to: string;
  label: string;
  icon: IconName;
  railed: boolean;
  onNavigate: () => void;
  /** Shown at the end of the item when non-zero. */
  badge?: number;
}

export function NavLeaf({ to, label, icon, railed, onNavigate, badge }: NavLeafProps) {
  return (
    <li className="nav-section">
      {/* `end` matters for "/" — without it the Dashboard link is active on every route. */}
      <NavLink
        to={to}
        end
        className={itemClass}
        title={railed ? label : undefined}
        aria-label={badgeLabel(label, badge)}
        onClick={onNavigate}
      >
        <Icon name={icon} size="m" />
        <span className="nav-item-title">{label}</span>
        {badge ? <NavBadge count={badge} /> : null}
      </NavLink>
    </li>
  );
}

interface NavGroupProps {
  label: string;
  icon: IconName;
  items: NavChild[];
  railed: boolean;
  onNavigate: () => void;
  /** Counts for this group's children, by route. */
  badges?: NavBadges;
}

export function NavGroup({ label, icon, items, railed, onNavigate, badges }: NavGroupProps) {
  const { pathname } = useLocation();
  const containsActive = items.some(
    (child) => pathname === child.to || pathname.startsWith(`${child.to}/`),
  );

  const [expanded, setExpanded] = useState(containsActive);
  const [flyoutOpen, setFlyoutOpen] = useState(false);

  // Navigating into a collapsed group opens it, matching Sonarr. Deliberately one-directional:
  // navigating away leaves it as the user last had it rather than snapping shut under them.
  useEffect(() => {
    if (containsActive) setExpanded(true);
  }, [containsActive]);

  // Railed, the group has no room for inline children, so it becomes a flyout. Opening on focus
  // as well as hover is what keeps it reachable by keyboard: tabbing to the group button renders
  // the children, and the next Tab moves into them.
  const open = railed ? flyoutOpen : expanded;

  const onBlur = (event: FocusEvent<HTMLLIElement>) => {
    if (!event.currentTarget.contains(event.relatedTarget)) setFlyoutOpen(false);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLLIElement>) => {
    if (event.key === 'Escape' && flyoutOpen) setFlyoutOpen(false);
  };

  // A closed group is hiding its children's badges, so it carries their total itself — otherwise
  // the count would be invisible in the rail, which is the layout a reader is most likely to leave
  // the sidebar in. Open, the children show their own and the header stops repeating them.
  const closedBadge = open
    ? 0
    : items.reduce((total, child) => total + (badges?.[child.to] ?? 0), 0);

  return (
    <li
      className="nav-section"
      data-flyout={railed || undefined}
      onMouseEnter={railed ? () => setFlyoutOpen(true) : undefined}
      onMouseLeave={railed ? () => setFlyoutOpen(false) : undefined}
      onFocus={railed ? () => setFlyoutOpen(true) : undefined}
      onBlur={railed ? onBlur : undefined}
      onKeyDown={railed ? onKeyDown : undefined}
    >
      <button
        type="button"
        className={containsActive ? 'nav-item nav-group-header is-active' : 'nav-item nav-group-header'}
        aria-expanded={open}
        title={railed ? label : undefined}
        aria-label={badgeLabel(label, closedBadge)}
        onClick={() => (railed ? setFlyoutOpen((value) => !value) : setExpanded((value) => !value))}
      >
        <Icon name={icon} size="m" />
        <span className="nav-item-title">{label}</span>
        {closedBadge ? <NavBadge count={closedBadge} /> : null}
        <Icon name="chevron-right" size="xs" className="nav-collapse-icon" />
      </button>

      {open && (
        <ul className="nav-children">
          {items.map((child) => {
            const badge = badges?.[child.to];

            return (
              <li key={child.to}>
                <NavLink
                  to={child.to}
                  className={itemClass}
                  aria-label={badgeLabel(child.label, badge)}
                  onClick={onNavigate}
                >
                  <span className="nav-item-title">{child.label}</span>
                  {badge ? <NavBadge count={badge} /> : null}
                </NavLink>
              </li>
            );
          })}
        </ul>
      )}
    </li>
  );
}
