// Modul: THE BREAKPOINT, READABLE FROM SCRIPT AS WELL AS FROM CSS.
//
// Almost everything responsive in this client is CSS, and should stay that
// way. This exists for the one case CSS cannot answer: VirtualList positions
// rows by ARITHMETIC, so a layout that makes a row taller below a breakpoint
// has to tell the list the new number. `rowHeight` is a contract - a row that
// renders taller than the value passed in overlaps its neighbour instead of
// pushing it down - and a media query cannot hand a number to a prop.
//
// 40rem is the same threshold app.css uses for the touch floor and the
// collapsed nav. Written once here so a future change to it cannot leave the
// JS half behind.
import { readable } from 'svelte/store';

export const NARROW_QUERY = '(max-width: 40rem)';

/**
 * True while the viewport is phone-width.
 *
 * Modul: SSR-safe and test-safe. `matchMedia` does not exist in the vitest
 * node environment or during any prerender, and a store that throws on import
 * would take down every module that touches it. Absent means "not narrow",
 * which is the desktop default and the safe answer for a list height - too
 * SHORT a row overlaps, too tall merely leaves a gap.
 */
export const isNarrow = readable(false, (set) => {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;

  const mq = window.matchMedia(NARROW_QUERY);
  set(mq.matches);

  const onChange = (e: MediaQueryListEvent) => set(e.matches);
  mq.addEventListener('change', onChange);
  return () => mq.removeEventListener('change', onChange);
});
