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
  /**
   * The listing page the back-catalogue walk asks for next, or null before it has started. Where
   * the last run landed rather than how deep the walk got: a stalling ship's cursor is dragged
   * backwards once per run, so a written-off one is usually parked well below where it read to.
   */
  backfillNextPage: number | null;
  /**
   * Where a restart resumes the current backfill — the deepest page a run has read, or the page an
   * admin last named. Null before either. The backwards drag cannot touch it, which is what makes
   * it the right default: resuming at `backfillNextPage` re-asks AO3 for pages already served.
   */
  backfillResumePage: number | null;
  /**
   * Consecutive runs that got no further through the listing. Above zero the ship is spending
   * requests on a page AO3 will not answer; at the scraper's limit the backfill is `Failed`.
   */
  backfillStalledRuns: number;
  /**
   * The listing page the sweep under way asks for next, or null when none is in flight — which is
   * the whole of "is this ship being swept". A sweep displaces the ship's pass for new works for as
   * many ticks as the walk takes, so this is what explains a quiet ship.
   */
  fullSweepNextPage: number | null;
  /**
   * When the most recent sweep started walking page 1, finished or not. A start with no completion
   * after it and nothing in flight is a sweep that was abandoned.
   */
  lastFullSweepStartedAt: string | null;
  /** When a sweep last reached the end of the listing. Null until one has. */
  lastFullSweepCompletedAt: string | null;
}

/** What an admin's restart left on a ship whose backfill this instance had given up on. */
export interface BackfillRestarted {
  shipId: number;
  tagName: string;
  backfillState: WatchedShip['backfillState'];
  backfillNextPage: number | null;
  backfillStalledRuns: number;
}

/** What an admin's recheck left on a ship AO3 had denied. */
export interface VerificationRechecked {
  shipId: number;
  tagName: string;
  verificationState: WatchedShip['verificationState'];
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

/**
 * One reader's own data about a work. A work nobody has touched carries the cleared state —
 * status "None", no rating, no note — rather than being absent, so nothing here needs a null check.
 */
export interface WorkState {
  status: ReadingStatus;
  /** Half-stars, 1-10, so 7 is three and a half. Null is unrated, not the lowest score. */
  rating: number | null;
  note: string | null;
  /** Whether the work is one of the reader's favorites. The flag every control sends back. */
  isFavorite: boolean;
  /**
   * When the favorite mark went on, or null while there is none. The server's to set: a saved
   * state carrying `isFavorite: true` keeps the date it already has rather than moving it.
   */
  favoritedAt: string | null;
}

export type ReadingStatus = 'None' | 'ToRead' | 'Reading' | 'Read' | 'Dropped';

/**
 * A whole replacement of one reader's state on one work — the body of `PUT /api/works/{id}/state`.
 *
 * Identical in shape to `WorkState` because the endpoint **replaces**: it writes every field from
 * what it was sent, so a field left out is cleared rather than left alone. Aliased rather than
 * declared separately so a caller cannot build one out of only the field it meant to change. The
 * server reads `isFavorite` and ignores `favoritedAt`, which is its own to keep.
 */
export type SetWorkStateInput = WorkState;

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
  /** The watched tags this work turned up under and still appears in. */
  ships: string[];
  /**
   * The watched tags whose listing has stopped carrying it. Normally empty: a work that has left
   * every tag the reader follows is out of the list altogether unless they have marked it, so a
   * row carrying one of these is here through another tag or through their own mark.
   */
  leftShips: string[];
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
  /** The reader's own state, never another's. On the row so a page costs one request. */
  state: WorkState;
}

/** One tag on a work, with the kind AO3 files it under. */
export interface WorkTag {
  type: Ao3TagType;
  name: string;
}

export interface WorkSeriesRef {
  id: number;
  title: string;
  /** Position within the series, or null where the blurb's wording could not be read as a number. */
  part: number | null;
}

/** A watched ship the work turned up under, with the id the feed narrows by. */
export interface WorkShipRef {
  shipId: number;
  tagName: string;
  /**
   * When a completed sweep of that tag last failed to find this work, or null while it is still
   * listed there. A work can be opened, marked and downloaded from a tag it has left, so the page
   * has to be able to say the archive no longer files it under this ship.
   */
  missingSinceAt: string | null;
}

