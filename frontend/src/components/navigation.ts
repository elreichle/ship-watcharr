import type { IconName } from './Icon';

export interface NavChild {
  label: string;
  to: string;
}

export interface NavSection {
  label: string;
  icon: IconName;
  /** Set for a plain link. Mutually exclusive with `children`. */
  to?: string;
  /** Set for an expandable group. */
  children?: NavChild[];
  adminOnly?: boolean;
}

/**
 * The sidebar tree, following Sonarr/Radarr's split: per-user preferences under Settings,
 * instance-wide administration under System.
 */
export const NAV_SECTIONS: NavSection[] = [
  {
    label: 'Dashboard',
    icon: 'layout-dashboard',
    children: [
      { label: 'Works', to: '/works' },
      { label: 'Favorites', to: '/favorites' },
      { label: 'Notifications', to: '/notifications' },
      { label: 'Filters', to: '/filters' },
      { label: 'Ships', to: '/ships' },
      { label: 'Downloads', to: '/downloads' },
      { label: 'Statistics', to: '/stats' },
      { label: 'Schedules', to: '/schedules' },
    ],
  },
  {
    label: 'Settings',
    icon: 'settings',
    children: [
      { label: 'Account', to: '/settings/account' },
      { label: 'Appearance', to: '/settings/appearance' },
    ],
  },
  {
    label: 'System',
    icon: 'zap',
    adminOnly: true,
    children: [
      { label: 'Scraping', to: '/system/scraping' },
      { label: 'Database', to: '/system/database' },
    ],
  },
];
