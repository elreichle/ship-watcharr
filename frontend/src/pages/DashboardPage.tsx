import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import type { ScrapeJob, ScrapeRun } from '../api/types';

function formatDate(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—';
}

function JobRuns({ jobId }: { jobId: number }) {
  const [runs, setRuns] = useState<ScrapeRun[] | null>(null);

  useEffect(() => {
    api.getScrapeRuns(jobId).then(setRuns).catch(() => setRuns([]));
  }, [jobId]);

  if (runs === null) return <p>Loading runs…</p>;
  if (runs.length === 0) return <p>No runs yet.</p>;

  return (
    <table className="runs-table">
      <thead>
        <tr>
          <th>Started</th>
          <th>Status</th>
          <th>Items</th>
          <th>Error</th>
        </tr>
      </thead>
      <tbody>
        {runs.map((run) => (
          <tr key={run.id}>
            <td>{formatDate(run.startedAt)}</td>
            <td>{run.status}</td>
            <td>{run.itemsScraped}</td>
            <td>{run.errorMessage ?? ''}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function DashboardPage() {
  const [jobs, setJobs] = useState<ScrapeJob[]>([]);
  const [scrapers, setScrapers] = useState<string[]>([]);
  const [expandedJobId, setExpandedJobId] = useState<number | null>(null);
  const [name, setName] = useState('');
  const [scraperKey, setScraperKey] = useState('');
  const [intervalMinutes, setIntervalMinutes] = useState(60);
  const [error, setError] = useState<string | null>(null);

  const loadJobs = () => api.getScrapeJobs().then(setJobs).catch(() => setJobs([]));

  useEffect(() => {
    loadJobs();
    api.getAvailableScrapers().then((keys) => {
      setScrapers(keys);
      if (keys.length > 0) setScraperKey(keys[0]);
    });
  }, []);

  const onCreate = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await api.createScrapeJob(name, scraperKey, intervalMinutes);
      setName('');
      await loadJobs();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to create job.');
    }
  };

  const onToggle = async (job: ScrapeJob) => {
    await api.setScrapeJobEnabled(job.id, !job.isEnabled);
    await loadJobs();
  };

  const onDelete = async (job: ScrapeJob) => {
    await api.deleteScrapeJob(job.id);
    await loadJobs();
  };

  return (
    <div className="page">
      <h1>Scrape jobs</h1>

      <table className="jobs-table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Scraper</th>
            <th>Interval</th>
            <th>Enabled</th>
            <th>Last run</th>
            <th>Last status</th>
            <th>Next run</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((job) => (
            <>
              <tr key={job.id}>
                <td>
                  <button className="link" onClick={() => setExpandedJobId(expandedJobId === job.id ? null : job.id)}>
                    {job.name}
                  </button>
                </td>
                <td>{job.scraperKey}</td>
                <td>{job.intervalMinutes} min</td>
                <td>{job.isEnabled ? 'yes' : 'no'}</td>
                <td>{formatDate(job.lastRunAt)}</td>
                <td>{job.lastRunStatus ?? '—'}{job.lastRunError ? ` (${job.lastRunError})` : ''}</td>
                <td>{formatDate(job.nextRunAt)}</td>
                <td>
                  <button onClick={() => onToggle(job)}>{job.isEnabled ? 'Disable' : 'Enable'}</button>
                  <button onClick={() => onDelete(job)}>Delete</button>
                </td>
              </tr>
              {expandedJobId === job.id && (
                <tr>
                  <td colSpan={8}>
                    <JobRuns jobId={job.id} />
                  </td>
                </tr>
              )}
            </>
          ))}
          {jobs.length === 0 && (
            <tr>
              <td colSpan={8}>No scrape jobs yet.</td>
            </tr>
          )}
        </tbody>
      </table>

      <h2>New scrape job</h2>
      <form onSubmit={onCreate} className="inline-form">
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} required />
        </label>
        <label>
          Scraper
          <select value={scraperKey} onChange={(e) => setScraperKey(e.target.value)}>
            {scrapers.map((key) => (
              <option key={key} value={key}>
                {key}
              </option>
            ))}
          </select>
        </label>
        <label>
          Interval (minutes)
          <input
            type="number"
            min={1}
            value={intervalMinutes}
            onChange={(e) => setIntervalMinutes(Number(e.target.value))}
          />
        </label>
        {error && <p className="error">{error}</p>}
        <button type="submit">Create job</button>
      </form>
    </div>
  );
}
