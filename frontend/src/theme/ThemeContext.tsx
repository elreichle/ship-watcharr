import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  applyCustomCss,
  applyMode,
  applyPreset,
  isSafeMode,
  readCustomCss,
  readMode,
  readPreset,
  resolveMode,
  watchPrefersDark,
  writeCustomCss,
  writeMode,
  writePreset,
  type ResolvedMode,
  type ThemeMode,
} from './theme';
import type { ThemePresetId } from './presets';

interface ThemeContextValue {
  /** What the user chose. 'system' tracks the OS preference live. */
  mode: ThemeMode;
  /** What that currently works out to — this is the class on <body>. */
  resolvedMode: ResolvedMode;
  setMode: (mode: ThemeMode) => void;
  /** Which built-in palette paints the page. A pasted theme sits on top of it. */
  preset: ThemePresetId;
  setPreset: (preset: ThemePresetId) => void;
  customCss: string;
  /** Throws if localStorage rejects the write (blocked, or the theme exceeds quota). */
  setCustomCss: (css: string) => void;
  /** True when ?safemode is in the URL: the theme is stored but not applied this page load. */
  safeMode: boolean;
}

const ThemeContext = createContext<ThemeContextValue | undefined>(undefined);

export function ThemeProvider({ children }: { children: ReactNode }) {
  const safeMode = useMemo(isSafeMode, []);
  const [mode, setModeState] = useState<ThemeMode>(readMode);
  const [preset, setPresetState] = useState<ThemePresetId>(readPreset);
  const [customCss, setCustomCssState] = useState<string>(readCustomCss);
  const [resolvedMode, setResolvedMode] = useState<ResolvedMode>(() => resolveMode(readMode()));

  // The boot script in index.html has already applied all of this before first paint. Re-applying
  // here is what keeps it correct afterwards: when the user switches mode, and when the OS flips
  // while 'system' is selected.
  useEffect(() => {
    const sync = () => {
      const resolved = resolveMode(mode);
      setResolvedMode(resolved);
      applyMode(resolved);
    };
    sync();
    return mode === 'system' ? watchPrefersDark(sync) : undefined;
  }, [mode]);

  useEffect(() => {
    applyPreset(preset);
  }, [preset]);

  useEffect(() => {
    applyCustomCss(safeMode ? '' : customCss);
  }, [customCss, safeMode]);

  // Persist first, then update state: if the write throws, the error reaches the caller and the
  // UI keeps showing what is actually stored rather than claiming a save that didn't happen.
  const setMode = useCallback((next: ThemeMode) => {
    writeMode(next);
    setModeState(next);
  }, []);

  const setPreset = useCallback((next: ThemePresetId) => {
    writePreset(next);
    setPresetState(next);
  }, []);

  const setCustomCss = useCallback((css: string) => {
    writeCustomCss(css);
    setCustomCssState(css);
  }, []);

  const value = useMemo(
    () => ({ mode, resolvedMode, setMode, preset, setPreset, customCss, setCustomCss, safeMode }),
    [mode, resolvedMode, setMode, preset, setPreset, customCss, setCustomCss, safeMode],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error('useTheme must be used within a ThemeProvider');
  return ctx;
}
