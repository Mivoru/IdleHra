// Modul: the fight log - what the health bar cannot say.
//
// Asked for directly: "I can't properly see the fight against the monster...
// I don't see the health bar moving or effects of my hits", and then "it needs
// a combat log, so the player doesn't only watch it happen but can read it
// back - who did how much and with which attack, whether it was a crit,
// whether the attack was blocked, lifesteal".
//
// THE REASON THIS IS FED BY A SERVER EVENT AND NOT BY stores/damage.ts:
// measured 2026-09-04, snapshots arrive about every 1090 ms and a geared
// character kills an early monster every ~1400 ms, so across 27 consecutive
// snapshots CurrentMonsterHp took exactly ONE value - its full health. Spawn
// and death both happened between two samples. Everything inferred from a
// health difference was therefore being handed a constant, and no amount of
// work on the bar could change that. The server now states each blow as it
// resolves, so a fight that is over inside one snapshot can still be read.
//
// A miss is the proof this is real: it moves no health at all, so it is the
// one line no inference from a health difference could ever have produced.
import { writable } from 'svelte/store';
import type { ResponseCombatEvent } from '../net/protocol.generated';

/** Mirrors ResponseCombatEventPacket's Kind constants. */
export const CombatEventKind = {
  PlayerHit: 0,
  PlayerMiss: 1,
  MonsterHit: 2,
  MonsterMiss: 3,
  Lifesteal: 4,
  Kill: 5,
} as const;

/** Mirrors ResponseCombatEventPacket's Flag constants. */
export const CombatEventFlag = {
  Crit: 1,
  Blocked: 2,
  Burn: 4,
  Thorns: 8,
} as const;

export interface CombatLogLine {
  /** The server's sequence number - also the keyed-each key. */
  id: number;
  kind: number;
  amount: number;
  monsterId: number;
  monsterHpAfter: number;
  flags: number;
  atMs: number;
}

/**
 * How many lines are kept.
 *
 * A log, not a ledger. An idle session resolves a blow every second or so
 * forever, so this is a window on the recent past and nothing else - the
 * durable record of what a character did is the codex and the progress screen.
 */
export const MAX_LOG_LINES = 50;

export const combatLog = writable<CombatLogLine[]>([]);

/**
 * Bumped once per kill, so the screen can play a death.
 *
 * A counter rather than a boolean or the monster's id: two kills in a row can
 * be the same monster type, and a value that does not change cannot restart an
 * animation.
 *
 * Safe to key an effect on, unlike the interpolated health or the damage array
 * - this changes about once a second at the very most, where those change every
 * animation frame. Keying an effect on a per-frame signal is a trap this
 * codebase has already paid for once: it starved the main thread badly enough
 * that every other screen stopped loading.
 */
export const killPulse = writable(0);

/**
 * The highest sequence number already shown.
 *
 * A reconnect can redeliver events the log already has (the server's queue is
 * drained per socket, and a resumed session can overlap), and a duplicated
 * "Critical! 861" reads as two crits rather than one packet arriving twice.
 */
let lastSequence = -1;

export function pushCombatEvent(packet: ResponseCombatEvent): void {
  const sequence = Number(packet.Sequence);
  if (sequence <= lastSequence) return;
  lastSequence = sequence;

  const line: CombatLogLine = {
    id: sequence,
    kind: Number(packet.EventKind),
    amount: Number(packet.Amount),
    monsterId: Number(packet.MonsterId),
    monsterHpAfter: Number(packet.MonsterHpAfter),
    flags: Number(packet.Flags),
    atMs: Date.now(),
  };

  if (line.kind === CombatEventKind.Kill) {
    killPulse.update((n) => n + 1);
  }

  combatLog.update((lines) => {
    // Newest first: the player looks at the top of the panel, and a log that
    // grows downward makes them chase it.
    const next = [line, ...lines];
    return next.length > MAX_LOG_LINES ? next.slice(0, MAX_LOG_LINES) : next;
  });
}

/**
 * Cleared on sign-out and on switching characters, along with the sequence
 * guard - a new session starts its own numbering, so a stale high-water mark
 * would silently swallow every line until the server caught up to it.
 */
export function resetCombatLog(): void {
  lastSequence = -1;
  combatLog.set([]);
  killPulse.set(0);
}

/**
 * The line's text, given a monster name resolved from the content mirror.
 *
 * Pure and exported so it can be tested without a DOM, and so the wording
 * lives in one place rather than inside the markup.
 *
 * NAMES ONLY WHAT THE SERVER ACTUALLY RESOLVES. "Blocked" appears on incoming
 * hits only, because the player has a block stat (BlockStrengthPct, derived
 * from CON) and monsters do not - a player's swing is never blocked. Armour is
 * never its own line: it reduces every hit rather than stopping any, so it is
 * already inside the number shown.
 */
export function describeCombatLine(line: CombatLogLine, monsterName: string): string {
  const crit = (line.flags & CombatEventFlag.Crit) !== 0;
  const blocked = (line.flags & CombatEventFlag.Blocked) !== 0;
  const burn = (line.flags & CombatEventFlag.Burn) !== 0;
  const thorns = (line.flags & CombatEventFlag.Thorns) !== 0;
  const amount = line.amount.toLocaleString();

  switch (line.kind) {
    case CombatEventKind.PlayerHit:
      if (thorns) return `Thorns reflect ${amount}`;
      return `${crit ? 'Critical! ' : ''}You hit ${monsterName} for ${amount}${burn ? ' (burning)' : ''}`;
    case CombatEventKind.PlayerMiss:
      return `You miss ${monsterName}`;
    case CombatEventKind.MonsterHit:
      return `${crit ? 'Critical! ' : ''}${monsterName} hits you for ${amount}${blocked ? ' (blocked)' : ''}`;
    case CombatEventKind.MonsterMiss:
      return `${monsterName} misses you`;
    case CombatEventKind.Lifesteal:
      return `Lifesteal heals you for ${amount}`;
    case CombatEventKind.Kill:
      return `${monsterName} dies — ${amount} xp`;
    default:
      return '';
  }
}
