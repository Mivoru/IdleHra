import { describe, it, expect } from 'vitest';
import {
  SnapshotInterpolator,
  lerp,
  extractInterpolated,
  INTERPOLATED_FIELD_NAMES,
  MIN_RENDER_DELAY_MS,
  MAX_RENDER_DELAY_MS,
  type InterpolatedFields,
} from '../src/lib/net/interpolation';

// Modul: these tests are written around the MEASURED arrival cadence, not the
// port plan's "~10/sec". Driving the real client against the real server put
// the gap between monster-HP changes at 1100-2183 ms, mean 1637 - because 10 Hz
// is the tick rate and SimulationEngine dirty-checks before it dispatches.
//
// The first version of this file encoded the wrong premise and passed
// completely while the bar stepped in the browser: a fixed 100 ms delay and a
// fixed 1000 ms "reconnect" threshold, both fine against synthetic 100 ms
// snapshots and both broken against the real thing. So the constants below are
// the real ones.
const REAL_GAP_SHORT = 1100;
const REAL_GAP_LONG = 2183;

function fields(overrides: Partial<InterpolatedFields> = {}): InterpolatedFields {
  return {
    PlayerHp: 0,
    CurrentMonsterHp: 0,
    CurrentProgressTicks: 0,
    CurrentXp: 0,
    Gold: 0,
    CurrentMana: 0,
    ...overrides,
  };
}

describe('lerp', () => {
  it('hits both endpoints exactly', () => {
    expect(lerp(10, 20, 0)).toBe(10);
    expect(lerp(10, 20, 1)).toBe(20);
    expect(lerp(10, 20, 0.5)).toBe(15);
  });
});

