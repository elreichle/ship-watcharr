import { Icon } from './Icon';

/**
 * What the app shows while it finds out who is signed in: the mark alone, breathing.
 *
 * It lasts a few hundred milliseconds on a warm cache, so it says nothing — a sentence that
 * appears and vanishes reads as a flicker, and a bare "Loading…" at the top-left corner read as
 * the app being broken. Announced to assistive technology once, as a busy region.
 */
export function BootScreen() {
  return (
    <div className="app-boot" role="status" aria-label="Loading" aria-busy="true">
      <Icon name="bookmark" size="xl" />
    </div>
  );
}
