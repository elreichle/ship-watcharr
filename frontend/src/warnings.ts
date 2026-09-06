/**
 * The two archive warnings that say a work carries none, or declines to say. Every other warning is
 * something a reader may be scanning *for*, and gets the warning colour; these two are on most works
 * and colouring them would make the colour mean nothing.
 */
const NEUTRAL_WARNINGS = new Set(['No Archive Warnings Apply', 'Creator Chose Not To Use Archive Warnings']);

/** The chip classes for one warning: coloured only when it warns of something. */
export function warningChipClass(warning: string): string {
  return NEUTRAL_WARNINGS.has(warning) ? 'chip' : 'chip chip-warning';
}