/**
 * Everything the instance holds about one work — the body of `GET /api/works/{id}`.
 *
 * A superset of `WorkListItem`, and fetched from the database alone: opening a work costs AO3
 * nothing. The fields a work's own page carries and a listing blurb does not are null until a
 * detail fetch fills them, which `detailFetchedAt` is what distinguishes from "AO3 has no value".
 */
export interface WorkDetail {
  id: number;
  title: string;
  authors: string[];
  isAnonymous: boolean;
  /**
   * The summary, already sanitized by the server — an allowlist of elements and no attributes at
   * all. The raw column is not this, and no endpoint hands it out; this is the one summary shape a
   * client is meant to render as HTML. Null where there is no summary to show.
   */
  summarySafeHtml: string | null;
  rating: string;
  categories: string[];
  warnings: string[];
  tags: WorkTag[];
  series: WorkSeriesRef[];
  /** Only the ships this reader follows it under. */
  ships: WorkShipRef[];
  isComplete: boolean;
  wordCount: number;
  chapterCount: number;
  plannedChapterCount: number | null;
  kudos: number;
  hits: number;
  bookmarks: number;
  commentCount: number;
  collectionCount: number;
  languageName: string | null;
  languageCode: string | null;
  updatedAt: string;
  updatedAtIsApproximate: boolean;
  /** Null until the work's own page has been fetched — see `detailFetchedAt`. */
  publishedAt: string | null;
  /** When the work's own page was last read. Null while everything here came from listings. */
  detailFetchedAt: string | null;
  isRestricted: boolean;
  firstSeenAt: string;
  lastSeenAt: string;
  state: WorkState;
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
  /** Only the works the reader has marked as favorites. Composes with everything else here. */
  favoritesOnly?: boolean;
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
  /**
   * Your own mark, not anything AO3 knows. `'None'` is a real criterion meaning unread in the
   * widest sense — never marked, or marked and cleared. Null is unconstrained.
   */
  readingStatus: ReadingStatus | null;
  /** Inclusive bounds on your own rating in half-stars, 1-10. Unrated works match neither. */
  minUserRating: number | null;
  maxUserRating: number | null;
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
  /**
   * Your own mark, not anything AO3 knows. `'None'` is a real criterion meaning unread in the
   * widest sense — never marked, or marked and cleared. Null is unconstrained.
   */
  readingStatus: ReadingStatus | null;
  /** Inclusive bounds on your own rating in half-stars, 1-10. Unrated works match neither. */
  minUserRating: number | null;
  maxUserRating: number | null;
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
  /** Why no honest User-Agent can be built, or null when one can. `problem`'s identity half alone. */
  identityProblem: string | null;
}

export interface DatabaseStatus {
  provider: 'Sqlite' | 'Postgres';
  sqliteDbPath: string;
  postgresConfigured: boolean;
  postgresConnectionSummary: string | null;
}

/**
 * The formats AO3 offers a work in, in the order the download menu lists them.
 *
 * Matches `Ao3DownloadFormat` on the server, whose names are what the wire carries — a format this
 * page does not know about is a format the server would refuse anyway.
 */
export const DOWNLOAD_FORMATS = ['Epub', 'Mobi', 'Pdf', 'Html', 'Azw3'] as const;

export type DownloadFormat = (typeof DOWNLOAD_FORMATS)[number];

/**
 * Where one request has got to. `Pending` is queued, `Downloading` means a worker holds it, and
 * both are states the list polls through; the other two are settled.
 */
export type DownloadStatus = 'Pending' | 'Downloading' | 'Complete' | 'Failed';

/** One reader's request for a downloadable copy of a work. PER-USER, unlike the bytes behind it. */
export interface Download {
  id: number;
  workId: number;
  workTitle: string;
  format: DownloadFormat;
  status: DownloadStatus;
  /** The stored file's size, or null while no file stands behind the request. */
  sizeBytes: number | null;
  /**
   * The size of the copy the reader still holds from before this request was re-armed onto a newer
   * version of the work — null where there is none. On a queued or failed request it is the
   * difference between having nothing and still having what you had, so it is what says whether an
   * earlier copy can be offered. Always null once the request is `Complete`.
   */
  previousSizeBytes: number | null;
  /** Why the fetch failed, naming which half of it did. Null unless `status` is `Failed`. */
  errorMessage: string | null;
  requestedAt: string;
  completedAt: string | null;
}

