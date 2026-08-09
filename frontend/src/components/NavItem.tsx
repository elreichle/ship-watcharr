import { useEffect, useState, type FocusEvent, type KeyboardEvent } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { Icon, type IconName } from './Icon';
import type { NavChild } from './navigation';

function itemClass({ isActive }: { isActive: boolean }): string {
  return isActive ? 'nav-item is-active' : 'nav-item';
}

interface NavLeafProps {
  to: string;
  label: string;
  icon: IconName;
  railed: boolean;
  onNavigate: () => void;
}

export function NavLeaf({ to, label, icon, railed, onNavigate }: NavLeafProps) {
  return (
    <li className="nav-section">
      {/* `end` matters for "/" — without it the Dashboard link is active on every route. */}
      <NavLink to={to} end className={itemClass} title={railed ? label : undefined} onClick={onNavigate}>
        <Icon name={icon} size="m" />
        <span className="nav-item-title">{label}</span>
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
}

export function NavGroup({ label, icon, items, railed, onNavigate }: NavGroupProps) {
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
        onClick={() => (railed ? setFlyoutOpen((value) => !value) : setExpanded((value) => !value))}
      >
        <Icon name={icon} size="m" />
        <span className="nav-item-title">{label}</span>
        <Icon name="chevron-right" size="xs" className="nav-collapse-icon" />
      </button>

      {open && (
        <ul className="nav-children">
          {items.map((child) => (
            <li key={child.to}>
              <NavLink to={child.to} className={itemClass} onClick={onNavigate}>
                <span className="nav-item-title">{child.label}</span>
              </NavLink>
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}
