// Modul: ONE shared, reactive handle on the content registry.
//
// loadContent() caches, so calling it repeatedly is cheap in bytes. What is
// not cheap is what each CALLER does around it: an `onMount` closure, an
// `await`, a `$state` write and the reactive invalidation that follows. ItemIcon
// did all four, per icon - and the chest renders one icon per item, which on a
// long-played account was 17,836 of them. That is 17,836 mount callbacks and
// 17,836 separate reactive updates to say the same thing.
//
// This says it once. The first module to touch `contentRegistry.current` starts
// the load; everything else reads the same rune and re-renders together when it
// arrives.
//
// A .svelte.ts module rather than a store, because $state gives fine-grained
// reactivity here and a component reading `.current` in a $derived picks it up
// with no subscription bookkeeping of its own.

import { loadContent, type ContentRegistry } from './content';

let registry = $state<ContentRegistry | null>(null);
let started = false;

/**
 * The loaded registry, or null until the first fetch lands.
 *
 * Reading this ARMS the load - a component does not have to remember to kick
 * it off, which is the step every caller of loadContent() had to duplicate and
 * is exactly what a forgotten one looks like: a screen that renders with no
 * names on it and says nothing about why.
 *
 * Null on failure too, and deliberately so. Every consumer already falls back
 * to something (an id, a prettified slug, a hidden tier badge) because the
 * registry was always async; turning a content-fetch failure into a thrown
 * error would take a screen down over a decoration.
 */
export const contentRegistry = {
  get current(): ContentRegistry | null {
    if (!started) {
      started = true;
      void loadContent()
        .then((loaded) => {
          registry = loaded;
        })
        .catch(() => {
          // Left null. See above - the callers all degrade, and retrying on
          // every read would turn one failed fetch into a request loop.
          registry = null;
        });
    }
    return registry;
  },
};
