import { Fragment, useEffect, useState } from 'react';
import { api } from '../api/client';
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
          <th>Mode</th>
          <th>Status</th>
          <th>Pages</th>
          <th>Requests</th>
          <th>Seen</th>
          <th>Added</th>
          <th>Updated</th>
          <th>Stopped because</th>
          <th>Error</th>
        </tr>
      </thead>
      <tbody>
        {runs.map((run) => (
          <tr key={run.id}>
            <td>{formatDate(run.startedAt)}</td>
            <td>{run.mode}</td>
            <td>{run.status}</td>
            <td>{run.pagesFetched}</td>
            <td>{run.requestsMade}</td>
            <td>{run.worksSeen}</td>
            <td>{run.worksAdded}</td>
            <td>{run.worksUpdated}</td>
            <td>{run.stopReason ?? '—'}</td>
            <td>{run.errorMessage ?? ''}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function DashboardPage() {
  const [jobs, setJobs] = useState<ScrapeJob[]>([]);
  const [expandedJobId, setExpandedJobId] = useState<number | null>(null);

  useEffect(() => {
    api.getScrapeJobs().then(setJobs).catch(() => setJobs([]));
  }, []);

  return (
    <div className="page">
      <h1>Scrape schedules</h1>

      {/*
        Read-only on purpose. A job belongs to a ship, not to a user, so its lifecycle follows
        subscription — it is created when the first user watches a ship and disabled when the
        last one stops. The watch/unwatch UI that drives that arrives with the scrape pipeline.
      */}
      <table className="jobs-table">
        <thead>
          <tr>
            <th>Ship</th>
            <th>Scraper</th>
            <th>Interval</th>
            <th>Enabled</th>
            <th>Last run</th>
            <th>Last status</th>
            <th>Next run</th>
          </tr>
        </thead>
        <tbody>
          {jobs.map((job) => (
            // Keyed on the Fragment, not the <tr>: the fragment is the array element, so a key on
            // the row inside it doesn't satisfy React.
            <Fragment key={job.id}>
              <tr>
                <td>
                  <button
                    className="link"
                    onClick={() => setExpandedJobId(expandedJobId === job.id ? null : job.id)}
                  >
                    {job.shipName}
                  </button>
                </td>
                <td>{job.scraperKey}</td>
                <td>{job.intervalMinutes} min</td>
                <td>{job.isEnabled ? 'yes' : 'no'}</td>
                <td>{formatDate(job.lastRunAt)}</td>
                <td>{job.lastRunStatus ?? '—'}{job.lastRunError ? ` (${job.lastRunError})` : ''}</td>
                <td>{formatDate(job.nextRunAt)}</td>
              </tr>
              {expandedJobId === job.id && (
                <tr>
                  <td colSpan={7}>
                    <JobRuns jobId={job.id} />
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
          {jobs.length === 0 && (
            <tr>
              <td colSpan={7}>No ships are being watched yet.</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
