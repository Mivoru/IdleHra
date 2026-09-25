// Modul: THE DRAWN RING IS THE SCORED RING (task 36). This reads the fixture
// the C# scorer tests read - server/FolkIdle.Server.Tests/Fixtures/
// shield_wheel_cases.json - and agrees with it point by point. If the server's
// geometry and this file's ever part, a player would watch a spear land on one
// plate and be scored on another; this is where that fails first.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  angleAt,
  plateAt,
  frozenAt,
  landWheelTap,
  correctChoice,
  isRead,
  nextSeamCrossing,
  classify,
  type WheelSchedule,
  type ParryTell,
} from '../src/lib/game/shieldWheel';

const here = dirname(fileURLToPath(import.meta.url));
const fixture = JSON.parse(
  readFileSync(join(here, '..', '..', 'server', 'FolkIdle.Server.Tests', 'Fixtures', 'shield_wheel_cases.json'), 'utf8'),
);
const schedule = fixture.schedule as WheelSchedule;

describe('shield wheel geometry', () => {
  it('puts the ring where the server says, at every fixture point', () => {
    for (const p of fixture.angles) {
      expect(Math.abs(angleAt(schedule, p.tMs) - p.angle)).toBeLessThan(1e-6);
      expect(plateAt(schedule, p.tMs)).toBe(p.plate);
      expect(frozenAt(schedule, p.tMs)).toBe(p.frozen);
    }
  });

  it('lands every non-dropped wheel tap on the plate and class the server scores', () => {
    let checked = 0;
    for (const c of fixture.cases) {
      if (c.expect.verdict !== 'Accepted') continue;
      for (const landing of c.expect.landings) {
        if (landing.IsCounter) continue;
        const tap = c.taps.find((t: { Seq: number }) => t.Seq === landing.Seq);
        const got = landWheelTap(schedule, tap.TapMs);
        expect({ case: c.name, plate: got.plate, cls: got.cls }).toEqual({ case: c.name, plate: landing.Plate, cls: landing.Class });
        checked++;
      }
    }
    expect(checked).toBeGreaterThan(10);
  });

  it('classifies the bands exactly as the server does', () => {
    expect(classify(0)).toBe('Glance');
    expect(classify(2.99)).toBe('Glance');
    expect(classify(3)).toBe('Plate');
    // Seam is 8 degrees since 2026-09-25: |offset - 36| <= 4.
    expect(classify(31.99)).toBe('Plate');
    expect(classify(32)).toBe('Seam');
    expect(classify(36)).toBe('Seam');
    expect(classify(40)).toBe('Seam');
    expect(classify(40.01)).toBe('Plate');
    expect(classify(69)).toBe('Plate');
    expect(classify(69.01)).toBe('Glance');
  });

  it('reads a tell the way the scorer does', () => {
    const tells: ParryTell[] = ['Left', 'Right', 'Overhead'];
    expect(tells.map(correctChoice)).toEqual(['DodgeRight', 'DodgeLeft', 'Block']);
    const interrupt = fixture.schedule.Interrupts[0];
    expect(isRead(interrupt, 'DodgeRight', interrupt.TellAtMs + 300)).toBe(true);
    expect(isRead(interrupt, 'DodgeRight', interrupt.TellAtMs + 100)).toBe(false);
    expect(isRead(interrupt, 'DodgeRight', interrupt.TellAtMs + 1100)).toBe(false);
    expect(isRead(interrupt, 'DodgeLeft', interrupt.TellAtMs + 300)).toBe(false);
  });

  it('finds the seam crossings the fixture was built from', () => {
    // The perfect case's landings: plate 0 at 400 ms and plate 1 at 1,200 ms.
    expect(nextSeamCrossing(schedule, 0, 0, 3000)).toBe(400);
    expect(nextSeamCrossing(schedule, 1, 0, 3000)).toBe(1200);
    // Nothing moves inside a freeze.
    expect(nextSeamCrossing(schedule, 4, 4000, 6400)).toBeNull();
  });
});
