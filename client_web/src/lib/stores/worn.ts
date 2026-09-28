import { writable, get } from 'svelte/store';
import { fetchWorn, type WornCosmetics } from '../net/cosmetics';

/**
 * Task 54: what other players wear, for chat, boards and rosters.
 *
 * Modul: ONE BATCHED LOOKUP, NOT A FIELD ON EVERY PAYLOAD. Chat, three boards,
 * the guild roster and the profile all name players, and giving each of their
 * payloads its own avatar fields would be six copies of one fact. Instead a
 * row asks here for its player id; asks made in the same moment are gathered
 * into one GET /api/v1/cosmetics/worn, and answers are kept for a minute.
 */
const CACHE_MS = 60_000;
const BATCH_MS = 40;
const MAX_PER_REQUEST = 100;

interface Cached {
  worn: WornCosmetics | null;
  atMs: number;
}

export const wornByPlayer = writable<Map<number, Cached>>(new Map());

let pending = new Set<number>();
let timer: ReturnType<typeof setTimeout> | null = null;

export function requestWorn(playerId: number): void {
  if (!(playerId > 0)) return;
  const cached = get(wornByPlayer).get(playerId);
  if (cached && Date.now() - cached.atMs < CACHE_MS) return;
  pending.add(playerId);
  if (!timer) timer = setTimeout(flush, BATCH_MS);
}

/** After the player changes their own look: ask again at once. */
export function forgetWorn(playerId: number): void {
  wornByPlayer.update((map) => {
    const next = new Map(map);
    next.delete(playerId);
    return next;
  });
  requestWorn(playerId);
}

async function flush(): Promise<void> {
  timer = null;
  const ids = [...pending].slice(0, MAX_PER_REQUEST);
  const rest = [...pending].slice(MAX_PER_REQUEST);
  pending = new Set(rest);
  if (rest.length > 0) timer = setTimeout(flush, BATCH_MS);
  if (ids.length === 0) return;

  const now = Date.now();
  // Marked before the request so a burst of rows does not ask twice.
  wornByPlayer.update((map) => {
    const next = new Map(map);
    for (const id of ids) if (!next.has(id)) next.set(id, { worn: null, atMs: now });
    return next;
  });

  try {
    const players = await fetchWorn(ids);
    wornByPlayer.update((map) => {
      const next = new Map(map);
      for (const worn of players) next.set(worn.PlayerId, { worn, atMs: Date.now() });
      return next;
    });
  } catch {
    // A failed lookup leaves the default portrait up; the next ask after the
    // cache window tries again.
  }
}
