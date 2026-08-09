import type {
  AccountEmail,
  Ao3CredentialStatus,
  CurrentUser,
  DatabaseStatus,
  PagedResult,
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

async function request<T>(path: string, init?: RequestInit): Promise<T> {
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
  return (await response.json()) as T;
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

  getWatchedShips: () => request<WatchedShipsResponse>('/ships'),

  watchShip: (tagName: string) =>
    request<WatchedShip>('/ships', { method: 'POST', body: JSON.stringify({ tagName }) }),

  unwatchShip: (shipId: number) => request<void>(`/ships/${shipId}`, { method: 'DELETE' }),

  getWorks: ({ page, pageSize, shipId, sort, ascending }: WorkQuery = {}) => {
    // Built key by key rather than from the object: a null shipId means "every watched ship", and
    // URLSearchParams would happily send it as the literal string "null".
    const query = new URLSearchParams();
    if (page !== undefined) query.set('page', String(page));
    if (pageSize !== undefined) query.set('pageSize', String(pageSize));
    if (shipId != null) query.set('shipId', String(shipId));
    if (sort !== undefined) query.set('sort', sort);
    if (ascending !== undefined) query.set('ascending', String(ascending));

    return request<PagedResult<WorkListItem>>(`/works?${query}`);
  },

  getScrapeJobs: () => request<ScrapeJob[]>('/scrape-jobs'),

  getAvailableScrapers: () => request<string[]>('/scrape-jobs/scrapers'),

  getScrapeRuns: (jobId: number) => request<ScrapeRun[]>(`/scrape-jobs/${jobId}/runs`),

  getScrapingIdentity: () => request<ScrapingIdentity>('/admin/scraping/identity'),

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
