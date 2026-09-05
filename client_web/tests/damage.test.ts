// The floating damage numbers, and where they stopped coming from.
//
// This file used to test `inferDamage` - a careful set of rules for deducing a
// hit from the difference between two CurrentMonsterHp snapshots, because the
// wire carried no combat event. It was careful about the monster changing,
// about heals and respawns, and about a reconnect gap collapsing thirty hits
// into one number.
//
// It was also wrong about the case that mattered most, and no amount of care
// could have fixed it. Measured 2026-09-04: snapshots arrive every ~1090 ms and
// a geared character kills an early monster every ~1400 ms, so spawn and death
// both fall between two samples. The inference refuses to report when the
// monster changed - which is exactly what a one-hit kill looks like - so the
// player whose character killed something instantly saw no number at all.
//
// The server states each blow now. These tests cover what is left: the feed's
// own bookkeeping, and the median that two things still read.
import { describe, it, expect } from 'vitest';
import { DamageFeed, DAMAGE_TEXT_LIFETIME_MS } from '../src/lib/stores/damage';

const MELEE = 0;
const MAGIC = 2;

describe('the damage feed', () => {
  it('turns a resolved blow into one floating number', () => {
    const feed = new DamageFeed();
    const event = feed.push(412, false, MELEE, 1000);

    expect(event).not.toBeNull();
    expect(event!.amount).toBe(412);
    expect(event!.isCrit).toBe(false);
    expect(event!.weaponKind).toBe(MELEE);
    expect(feed.current).toHaveLength(1);
  });

  it('carries the crit and the weapon family, because the server states both', () => {
    const feed = new DamageFeed();
    const event = feed.push(861, true, MAGIC, 1000);

    // A crit used to be a guess from a running median - the client was careful
    // never to call it one - and then LastHitWasCrit on the snapshot, which
    // described the LAST swing rather than any number on screen. It belongs to
    // the hit now.
    expect(event!.isCrit).toBe(true);
    expect(event!.weaponKind).toBe(MAGIC);
  });

  it('refuses a nonsense amount rather than drawing a zero', () => {
    const feed = new DamageFeed();
    expect(feed.push(0, false, MELEE, 1000)).toBeNull();
    expect(feed.push(-5, false, MELEE, 1000)).toBeNull();
    expect(feed.push(Number.NaN, false, MELEE, 1000)).toBeNull();
    expect(feed.current).toHaveLength(0);
  });

  it('gives every number its own id and jitter, so two at once do not stack', () => {
    const feed = new DamageFeed();
    const a = feed.push(100, false, MELEE, 1000)!;
    const b = feed.push(100, false, MELEE, 1000)!;

    expect(a.id).not.toBe(b.id);
    expect(a.offset).toBeGreaterThanOrEqual(0);
    expect(a.offset).toBeLessThanOrEqual(1);
  });

  it('prunes expired numbers from the render loop, not from a timer per event', () => {
    const feed = new DamageFeed();
    feed.push(100, false, MELEE, 1000);
    feed.push(200, false, MELEE, 1000 + DAMAGE_TEXT_LIFETIME_MS);

    const kept = feed.prune(1000 + DAMAGE_TEXT_LIFETIME_MS + 1);
    expect(kept).toHaveLength(1);
    expect(kept[0].amount).toBe(200);
  });

  it('reset clears the numbers and the running median together', () => {
    const feed = new DamageFeed();
    feed.push(500, false, MELEE, 1000);
    expect(feed.typicalHit).toBe(500);

    feed.reset();
    expect(feed.current).toHaveLength(0);
    expect(feed.typicalHit).toBeNull();
  });
});

describe('the typical hit', () => {
  // Modul: TWO THINGS STILL READ THIS. The floating text scales its font
  // against the median so a big hit looks big, and the guild war shard attack
  // still posts a client-computed damage figure. The world boss used to and no
  // longer does - it sends a plate index and the server reads the player's own
  // attack power.
  it('is null until something has actually been hit', () => {
    expect(new DamageFeed().typicalHit).toBeNull();
  });

  it('is a median, so one lucky crit cannot drag it', () => {
    const feed = new DamageFeed();
    for (const amount of [100, 100, 100, 100, 9999]) {
      feed.push(amount, false, MELEE, 1000);
    }

    // A running mean would read 2079 here. The median is the point.
    expect(feed.typicalHit).toBe(100);
  });

  it('keeps only the last sixteen, so it follows the character as it grows', () => {
    const feed = new DamageFeed();
    for (let i = 0; i < 16; i++) feed.push(10, false, MELEE, 1000);
    expect(feed.typicalHit).toBe(10);

    // Sixteen much larger hits displace every one of the old ones.
    for (let i = 0; i < 16; i++) feed.push(1000, false, MELEE, 2000);
    expect(feed.typicalHit).toBe(1000);
  });
});
