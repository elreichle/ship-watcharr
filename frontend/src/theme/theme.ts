/*
 * Theme storage and DOM application.
 *
 * Kept separate from ThemeContext so the pure helpers can be read (and reasoned about) without
 * React in the way — and so ThemeContext stays a components-only module for fast refresh.
 *
 * The storage keys, the style element id and the `data-theme` attribute are duplicated verbatim
 * by the inline boot script in index.html, which has to run before any bundle loads and therefore
 * cannot import from here. Change one, change both.
 */

import { DEFAULT_THEME_PRESET, isThemePresetId, type ThemePresetId } from './presets';

export type ThemeMode = 'system' | 'light' | 'dark';
export type ResolvedMode = 'light' | 'dark';

export const THEME_MODE_KEY = 'shipwatcharr.theme.mode';
export const THEME_PRESET_KEY = 'shipwatcharr.theme.preset';
export const THEME_CSS_KEY = 'shipwatcharr.theme.css';
export const USER_THEME_STYLE_ID = 'user-theme';

const DARK_QUERY = '(prefers-color-scheme: dark)';

/**
 * Safe mode skips injecting the user's CSS for one page load. A pasted theme is arbitrary CSS and
 * can perfectly well hide the sidebar, the Appearance page, or the reset button — without an
 * escape hatch in the URL the only way back is devtools.
 */
export function isSafeMode(): boolean {
  return new URLSearchParams(window.location.search).has('safemode');
}

export function readMode(): ThemeMode {
  try {
    const stored = window.localStorage.getItem(THEME_MODE_KEY);
    if (stored === 'light' || stored === 'dark' || stored === 'system') return stored;
  } catch {
    // Storage blocked (private browsing, or the user disabled it). Following the OS is the
    // right answer when we can't know what they picked.
  }
  return 'system';
}

export function readPreset(): ThemePresetId {
  try {
    const stored = window.localStorage.getItem(THEME_PRESET_KEY);
    // A preset that has since been renamed or removed falls back to the default rather than
    // leaving <html> pointing at CSS that no longer exists.
    if (isThemePresetId(stored)) return stored;
  } catch {
    // Storage blocked; the default palette is the only one we can promise.
  }
  return DEFAULT_THEME_PRESET;
}

export function readCustomCss(): string {
  try {
    return window.localStorage.getItem(THEME_CSS_KEY) ?? '';
  } catch {
    return '';
  }
}

/** Throws if storage is unavailable or full, so callers can tell the user the save didn't stick. */
export function writeMode(mode: ThemeMode): void {
  window.localStorage.setItem(THEME_MODE_KEY, mode);
}

/** Throws if storage is unavailable. The default is stored as an absence, like the mode. */
export function writePreset(preset: ThemePresetId): void {
  if (preset === DEFAULT_THEME_PRESET) window.localStorage.removeItem(THEME_PRESET_KEY);
  else window.localStorage.setItem(THEME_PRESET_KEY, preset);
}

/** Throws on quota exhaustion — themes run to hundreds of KB, so this is a real possibility. */
export function writeCustomCss(css: string): void {
  if (css) window.localStorage.setItem(THEME_CSS_KEY, css);
  else window.localStorage.removeItem(THEME_CSS_KEY);
}

export function prefersDark(): boolean {
  return window.matchMedia(DARK_QUERY).matches;
}

export function watchPrefersDark(onChange: () => void): () => void {
  const query = window.matchMedia(DARK_QUERY);
  query.addEventListener('change', onChange);
  return () => query.removeEventListener('change', onChange);
}

export function resolveMode(mode: ThemeMode): ResolvedMode {
  if (mode !== 'system') return mode;
  return prefersDark() ? 'dark' : 'light';
}

/**
 * Obsidian puts `theme-dark`/`theme-light` on <body>, so `body.theme-dark { … }` is what themes
 * are written against. We mirror the class onto <html> as well, because a handful of themes reach
 * for `:root.theme-dark` instead and it costs nothing to match both.
 */
export function applyMode(resolved: ResolvedMode): void {
  const next = `theme-${resolved}`;
  const previous = resolved === 'dark' ? 'theme-light' : 'theme-dark';
  for (const element of [document.documentElement, document.body]) {
    element.classList.remove(previous);
    element.classList.add(next);
  }
}

/**
 * A palette is selected by one attribute on <html>: presets.css keys its colour blocks on
 * `[data-theme='…'] > .theme-dark`, so the mode class and the attribute together pick the block.
 * The default palette has no block of its own — it is what obsidian-defaults.css paints — but the
 * attribute is set for it too, so the DOM always says which palette is showing.
 */
export function applyPreset(preset: ThemePresetId): void {
  document.documentElement.dataset.theme = preset;
}

/**
 * Writes the user's theme into a single <style> in <head>, deliberately *not* wrapped in a cascade
 * layer. Unlayered rules outrank every layered one regardless of specificity, which is what lets
 * an arbitrary theme override our defaults without us knowing which selectors it uses.
 */
export function applyCustomCss(css: string): void {
  const existing = document.getElementById(USER_THEME_STYLE_ID);
  if (!css) {
    existing?.remove();
    return;
  }
  const style = existing ?? document.createElement('style');
  if (!existing) {
    style.id = USER_THEME_STYLE_ID;
    document.head.append(style);
  }
  style.textContent = css;
}
