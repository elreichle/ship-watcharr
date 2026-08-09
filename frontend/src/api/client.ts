import type {
  Ao3CredentialStatus,
  CurrentUser,
  DatabaseStatus,
  ScrapeJob,
  ScrapeRun,
  ScrapingIdentity,
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
      message = body.message ?? body.title ?? message;
    } catch {
      // response had no JSON body
    }
    throw new ApiError(response.status, message);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const api = {
  register: (email: string, password: string) =>
    request<CurrentUser>('/auth/register', { method: 'POST', body: JSON.stringify({ email, password }) }),

  login: (email: string, password: string) =>
    request<CurrentUser>('/auth/login', { method: 'POST', body: JSON.stringify({ email, password }) }),

  logout: () => request<void>('/auth/logout', { method: 'POST' }),

  me: () => request<CurrentUser>('/auth/me'),

  getAo3Credential: () => request<Ao3CredentialStatus>('/account/ao3-credential'),

  setAo3Credential: (ao3Username: string, ao3Password: string) =>
    request<void>('/account/ao3-credential', {
      method: 'PUT',
      body: JSON.stringify({ ao3Username, ao3Password }),
    }),

  removeAo3Credential: () => request<void>('/account/ao3-credential', { method: 'DELETE' }),

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
