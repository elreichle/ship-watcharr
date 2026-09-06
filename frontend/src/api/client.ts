import type {
  AccountEmail,
  Ao3TagType,
  BackfillRestarted,
  CurrentUser,
  DatabaseStatus,
  Download,
  DownloadFormat,
  FilterVocabulary,
  InstanceAo3Credential,
  PagedResult,
  SavedFilter,
  SavedFilterAuthor,
  SavedFilterTag,
  SaveFilterInput,
  ScrapeJob,
  ScrapeRun,
  ScrapingIdentity,
  SetWorkStateInput,
  ShipNotification,
  Stats,
  UnreadNotifications,
  VerificationRechecked,
  WatchedShip,
  WatchedShipsResponse,
  WorkDetail,
  WorkListItem,
  WorkQuery,
  WorkState,
} from './types';

export class ApiError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

/**
 * A check on a successful response body, run before the caller ever sees it.
 *
 * `request<T>` only *asserts* T — nothing verifies the server actually sent that shape. A server
 * running different code than this page (a stale dev process, a half-applied deploy) can answer
 * 200 with a body that satisfies the compiler and is `undefined` where the UI expects an array.
 * The first `.map` on it then throws during render, which used to take the entire app down.
 *
 * Checking here turns that into an ordinary failed request, which every page already knows how to
 * show. Only shapes the UI indexes into are worth checking — a missing scalar renders as blank,
 * which is untidy rather than fatal.
 */
type ResponseCheck = (body: unknown) => boolean;

function isRecord(body: unknown): body is Record<string, unknown> {
  return typeof body === 'object' && body !== null;
}

const hasArray =
  (field: string): ResponseCheck =>
  (body) =>
    isRecord(body) && Array.isArray(body[field]);

/**
 * A page of works, with every row carrying the caller's own state.
 *
 * `items` alone is not enough any more: the works list reads `work.state.status` on every row, so a
 * server that does not send `state` — one deployed behind this page — would throw during render
 * rather than produce the version-mismatch message this whole mechanism exists for.
 */
const isWorksPage: ResponseCheck = (body) =>
  isRecord(body) &&
  Array.isArray(body.items) &&
  body.items.every((item) => isRecord(item) && isRecord(item.state));

/**
 * One work's detail. The page maps over four of these lists and reads `state` on every render, so
 * a server that sent none of them would throw during render rather than report a version mismatch.
 */
const isWorkDetail: ResponseCheck = (body) =>
  isRecord(body) &&
  Array.isArray(body.authors) &&
  Array.isArray(body.tags) &&
  Array.isArray(body.series) &&
  Array.isArray(body.ships) &&
  Array.isArray(body.categories) &&
  Array.isArray(body.warnings) &&
  isRecord(body.state);

/**
 * Statistics over the library. The page maps over six lists inside the two lenses and reads the
 * counts on every row, so a server that sent a body without them would throw during render rather
 * than report the version mismatch this check exists to name.
 */
const isStats: ResponseCheck = (body) =>
  isRecord(body) &&
  Array.isArray(body.ships) &&
  isRecord(body.corpus) &&
  isRecord(body.reading) &&
  Array.isArray(body.corpus.worksByUpdatedMonth) &&
  Array.isArray(body.corpus.ratingMix) &&
  Array.isArray(body.corpus.kudosDistribution) &&
  Array.isArray(body.corpus.wordCountDistribution) &&
  Array.isArray(body.corpus.topAuthors) &&
  Array.isArray(body.reading.statusMix) &&
  Array.isArray(body.reading.ratingsAgainstReception);

async function request<T>(path: string, init?: RequestInit, isValid?: ResponseCheck): Promise<T> {
  const response = await fetch(`/api${path}`, {
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    ...init,
  });

  if (!response.ok) {
    let message = response.statusText;
    try {
      const body = await response.json();
      // ValidationProblem (which is what every failed model/Identity check returns) puts the text
      // worth reading under `errors`, keyed by field name or Identity error code, and leaves
      // `title` as the generic "One or more validation errors occurred." Reading only `title` is
      // what made a rejected password look like an unexplained 400. Endpoints that hand back a
      // plain { message } -- login, for one -- still fall through to it.
      const details = Object.values(body.errors ?? {}).flat() as string[];
      message = details.length > 0 ? details.join(' ') : (body.message ?? body.title ?? message);
    } catch {
      // response had no JSON body
    }
    throw new ApiError(response.status, message);
  }

  if (response.status === 204) return undefined as T;

  const body = (await response.json()) as T;
  if (isValid && !isValid(body)) {
    throw new ApiError(
      response.status,
      `The server's response to ${path} wasn't in the expected format. This usually means it is ` +
        `running a different version than this page — restarting it, or reloading after a deploy ` +
        `finishes, normally clears it.`,
    );
  }
  return body;
}

