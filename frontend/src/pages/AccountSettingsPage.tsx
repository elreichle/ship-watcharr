import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import type { AccountEmail } from '../api/types';

export function AccountSettingsPage() {
  const [accountEmail, setAccountEmail] = useState<AccountEmail | null>(null);
  const [email, setEmail] = useState('');
  const [emailMessage, setEmailMessage] = useState<string | null>(null);
  const [emailError, setEmailError] = useState<string | null>(null);
  const [savingEmail, setSavingEmail] = useState(false);

  const loadEmail = () => {
    api
      .getAccountEmail()
      .then((value) => {
        setAccountEmail(value);
        setEmail(value.email ?? '');
      })
      .catch((err) => setEmailError(err instanceof ApiError ? err.message : 'Failed to load your email.'));
  };

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
            or set the contact directly under System → Scraping.
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

    </div>
  );
}
