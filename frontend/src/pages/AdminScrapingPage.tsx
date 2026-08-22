import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import type { ScrapingIdentity } from '../api/types';

const SOURCE_LABELS: Record<ScrapingIdentity['contactSource'], string> = {
  AdminSetting: 'set here, on this page',
  Configuration: 'from this deployment’s configuration',
  AdminAccount: 'from the first admin’s account email',
  None: 'not set',
};

export function AdminScrapingPage() {
  const [identity, setIdentity] = useState<ScrapingIdentity | null>(null);
  const [contact, setContact] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const load = () =>
    api.getScrapingIdentity().then((next) => {
      setIdentity(next);
      setContact(next.isOverridden ? (next.operatorContact ?? '') : '');
    });

  useEffect(() => {
    load().catch(() => setError('Failed to load scraping settings.'));
  }, []);

  const save = async (value: string | null) => {
    setError(null);
    setSaved(false);
    setSubmitting(true);
    try {
      const next = await api.updateScrapingIdentity(value);
      setIdentity(next);
      setContact(next.isOverridden ? (next.operatorContact ?? '') : '');
      setSaved(true);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save.');
    } finally {
      setSubmitting(false);
    }
  };

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    void save(contact.trim() === '' ? null : contact.trim());
  };

  if (!identity) {
    return (
      <div className="page">
        <h1>Scraping identity</h1>
        {error ? <p className="error">{error}</p> : <p>Loading…</p>}
      </div>
    );
  }

  return (
    <div className="page">
      <h1>Scraping identity</h1>

      <p className="hint">
        Every request this app makes to AO3 carries a User-Agent header identifying the
        software and giving AO3 a way to reach whoever runs this copy. It is a public label,
        not a login, and it is separate from your AO3 account credentials. AO3 is volunteer-run
        — an instance they can contact gets asked to slow down; one they can’t gets blocked.
      </p>

      <h2>What AO3 currently sees</h2>
      {/* Keyed off the identity gate, not scrapingEnabled: an instance held for want of an AO3
          login still has a User-Agent, and this section is about what AO3 sees. */}
      {identity.identityConfigured ? (
        <pre className="user-agent">{identity.userAgent}</pre>
      ) : (
        <p className="error pre-wrap">{identity.problem}</p>
      )}

      <table className="identity-table">
        <tbody>
          <tr>
            <th>Software</th>
            <td>
              <code>{identity.productToken}</code>{' '}
              <span className="hint">fixed — this is what lets AO3 recognise the tool</span>
            </td>
          </tr>
          <tr>
            <th>This instance</th>
            <td>
              <code>instance/{identity.instanceId}</code>{' '}
              <span className="hint">
                random, generated on first run — distinguishes deployments without identifying anyone
              </span>
            </td>
          </tr>
          <tr>
            <th>Contact</th>
            <td>
              {identity.operatorContact ? <code>{identity.operatorContact}</code> : <em>none</em>}{' '}
              <span className="hint">({SOURCE_LABELS[identity.contactSource]})</span>
            </td>
          </tr>
        </tbody>
      </table>

      <h2>Contact address</h2>
      <form onSubmit={onSubmit}>
        <label>
          Email address or project URL
          <input
            value={contact}
            onChange={(e) => setContact(e.target.value)}
            placeholder={identity.defaultContact ?? 'you@example.com'}
          />
        </label>

        <p className="hint">
          {identity.isOverridden ? (
            <>Leave blank and save to revert to <code>{identity.defaultContact ?? 'no contact'}</code>.</>
          ) : (
            <>
              Currently using the default. Enter a value to override it — a <code>+</code> alias
              such as <code>you+ao3@example.com</code> works and keeps it filterable.
            </>
          )}{' '}
          Changes apply immediately; no restart needed.
        </p>

        {error && <p className="error">{error}</p>}
        {saved && <p className="success">Saved.</p>}

        <div className="button-row">
          <button type="submit" disabled={submitting}>
            {submitting ? 'Saving…' : 'Save'}
          </button>
          {identity.isOverridden && (
            <button type="button" disabled={submitting} onClick={() => void save(null)}>
              Reset to default
            </button>
          )}
        </div>
      </form>
    </div>
  );
}
