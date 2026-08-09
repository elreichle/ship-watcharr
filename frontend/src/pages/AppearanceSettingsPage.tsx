import { useState, type ChangeEvent } from 'react';
import { useTheme } from '../theme/ThemeContext';
import type { ThemeMode } from '../theme/theme';

const MODES: { value: ThemeMode; label: string }[] = [
  { value: 'system', label: 'System' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
];

export function AppearanceSettingsPage() {
  const { mode, setMode, customCss, setCustomCss, safeMode } = useTheme();
  const [draft, setDraft] = useState(customCss);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const dirty = draft !== customCss;

  const onChangeMode = (next: ThemeMode) => {
    setError(null);
    setMessage(null);
    try {
      setMode(next);
    } catch {
      setError('Could not save your theme mode — this browser is blocking local storage.');
    }
  };

  const onPickFile = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    // Reset the input so picking the same file twice still fires a change event.
    event.target.value = '';
    if (!file) return;
    setError(null);
    setMessage(null);
    try {
      setDraft(await file.text());
      setMessage(`Loaded ${file.name}. Review it, then apply.`);
    } catch {
      setError(`Could not read ${file.name}.`);
    }
  };

  const onApply = () => {
    setError(null);
    setMessage(null);
    try {
      setCustomCss(draft);
      setMessage(draft ? 'Theme applied.' : 'Theme cleared.');
    } catch {
      // Themes run to hundreds of KB, so hitting the storage quota is a realistic outcome
      // rather than a theoretical one.
      setError('Could not save the theme — it may be too large for this browser’s storage.');
    }
  };

  const onReset = () => {
    setDraft('');
    setError(null);
    setMessage(null);
    try {
      setCustomCss('');
      setMessage('Theme cleared.');
    } catch {
      setError('Could not clear the theme — this browser is blocking local storage.');
    }
  };

  return (
    <div className="page">
      <h1>Appearance</h1>

      {safeMode && (
        <p className="callout callout-warning">
          <strong>Safe mode.</strong> Your theme is still saved but is not being applied on this
          page load. Clear it below, or drop <code>?safemode</code> from the URL to load it again.
        </p>
      )}

      <section>
        <h2>Colour scheme</h2>
        <div className="segmented" role="group" aria-label="Colour scheme">
          {MODES.map((option) => (
            <button
              key={option.value}
              type="button"
              className={mode === option.value ? 'segmented-option is-active' : 'segmented-option'}
              aria-pressed={mode === option.value}
              onClick={() => onChangeMode(option.value)}
            >
              {option.label}
            </button>
          ))}
        </div>
        <p className="hint">
          Sets <code>theme-light</code> or <code>theme-dark</code> on the page, the same switch
          Obsidian themes are written against. <strong>System</strong> follows your OS and updates
          live when it changes.
        </p>
      </section>

      <section>
        <h2>Custom theme</h2>
        <p className="hint">
          This interface is built from Obsidian's CSS variables, so an Obsidian community theme's{' '}
          <code>theme.css</code> can be pasted in below and will restyle it. Two things worth
          knowing before you do:
        </p>
        <ul className="hint">
          <li>
            Only the theme's <strong>variables</strong> carry over — its colours, fonts, radii and
            spacing. Rules it writes for Obsidian's own editor and panes have nothing to match here
            and are simply ignored, so a theme will look like its palette rather than like Obsidian.
          </li>
          <li>
            CSS can load remote images and fonts, which means a theme from a source you don't trust
            can tell whoever wrote it when you open the page. Read it, or use one you trust.
          </li>
          <li>
            Themes are saved <strong>in this browser only</strong> — nothing is sent to the server,
            and the theme won't follow you to another device.
          </li>
          <li>
            If a theme leaves the interface unusable, add <code>?safemode</code> to the URL to load
            without it, then clear it here.
          </li>
        </ul>

        <label>
          Theme CSS
          <textarea
            className="theme-css-input"
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            spellCheck={false}
            rows={14}
            placeholder="Paste an Obsidian theme.css here…"
          />
        </label>

        <p className="hint">
          {draft.length.toLocaleString()} characters
          {dirty && ' — unsaved'}
        </p>

        {message && <p className="success">{message}</p>}
        {error && <p className="error">{error}</p>}

        <div className="button-row">
          <button type="button" onClick={onApply} disabled={!dirty}>
            Apply theme
          </button>
          <label className="file-button">
            Load .css file…
            <input type="file" accept=".css,text/css" onChange={onPickFile} />
          </label>
          <button type="button" onClick={onReset} disabled={!draft && !customCss}>
            Reset to default
          </button>
        </div>
      </section>
    </div>
  );
}
