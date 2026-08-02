// Modul: snapshot interpolation. The descendant of the Unity client's
// VisualSyncProxy lerp.
//
// The model: hold the previous and current snapshot with the wall-clock time
// each ARRIVED, then render at time `now - delay`, one packet interval in the
// past. Rendering in the past is what makes this interpolation rather than
// extrapolation - there is always a real snapshot on both sides of the render
// time, so the value never overshoots and never has to snap back when the next
// packet contradicts a guess.
//
// THE DELAY MUST BE ADAPTIVE, and this is the whole subtlety of the file.
//
// The port plan says StateUpdate arrives "~10/sec", and a first version of
// this file took that literally: a fixed 100 ms delay, and any gap over
// 1000 ms treated as a reconnect. Driving the real client against the real
// server with a MutationObserver on the health bar measured the truth:
//
//   gaps between monster-HP changes: 2183, 1100, 2183, 1084, 2182, 1100, ...
//   mean 1637 ms
//
// 10 Hz is the TICK rate, not the arrival rate - SimulationEngine dirty-checks
// before dispatching, and monster health only changes when an attack lands.
// Against a 1637 ms cadence the fixed version failed twice over: a 100 ms
// delay put the render time at or past the newest snapshot, so t was pinned at
// 1 and nothing was ever interpolated, and the 1000 ms reconnect threshold
// then discarded `previous` on most transitions anyway. The bar stepped, which
// is exactly the outcome the plan says would poison this phase's decision.
//
// So the delay is estimated from observed arrivals instead of assumed. The
// cost is staleness: the smoothed value trails the server by up to
// MAX_RENDER_DELAY_MS. That is deliberate and safe here only because the
// authoritative snapshot is a SEPARATE store - anything that decides reads
// `playerState`, and only things that animate read this. Nothing may ever
// decide from an interpolated value.
//
// Deliberately a plain class over numbers rather than anything Svelte-aware,
// so it can be unit tested with no DOM and no component harness.

/** Only fields where smooth motion is the point. Everything else reads raw. */
export interface InterpolatedFields {
  PlayerHp: number;
  CurrentMonsterHp: number;
  CurrentProgressTicks: number;
  CurrentXp: number;
  Gold: number;
  CurrentMana: number;
}

export const INTERPOLATED_FIELD_NAMES: readonly (keyof InterpolatedFields)[] = [
  'PlayerHp',
  'CurrentMonsterHp',
  'CurrentProgressTicks',
  'CurrentXp',
  'Gold',
  'CurrentMana',
];

interface Snapshot {
  values: InterpolatedFields;
  arrivedAtMs: number;
  /** Monster identity at this snapshot; see the discontinuity note below. */
  monsterId: number;
}

export function lerp(from: number, to: number, t: number): number {
  return from + (to - from) * t;
}

/** Floor on the render delay - below a frame or two there is nothing to gain. */
export const MIN_RENDER_DELAY_MS = 80;

/**
 * Ceiling on the render delay, and therefore on how stale the animated value
 * may be. Set just under the measured 1637 ms cadence: high enough that a
 * normal transition is smoothed across most of its span, low enough that a
 * long stall cannot leave the bar visibly lying about the current state.
 */
export const MAX_RENDER_DELAY_MS = 1500;

/** Seed used until two snapshots have actually been observed. */
export const INITIAL_INTERVAL_ESTIMATE_MS = 400;

export class SnapshotInterpolator {
  private previous: Snapshot | null = null;
  private current: Snapshot | null = null;

  /**
   * Exponentially weighted estimate of the gap between arrivals. Weighted
   * toward history (0.75) because the real cadence alternates - roughly
   * 1100 ms and 2180 ms in the measured trace - and chasing each swing would
   * make the delay itself jitter, which is visible as the bar changing speed.
   */
  private intervalEstimateMs = INITIAL_INTERVAL_ESTIMATE_MS;

  constructor(private readonly maxDelayMs = MAX_RENDER_DELAY_MS) {}

  /** How far behind live this is currently rendering. Exposed for tests. */
  get renderDelayMs(): number {
    return Math.max(MIN_RENDER_DELAY_MS, Math.min(this.maxDelayMs, this.intervalEstimateMs));
  }

  push(values: InterpolatedFields, monsterId: number, arrivedAtMs: number): void {
    const gap = this.current === null ? 0 : arrivedAtMs - this.current.arrivedAtMs;

    // A dead monster replaced by the next one is a genuine discontinuity, not
    // motion: interpolating across it would animate the new monster's health
    // sliding down from the old one's, which is not what happened.
    //
    // The stall threshold is derived from the observed cadence rather than
    // hardcoded. A fixed 1000 ms - which an earlier version used - is SHORTER
    // than this game's normal 2183 ms gap between attacks, so it classified
    // ordinary combat as a reconnect and threw away the previous snapshot
    // every time, disabling interpolation entirely.
    const stallThreshold = Math.max(3000, this.intervalEstimateMs * 4);
    const isDiscontinuity =
      this.current !== null && (this.current.monsterId !== monsterId || gap > stallThreshold);

    if (isDiscontinuity) {
      this.previous = null;
      this.current = { values, monsterId, arrivedAtMs };
      return;
    }

    if (this.current !== null && gap > 0) {
      this.intervalEstimateMs =
        this.previous === null ? gap : this.intervalEstimateMs * 0.75 + gap * 0.25;
    }

    this.previous = this.current;
    this.current = { values, monsterId, arrivedAtMs };
  }

  reset(): void {
    this.previous = null;
    this.current = null;
    this.intervalEstimateMs = INITIAL_INTERVAL_ESTIMATE_MS;
  }

  /** The values to render at wall-clock `nowMs`. Null until a snapshot lands. */
  sample(nowMs: number): InterpolatedFields | null {
    if (this.current === null) return null;
    if (this.previous === null) return this.current.values;

    const span = this.current.arrivedAtMs - this.previous.arrivedAtMs;
    if (span <= 0) return this.current.values;

    const renderAt = nowMs - this.renderDelayMs;
    // Clamped rather than extrapolated: past 1 the honest answer is "the
    // newest thing the server actually told us", not a guess about the future
    // that the next packet may have to visibly undo.
    const t = Math.max(0, Math.min(1, (renderAt - this.previous.arrivedAtMs) / span));

    const from = this.previous.values;
    const to = this.current.values;
    const out = {} as InterpolatedFields;
    for (const field of INTERPOLATED_FIELD_NAMES) {
      out[field] = lerp(from[field], to[field], t);
    }
    return out;
  }
}

/** Pulls the interpolated subset out of a full StateUpdate. */
export function extractInterpolated(packet: Record<string, unknown>): InterpolatedFields {
  const out = {} as InterpolatedFields;
  for (const field of INTERPOLATED_FIELD_NAMES) {
    const value = packet[field];
    out[field] = typeof value === 'number' ? value : 0;
  }
  return out;
}
