import type {
  AccountEmail,
  Ao3CredentialStatus,
  Ao3TagType,
  CurrentUser,
  DatabaseStatus,
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
  WatchedShip,
  WatchedShipsResponse,
  WorkListItem,
  WorkQuery,
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

  getAo3Credential: () => request<Ao3CredentialStatus>('/account/ao3-credential'),

  setAo3Credential: (ao3Username: string, ao3Password: string) =>
    request<void>('/account/ao3-credential', {
      method: 'PUT',
      body: JSON.stringify({ ao3Username, ao3Password }),
    }),

  removeAo3Credential: () => request<void>('/account/ao3-credential', { method: 'DELETE' }),

  getWatchedShips: () => request<WatchedShipsResponse>('/ships', undefined, hasArray('ships')),

  watchShip: (tagName: string) =>
    request<WatchedShip>('/ships', { method: 'POST', body: JSON.stringify({ tagName }) }),

  unwatchShip: (shipId: number) => request<void>(`/ships/${shipId}`, { method: 'DELETE' }),

  getWorks: ({
    page,
    pageSize,
    shipId,
    sort,
    ascending,
    savedFilterId,
    useDefaultFilter,
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

    return request<PagedResult<WorkListItem>>(`/works?${query}`, undefined, hasArray('items'));
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
