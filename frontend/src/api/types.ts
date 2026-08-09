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

export interface ScrapeJob {
  id: number;
  name: string;
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
  startedAt: string;
  completedAt: string | null;
  itemsScraped: number;
  errorMessage: string | null;
}

export interface ScrapedItem {
  id: number;
  scrapeRunId: number;
  sourceUrl: string;
  title: string | null;
  payloadJson: string;
  scrapedAt: string;
}
