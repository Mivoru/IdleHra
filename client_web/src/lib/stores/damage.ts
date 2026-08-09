// Modul: floating damage text. The descendant of UiFloatingDamageText plus
// CombatVfxPool, minus the pooling - a keyed Svelte each-block reuses DOM
// nodes on its own, which is what UIComponentPool existed to do by hand.
//
// The hard part is not the animation, it is that THE WIRE CARRIES NO DAMAGE
// EVENT. There is no "you hit for N" packet anywhere in this protocol; there
// is only `CurrentMonsterHp` on a snapshot. So a hit has to be inferred from
// the difference between two snapshots, and the inference has to be careful
// about the three ways that difference can lie:
//
//   1. The monster changed. Health going from 6 to 3500 is a new monster, not
//      a heal, and 3500 to 6 on the same tick is not a 3494 hit.
//   2. The monster REGENERATED or the same monster id respawned at full
//      health. An increase is never a hit.
//   3. A reconnect gap collapses many hits into one difference. Reporting
//      "4210" for what was really thirty hits is worse than reporting nothing.
//
// This is deliberately fed from the AUTHORITATIVE snapshot stream, never from
// the interpolated one - the smoothed value passes through every intermediate
// number on its way, which would produce a blizzard of tiny fictional hits.

export interface DamageEvent {
  id: number;
  amount: number;
  /** 0..1 horizontal jitter so simultaneous numbers do not stack exactly. */
  offset: number;
  atMs: number;

  /**
   * Whether the blow that caused this crit.
   *
   * Modul: THE WIRE CARRIES THIS NOW. It did not when this file was written -
   * the comment above still explains why a hit has to be inferred at all - so
   * the client used to guess "that one was larger than usual" from a running
   * median and was careful never to call it a crit. The server now says
   * outright (LastHitWasCrit), because a crit that looks like every other hit
   * is a stat the player pays for and never sees.
   */
  isCrit: boolean;

  /** 0 melee, 1 ranged, 2 magic - which effect to draw. */
  weaponKind: number;
}

/** How long a number stays on screen. Matches the CSS animation duration. */
export const DAMAGE_TEXT_LIFETIME_MS = 1100;

/**
 * Beyond this the two snapshots are not adjacent - a reconnect, a tab that was
 * frozen by the OS, or a long dirty-check silence. Attributing the whole
 * accumulated difference to one hit would put an absurd number on screen.
 */
export const MAX_ADJACENT_SNAPSHOT_GAP_MS = 3000;

export interface CombatSample {
  monsterId: number;
  monsterHp: number;
  atMs: number;

  /** From the packet's LastHitWasCrit. Optional so the pure damage rules and
   *  their tests stay independent of it. */
  wasCrit?: boolean;
  /** From the packet's EquippedWeaponKind: 0 melee, 1 ranged, 2 magic. */
  weaponKind?: number;
}

/**
 * Pure: given the previous and current combat samples, the damage to show, or
 * null. Separated from any store so the rules above are unit-testable without
 * a DOM, a clock or a component.
 */
export function inferDamage(previous: CombatSample | null, current: CombatSample): number | null {
  if (previous === null) return null;
  if (previous.monsterId !== current.monsterId) return null;
  if (current.monsterId <= 0) return null;

  const gap = current.atMs - previous.atMs;
  if (gap <= 0 || gap > MAX_ADJACENT_SNAPSHOT_GAP_MS) return null;

  const delta = previous.monsterHp - current.monsterHp;
  // Zero is the common case (most snapshots carry no hit); negative is a heal
  // or a respawn at full health, and neither is damage the player dealt.
  return delta > 0 ? delta : null;
}

let sequence = 0;

export class DamageFeed {
  private previous: CombatSample | null = null;
  private events: DamageEvent[] = [];

  /** Returns the new event, if this snapshot represented a hit. */
  push(sample: CombatSample): DamageEvent | null {
    const amount = inferDamage(this.previous, sample);
    this.previous = sample;
    if (amount === null) return null;

    const event: DamageEvent = {
      id: ++sequence,
      amount,
      offset: Math.random(),
      atMs: sample.atMs,
      isCrit: sample.wasCrit === true,
      weaponKind: sample.weaponKind ?? 0,
    };
    this.events = [...this.events, event];
    this.record(amount);
    return event;
  }

  /** Drops expired events. Called from the render loop, not a timer per event. */
  prune(nowMs: number): DamageEvent[] {
    const kept = this.events.filter((e) => nowMs - e.atMs < DAMAGE_TEXT_LIFETIME_MS);
    if (kept.length !== this.events.length) this.events = kept;
    return this.events;
  }

  get current(): DamageEvent[] {
    return this.events;
  }

  reset(): void {
    this.previous = null;
    this.events = [];
    this.recentHits = [];
  }

  // Modul: a rolling record of real hits, kept SEPARATELY from `events`.
  //
  // `events` exists to drive floating damage text and is pruned after about a
  // second, so it is empty most of the time and useless as a measurement. The
  // world boss needs a damage estimate to send, and the only honest source of
  // one is what this player's hits actually land for.
  //
  // Deliberately not a running mean: a single outlier crit would drag it for
  // the rest of the session. The median of the last sixteen is stable, cheap,
  // and cannot be steered by one lucky hit.
  private recentHits: number[] = [];
  private static readonly SAMPLE_SIZE = 16;

  private record(amount: number): void {
    this.recentHits.push(amount);
    if (this.recentHits.length > DamageFeed.SAMPLE_SIZE) this.recentHits.shift();
  }

  /** Median of the last sixteen hits, or null when nothing has been observed. */
  get typicalHit(): number | null {
    if (this.recentHits.length === 0) return null;
    const sorted = [...this.recentHits].sort((a, b) => a - b);
    return sorted[Math.floor(sorted.length / 2)];
  }
}
