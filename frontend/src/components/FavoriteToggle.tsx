/*
 * The reader's favorite mark on a work: a heart, hollow until pressed and filled once it is.
 *
 * A toggle button rather than a checkbox, because the mark is a single action on a row that is
 * already carrying a select, a slider and a link, and a native checkbox beside them would read as
 * a form field. `aria-pressed` is what tells assistive tech it is a toggle and which way it is.
 */

import { Icon } from './Icon';

interface FavoriteToggleProps {
  value: boolean;
  onChange: (value: boolean) => void;
  /** The work's title, for the accessible name — nothing visible labels the control in a row. */
  title: string;
}

export function FavoriteToggle({ value, onChange, title }: FavoriteToggleProps) {
  return (
    <button
      type="button"
      className="favorite-toggle"
      aria-pressed={value}
      aria-label={value ? `Remove ${title} from favorites` : `Add ${title} to favorites`}
      title={value ? 'Remove from favorites' : 'Add to favorites'}
      onClick={() => onChange(!value)}
    >
      <Icon name="heart" size="s" className="favorite-toggle-icon" />
    </button>
  );
}
