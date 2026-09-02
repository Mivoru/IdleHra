// Modul: WHICH EXPLANATIONS HAVE BEEN SHOWN. Nothing else is persisted.
//
// The single most important property of this file is what it does NOT store: a
// step number, or any notion of "how far through onboarding you are". Progress
// is re-derived from the state packet on every frame - that is the whole
// design - and a stored copy of it would be a second source of truth that goes
// stale the moment a player does something in another tab. The previous
// tutorial's storage key held exactly such a number, and it is deliberately
// not reused.
//
// WHERE IT LIVES: localStorage, keyed by PlayerId.
//
// The alternative is the server, and the server was rejected on price. Storing
// this player-side costs a wire field or a REST endpoint plus a schema
// migration, to carry the cheapest data in the game - "has this person read a
// sentence" - whose worst failure is being told something you already know,
// once. Server storage is the right call for anything the player EARNED; this
// is not that.
//
// The honest cost is that localStorage is per-device: sign in on a phone and
// you are taught again there. That is accepted, and it is written down in
// docs/onboarding_steps.md rather than left to be discovered.
//
// Keyed by PlayerId so two accounts sharing one browser do not inherit each
// other's seen-set - which also happens to be why a SEASON RESET does not
// re-teach anything. A reset drives the predicates back to false and then true
// again; the seen-set is attached to the account, not the season, so none of
// it fires twice.
import { writable } from 'svelte/store';

function storageKey(playerId: number): string {
  return `folkidle.onboardingSeen.${playerId}`;
}

const seen = writable<ReadonlySet<string>>(new Set<string>());

/** Read-only view. Subscribe to be told when something is marked. */
export const seenExplanations = { subscribe: seen.subscribe };

/** 0 means "no account adopted yet"; nothing is written until one is. */
let activePlayerId = 0;
let current = new Set<string>();

function load(playerId: number): Set<string> | null {
  try {
    const raw = localStorage.getItem(storageKey(playerId));
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return new Set<string>();
    return new Set(parsed.filter((entry): entry is string => typeof entry === 'string'));
  } catch {
    // A browser refusing storage, or a corrupted value, must not break the
    // game. Treating it as "nothing stored" re-teaches at worst.
    return null;
  }
}

function persist(): void {
  if (activePlayerId <= 0) return;
  try {
    localStorage.setItem(storageKey(activePlayerId), JSON.stringify([...current]));
  } catch {
    // See load(). The session still behaves correctly in memory.
  }
}

function publish(): void {
  seen.set(new Set(current));
}

/**
 * Attach the seen-set to an account.
 *
 * Returns true when this device has NEVER stored anything for this player -
 * which is the caller's signal to baseline (see tutorial.ts). Returns false on
 * every subsequent call for the same account, so it is safe to call on every
 * packet.
 */
export function adoptPlayer(playerId: number): boolean {
  if (playerId <= 0 || playerId === activePlayerId) return false;
  activePlayerId = playerId;
  const stored = load(playerId);
  current = stored ?? new Set<string>();
  publish();
  return stored === null;
}

export function markSeen(id: string): void {
  if (current.has(id)) return;
  current.add(id);
  persist();
  publish();
}

/**
 * Mark several at once, writing storage once.
 *
 * Modul: THIS IS THE BASELINE, and it is the thing that stops a veteran being
 * buried. A player who has been at this for weeks and then clears their
 * browser has fifteen of the seventeen moments already true; the naive rule
 * would queue every one of them. So on the first packet for an account with
 * nothing stored, everything already reached is recorded as seen.
 *
 * The consequence is right in both directions: a brand-new account baselines
 * with almost nothing true and is therefore taught everything as it arrives -
 * including whatever became true while the tab was closed, because the check
 * is "true and unseen", not "changed just now".
 */
export function markAllSeen(ids: readonly string[]): void {
  let changed = false;
  for (const id of ids) {
    if (!current.has(id)) {
      current.add(id);
      changed = true;
    }
  }
  // Modul: WRITTEN EVEN WHEN NOTHING CHANGED, and that is load-bearing. An
  // empty baseline is the normal case for a brand-new account, and skipping
  // the write would leave the storage key absent - so the next reload would
  // see "nothing stored", baseline a SECOND time, and this time the player has
  // meanwhile reached three systems, all of which would be silently marked as
  // already explained. The write is what makes the baseline happen once.
  persist();
  if (changed) publish();
}

/** Undo one, so a player can read an explanation again. */
export function forgetSeen(id: string): void {
  if (!current.delete(id)) return;
  persist();
  publish();
}

/** Undo all of them. The "show me everything again" button. */
export function forgetAllSeen(): void {
  current = new Set<string>();
  persist();
  publish();
}

/** Test seam: the module holds per-account state across a page's lifetime. */
export function resetForTests(): void {
  activePlayerId = 0;
  current = new Set<string>();
  publish();
}
