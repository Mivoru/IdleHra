// Modul: floating damage text. The descendant of UiFloatingDamageText plus
// CombatVfxPool, minus the pooling - a keyed Svelte each-block reuses DOM
// nodes on its own, which is what UIComponentPool existed to do by hand.
//
// THIS FILE USED TO INFER EVERY HIT FROM THE DIFFERENCE BETWEEN TWO SNAPSHOTS,
// because the wire carried no damage event. It carried none for a long time,
// and the inference had to be careful about the three ways a health difference
// can lie: the monster changing, a regeneration or respawn, and a reconnect gap
// collapsing thirty hits into one number.
//
// It was careful, and it was still wrong about the case that mattered most.
// Measured 2026-09-04: snapshots arrive every ~1090 ms and a geared character
// kills an early monster every ~1400 ms, so spawn and death both happen between
// two samples. `CurrentMonsterHp` took ONE value across 27 consecutive
// snapshots. The inference refuses to report when the monster changed - which
// is exactly what a one-hit kill looks like - so the player who most needed a
// number on screen got nothing at all.
//
// The server states each blow now (ResponseCombatEventPacket), so this reads a
// fact instead of deducing one. `inferDamage` and its snapshot plumbing are
// GONE rather than kept as a fallback: two sources for one truth is this
// codebase's dominant bug class, and a cosmetic number is not worth being the
// exception.

export interface DamageEvent {
  id: number;
  amount: number;
  /** 0..1 horizontal jitter so simultaneous numbers do not stack exactly. */
  offset: number;
  atMs: number;

  /**
   * Whether the blow that caused this crit.
   *
   * Stated by the server, on the event itself. It used to be a guess from a
   * running median - the client was careful never to call it a crit - then
   * became `LastHitWasCrit` on the snapshot, which was true of the LAST swing
   * rather than of any particular number on screen. Now it belongs to the hit
   * it describes.
   */
  isCrit: boolean;

  /** 0 melee, 1 ranged, 2 magic - which effect to draw. */
  weaponKind: number;
}

/** How long a number stays on screen. Matches the CSS animation duration. */
export const DAMAGE_TEXT_LIFETIME_MS = 1100;

let sequence = 0;

export class DamageFeed {
  private events: DamageEvent[] = [];

  /**
   * One resolved blow, as the server reported it.
   *
   * `amount` is whole hit points and already final - post-armour, post-crit,
   * burn folded in. Nothing here recomputes or second-guesses it.
   */
  push(amount: number, isCrit: boolean, weaponKind: number, atMs: number): DamageEvent | null {
    if (!Number.isFinite(amount) || amount <= 0) return null;

    const event: DamageEvent = {
      id: ++sequence,
      amount,
      offset: Math.random(),
      atMs,
      isCrit,
      weaponKind,
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
    this.events = [];
    this.recentHits = [];
  }

  // Modul: a rolling record of real hits, kept SEPARATELY from `events`.
  //
  // `events` exists to drive floating damage text and is pruned after about a
  // second, so it is empty most of the time and useless as a measurement.
  //
  // Two things still read it: the floating text scales its font against the
  // median so a big hit looks big, and the GUILD WAR shard attack still posts a
  // client-computed damage figure. The world boss used to as well and no longer
  // does - it sends a plate index and the server reads the player's own attack
  // power - so this is the last consumer of that shape, and it goes when Guild
  // Wars comes off the roadmap.
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
