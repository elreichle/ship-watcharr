import { useEffect, useState } from 'react';

/**
 * Tracks a media query in JS. The sidebar needs this rather than pure CSS because the breakpoints
 * change component *behaviour*, not just appearance: below 700px the hamburger opens a drawer
 * instead of toggling the rail, and a railed group swaps its inline children for a flyout.
 */
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(() => window.matchMedia(query).matches);

  useEffect(() => {
    const list = window.matchMedia(query);
    const onChange = () => setMatches(list.matches);
    onChange();
    list.addEventListener('change', onChange);
    return () => list.removeEventListener('change', onChange);
  }, [query]);

  return matches;
}