/**
 * One "a ship you follow gained a work" row.
 *
 * Named for the ship rather than called `Notification`, which is a DOM global: a module-scoped
 * interface of that name would shadow it, and a later `new Notification(...)` would then be a
 * type error nothing in the file explains.
 *
 * The ship and work names are joined at read time by the server, so a work AO3 has since retitled
 * is named here by its current title.
 */
export interface ShipNotification {
  id: number;
  shipId: number;
  shipName: string;
  workId: number;
  workTitle: string;
  createdAt: string;
  /** Null while unread — the same fact the unread count counts. */
  readAt: string | null;
}

/**
 * The count the sidebar shows. Every mark-read call answers with one too, so the number that lands
 * after a write is the server's own rather than one the page decremented for itself.
 */
export interface UnreadNotifications {
  unread: number;
}

/**
 * The body of `GET /api/stats`: two lenses over the caller's library, and a table where they meet.
 *
 * Nothing here is stored — every figure is an aggregate the server computed over exactly the works
 * `/api/works` would list, so the page can never disagree with the feed about what the library is.
 */
export interface Stats {
  /** The ship every figure was narrowed to, echoed back, or null for the whole library. */
  shipId: number | null;
  /**
   * One row per watched ship, carrying both its corpus size and this reader's marks on it, so a
   * share is arithmetic within a row rather than a join across two lists. Narrowed with the rest:
   * asking about one ship returns one row, which is why the ship picker is fed by `/api/ships`.
   */
  ships: ShipStats[];
  corpus: CorpusStats;
  reading: ReadingStats;
}

export interface ShipStats {
  shipId: number;
  tagName: string;
  /** Zero for a followed ship nothing has been scraped into yet — a state the page explains. */
  workCount: number;
  wordCount: number;
  /** Works given any status other than `None`. */
  markedCount: number;
  readCount: number;
  ratedCount: number;
}

/** The corpus as it stands, with nobody's reading in it. */
export interface CorpusStats {
  workCount: number;
  completeCount: number;
  wordCount: number;
  kudos: number;
  /** Null for an empty library, where the alternative is a zero that reads as "nobody left kudos". */
  averageKudos: number | null;
  averageWordCount: number | null;
  /** Ascending, and carrying only the months that have works — the gaps are this page's to fill. */
  worksByUpdatedMonth: MonthCount[];
  /** Every AO3 content rating in AO3's own order, zero-count ones included. */
  ratingMix: LabelledCount[];
  kudosDistribution: BucketCount[];
  wordCountDistribution: BucketCount[];
  /** At most ten. Anonymous works have no creator and are absent rather than pooled. */
  topAuthors: AuthorStats[];
}

/** The caller's own reading, laid over the corpus above. PER-USER. */
export interface ReadingStats {
  markedCount: number;
  readCount: number;
  /** Words in the works marked Read — how much of the library this reader has been through. */
  readWordCount: number;
  ratedCount: number;
  /** Half-stars, weighted by how many works sit at each. Null where nothing is rated. */
  averageRating: number | null;
  /** Every reading status, zero-count ones included; sums to `corpus.workCount`. */
  statusMix: LabelledCount[];
  /** One row per half-star awarded, with what the archive made of the works put there. */
  ratingsAgainstReception: RatingReception[];
}

export interface MonthCount {
  year: number;
  /** 1-12, as the server counts months — not JavaScript's zero-based one. */
  month: number;
  workCount: number;
}

export interface LabelledCount {
  label: string;
  workCount: number;
}

/** A histogram bar. The bounds ride along with the label so a client can format its own axis. */
export interface BucketCount {
  label: string;
  min: number;
  /** Null on the open-ended top bucket. */
  max: number | null;
  workCount: number;
}

export interface AuthorStats {
  pseudId: number;
  name: string;
  workCount: number;
  kudos: number;
}

export interface RatingReception {
  /** Half-stars, 1-10, the same scale `WorkState.rating` uses. */
  rating: number;
  workCount: number;
  averageKudos: number;
  averageWordCount: number;
}
