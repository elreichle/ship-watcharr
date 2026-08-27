import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import type { InstanceAo3Credential, ScrapingIdentity } from '../api/types';

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

  const [credential, setCredential] = useState<InstanceAo3Credential | null>(null);
  const [ao3Username, setAo3Username] = useState('');
  const [ao3Password, setAo3Password] = useState('');
  const [loginError, setLoginError] = useState<string | null>(null);
  const [loginSaved, setLoginSaved] = useState<string | null>(null);
  const [savingLogin, setSavingLogin] = useState(false);

  const load = () =>
    api.getScrapingIdentity().then((next) => {
      setIdentity(next);
      setContact(next.isOverridden ? (next.operatorContact ?? '') : '');
    });

  const loadCredential = () => api.getInstanceAo3Credential().then(setCredential);

  useEffect(() => {
    load().catch(() => setError('Failed to load scraping settings.'));
    loadCredential().catch(() => setLoginError('Failed to load the AO3 login.'));
  }, []);

  // Saving or clearing the login changes what the gate says, so the identity block above it is
  // re-read too rather than left showing the state from before the change.
  const applyCredential = async (next: InstanceAo3Credential, message: string) => {
    setCredential(next);
    setLoginSaved(message);
    await load().catch(() => undefined);
  };

  const onSubmitLogin = async (e: FormEvent) => {
    e.preventDefault();
    setLoginError(null);
    setLoginSaved(null);
    setSavingLogin(true);
    try {
      const next = await api.setInstanceAo3Credential(ao3Username.trim(), ao3Password);
      setAo3Username('');
      setAo3Password('');
      await applyCredential(next, 'Saved. Scraping picks this up on the next poll.');
    } catch (err) {
      setLoginError(err instanceof ApiError ? err.message : 'Failed to save the AO3 login.');
    } finally {
      setSavingLogin(false);
    }
  };

  const onRemoveLogin = async () => {
    setLoginError(null);
    setLoginSaved(null);
    setSavingLogin(true);
    try {
      const next = await api.removeInstanceAo3Credential();
      await applyCredential(next, 'Removed. Scraping is held until a login is saved.');
    } catch (err) {
      setLoginError(err instanceof ApiError ? err.message : 'Failed to remove the AO3 login.');
    } finally {
      setSavingLogin(false);
    }
  };

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

      {/* Every user sees the same fact on the Ships page; an admin sees it here, where the form
          that fixes it is. Not gated on the identity being configured too: an instance missing
          both blockers is the case that most needs telling, and this is the only section that
          says anything about the login. */}
      {!identity.ao3LoginConfigured && (
        <p className="callout callout-error">
          Scraping is held: no AO3 login is stored for this instance. Followed ships stay scheduled
          and nothing is fetched until one is saved below.
        </p>
      )}

      <p className="hint">
        Every request this app makes to AO3 carries a User-Agent header identifying the
        software and giving AO3 a way to reach whoever runs this copy. It is a public label,
        not a login, and it is separate from your AO3 account credentials. AO3 is volunteer-run
        — an instance they can contact gets asked to slow down; one they can’t gets blocked.
      </p>

      <h2>What AO3 currently sees</h2>
      {/* Keyed off the identity gate, not scrapingEnabled: an instance held for want of an AO3
          login still has a User-Agent, and this section is about what AO3 sees. `identityProblem`
          rather than `problem` for the same reason — the login blocker is reported by the callout
          above, and repeating it here would answer a question this heading did not ask. */}
      {identity.identityConfigured ? (
        <pre className="user-agent">{identity.userAgent}</pre>
      ) : (
        <p className="error pre-wrap">{identity.identityProblem}</p>
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

      <h2>AO3 login</h2>

      <p className="hint">
        One AO3 account for the whole deployment, not one per user: a ship is scraped once for
        everyone following it, so there is no per-user answer to whose session that scrape runs as.
        The password is stored encrypted and is never shown again. It is used to read the archive
        and nothing else — this app never posts, kudos, bookmarks or subscribes.
      </p>

      {credential === null ? (
        <p>Loading…</p>
      ) : credential.hasCredential ? (
        <table className="identity-table">
          <tbody>
            <tr>
              <th>Account</th>
              <td>
                <code>{credential.ao3Username}</code>
              </td>
            </tr>
            <tr>
              <th>Session</th>
              <td>
                {credential.hasCachedSession ? 'cached' : 'none cached'}{' '}
                {/* Said plainly because it is easy to mistake for a problem: the cookie is a cache
                    of the password, so its absence costs one login and nothing else. */}
                <span className="hint">
                  a cache of the login — its absence just means the next scrape logs in again
                </span>
              </td>
            </tr>
          </tbody>
        </table>
      ) : (
        <p className="hint">No AO3 login stored. Scraping is held until there is one.</p>
      )}

      <form onSubmit={(e) => void onSubmitLogin(e)}>
        <label>
          AO3 username
          <input
            value={ao3Username}
            onChange={(e) => setAo3Username(e.target.value)}
            autoComplete="off"
            required
          />
        </label>
        <label>
          AO3 password
          <input
            type="password"
            value={ao3Password}
            onChange={(e) => setAo3Password(e.target.value)}
            autoComplete="new-password"
            required
          />
        </label>

        <p className="hint">
          Saving replaces any stored login and discards the cached session, which belonged to the
          old password. Changes apply on the next poll; no restart needed.
        </p>

        {loginError && <p className="error">{loginError}</p>}
        {loginSaved && <p className="success">{loginSaved}</p>}

        <div className="button-row">
          <button type="submit" disabled={savingLogin}>
            {savingLogin ? 'Saving…' : credential?.hasCredential ? 'Replace login' : 'Save login'}
          </button>
          {credential?.hasCredential && (
            <button type="button" disabled={savingLogin} onClick={() => void onRemoveLogin()}>
              Remove login
            </button>
          )}
        </div>
      </form>
    </div>
  );
}
