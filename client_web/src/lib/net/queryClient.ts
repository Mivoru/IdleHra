// Modul: the single query client. Replaces the Unity client's 24 hand-written
// REST caches, each of which had its own timer, its own staleness rule and its
// own invalidation bug.

import { QueryClient } from '@tanstack/svelte-query';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Everything reachable through REST here is per-player data that only
      // changes when this client does something (equip, craft, deposit). The
      // WebSocket is what carries anything genuinely live, so polling would be
      // wasted requests - invalidation on mutation is the right trigger.
      staleTime: 30_000,
      refetchOnWindowFocus: false,

      // One retry, not the default three. A failing request here is almost
      // always a dead backend or an expired token, and neither improves by
      // being asked three more times; a fast, visible failure is worth more
      // than a slow, hidden one. This client's entire prior art on the
      // subject is a registration screen that showed nothing at all when the
      // backend was down.
      retry: 1,
    },
  },
});
