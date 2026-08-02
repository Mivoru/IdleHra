import { describe, it, expect } from 'vitest';
import {
  inferDamage,
  DamageFeed,
  DAMAGE_TEXT_LIFETIME_MS,
  MAX_ADJACENT_SNAPSHOT_GAP_MS,
  type CombatSample,
} from '../src/lib/stores/damage';

// Modul: THE WIRE CARRIES NO DAMAGE EVENT. There is no "you hit for N" packet
// anywhere in this protocol - only CurrentMonsterHp on a snapshot - so every
// number the player sees is inferred from a difference between two snapshots.
// That makes the inference rules the whole feature, and each of the cases
// below is a way the difference lies.

function sample(monsterId: number, monsterHp: number, atMs: number): CombatSample {
  return { monsterId, monsterHp, atMs };
}

describe('inferDamage', () => {
  it('reports the drop between two adjacent snapshots of the same monster', () => {
    expect(inferDamage(sample(91, 80, 0), sample(91, 74, 1100))).toBe(6);
  });

  it('reports nothing when health did not move', () => {
    // Most snapshots carry no hit at all, so this is the common path.
    expect(inferDamage(sample(91, 80, 0), sample(91, 80, 1100))).toBeNull();
  });

  it('reports nothing for the first snapshot of a session', () => {
    expect(inferDamage(null, sample(91, 80, 0))).toBeNull();
  });

  it('never reports a hit across a monster change', () => {
    // The previous monster died at 6 HP and a fresh one is at 3500. Neither
    // direction of that difference is damage the player dealt.
    expect(inferDamage(sample(91, 6, 0), sample(95, 3500, 1100))).toBeNull();
    expect(inferDamage(sample(95, 3500, 0), sample(91, 6, 1100))).toBeNull();
  });

  it('never reports a heal or a respawn as damage', () => {
    // Same monster id back at full health is a respawn, not a negative hit.
    expect(inferDamage(sample(91, 6, 0), sample(91, 80, 1100))).toBeNull();
  });

  it('reports nothing when not in combat', () => {
    expect(inferDamage(sample(0, 0, 0), sample(0, 0, 1100))).toBeNull();
  });

  it('refuses to attribute a reconnect gap to a single hit', () => {
    // Thirty hits collapsed into one difference would put an absurd number on
    // screen and misrepresent what happened. Silence is the honest answer.
    const gap = MAX_ADJACENT_SNAPSHOT_GAP_MS + 1;
    expect(inferDamage(sample(91, 4300, 0), sample(91, 90, gap))).toBeNull();
  });

  it('still reports across this game\'s real 2183 ms combat cadence', () => {
    // The measured gap between monster-HP changes is 1100-2183 ms. A threshold
    // tighter than that would suppress most real hits - the same mistake the
    // interpolator originally made with its 1000 ms reconnect guard.
    expect(inferDamage(sample(91, 80, 0), sample(91, 74, 2183))).toBe(6);
  });

  it('reports nothing for a non-advancing or reordered timestamp', () => {
    expect(inferDamage(sample(91, 80, 1000), sample(91, 74, 1000))).toBeNull();
    expect(inferDamage(sample(91, 80, 1000), sample(91, 74, 900))).toBeNull();
  });
});

describe('DamageFeed', () => {
  it('emits one event per hit and keeps them until they expire', () => {
    const feed = new DamageFeed();
    expect(feed.push(sample(91, 80, 0))).toBeNull();

    const hit = feed.push(sample(91, 74, 1000));
    expect(hit?.amount).toBe(6);
    expect(feed.current).toHaveLength(1);

    feed.push(sample(91, 68, 2000));
    expect(feed.current).toHaveLength(2);

    // Still inside the lifetime of the second, past that of the first.
    expect(feed.prune(2000 + DAMAGE_TEXT_LIFETIME_MS - 1)).toHaveLength(1);
    expect(feed.prune(2000 + DAMAGE_TEXT_LIFETIME_MS + 1)).toHaveLength(0);
  });

  it('gives each event a distinct id so a keyed list animates each once', () => {
    const feed = new DamageFeed();
    feed.push(sample(91, 80, 0));
    const first = feed.push(sample(91, 74, 1000))!;
    const second = feed.push(sample(91, 68, 2000))!;
    expect(first.id).not.toBe(second.id);
  });

  it('forgets its previous sample on reset, so a reconnect invents no hit', () => {
    const feed = new DamageFeed();
    feed.push(sample(91, 80, 0));
    feed.reset();
    // Without the reset this would report a 74-point hit spanning the outage.
    expect(feed.push(sample(91, 6, 1000))).toBeNull();
    expect(feed.current).toHaveLength(0);
  });
});
