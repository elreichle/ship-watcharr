export interface CurrentUser {
  id: string;
  username: string;
  /** Optional at registration — null when the account was created without one. */
  email: string | null;
  isAdmin: boolean;
}

/** Optional per-account contact address, set at Settings → Account rather than at registration. */
export interface AccountEmail {
  email: string | null;
  /** True when this address is what AO3 currently sees as the operator contact. */
  isUsedAsOperatorContact: boolean;
}

export interface Ao3CredentialStatus {
  hasCredential: boolean;
  ao3Username: string | null;
  hasActiveSession: boolean;
  sessionExpiresAt: string | null;
}

/** One ship the signed-in user watches, joined to the shared scrape state behind it. */
export interface WatchedShip {
  /** The shared ship, not the subscription row — this is what unwatch and the works filter take. */
  shipId: number;
  tagName: string;
  notificationsEnabled: boolean;
  watchedSince: string;
  /** Works currently in the tag's index; excludes ones that left it or that AO3 deleted. */
  workCount: number;
  /** Watchers on this instance. Unwatching keeps the data while this is above one. */
  watcherCount: number;
  backfillState: 'NotStarted' | 'InProgress' | 'Complete' | 'Failed';
  lastScrapedAt: string | null;
  nextScrapeAt: string | null;
  isScheduled: boolean;
  /** False until an AO3 scraper is registered for the job's key — see the README's scaffold note. */
  scraperAvailable: boolean;
  /** Whether AO3 has confirmed the tag exists. Follows are accepted first and checked after. */
  verificationState: 'Pending' | 'Verified' | 'NotFoundOnAo3';
  /** Why the last check settled nothing. Only present while Pending, after at least one attempt. */
  verificationError: string | null;
  /** What you typed, when AO3 turned out to call the tag something else. Null when they agree. */
  requestedTagName: string | null;
}

export interface WatchedShipsResponse {
  ships: WatchedShip[];
  /**
   * False when this instance has no operator contact and therefore cannot reach AO3 at all — the
   * reason every ship would otherwise sit on "Checking…" with no explanation.
   */
  verificationEnabled: boolean;
}

/** One page of a larger result set, plus what a pager needs to render itself. */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** A scraped work, as listed. Carries no summary — see WorkDtos.cs for why. */
export interface WorkListItem {
  id: number;
  title: string;
  authors: string[];
  isAnonymous: boolean;
  rating: string;
  categories: string[];
  warnings: string[];
  fandoms: string[];
  /** The watched tags this work turned up under. */
  ships: string[];
  isComplete: boolean;
  wordCount: number;
  chapterCount: number;
  /** Null for an open-ended WIP — AO3's "?" — not the same as "planned equals current". */
  plannedChapterCount: number | null;
  kudos: number;
  hits: number;
  bookmarks: number;
  commentCount: number;
  languageName: string | null;
  updatedAt: string;
  /** True when only a day-granular date was available, so the UI shouldn't imply a clock time. */
  updatedAtIsApproximate: boolean;
  isRestricted: boolean;
}

export type WorkSort = 'updated' | 'kudos' | 'hits' | 'bookmarks' | 'comments' | 'words';

export interface WorkQuery {
  page?: number;
  pageSize?: number;
  shipId?: number | null;
  sort?: WorkSort;
  ascending?: boolean;
}

/** A scrape schedule. Belongs to a ship, and is visible to a user via the ships they watch. */
export interface ScrapeJob {
  id: number;
  shipId: number;
  shipName: string;
  scraperKey: string;
  intervalMinutes: number;
  isEnabled: boolean;
  lastRunAt: string | null;
  nextRunAt: string | null;
  lastRunStatus: string | null;
  lastRunError: string | null;
}

export interface ScrapeRun {
  id: number;
  scrapeJobId: number;
  status: string;
  mode: string;
  startedAt: string;
  completedAt: string | null;
  pagesFetched: number;
  requestsMade: number;
  worksSeen: number;
  worksAdded: number;
  worksUpdated: number;
  stopReason: string | null;
  errorMessage: string | null;
}

/** How this instance identifies itself to AO3 on every scrape request. */
export interface ScrapingIdentity {
  /** The exact User-Agent being sent, or null when scraping is disabled. */
  userAgent: string | null;
  operatorContact: string | null;
  contactSource: 'AdminSetting' | 'Configuration' | 'AdminAccount' | 'None';
  /** True when an explicit contact is saved, rather than a resolved default. */
  isOverridden: boolean;
  /** What the contact reverts to if the override is cleared. */
  defaultContact: string | null;
  scrapingEnabled: boolean;
  problem: string | null;
  productToken: string;
  instanceId: string;
}

export interface DatabaseStatus {
  provider: 'Sqlite' | 'Postgres';
  sqliteDbPath: string;
  postgresConfigured: boolean;
  postgresConnectionSummary: string | null;
}
