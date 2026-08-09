import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import type { DatabaseStatus } from '../api/types';

export function AdminDatabasePage() {
  const [status, setStatus] = useState<DatabaseStatus | null>(null);
  const [provider, setProvider] = useState<'Sqlite' | 'Postgres'>('Sqlite');
  const [postgresConnectionString, setPostgresConnectionString] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [restarting, setRestarting] = useState(false);

  useEffect(() => {
    api.getDatabaseStatus().then((s) => {
      setStatus(s);
      setProvider(s.provider);
    });
  }, []);

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      await api.updateDatabaseSettings(
        provider,
        provider === 'Postgres' ? postgresConnectionString : undefined,
      );
      setRestarting(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save database settings.');
    } finally {
      setSubmitting(false);
    }
  };

  if (restarting) {
    return (
      <div className="page">
        <h1>Database</h1>
        <p className="success">
          Settings saved. The application is restarting to apply the new database
          configuration — this page will stop responding for a few seconds. Reload once it
          comes back.
        </p>
      </div>
    );
  }

  return (
    <div className="page">
      <h1>Database</h1>

      {status && (
        <p>
          Currently running on <strong>{status.provider}</strong>
          {status.provider === 'Sqlite' && <> ({status.sqliteDbPath})</>}
          {status.provider === 'Postgres' && status.postgresConnectionSummary && (
            <> ({status.postgresConnectionSummary})</>
          )}
          .
        </p>
      )}

      <p className="hint">
        SQLite is the zero-config default — a single file, no setup required. Switch to
        PostgreSQL only if you know what you're doing: this does <strong>not</strong> migrate
        existing data, the new database starts empty, and saving restarts the application to
        apply the change.
      </p>

      <form onSubmit={onSubmit}>
        <label>
          <input
            type="radio"
            name="provider"
            value="Sqlite"
            checked={provider === 'Sqlite'}
            onChange={() => setProvider('Sqlite')}
          />
          {' '}SQLite (default)
        </label>
        <label>
          <input
            type="radio"
            name="provider"
            value="Postgres"
            checked={provider === 'Postgres'}
            onChange={() => setProvider('Postgres')}
          />
          {' '}PostgreSQL
        </label>

        {provider === 'Postgres' && (
          <label>
            Connection string
            <input
              value={postgresConnectionString}
              onChange={(e) => setPostgresConnectionString(e.target.value)}
              placeholder="Host=db;Port=5432;Database=shipwatcharr;Username=shipwatcharr;Password=..."
              required
            />
          </label>
        )}

        {error && <p className="error">{error}</p>}
        <button type="submit" disabled={submitting}>
          {submitting ? 'Saving…' : 'Save and restart'}
        </button>
      </form>
    </div>
  );
}
