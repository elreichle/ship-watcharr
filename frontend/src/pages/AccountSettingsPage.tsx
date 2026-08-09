import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import type { AccountEmail, Ao3CredentialStatus } from '../api/types';

export function AccountSettingsPage() {
  const [status, setStatus] = useState<Ao3CredentialStatus | null>(null);
  const [ao3Username, setAo3Username] = useState('');
  const [ao3Password, setAo3Password] = useState('');
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const [accountEmail, setAccountEmail] = useState<AccountEmail | null>(null);
  const [email, setEmail] = useState('');
  const [emailMessage, setEmailMessage] = useState<string | null>(null);
  const [emailError, setEmailError] = useState<string | null>(null);
  const [savingEmail, setSavingEmail] = useState(false);

  const load = () => {
    api
      .getAo3Credential()
      .then(setStatus)
      .catch((err) => setError(err instanceof ApiError ? err.message : 'Failed to load AO3 credential status.'));
  };

  const loadEmail = () => {
    api
      .getAccountEmail()
      .then((value) => {
        setAccountEmail(value);
        setEmail(value.email ?? '');
      })
      .catch((err) => setEmailError(err instanceof ApiError ? err.message : 'Failed to load your email.'));
  };

  useEffect(load, []);
  useEffect(loadEmail, []);

  const onSubmitEmail = async (e: FormEvent) => {
    e.preventDefault();
    setEmailError(null);
    setEmailMessage(null);
    setSavingEmail(true);
    try {
      const updated = await api.updateAccountEmail(email);
      setAccountEmail(updated);
      setEmail(updated.email ?? '');
      setEmailMessage(updated.email ? 'Email saved.' : 'Email removed.');
    } catch (err) {
      setEmailError(err instanceof ApiError ? err.message : 'Failed to save your email.');
    } finally {
      setSavingEmail(false);
    }
  };

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setMessage(null);
    setSubmitting(true);
    try {
      await api.setAo3Credential(ao3Username, ao3Password);
      setAo3Username('');
      setAo3Password('');
      setMessage('AO3 credentials saved.');
      load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save AO3 credentials.');
    } finally {
      setSubmitting(false);
    }
  };

  const onRemove = async () => {
    setError(null);
    setMessage(null);
    try {
      await api.removeAo3Credential();
      setMessage('AO3 credentials removed.');
      load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to remove AO3 credentials.');
    }
  };

  return (
    <div className="page">
      <h1>Account settings</h1>

      <section>
        <h2>Email (optional)</h2>
        <form onSubmit={onSubmitEmail}>
          <label>
            Email
            <input
              type="email"
              autoComplete="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="not set"
            />
          </label>
          <p className="hint">
            Not needed to sign in — you log in with your username. The one thing it is used for: if
            you are the admin of this instance, it becomes the contact AO3 sees in the scraper's
            User-Agent, so they can reach you instead of blocking you. Leave it blank to clear it,
            or set the contact directly under Scraping.
          </p>
          {accountEmail?.isUsedAsOperatorContact && (
            <p className="hint">
              <strong>In use:</strong> this address is the operator contact AO3 currently sees.
              Clearing it disables scraping until another contact is set.
            </p>
          )}
          {emailMessage && <p className="success">{emailMessage}</p>}
          {emailError && <p className="error">{emailError}</p>}
          <button type="submit" disabled={savingEmail}>
            {savingEmail ? 'Saving…' : 'Save email'}
          </button>
        </form>
      </section>

      <section>
        <h2>AO3 credentials</h2>
        {status?.hasCredential ? (
          <div>
            <p>
              Stored AO3 username: <strong>{status.ao3Username}</strong>
            </p>
            <p>Active session: {status.hasActiveSession ? 'yes' : 'no (will re-authenticate on next scrape)'}</p>
            <button onClick={onRemove}>Remove credentials</button>
          </div>
        ) : (
          <p>No AO3 credentials stored yet.</p>
        )}

        <form onSubmit={onSubmit}>
          <label>
            AO3 username
            <input value={ao3Username} onChange={(e) => setAo3Username(e.target.value)} required />
          </label>
          <label>
            AO3 password
            <input type="password" value={ao3Password} onChange={(e) => setAo3Password(e.target.value)} required />
          </label>
          <p className="hint">
            Stored encrypted at rest. It is never shown again after saving, and is only used
            server-side to authenticate scrape requests on your behalf.
          </p>
          {message && <p className="success">{message}</p>}
          {error && <p className="error">{error}</p>}
          <button type="submit" disabled={submitting}>
            {submitting ? 'Saving…' : status?.hasCredential ? 'Update credentials' : 'Save credentials'}
          </button>
        </form>
      </section>
    </div>
  );
}
