import { useNavigate } from 'react-router-dom';
import { APP_NAME } from '../appInfo';
import { useAuth } from '../auth/AuthContext';
import { useNotifications } from '../hooks/useNotifications';
import { Icon } from './Icon';
import { NavGroup, NavLeaf, type NavBadges } from './NavItem';
import { NAV_SECTIONS } from './navigation';

interface SidebarProps {
  /** Collapsed to the icon rail. Labels are hidden and groups open as flyouts. */
  railed: boolean;
  onToggleRail: () => void;
  /** Called after any navigation, so the mobile drawer can close itself. */
  onNavigate: () => void;
}

export function Sidebar({ railed, onToggleRail, onNavigate }: SidebarProps) {
  const { user, logout } = useAuth();
  const { unread } = useNotifications();
  const navigate = useNavigate();

  // Keyed by route rather than handed to one named item, so the nav tree stays a plain list of
  // links and nothing in it has to know what a notification is.
  const badges: NavBadges = { '/notifications': unread ?? 0 };

  const onLogout = async () => {
    await logout();
    navigate('/login');
  };

  return (
    <nav className="sidebar" aria-label="Main">
      <div className="sidebar-header">
        <button
          type="button"
          className="clickable-icon sidebar-toggle"
          onClick={onToggleRail}
          aria-label={railed ? 'Expand sidebar' : 'Collapse sidebar'}
          aria-expanded={!railed}
        >
          <Icon name="menu" size="m" />
        </button>
        {/* The mark is a bookmark: the app's job is remembering where a reader was. Decorative,
            so the name beside it is the whole accessible label. */}
        <span className="sidebar-brand">
          <Icon name="bookmark" size="m" className="sidebar-mark" />
          <span className="sidebar-title">{APP_NAME}</span>
        </span>
      </div>

      <ul className="nav-sections">
        {NAV_SECTIONS.filter((section) => !section.adminOnly || user?.isAdmin).map((section) =>
          section.children ? (
            <NavGroup
              key={section.label}
              label={section.label}
              icon={section.icon}
              items={section.children}
              railed={railed}
              onNavigate={onNavigate}
              badges={badges}
            />
          ) : (
            <NavLeaf
              key={section.label}
              to={section.to!}
              label={section.label}
              icon={section.icon}
              railed={railed}
              onNavigate={onNavigate}
            />
          ),
        )}
      </ul>

      <div className="sidebar-footer">
        <div className="sidebar-user" title={railed ? user?.username : undefined}>
          <Icon name="user" size="m" />
          <span className="nav-item-title">{user?.username}</span>
        </div>
        <button
          type="button"
          className="nav-item"
          onClick={onLogout}
          title={railed ? 'Log out' : undefined}
        >
          <Icon name="log-out" size="m" />
          <span className="nav-item-title">Log out</span>
        </button>
      </div>
    </nav>
  );
}