export const api = {
  register: (username: string, password: string) =>
    request<CurrentUser>('/auth/register', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    }),

  login: (username: string, password: string) =>
    request<CurrentUser>('/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }),

  logout: () => request<void>('/auth/logout', { method: 'POST' }),

  me: () => request<CurrentUser>('/auth/me'),

  getAccountEmail: () => request<AccountEmail>('/account/email'),

  updateAccountEmail: (email: string | null) =>
    request<AccountEmail>('/account/email', {
      method: 'PUT',
      body: JSON.stringify({ email: email?.trim() || null }),
    }),

  changeUsername: (username: string) =>
    request<CurrentUser>('/account/username', { method: 'PUT', body: JSON.stringify({ username }) }),

  changePassword: (currentPassword: string, newPassword: string) =>
    request<void>('/account/password', {
      method: 'PUT',
      body: JSON.stringify({ currentPassword, newPassword }),
    }),

  getWatchedShips: () => request<WatchedShipsResponse>('/ships', undefined, hasArray('ships')),

  watchShip: (tagName: string) =>
    request<WatchedShip>('/ships', { method: 'POST', body: JSON.stringify({ tagName }) }),

  unwatchShip: (shipId: number) => request<void>(`/ships/${shipId}`, { method: 'DELETE' }),

  /**
   * Puts a written-off backfill back to InProgress. Admin-only, and shared: the walk belongs to the
   * ship every watcher shares, not to the caller's subscription. `fromPage` null means the ship's
   * stored cursor — where its last run landed, which the halving retreat may have dragged well
   * above where the walk actually read to.
   */
  restartBackfill: (shipId: number, fromPage: number | null) =>
    request<BackfillRestarted>(`/admin/ships/${shipId}/backfill/restart`, {
      method: 'POST',
      body: JSON.stringify({ fromPage }),
    }),

  /**
   * Sends a tag AO3 denied back through verification. Admin-only and shared, like the restart
   * above: the check spends a request against a tag the archive has already refused, on behalf of
   * everyone watching it. The schedule stays off until AO3 answers for the tag.
   */
  recheckVerification: (shipId: number) =>
    request<VerificationRechecked>(`/admin/ships/${shipId}/verification/recheck`, { method: 'POST' }),

  getWorks: ({
    page,
    pageSize,
    shipId,
    sort,
    ascending,
    savedFilterId,
    useDefaultFilter,
    favoritesOnly,
  }: WorkQuery = {}) => {
    // Built key by key rather than from the object: a null shipId means "every watched ship", and
    // URLSearchParams would happily send it as the literal string "null". An omitted sort matters
    // for the same reason — it is what lets an applied filter's own sort stand.
    const query = new URLSearchParams();
    if (page !== undefined) query.set('page', String(page));
    if (pageSize !== undefined) query.set('pageSize', String(pageSize));
    if (shipId != null) query.set('shipId', String(shipId));
    if (sort !== undefined) query.set('sort', sort);
    if (ascending !== undefined) query.set('ascending', String(ascending));
    if (savedFilterId != null) query.set('savedFilterId', String(savedFilterId));
    if (useDefaultFilter !== undefined) query.set('useDefaultFilter', String(useDefaultFilter));
    if (favoritesOnly) query.set('favoritesOnly', 'true');

    return request<PagedResult<WorkListItem>>(`/works?${query}`, undefined, isWorksPage);
  },

  /**
   * Everything held about one work. 404s for a work no ship the caller follows carries, which is
   * the same scoping the list applies rather than a separate rule about detail pages.
   */
  getWork: (workId: number) =>
    request<WorkDetail>(`/works/${workId}`, undefined, isWorkDetail),

  /**
   * Replaces the caller's own state on one work. There is no patch: the server writes all three
   * fields, so every caller sends the state it wants to hold, not the one field it touched.
   */
  setWorkState: (workId: number, state: SetWorkStateInput) =>
    request<WorkState>(`/works/${workId}/state`, { method: 'PUT', body: JSON.stringify(state) }),

  /**
   * Everything this reader has asked for, newest first. Not scoped to what they still watch — a
   * download is a request they made, and hiding one with its ship would strand the file.
   */
  getDownloads: () => request<Download[]>('/downloads', undefined, Array.isArray),

  /**
   * Asks for one format of one work, and answers with the request as it now stands. Idempotent per
   * (reader, work, format): a second click reads back the first request rather than queueing a
   * second fetch of identical bytes, so no caller needs to guard against one.
   */
  requestDownload: (workId: number, format: DownloadFormat) =>
    request<Download>(`/works/${workId}/downloads`, {
      method: 'POST',
      body: JSON.stringify({ format }),
    }),

  deleteDownload: (id: number) => request<void>(`/downloads/${id}`, { method: 'DELETE' }),

  /**
   * Where the bytes live. A plain address rather than a `fetch`, because the browser's own
   * download machinery is what should stream a file to disk — reading it through this client would
   * buffer a whole PDF in the page to hand it straight back.
   */
  downloadFileUrl: (id: number) => `/api/downloads/${id}/file`,

  /**
   * The caller's notifications, newest first. `unreadOnly` narrows to what has not been marked
   * read, which is the list a reader opening this page from a lit badge actually wants.
   */
  getNotifications: (
    { unreadOnly, page, pageSize }: { unreadOnly?: boolean; page?: number; pageSize?: number } = {},
  ) => {
    const query = new URLSearchParams();
    if (unreadOnly !== undefined) query.set('unreadOnly', String(unreadOnly));
    if (page !== undefined) query.set('page', String(page));
    if (pageSize !== undefined) query.set('pageSize', String(pageSize));

    return request<PagedResult<ShipNotification>>(
      `/notifications?${query}`,
      undefined,
      hasArray('items'),
    );
  },

  /** The one number the shell polls for. */
  getUnreadNotificationCount: () => request<UnreadNotifications>('/notifications/unread-count'),

  /**
   * Marks the named notifications read and answers with the count that survived. Ids the caller
   * does not own match nothing rather than failing the request, so a stale list cannot 404 a click.
   */
  markNotificationsRead: (ids: number[]) =>
    request<UnreadNotifications>('/notifications/mark-read', {
      method: 'POST',
      body: JSON.stringify({ ids }),
    }),

  /** Marks the caller's whole list read, and answers with the count that survived — zero. */
  markAllNotificationsRead: () =>
    request<UnreadNotifications>('/notifications/mark-all-read', { method: 'POST' }),

  /**
   * Two lenses over the caller's library. `shipId` narrows every figure to one watched ship and
   * 404s on a ship they do not watch — the same scoping the works list applies.
   *
   * Sent key by key rather than from an object, for the reason `getWorks` gives: an omitted ship
   * means the whole library, and `URLSearchParams` would send a null one as the string "null".
   */
  getStats: (shipId?: number | null) => {
    const query = new URLSearchParams();
    if (shipId != null) query.set('shipId', String(shipId));

    return request<Stats>(`/stats?${query}`, undefined, isStats);
  },

  getSavedFilters: () => request<SavedFilter[]>('/saved-filters', undefined, Array.isArray),

  createSavedFilter: (filter: SaveFilterInput) =>
    request<SavedFilter>('/saved-filters', { method: 'POST', body: JSON.stringify(filter) }),

  updateSavedFilter: (id: number, filter: SaveFilterInput) =>
    request<SavedFilter>(`/saved-filters/${id}`, { method: 'PUT', body: JSON.stringify(filter) }),

  /**
   * Toggles which set Works opens with. Separate from a full save so the list can flip it without
   * re-posting criteria it may not have reloaded since they were last edited elsewhere.
   */
  setSavedFilterDefault: (id: number, isDefault: boolean) =>
    request<SavedFilter>(`/saved-filters/${id}/default`, {
      method: 'PUT',
      body: JSON.stringify({ isDefault }),
    }),

  deleteSavedFilter: (id: number) => request<void>(`/saved-filters/${id}`, { method: 'DELETE' }),

  getFilterVocabulary: () => request<FilterVocabulary>('/lookups/vocabulary'),

  searchTags: (q: string, type?: Ao3TagType) => {
    const query = new URLSearchParams({ q });
    if (type) query.set('type', type);

    return request<SavedFilterTag[]>(`/lookups/tags?${query}`, undefined, Array.isArray);
  },

  searchAuthors: (q: string) =>
    request<SavedFilterAuthor[]>(`/lookups/authors?${new URLSearchParams({ q })}`, undefined, Array.isArray),

  getScrapeJobs: () => request<ScrapeJob[]>('/scrape-jobs', undefined, Array.isArray),

  getAvailableScrapers: () => request<string[]>('/scrape-jobs/scrapers', undefined, Array.isArray),

  getScrapeRuns: (jobId: number) =>
    request<ScrapeRun[]>(`/scrape-jobs/${jobId}/runs`, undefined, Array.isArray),

  getScrapingIdentity: () => request<ScrapingIdentity>('/admin/scraping/identity'),

  getInstanceAo3Credential: () =>
    request<InstanceAo3Credential>('/admin/scraping/ao3-credential'),

  setInstanceAo3Credential: (ao3Username: string, ao3Password: string) =>
    request<InstanceAo3Credential>('/admin/scraping/ao3-credential', {
      method: 'PUT',
      body: JSON.stringify({ ao3Username, ao3Password }),
    }),

  removeInstanceAo3Credential: () =>
    request<InstanceAo3Credential>('/admin/scraping/ao3-credential', { method: 'DELETE' }),

  updateScrapingIdentity: (operatorContact: string | null) =>
    request<ScrapingIdentity>('/admin/scraping/identity', {
      method: 'PUT',
      body: JSON.stringify({ operatorContact }),
    }),

  getDatabaseStatus: () => request<DatabaseStatus>('/admin/database'),

  updateDatabaseSettings: (provider: 'Sqlite' | 'Postgres', postgresConnectionString?: string) =>
    request<{ message: string }>('/admin/database', {
      method: 'PUT',
      body: JSON.stringify({ provider, postgresConnectionString }),
    }),
};
