import { writable, get } from 'svelte/store';
import { queryClient } from '../net/queryClient';
import { queryKeys, fetchPlayerProfile, fetchGuildView } from '../net/rest';

/**
 * Who and which guild is being looked at, as a stack.
 *
 * Modul: ONE HOST, NOT A MODAL PER SCREEN. Chat, Friends and Leaderboards each
 * mounted their own PlayerProfileModal off their own local state, which is why
 * the guild roster - a fourth list of names - never got one: every new entry
 * point was a fourth copy of the same twenty lines. Any name now calls
 * `openProfile`, and ProfileHost (mounted once in App) draws the top entry.
 *
 * A STACK because the guild view is reached FROM a profile and its members
 * open profiles in turn: profile -> guild -> member -> back -> back. Back pops
 * one; the close button empties it.
 */
export type ProfileEntry =
  | { kind: 'player'; playerId: number; name?: string }
  | { kind: 'guild'; guildId: number; name?: string };

export const profileStack = writable<ProfileEntry[]>([]);

/** How deep the stack may grow: a loop of taps does not grow it for ever. */
export const MAX_PROFILE_DEPTH = 8;

function sameEntry(a: ProfileEntry, b: ProfileEntry): boolean {
  if (a.kind === 'player' && b.kind === 'player') return a.playerId === b.playerId;
  if (a.kind === 'guild' && b.kind === 'guild') return a.guildId === b.guildId;
  return false;
}

/**
 * Push an entry. Pure, so the stack rules are testable: re-opening what is
 * already on top is a no-op, and re-opening something deeper in the stack
 * unwinds back to it rather than stacking a second copy.
 */
export function pushEntry(stack: readonly ProfileEntry[], entry: ProfileEntry): ProfileEntry[] {
  const at = stack.findIndex((e) => sameEntry(e, entry));
  if (at >= 0) return stack.slice(0, at + 1);
  const next = [...stack, entry];
  return next.length > MAX_PROFILE_DEPTH ? next.slice(next.length - MAX_PROFILE_DEPTH) : next;
}

export function openProfile(playerId: number, name?: string): void {
  if (!(playerId > 0)) return;
  prefetchProfile(playerId);
  profileStack.update((stack) => pushEntry(stack, { kind: 'player', playerId, name }));
}

export function openGuildView(guildId: number, name?: string): void {
  if (!(guildId > 0)) return;
  prefetchGuildView(guildId);
  profileStack.update((stack) => pushEntry(stack, { kind: 'guild', guildId, name }));
}

/** Back: one level. */
export function popProfile(): void {
  profileStack.update((stack) => stack.slice(0, -1));
}

/** Close: all of it. */
export function closeProfiles(): void {
  if (get(profileStack).length > 0) profileStack.set([]);
}

// Modul: HOW A PROFILE OPENS "ALMOST INSTANTLY". Three things together:
//  1. the shell renders at once from what the tapped row already knew (the
//     name; the avatar comes from the batched worn-cosmetics cache the row
//     itself already filled);
//  2. the request is started on pointerdown / hover / focus (profileLink.ts),
//     so the ~100-200 ms before the click lands is already spent fetching;
//  3. answers are kept STALE_MS, so a second look - or the back button from a
//     guild view - is served from the cache with no request at all.
export const PROFILE_STALE_MS = 60_000;

export function prefetchProfile(playerId: number): void {
  if (!(playerId > 0)) return;
  void queryClient.prefetchQuery({
    queryKey: queryKeys.playerProfile(playerId),
    queryFn: () => fetchPlayerProfile(playerId),
    staleTime: PROFILE_STALE_MS,
  });
}

export function prefetchGuildView(guildId: number): void {
  if (!(guildId > 0)) return;
  void queryClient.prefetchQuery({
    queryKey: queryKeys.guildView(guildId),
    queryFn: () => fetchGuildView(guildId),
    staleTime: PROFILE_STALE_MS,
  });
}
