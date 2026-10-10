// Modul: THE CAILLEACH IS FOUGHT IN A WINDOW (owner, 2026-10-10). Fight used
// to send the player to the Combat screen, which drew the region boss she
// borrows her strength from - "why am I fighting the Alpha Wolf instead of
// the Cailleach". The fight now opens a window over the World Boss screen and
// ends there: a win says what it paid and offers the next winter, a loss
// offers another try. The server ends the attempt either way and puts the
// character back on its old work (SimulationEngine.ReturnFromSeasonalBoss).
//
// NOTHING HERE IS INFERRED FROM HEALTH. The outcome is read from what the
// server states: the Kill combat event for her boss (a win), the death card's
// LastDeathTick naming her boss (a loss), result 69 (a first clear's reward
// committed). This store only remembers which fight the window is about.
import { writable, get } from 'svelte/store';

export type SeasonalFightPhase = 'starting' | 'fighting' | 'won' | 'lost';

export interface SeasonalFight {
  tier: number;
  /** The region boss whose stats she wears - how the server's events name her. */
  bossId: number;
  eventId: number;
  phase: SeasonalFightPhase;
  /** The tier was not yet broken when Fight was pressed: a win pays. */
  firstClear: boolean;
  /** Result 69 arrived: the first clear's mail is written. */
  rewardSealed: boolean;
  /** The kill's xp, from the Kill event. */
  xp: number;
  /** Hidden while the fight runs on; the World Boss screen can reopen it. */
  hidden: boolean;
  /** Combat-feed sequence numbers below this belong to an earlier fight. */
  sinceSequence: number;
}

export const seasonalFight = writable<SeasonalFight | null>(null);

/** Result codes the seasonal fight listens for (StateUpdatePacket.cs CommandResultCode). */
export const SEASONAL_BOSS_CLEARED = 69;
export const SEASONAL_BOSS_STARTED = 70;

let lastSequenceSeen = -1;

export function beginSeasonalFight(tier: number, bossId: number, eventId: number, firstClear: boolean): void {
  seasonalFight.set({
    tier, bossId, eventId, firstClear,
    phase: 'starting',
    rewardSealed: false,
    xp: 0,
    hidden: false,
    sinceSequence: lastSequenceSeen + 1,
  });
}

export function closeSeasonalFight(): void {
  seasonalFight.set(null);
}

export function setSeasonalFightHidden(hidden: boolean): void {
  seasonalFight.update((f) => (f ? { ...f, hidden } : f));
}

/** Every combat event passes through here, so the window knows where its fight begins. */
export function noteSeasonalCombatEvent(sequence: number, isKill: boolean, monsterId: number, xp: number): void {
  if (sequence > lastSequenceSeen) lastSequenceSeen = sequence;
  if (!isKill) return;
  const fight = get(seasonalFight);
  if (!fight || monsterId !== fight.bossId || sequence < fight.sinceSequence) return;
  if (fight.phase !== 'fighting' && fight.phase !== 'starting') return;
  seasonalFight.set({ ...fight, phase: 'won', xp, hidden: false });
}

/**
 * A death the window owns: her boss killed slot 1 during the fight. Returns
 * true when claimed, so the ordinary death card stays shut - one window says
 * what happened, not two stacked over each other.
 */
export function claimSeasonalDeath(monsterId: number): boolean {
  const fight = get(seasonalFight);
  if (!fight || monsterId !== fight.bossId) return false;
  if (fight.phase !== 'fighting' && fight.phase !== 'starting') return false;
  seasonalFight.set({ ...fight, phase: 'lost', hidden: false });
  return true;
}

/**
 * The command results of one packet. A refusal while starting closes the
 * window (the toast already says why); 70 means she is on the field; 69 means
 * the first clear's reward is in the mail.
 */
export function noteSeasonalResults(codes: readonly number[], isRefusal: (code: number) => boolean): void {
  const fight = get(seasonalFight);
  if (!fight) return;
  if (codes.includes(SEASONAL_BOSS_CLEARED) && !fight.rewardSealed) {
    seasonalFight.set({ ...fight, rewardSealed: true });
    return;
  }
  if (fight.phase !== 'starting') return;
  if (codes.includes(SEASONAL_BOSS_STARTED)) {
    seasonalFight.set({ ...fight, phase: 'fighting' });
  } else if (codes.some(isRefusal)) {
    seasonalFight.set(null);
  }
}

/**
 * A new session numbers its combat events from zero. The same player's open
 * window survives a reconnect (its fight may still be running); another
 * account's does not.
 */
export function resetSeasonalFightSession(samePlayer: boolean): void {
  lastSequenceSeen = -1;
  if (!samePlayer) seasonalFight.set(null);
  else seasonalFight.update((f) => (f ? { ...f, sinceSequence: 0 } : f));
}
