import { Fragment, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { ScrapeJob, ScrapeRun } from '../api/types';
import { EmptyState } from '../components/EmptyState';
import { Icon } from '../components/Icon';
import { SkeletonRows } from '../components/Skeleton';
import { formatDateTimeOrDash as formatDate } from '../format';

function JobRuns({ jobId }: { jobId: number }) {
  const [runs, setRuns] = useState<ScrapeRun[] | null>(null);

  useEffect(() => {
    api.getScrapeRuns(jobId).then(setRuns).catch(() => setRuns([]));
  }, [jobId]);

  if (runs === null) return <SkeletonRows rows={2} kind="line" />;
  if (runs.length === 0) return <p className="hint">No runs yet.</p>;

  return (
    <table className="runs-table">
      <thead>
        <tr>
          <th>Started</th>
          <th>Mode</th>
          <th>Status</th>
          <th className="numeric">Pages</th>
          <th className="numeric">Requests</th>
          <th className="numeric">Seen</th>
          <th className="numeric">Added</th>
          <th className="numeric">Updated</th>
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
            <td className="numeric">{run.pagesFetched}</td>
            <td className="numeric">{run.requestsMade}</td>
            <td className="numeric">{run.worksSeen}</td>
            <td className="numeric">{run.worksAdded}</td>
            <td className="numeric">{run.worksUpdated}</td>
            <td>{run.stopReason ?? '—'}</td>
            <td>{run.errorMessage ?? ''}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function SchedulesPage() {
  // Null while loading, so the page cannot claim "no ships are being watched" before it knows.
  const [jobs, setJobs] = useState<ScrapeJob[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [expandedJobId, setExpandedJobId] = useState<number | null>(null);

  useEffect(() => {
    api
      .getScrapeJobs()
      .then(setJobs)
      .catch(() => setError('Could not load the schedules.'));
  }, []);

  return (
    <div className="page">
      <h1>Schedules</h1>

      <p className="hint">
        One schedule per ship, shared by everyone watching it. Add and remove ships on the{' '}
        <Link to="/ships">Ships</Link> tab — a schedule appears when the first user watches a ship
        and switches off when the last one stops.
      </p>

      {error !== null && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      {jobs === null ? (
        !error && <SkeletonRows rows={3} kind="line" />
      ) : jobs.length === 0 ? (
        <EmptyState
          title="No ships are being watched yet"
          action={
            <Link className="button" to="/ships">
              Follow a ship
            </Link>
          }
        >
          A schedule appears here the moment the first reader follows a ship.
        </EmptyState>
      ) : (
        /* Read-only on purpose: a job belongs to a ship, not to a user, so its lifecycle follows
           subscription rather than anything editable here. */
        <table className="jobs-table">
          <thead>
            <tr>
              <th>Ship</th>
              <th>Source</th>
              <th className="numeric">Interval</th>
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
                    {/* Opens the run history underneath. The chevron turns, the same way the
                        sidebar's groups say they are open. */}
                    <button
                      type="button"
                      className="link expander"
                      aria-expanded={expandedJobId === job.id}
                      onClick={() => setExpandedJobId(expandedJobId === job.id ? null : job.id)}
                    >
                      <Icon name="chevron-right" size="xs" className="expander-icon" />
                      {job.shipName}
                    </button>
                  </td>
                  <td>{job.scraperKey}</td>
                  <td className="numeric">{job.intervalMinutes} min</td>
                  <td>{job.isEnabled ? 'Yes' : 'No'}</td>
                  <td>{formatDate(job.lastRunAt)}</td>
                  <td>
                    {job.lastRunStatus ?? '—'}
                    {job.lastRunError ? ` (${job.lastRunError})` : ''}
                  </td>
                  <td>{formatDate(job.nextRunAt)}</td>
                </tr>
                {expandedJobId === job.id && (
                  <tr className="jobs-runs-row">
                    <td colSpan={7}>
                      <JobRuns jobId={job.id} />
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
