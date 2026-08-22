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
  /**
   * False when no AO3 login is stored for this deployment. Scraping is held until one is, so this
   * is the reason a library stays empty while every ship looks scheduled. Readable by every user,
   * because every user can see the empty library.
   */
  ao3LoginConfigured: boolean;
}

/**
 * The one AO3 login this deployment scrapes as. Status only — the password and the session cookie
 * never leave the server, and no endpoint reads them back.
 */
export interface InstanceAo3Credential {
  hasCredential: boolean;
  ao3Username: string | null;
  /** Whether a session cookie happens to be cached. It is a cache of the password, nothing more. */
  hasCachedSession: boolean;
  sessionEstablishedAt: string | null;
  sessionExpiresAt: string | null;
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
  /** Omit to let a saved filter's own sort apply; sending one always wins over it. */
  sort?: WorkSort;
  ascending?: boolean;
  /** A saved set of criteria to apply. */
  savedFilterId?: number | null;
  /**
   * Whether an unqualified request picks up the user's default set. Send false for "show me
   * everything I follow", which is otherwise inexpressible once a default exists.
   */
  useDefaultFilter?: boolean;
}

export type Ao3TagType = 'Fandom' | 'Relationship' | 'Character' | 'Freeform' | 'Warning';

export interface SavedFilterTag {
  tagId: number;
  name: string;
  type: Ao3TagType;
}

export interface SavedFilterAuthor {
  pseudId: number;
  displayName: string;
  username: string;
}

/**
 * A named, reusable set of library criteria — AO3's filter sidebar, saved.
 *
 * Enum-valued criteria are the API's own names ("TeenAndUpAudiences", "FF"), never the numbers
 * behind them and never display text. `FilterVocabulary` turns a name into something to show, so
 * AO3's wording has exactly one home and it is the server's.
 *
 * Null means "unconstrained" throughout, including `isComplete`, where it is genuinely a third
 * state rather than a default.
 */
export interface SavedFilter {
  id: number;
  name: string;
  /** Applied when Works is opened without naming a set. At most one per account. */
  isDefault: boolean;
  shipId: number | null;
  /** Present even when you no longer follow that ship — the set stays valid and matches nothing. */
  shipTagName: string | null;
  isComplete: boolean | null;
  minWordCount: number | null;
  maxWordCount: number | null;
  minChapterCount: number | null;
  maxChapterCount: number | null;
  minKudos: number | null;
  maxKudos: number | null;
  minHits: number | null;
  maxHits: number | null;
  minComments: number | null;
  maxComments: number | null;
  minBookmarks: number | null;
  maxBookmarks: number | null;
  minRating: string | null;
  maxRating: string | null;
  includeCategories: string[];
  excludeCategories: string[];
  includeWarnings: string[];
  excludeWarnings: string[];
  languageCode: string | null;
  updatedAfter: string | null;
  updatedBefore: string | null;
  sort: WorkSort;
  ascending: boolean;
  includeTags: SavedFilterTag[];
  excludeTags: SavedFilterTag[];
  includeAuthors: SavedFilterAuthor[];
  excludeAuthors: SavedFilterAuthor[];
  /** How many works in your library this set currently matches. */
  matchingWorkCount: number;
  createdAt: string;
  updatedAt: string;
}

/**
 * A set to create or replace. Sent whole rather than patched: null means "unconstrained", so an
 * omitted field and a cleared one are the same thing and a merge could not tell them apart.
 */
export interface SaveFilterInput {
  name: string;
  isDefault: boolean;
  shipId: number | null;
  isComplete: boolean | null;
  minWordCount: number | null;
  maxWordCount: number | null;
  minChapterCount: number | null;
  maxChapterCount: number | null;
  minKudos: number | null;
  maxKudos: number | null;
  minHits: number | null;
  maxHits: number | null;
  minComments: number | null;
  maxComments: number | null;
  minBookmarks: number | null;
  maxBookmarks: number | null;
  minRating: string | null;
  maxRating: string | null;
  includeCategories: string[];
  excludeCategories: string[];
  includeWarnings: string[];
  excludeWarnings: string[];
  languageCode: string | null;
  updatedAfter: string | null;
  updatedBefore: string | null;
  sort: WorkSort;
  ascending: boolean;
  includeTagIds: number[];
  excludeTagIds: number[];
  includeAuthorIds: number[];
  excludeAuthorIds: number[];
}

/** One choice a filter can be built from: what the API takes, and what a human reads. */
export interface VocabularyOption {
  value: string;
  label: string;
}

/**
 * The controlled vocabulary behind the filter editor, fetched rather than hard-coded — this
 * wording is AO3's, and the parser that reads it off a blurb lives on the server too.
 */
export interface FilterVocabulary {
  ratings: VocabularyOption[];
  categories: VocabularyOption[];
  warnings: VocabularyOption[];
  sorts: VocabularyOption[];
  /** Only languages present in your own library. Empty until something has been scraped. */
  languages: VocabularyOption[];
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
  /** Whether scraping may run at all — both gates, not just this instance's identity. */
  scrapingEnabled: boolean;
  /** Every reason scraping is held, or null when it is running. */
  problem: string | null;
  productToken: string;
  instanceId: string;
  /** Whether an honest User-Agent can be built. Narrower than `scrapingEnabled`. */
  identityConfigured: boolean;
  /** Whether the deployment's AO3 login is stored. The other gate. */
  ao3LoginConfigured: boolean;
}

export interface DatabaseStatus {
  provider: 'Sqlite' | 'Postgres';
  sqliteDbPath: string;
  postgresConfigured: boolean;
  postgresConnectionSummary: string | null;
}
