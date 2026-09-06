/*
 * The in-app reader's text settings: how big, how wide, how spaced.
 *
 * Kept in localStorage rather than on the server, unlike the reader's place in a book: where they
 * are in a work is theirs and follows them between devices, but how large the text is depends on
 * the screen it is on. The same try/catch discipline as the theme helpers, for the same reason —
 * storage can be blocked, and the reader still has to read.
 */

export interface ReaderSettings {
  /** Text size in px. The app's own reading size is 16. */
  fontSize: number;
  /** Line length in ch — the measure. 62 is what the work page uses for a summary. */
  measure: number;
  lineHeight: number;
}

export const READER_SETTINGS_KEY = 'shipwatcharr.reader.settings';

export const DEFAULT_READER_SETTINGS: ReaderSettings = { fontSize: 18, measure: 66, lineHeight: 1.65 };

/** The ranges the controls offer, and the bounds a stored value is clamped to. */
export const READER_SETTING_RANGES = {
  fontSize: { min: 14, max: 26, step: 1 },
  measure: { min: 45, max: 95, step: 1 },
  lineHeight: { min: 1.3, max: 2.1, step: 0.05 },
} as const;

function clamp(value: unknown, range: { min: number; max: number }, fallback: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback;
  return Math.min(range.max, Math.max(range.min, value));
}

export function readReaderSettings(): ReaderSettings {
  try {
    const raw = window.localStorage.getItem(READER_SETTINGS_KEY);
    if (raw === null) return DEFAULT_READER_SETTINGS;

    // Clamped field by field: a value written by an older build with a different range should
    // land inside today's rather than throw the whole record away.
    const stored = JSON.parse(raw) as Partial<Record<keyof ReaderSettings, unknown>>;
    return {
      fontSize: clamp(stored.fontSize, READER_SETTING_RANGES.fontSize, DEFAULT_READER_SETTINGS.fontSize),
      measure: clamp(stored.measure, READER_SETTING_RANGES.measure, DEFAULT_READER_SETTINGS.measure),
      lineHeight: clamp(stored.lineHeight, READER_SETTING_RANGES.lineHeight, DEFAULT_READER_SETTINGS.lineHeight),
    };
  } catch {
    // Storage blocked, or a record that is not JSON. The defaults are readable.
    return DEFAULT_READER_SETTINGS;
  }
}

export function writeReaderSettings(settings: ReaderSettings): void {
  try {
    window.localStorage.setItem(READER_SETTINGS_KEY, JSON.stringify(settings));
  } catch {
    // Not worth surfacing: the text is already set the way they asked, it just won't be next time.
  }
}
