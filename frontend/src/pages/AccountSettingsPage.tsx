import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/AuthContext';
import type { AccountEmail } from '../api/types';

export function AccountSettingsPage() {
  const { user, refresh } = useAuth();

  const [username, setUsername] = useState(user?.username ?? '');
  const [usernameMessage, setUsernameMessage] = useState<string | null>(null);
  const [usernameError, setUsernameError] = useState<string | null>(null);
  const [savingUsername, setSavingUsername] = useState(false);

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [passwordMessage, setPasswordMessage] = useState<string | null>(null);
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const [savingPassword, setSavingPassword] = useState(false);

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

  const onSubmitUsername = async (e: FormEvent) => {
    e.preventDefault();
    setUsernameError(null);
    setUsernameMessage(null);
    setSavingUsername(true);
    try {
      await api.changeUsername(username);
      await refresh();
      setUsernameMessage('Username saved.');
    } catch (err) {
      setUsernameError(err instanceof ApiError ? err.message : 'Failed to save your username.');
    } finally {
      setSavingUsername(false);
    }
  };

  const onSubmitPassword = async (e: FormEvent) => {
    e.preventDefault();
    setPasswordError(null);
    setPasswordMessage(null);
    if (newPassword !== confirmPassword) {
      setPasswordError('New password and confirmation do not match.');
      return;
    }
    setSavingPassword(true);
    try {
      await api.changePassword(currentPassword, newPassword);
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
      setPasswordMessage('Password saved.');
    } catch (err) {
      setPasswordError(err instanceof ApiError ? err.message : 'Failed to save your password.');
    } finally {
      setSavingPassword(false);
    }
  };

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
        <h2>Username</h2>
        <form onSubmit={onSubmitUsername}>
          <label>
            Username
            <input
              type="text"
              autoComplete="username"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              minLength={3}
              maxLength={64}
              required
            />
          </label>
          {usernameMessage && <p className="success">{usernameMessage}</p>}
          {usernameError && <p className="error">{usernameError}</p>}
          <button type="submit" disabled={savingUsername}>
            {savingUsername ? 'Saving…' : 'Save username'}
          </button>
        </form>
      </section>

      <section>
        <h2>Password</h2>
        <form onSubmit={onSubmitPassword}>
          <label>
            Current password
            <input
              type="password"
              autoComplete="current-password"
              value={currentPassword}
              onChange={(e) => setCurrentPassword(e.target.value)}
              required
            />
          </label>
          <label>
            New password
            <input
              type="password"
              autoComplete="new-password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              required
            />
          </label>
          <label>
            Confirm new password
            <input
              type="password"
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              required
            />
          </label>
          {passwordMessage && <p className="success">{passwordMessage}</p>}
          {passwordError && <p className="error">{passwordError}</p>}
          <button type="submit" disabled={savingPassword}>
            {savingPassword ? 'Saving…' : 'Save password'}
          </button>
        </form>
      </section>

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
