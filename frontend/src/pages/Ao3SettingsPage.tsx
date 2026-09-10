import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import type { InstanceAo3Credential, ScrapingIdentity } from '../api/types';
import { SkeletonRows } from '../components/Skeleton';

const SOURCE_LABELS: Record<ScrapingIdentity['contactSource'], string> = {
  AdminSetting: 'set here, on this page',
  Configuration: 'from this deployment’s configuration',
  AdminAccount: 'from the first admin’s account email',
  None: 'not set',
};

export function Ao3SettingsPage() {
  const [identity, setIdentity] = useState<ScrapingIdentity | null>(null);
  const [contact, setContact] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const [credential, setCredential] = useState<InstanceAo3Credential | null>(null);
  const [ao3Username, setAo3Username] = useState('');
  const [ao3Password, setAo3Password] = useState('');
  const [credentialError, setCredentialError] = useState<string | null>(null);
  const [loginError, setLoginError] = useState<string | null>(null);
  const [loginSaved, setLoginSaved] = useState<string | null>(null);
  const [savingLogin, setSavingLogin] = useState(false);

  const load = () =>
    api.getScrapingIdentity().then((next) => {
      setIdentity(next);
      setContact(next.isOverridden ? (next.operatorContact ?? '') : '');
    });

  // A failed read is its own state, kept apart from `loginError` (which belongs to the form
  // below): `credential` stays null either way, so without it a rejected fetch is indistinguishable
  // from one still in flight and the block says "Loading…" forever.
  const loadCredential = () =>
    api.getInstanceAo3Credential().then((next) => {
      setCredential(next);
      setCredentialError(null);
    });

  const retryCredential = () => {
    setCredentialError(null);
    loadCredential().catch(() => setCredentialError('Failed to load the AO3 login.'));
  };

  useEffect(() => {
    load().catch(() => setError('Failed to load the AO3 settings.'));
    loadCredential().catch(() => setCredentialError('Failed to load the AO3 login.'));
  }, []);

  // Saving or clearing the login changes what the gate says, so the identity block above it is
  // re-read too rather than left showing the state from before the change.
  const applyCredential = async (next: InstanceAo3Credential, message: string) => {
    setCredential(next);
    setCredentialError(null);
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
      await applyCredential(next, 'Saved. The next check signs in with this login.');
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
      await applyCredential(next, 'Removed. Checks are paused until a login is saved.');
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
        <h1>AO3 connection</h1>
        {error ? (
          <p className="error" role="alert">
            {error}
          </p>
        ) : (
          <SkeletonRows rows={4} kind="text" />
        )}
      </div>
    );
  }

  return (
    <div className="page">
      <h1>AO3 connection</h1>

      {/* Every user sees the same fact on the Ships page; an admin sees it here, where the form
          that fixes it is. Not gated on the identity being configured too: an instance missing
          both blockers is the case that most needs telling, and this is the only section that
          says anything about the login. */}
      {!identity.ao3LoginConfigured && (
        <p className="callout callout-error">
          Checks are paused: no AO3 login is stored for this instance. Followed ships stay scheduled
          and nothing is fetched until one is saved below.
        </p>
      )}

      <p className="hint">
        Every request this app makes to AO3 carries a User-Agent header identifying the
        software and giving AO3 a way to reach whoever runs this copy. It is a public label,
        not a login, and it is separate from your AO3 account credentials. AO3 is volunteer-run
        — an instance they can contact gets asked to slow down; one they can’t gets blocked.
      </p>

      <section>
        <h2>What AO3 currently sees</h2>
        {/* Keyed off the identity gate, not scrapingEnabled: an instance held for want of an AO3
            login still has a User-Agent, and this section is about what AO3 sees. `identityProblem`
            rather than `problem` for the same reason — the login blocker is reported by the callout
            above, and repeating it here would answer a question this heading did not ask. */}
        {identity.identityConfigured ? (
          <pre className="user-agent">{identity.userAgent}</pre>
        ) : (
          <p className="callout callout-error pre-wrap">{identity.identityProblem}</p>
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
      </section>

      <section>
        <h2>Contact address</h2>
        <form onSubmit={onSubmit}>
          <label>
            Email address or project URL
            <input
              type="text"
              name="contact"
              autoComplete="off"
              spellCheck={false}
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

          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          {saved && (
            <p className="success" role="status">
              Saved.
            </p>
          )}

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
      </section>

      <section>
        <h2>AO3 login</h2>

        <p className="hint">
          One AO3 account for the whole deployment, not one per user: a ship is checked once for
          everyone following it, so there is no per-user answer to whose session that check runs as.
          The password is stored encrypted and is never shown again. It is used to read the archive
          and nothing else — this app never posts, kudos, bookmarks or subscribes.
        </p>

        {credentialError !== null ? (
          <p className="error" role="alert">
            {credentialError}{' '}
            <button type="button" className="link" onClick={retryCredential}>
              Try again
            </button>
          </p>
        ) : credential === null ? (
          <SkeletonRows rows={2} kind="line" />
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
                    a cache of the login — its absence just means the next check logs in again
                  </span>
                </td>
              </tr>
            </tbody>
          </table>
        ) : (
          <p className="hint">No AO3 login stored. Checks are paused until there is one.</p>
        )}

        <form onSubmit={(e) => void onSubmitLogin(e)}>
          <label>
            AO3 username
            <input
              name="ao3Username"
              value={ao3Username}
              onChange={(e) => setAo3Username(e.target.value)}
              autoComplete="off"
              spellCheck={false}
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

          {loginError && (
            <p className="error" role="alert">
              {loginError}
            </p>
          )}
          {loginSaved && (
            <p className="success" role="status">
              {loginSaved}
            </p>
          )}

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
      </section>
    </div>
  );
}
