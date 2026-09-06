/**
 * The built-in palettes. The CSS for each lives in presets.css (the default's in
 * obsidian-defaults.css); this list is what the Appearance page offers and what theme.ts
 * validates a stored id against. Adding a palette means a pair of blocks there and a row here.
 */

export interface ThemePreset {
  /** The `data-theme` value on <html>, and the key stored in localStorage. */
  id: string;
  name: string;
  /** The names of the palette's dark and light variants, where it has its own. */
  dark: string;
  light: string;
}

export const THEME_PRESETS: readonly ThemePreset[] = [
  { id: 'tokyo-night', name: 'Tokyo Night', dark: 'Night', light: 'Day' },
  { id: 'catppuccin', name: 'Catppuccin', dark: 'Mocha', light: 'Latte' },
  { id: 'solarized', name: 'Solarized', dark: 'Dark', light: 'Light' },
  { id: 'gruvbox', name: 'Gruvbox', dark: 'Dark', light: 'Light' },
  { id: 'nord', name: 'Nord', dark: 'Polar Night', light: 'Snow Storm' },
  { id: 'rose-pine', name: 'Rosé Pine', dark: 'Main', light: 'Dawn' },
  { id: 'everforest', name: 'Everforest', dark: 'Dark', light: 'Light' },
  { id: 'dracula', name: 'Dracula', dark: 'Dracula', light: 'Alucard' },
  { id: 'one-dark', name: 'One Dark', dark: 'One Dark', light: 'One Light' },
];

export type ThemePresetId = (typeof THEME_PRESETS)[number]['id'];

export const DEFAULT_THEME_PRESET: ThemePresetId = 'tokyo-night';

export function isThemePresetId(value: unknown): value is ThemePresetId {
  return typeof value === 'string' && THEME_PRESETS.some((preset) => preset.id === value);
}
