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
let loadError = $state<unknown>(null);
let fetching = $state(false);
let started = false;

// Modul: start() runs from a GETTER, and getters are read inside $derived and
// template expressions, where Svelte throws state_unsafe_mutation on any $state
// write. So the synchronous part touches only the plain `started` flag, and
// every rune write waits for a microtask.
function start(): void {
  started = true;
  void Promise.resolve()
    .then(() => {
      fetching = true;
      loadError = null;
      return loadContent();
    })
    .then((loaded) => {
      registry = loaded;
    })
    .catch((error: unknown) => {
      registry = null;
      loadError = error;
    })
    .finally(() => {
      fetching = false;
    });
}

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
 * error would take a screen down over a decoration. A failed load is retried
 * only through `contentQuery.refetch()` - retrying on every read would turn
 * one failed fetch into a request loop.
 */
export const contentRegistry = {
  get current(): ContentRegistry | null {
    if (!started) start();
    return registry;
  },
};

// Modul: THE SAME REGISTRY, SHAPED LIKE A QUERY, for the screens that cannot
// render without it (Auto-Eat, the Market's order panel).
//
// They used to wait on `!registry` after a `loadContent()` with no catch, so a
// failed fetch left them on "Checking the chest..." for ever. QueryState and
// QueryError need `error`/`isError`/`isFetching`/`refetch`, and this provides
// exactly that over the one shared load above.
//
// It is NOT a TanStack query, and that is deliberate. It was one for a day
// (`createQuery` keyed 'content-registry'), and a brand-new account following
// the tutorial onto Auto-Eat sat on "Checking the chest..." for ever while the
// dev fixture never did - the list only appeared when something else on the
// screen happened to read the query's status, and exercise.mjs's onboarding
// steps were the only thing that noticed. A plain promise into a $state rune
// has no such subscription rules to get wrong.
export const contentQuery = {
  get data(): ContentRegistry | undefined {
    return contentRegistry.current ?? undefined;
  },
  get error(): unknown {
    return loadError;
  },
  get isError(): boolean {
    return loadError !== null;
  },
  get isPending(): boolean {
    return contentRegistry.current === null && loadError === null;
  },
  get isFetching(): boolean {
    return fetching;
  },
  refetch(): void {
    if (!fetching) start();
  },
};
