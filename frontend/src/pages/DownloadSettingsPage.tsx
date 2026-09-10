import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import type { AccountPreferences } from '../api/types';

/**
 * Settings → Downloads: what the server fetches on this reader's behalf without being clicked.
 *
 * Saved on change rather than behind a button — one checkbox is one decision — and shown as what
 * the server answered rather than what was clicked, so a save that did not land does not leave
 * the box showing a preference the account does not hold.
 */
export function DownloadSettingsPage() {
  const [preferences, setPreferences] = useState<AccountPreferences | null>(null);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let current = true;

    api
      .getAccountPreferences()
      .then((loaded) => {
        if (current) setPreferences(loaded);
      })
      .catch((err) => {
        if (current) {
          setError(err instanceof ApiError ? err.message : 'Failed to load your settings.');
        }
      });

    return () => {
      current = false;
    };
  }, []);

  const onChangeAutoDownload = async (autoDownloadFavorites: boolean) => {
    if (preferences === null) return;

    setError(null);
    setMessage(null);
    setSaving(true);
    try {
      const saved = await api.updateAccountPreferences({ ...preferences, autoDownloadFavorites });
      setPreferences(saved);
      setMessage(
        saved.autoDownloadFavorites
          ? 'Saved. New favorites will be fetched as EPUB.'
          : 'Saved. Favoriting a work no longer fetches it.',
      );
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save your settings.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="page">
      <h1>Download settings</h1>

      <section>
        <h2>Favorites</h2>
        <label>
          <input
            type="checkbox"
            checked={preferences?.autoDownloadFavorites ?? false}
            disabled={preferences === null || saving}
            onChange={(e) => void onChangeAutoDownload(e.target.checked)}
          />
          Fetch the EPUB of every work I favorite
        </label>
        <p className="hint">
          Marking a work a favorite then asks for its EPUB exactly as the button on the work's page
          would: it joins the queue behind the same rate limit every AO3 request shares, and a copy the
          server already holds at the current version is used without touching AO3. Only the moment
          the mark goes on — re-rating a favorite does not ask again, and taking the mark off leaves
          the file where it is. Works you favorited before turning this on are not fetched; ask for
          those from their own pages. Everything queued this way is listed under{' '}
          <Link to="/downloads">Downloads</Link>, where it can be removed like any other request.
        </p>
        {message && (
          <p className="success" role="status">
            {message}
          </p>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </section>
    </div>
  );
}
