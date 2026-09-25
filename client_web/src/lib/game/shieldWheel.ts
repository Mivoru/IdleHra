// Modul: THE SHIELD WHEEL, CLIENT SIDE (task 36). This file DRAWS and
// PREVIEWS; it never scores. The server scores the same taps against its own
// schedule and its answer is authoritative - these functions exist so the ring
// the player sees is the ring the server scores, which is pinned by
// tests/shieldWheel.test.ts reading the very fixture the C# scorer tests read
// (server/FolkIdle.Server.Tests/Fixtures/shield_wheel_cases.json).
//
// Angles are RING-LOCAL UNDER THE IMPACT POINT: angleAt(t) is the part of the
// ring sitting at the impact point t ms after the countdown, so the ring is
// drawn rotated by -angleAt(t). Keep every constant in step with
// server/FolkIdle.Server/Domain/Combat/WorldBossStrike/WorldBossStrikeRules.cs;
// the challenge also carries the ones the client needs, and the component
// reads those rather than these defaults where it can.

export type ParryTell = 'Left' | 'Right' | 'Overhead';
export type ParryChoice = 'DodgeLeft' | 'Block' | 'DodgeRight';
export type SpearClass = 'None' | 'Glance' | 'Plate' | 'Seam';

export interface WheelSegment {
  StartMs: number;
  DurationMs: number;
  DegPerSec: number;
  Interrupt?: number | null;
}

export interface WheelInterrupt {
  Index: number;
  TellAtMs: number;
  Tell: ParryTell;
  ReactionFloorMs: number;
  ResponseCloseMs: number;
  InterruptMs: number;
}

export interface WheelSchedule {
  StartAngleDeg: number;
  Segments: WheelSegment[];
  Interrupts: WheelInterrupt[];
}

export const PLATE_COUNT = 5;
export const PLATE_DEGREES = 360 / PLATE_COUNT;
export const SEAM_DEGREES = 12;
export const RIVET_DEGREES = 3;
export const FLIGHT_MS = 120;
export const TOLERANCE_MS = 35;
export const TOLERANCE_STEP_MS = 5;

const CLASS_RANK: Record<SpearClass, number> = { None: 0, Glance: 1, Plate: 2, Seam: 3 };

export function normalize(angle: number): number {
  let a = angle % 360;
  if (a < 0) a += 360;
  if (a >= 360) a -= 360;
  return a;
}

function isFrozen(segment: WheelSegment): boolean {
  return segment.Interrupt !== undefined && segment.Interrupt !== null;
}

/** The ring-local angle under the impact point, t ms after the countdown. */
export function angleAt(schedule: WheelSchedule, tMs: number): number {
  let angle = schedule.StartAngleDeg;
  if (tMs <= 0) return normalize(angle);

  let last: WheelSegment | undefined;
  for (const segment of schedule.Segments) {
    last = segment;
    if (tMs <= segment.StartMs) break;
    const end = segment.StartMs + segment.DurationMs;
    const inside = Math.min(tMs, end) - segment.StartMs;
    angle += (segment.DegPerSec * inside) / 1000;
    if (tMs <= end) return normalize(angle);
  }

  // Past the last segment: keep turning at the last moving speed.
  if (last && tMs > last.StartMs + last.DurationMs) {
    const moving = [...schedule.Segments].reverse().find((s) => !isFrozen(s));
    if (moving) angle += (moving.DegPerSec * (tMs - (last.StartMs + last.DurationMs))) / 1000;
  }
  return normalize(angle);
}

export function plateOf(angle: number): number {
  const plate = Math.floor(normalize(angle) / PLATE_DEGREES);
  return Math.min(PLATE_COUNT - 1, Math.max(0, plate));
}

export function offsetInPlate(angle: number): number {
  const a = normalize(angle);
  return a - plateOf(a) * PLATE_DEGREES;
}

export function classify(offset: number): SpearClass {
  if (offset < RIVET_DEGREES || offset > PLATE_DEGREES - RIVET_DEGREES) return 'Glance';
  if (Math.abs(offset - PLATE_DEGREES / 2) <= SEAM_DEGREES / 2) return 'Seam';
  return 'Plate';
}

export function plateAt(schedule: WheelSchedule, tMs: number): number {
  return plateOf(angleAt(schedule, tMs));
}

/** The interrupt whose frozen interval contains t, if any. */
export function interruptAt(schedule: WheelSchedule, tMs: number): WheelInterrupt | null {
  for (const interrupt of schedule.Interrupts) {
    if (tMs >= interrupt.TellAtMs && tMs < interrupt.TellAtMs + interrupt.InterruptMs) return interrupt;
  }
  return null;
}

export function frozenAt(schedule: WheelSchedule, tMs: number): boolean {
  return interruptAt(schedule, tMs) !== null;
}

/** Move away from the blow; block what comes from above. */
export function correctChoice(tell: ParryTell): ParryChoice {
  if (tell === 'Left') return 'DodgeRight';
  if (tell === 'Right') return 'DodgeLeft';
  return 'Block';
}

/** A correct read: the right answer after the reaction floor and inside the response window. */
export function isRead(interrupt: WheelInterrupt, choice: ParryChoice, choiceMs: number): boolean {
  return (
    choice === correctChoice(interrupt.Tell) &&
    choiceMs >= interrupt.TellAtMs + interrupt.ReactionFloorMs &&
    choiceMs <= interrupt.TellAtMs + interrupt.ResponseCloseMs
  );
}

/**
 * Where a wheel tap lands, the way the server scores it: the plate under the
 * impact point FLIGHT_MS after the exact tap, and the best class on THAT plate
 * within the tolerance window. Used for the instant animation only.
 */
export function landWheelTap(schedule: WheelSchedule, tapMs: number): { plate: number; cls: SpearClass } {
  const plate = plateAt(schedule, tapMs + FLIGHT_MS);
  let best: SpearClass = 'None';
  for (let d = -TOLERANCE_MS; d <= TOLERANCE_MS; d += TOLERANCE_STEP_MS) {
    const angle = angleAt(schedule, tapMs + d + FLIGHT_MS);
    if (plateOf(angle) !== plate) continue;
    const cls = classify(offsetInPlate(angle));
    if (CLASS_RANK[cls] > CLASS_RANK[best]) best = cls;
  }
  return { plate, cls: best === 'None' ? 'Glance' : best };
}

/**
 * The next moment the centre of `plate`'s seam reaches the impact point after
 * `fromMs`, or null if it does not come round before `untilMs`. Used by the
 * exercise script to aim, and by nothing that scores.
 */
export function nextSeamCrossing(schedule: WheelSchedule, plate: number, fromMs: number, untilMs: number): number | null {
  const centre = plate * PLATE_DEGREES + PLATE_DEGREES / 2;
  let prev = angleAt(schedule, fromMs);
  for (let t = fromMs + 1; t <= untilMs; t += 1) {
    if (frozenAt(schedule, t)) {
      prev = angleAt(schedule, t);
      continue;
    }
    const cur = angleAt(schedule, t);
    // Unwrapped step is at most a few degrees per ms, so the shorter arc is the path.
    let delta = cur - prev;
    if (delta > 180) delta -= 360;
    if (delta < -180) delta += 360;
    const lo = Math.min(prev, prev + delta);
    const hi = Math.max(prev, prev + delta);
    for (const c of [centre - 360, centre, centre + 360]) {
      if (c > lo && c <= hi) return t;
    }
    prev = cur;
  }
  return null;
}
