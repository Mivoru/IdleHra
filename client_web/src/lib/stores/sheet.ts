// Modul: THE OPEN OVERLAYS, for the hardware back button and for Escape.
//
// Back means "close the thing I am looking at". The big App-level layers (the
// death, victory and offline cards, the chat dock, the nav menu) keep their
// open state in durable stores, and backButton.ts reads those directly. This
// is for the other kind: an overlay whose open state lives INSIDE a component
// - the picker sheet, the player profile, the name menu, What's new, the
// shield wheel. App cannot see that state, so the component tells it how to
// make the overlay go away.
//
// WHY A STACK AND NOT ONE SLOT. It was one slot (`openSheetCloser`), written
// for PersonPicker, and with one slot the second overlay to register erased
// the first: a name menu opened over a profile, closed, and took the
// profile's closer with it, so the next back press walked off the screen
// under an open profile. Every overlay pushes on mount and removes ITS OWN
// entry on destroy - never "whatever is on top" - so the order components
// unmount in cannot unregister the wrong one.
//
// WHY EACH ENTRY CARRIES ITS z-index. "Top" means topmost on SCREEN, which is
// not the same as most recently opened: What's new can arrive while a profile
// is open, and the profile (z 1000) still paints over it (z 60). The entry
// with the highest z is the one a back press closes; on a tie, the newer one.
// backButton.ts compares that same z against the App-level layers.
import { derived, writable } from 'svelte/store';

export interface OverlayEntry {
  /** Makes the overlay go away. May be a deliberate no-op (see ShieldWheel). */
  close: () => void;
  /** The overlay's z-index, as painted. See LAYER_Z in net/backButton.ts. */
  z: number;
  /** Registration order, which breaks a tie in z. */
  seq: number;
}

const entries = writable<OverlayEntry[]>([]);
let nextSeq = 0;

/**
 * Registers an open overlay. Returns the unregister - call it when the overlay
 * closes or the component is destroyed. Shaped so `$effect(() =>
 * registerOverlay(close, z))` is the whole integration.
 */
export function registerOverlay(close: () => void, z: number): () => void {
  const entry: OverlayEntry = { close, z, seq: nextSeq++ };
  entries.update((list) => [...list, entry]);
  return () => entries.update((list) => list.filter((e) => e !== entry));
}

/** The topmost entry: highest z, newest on a tie. Pure, so it is testable. */
export function topOfStack(list: readonly OverlayEntry[]): OverlayEntry | null {
  let top: OverlayEntry | null = null;
  for (const entry of list) {
    if (!top || entry.z > top.z || (entry.z === top.z && entry.seq > top.seq)) top = entry;
  }
  return top;
}

export const topOverlay = derived(entries, (list) => topOfStack(list));