describe('SnapshotInterpolator', () => {
  it('returns nothing before any snapshot arrives', () => {
    expect(new SnapshotInterpolator().sample(1000)).toBeNull();
  });

  it('returns the first snapshot verbatim - there is nothing to interpolate to', () => {
    const interp = new SnapshotInterpolator();
    interp.push(fields({ PlayerHp: 80 }), 91, 1000);
    expect(interp.sample(1000)?.PlayerHp).toBe(80);
  });

  // THE regression test for the bug the browser found. At this game's real
  // cadence the bar must pass through intermediate values rather than jumping.
  it('actually produces intermediate values at the real arrival cadence', () => {
    const interp = new SnapshotInterpolator();
    interp.push(fields({ CurrentMonsterHp: 80 }), 91, 0);
    interp.push(fields({ CurrentMonsterHp: 74 }), 91, REAL_GAP_LONG);

    const samples: number[] = [];
    for (let now = REAL_GAP_LONG; now <= REAL_GAP_LONG + 1600; now += 16) {
      samples.push(interp.sample(now)!.CurrentMonsterHp);
    }

    const distinct = new Set(samples.map((v) => v.toFixed(3)));
    // The fixed-delay version produced exactly 1 distinct value here, which is
    // precisely what a stepping bar looks like.
    expect(distinct.size).toBeGreaterThan(20);

    // And it must be monotonic toward the new value, never overshooting it.
    expect(Math.min(...samples)).toBeGreaterThanOrEqual(74);
    expect(Math.max(...samples)).toBeLessThanOrEqual(80);
    expect(samples[samples.length - 1]).toBeCloseTo(74, 6);
  });

  it('adapts its render delay to the observed interval', () => {
    const interp = new SnapshotInterpolator();
    expect(interp.renderDelayMs).toBeLessThanOrEqual(MAX_RENDER_DELAY_MS);

    let at = 0;
    for (let i = 0; i < 12; i++) {
      interp.push(fields(), 91, at);
      at += REAL_GAP_SHORT;
    }
    // Converged on the real cadence, capped by the staleness ceiling.
    expect(interp.renderDelayMs).toBeCloseTo(Math.min(REAL_GAP_SHORT, MAX_RENDER_DELAY_MS), 0);
  });

  it('never renders staler than the declared ceiling, however slow the feed', () => {
    const interp = new SnapshotInterpolator();
    let at = 0;
    for (let i = 0; i < 5; i++) {
      // Just under the stall threshold, so these stay "slow" rather than
      // becoming discontinuities.
      interp.push(fields(), 91, at);
      at += 2900;
    }
    expect(interp.renderDelayMs).toBeLessThanOrEqual(MAX_RENDER_DELAY_MS);
    expect(interp.renderDelayMs).toBeGreaterThanOrEqual(MIN_RENDER_DELAY_MS);
  });

  it('clamps rather than extrapolating past the newest snapshot', () => {
    const interp = new SnapshotInterpolator();
    interp.push(fields({ PlayerHp: 100 }), 91, 0);
    interp.push(fields({ PlayerHp: 200 }), 91, REAL_GAP_SHORT);

    // A late frame must not invent a value the server never sent; guessing
    // forward is what forces a visible snap-back when the next packet lands.
    expect(interp.sample(60_000)?.PlayerHp).toBe(200);
  });

  it('clamps below zero t as well, when a frame runs early', () => {
    const interp = new SnapshotInterpolator();
    interp.push(fields({ PlayerHp: 100 }), 91, 0);
    interp.push(fields({ PlayerHp: 200 }), 91, REAL_GAP_SHORT);
    expect(interp.sample(0)?.PlayerHp).toBe(100);
  });

  it('does NOT interpolate across a monster change', () => {
    const interp = new SnapshotInterpolator();
    interp.push(fields({ CurrentMonsterHp: 10 }), 91, 0);
    // The old monster died and a fresh one spawned at full health. Sliding
    // from 10 to 3500 would animate a health bar filling up, which is not what
    // happened - a different monster is simply there now.
    interp.push(fields({ CurrentMonsterHp: 3500 }), 95, REAL_GAP_SHORT);

    expect(interp.sample(REAL_GAP_SHORT + 400)?.CurrentMonsterHp).toBe(3500);
  });

  it('treats a genuine stall as a discontinuity', () => {
    const interp = new SnapshotInterpolator();
    interp.push(fields({ CurrentXp: 100 }), 91, 0);
    // Well past both the 3000 ms floor and four times the estimate.
    interp.push(fields({ CurrentXp: 9000 }), 91, 30_000);

    expect(interp.sample(30_400)?.CurrentXp).toBe(9000);
  });

  it('does NOT treat this game\'s normal 2183 ms combat gap as a stall', () => {
    // The exact regression: a fixed 1000 ms threshold classified ordinary
    // combat as a reconnect and disabled interpolation on most transitions.
    const interp = new SnapshotInterpolator();
    interp.push(fields({ CurrentMonsterHp: 80 }), 91, 0);
    interp.push(fields({ CurrentMonsterHp: 74 }), 91, REAL_GAP_LONG);

    const midway = interp.sample(REAL_GAP_LONG + 200)!.CurrentMonsterHp;
    expect(midway).toBeGreaterThan(74);
    expect(midway).toBeLessThan(80);
  });

  it('interpolates every declared field, not just the one being watched', () => {
    const interp = new SnapshotInterpolator();
    interp.push(fields(), 91, 0);
    interp.push(
      fields({
        PlayerHp: 100,
        CurrentMonsterHp: 100,
        CurrentProgressTicks: 100,
        CurrentXp: 100,
        Gold: 100,
        CurrentMana: 100,
      }),
      91,
      1000,
    );

    // Sample where the render time lands exactly midway between the two
    // arrivals (0 and 1000), derived from the delay rather than hardcoded
    // because the delay adapts.
    const at = 500 + interp.renderDelayMs;
    const sampled = interp.sample(at)!;
    for (const field of INTERPOLATED_FIELD_NAMES) {
      expect(sampled[field]).toBeCloseTo(50, 6);
    }
  });

  it('forgets everything on reset, so a resumed session does not animate the gap', () => {
    const interp = new SnapshotInterpolator();
    interp.push(fields({ PlayerHp: 100 }), 91, 0);
    interp.push(fields({ PlayerHp: 200 }), 91, REAL_GAP_SHORT);
    interp.reset();
    expect(interp.sample(REAL_GAP_SHORT + 100)).toBeNull();
  });
});

describe('extractInterpolated', () => {
  it('pulls the interpolated subset out of a full StateUpdate', () => {
    const extracted = extractInterpolated({
      PlayerHp: 42,
      Gold: 1500,
      CurrentLevel: 7,
      SomeUnrelatedField: 'x',
    });
    expect(extracted.PlayerHp).toBe(42);
    expect(extracted.Gold).toBe(1500);
  });

  it('defaults a missing or non-numeric field to zero rather than NaN', () => {
    // A NaN would propagate through the lerp into a bar width of "NaN%", which
    // renders as an empty bar with no error anywhere - the silent-failure
    // shape this project keeps hitting.
    const extracted = extractInterpolated({ PlayerHp: 'Infinity' });
    expect(extracted.PlayerHp).toBe(0);
    expect(extracted.Gold).toBe(0);
  });
});
