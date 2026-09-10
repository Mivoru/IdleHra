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
// WHERE IT LIVES: THE SERVER, with localStorage in front of it as a cache.
//
// This used to be localStorage alone, and that file said outright that the
// server had been "rejected on price" - one wire field and a migration to carry
// "has this person read a sentence", whose worst failure is being told
// something you already know, once. That was a fair trade when onboarding was
// three steps. It stopped being one at three tiers and twenty-six
// explanations, where the failure is not "once" but a returning player picking
// up a phone and being taught the entire game again.
//
// So `PlayerRecord.OnboardingSeenIds` is the truth and this is a cache. The
// cache is not an optimisation and cannot be removed: `adoptPlayer` and the
// derived cue run on every packet and must answer SYNCHRONOUSLY, and a network
// round trip cannot. The rules between the two are:
//
//   - The local copy is read first, so the very first frame has an answer.
//   - The server's set is UNIONED into it when it arrives. Never subtracted -
//     a set that is behind is a player told something twice; a set that
//     over-forgets is a player buried in explanations they have read.
//   - Every write goes to both, the remote one debounced.
//   - A server that cannot be reached changes nothing. The session behaves
//     exactly as it did when this was local-only, which is the behaviour this
//     replaced and is therefore a safe floor.
//
// NULL IS NOT AN EMPTY SET. `HasRecord: false` means the account has never been
// baselined anywhere, which is the signal to mark everything ALREADY TRUE as
// seen rather than queueing seventeen explanations at somebody who has been
// playing for weeks. Empty-but-present means the opposite: teach everything as
// it arrives. Collapsing the two would bury exactly the player the baseline
// exists to protect.
//
// Keyed by PlayerId locally so two accounts sharing one browser do not inherit
// each other's seen-set - which also happens to be why a SEASON RESET does not
// re-teach anything. A reset drives the predicates back to false and then true
// again; the seen-set is attached to the account, not the season, so none of it
// fires twice.
import { writable } from 'svelte/store';
import { fetchOnboardingSeen, saveOnboardingSeen } from '../net/rest';


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
  // Modul: the remote half is scheduled from HERE rather than from each of the
  // four callers, so a future fifth cannot forget it. Debounced; see
  // scheduleRemoteWrite.
  scheduleRemoteWrite();
}

function publish(): void {
  seen.set(new Set(current));
}

/**
 * How long a burst of `markSeen` calls is allowed to gather before one PUT.
 *
 * The baseline marks up to seventeen ids in one go and a player reading through
 * Settings can un-forget several in a few seconds. One request per id would be
 * a request per explanation for data whose whole point is that it is cheap.
 */
const REMOTE_WRITE_DEBOUNCE_MS = 800;

let remoteWriteTimer: ReturnType<typeof setTimeout> | null = null;

/**
 * Whether the server has answered for the CURRENT account.
 *
 * Modul: THE BASELINE WAITS FOR THIS, and that is the point of the flag.
 * Baselining marks everything already true as read, so doing it before the
 * server has been heard from would mark a returning player's whole seen-set on
 * the strength of an empty local cache - and then write that over the top of
 * the real one. The cost of waiting is at most a second of no coach panel on a
 * cold start, which is the same price `tutorial.ts` already pays waiting for
 * the guild answer.
 */
let serverAnswered = false;
let serverHadRecord = false;
let localHadRecord = false;
let baselineOffered = false;

function scheduleRemoteWrite(): void {
  if (activePlayerId <= 0) return;
  if (remoteWriteTimer !== null) clearTimeout(remoteWriteTimer);
  remoteWriteTimer = setTimeout(() => {
    remoteWriteTimer = null;
    // Modul: the set is read at SEND time rather than captured when the write
    // was scheduled. Several marks inside one debounce window must all travel,
    // and a snapshot taken at schedule time would send the first and silently
    // drop the rest.
    void saveOnboardingSeen([...current]).catch(() => {
      // A failed write leaves the local copy correct and the server behind. The
      // next mark schedules another PUT of the whole set, so this heals on its
      // own; and if it never does, the outcome is one explanation shown twice.
    });
  }, REMOTE_WRITE_DEBOUNCE_MS);
}

/**
 * Fetches the account's seen-set and merges it in.
 *
 * UNION, never replace. The local copy may hold marks made seconds ago that
 * have not been written yet, and the server's copy may hold marks from another
 * device. Both are true; neither is authoritative about the other.
 */
async function hydrateFromServer(playerId: number): Promise<void> {
  try {
    const state = await fetchOnboardingSeen();
    // A second account may have been adopted while this was in flight.
    if (playerId !== activePlayerId) return;

    serverHadRecord = state.HasRecord;
    let changed = false;
    for (const id of state.Seen ?? []) {
      if (!current.has(id)) {
        current.add(id);
        changed = true;
      }
    }
    if (changed) {
      persist();
      publish();
    }
  } catch {
    // Unreachable, unauthorised, or offline. Leaving serverHadRecord false is
    // the safe reading: it lets the baseline run, which is what happened before
    // any of this existed.
  } finally {
    if (playerId === activePlayerId) serverAnswered = true;
  }
}

/**
 * Attach the seen-set to an account.
 *
 * Returns true EXACTLY ONCE per account, on the first call after the server has
 * answered, and only when neither the server nor this device has ever recorded
 * anything. That is the caller's signal to baseline (see tutorial.ts).
 *
 * Safe to call on every packet: it is a no-op in every other case.
 */
export function adoptPlayer(playerId: number): boolean {
  if (playerId <= 0) return false;

  if (playerId !== activePlayerId) {
    activePlayerId = playerId;
    const stored = load(playerId);
    localHadRecord = stored !== null;
    current = stored ?? new Set<string>();
    serverAnswered = false;
    serverHadRecord = false;
    baselineOffered = false;
    publish();
    void hydrateFromServer(playerId);
    // Never on the adopting call: the server has not spoken yet.
    return false;
  }

  if (!serverAnswered || baselineOffered) return false;
  baselineOffered = true;
  return !serverHadRecord && !localHadRecord;
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
  // The hydration flags are per-account state and a test that left them set
  // would see the NEXT test's first adopt answer from the previous server.
  serverAnswered = false;
  serverHadRecord = false;
  localHadRecord = false;
  baselineOffered = false;
  if (remoteWriteTimer !== null) {
    clearTimeout(remoteWriteTimer);
    remoteWriteTimer = null;
  }
  publish();
}
