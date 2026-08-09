export interface CurrentUser {
  id: string;
  email: string;
  isAdmin: boolean;
}

export interface Ao3CredentialStatus {
  hasCredential: boolean;
  ao3Username: string | null;
  hasActiveSession: boolean;
  sessionExpiresAt: string | null;
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
