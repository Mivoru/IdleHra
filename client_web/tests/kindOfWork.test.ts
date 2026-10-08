import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { kindOfWork, isSameKindOfWork } from '../src/lib/ui/slots';
import { kindTakenBy, lootOwner, slotBars, type Worker } from '../src/lib/ui/workers';
import { REBIRTH_CONFIRMATION_WORD } from '../src/lib/net/rest';

// 2026-10-08: one character per kind of work. kindOfWork mirrors
// CharacterSlotEngine.ActivityCategory; the numbers are read from the C# so the
// two cannot drift (an index crossing the wire is a two-sources-of-truth
// surface - see the root CLAUDE.md).
const here = dirname(fileURLToPath(import.meta.url));
const engine = readFileSync(
  join(here, '..', '..', 'server', 'FolkIdle.Server', 'Domain', 'Combat', 'CharacterSlotEngine.cs'),
  'utf8',
);
const category = (name: string) => {
  const match = engine.match(new RegExp(`const int Category${name} = (\\d+);`));
  if (!match) throw new Error(`Category${name} not found - update the pattern, not the test`);
  return Number(match[1]);
};

const worker = (slot: number, activity: number): Worker => ({ slot, id: `id-${slot}`, raceId: 1, activity, halt: 0 });

describe('kind of work', () => {
  it('matches the server categories', () => {
    expect(kindOfWork(0)).toBe(category('None'));
    expect(kindOfWork(91)).toBe(category('Combat'));
    expect(kindOfWork(1001)).toBe(category('Woodcutting'));
    expect(kindOfWork(2003)).toBe(category('Mining'));
    expect(kindOfWork(3002)).toBe(category('Fishing'));
    expect(kindOfWork(5007)).toBe(category('Crafting'));
    expect(kindOfWork(9999)).toBe(category('WorldBoss'));
  });

  it('collides on the kind, not the id', () => {
    expect(isSameKindOfWork(91, 95)).toBe(true);
    expect(isSameKindOfWork(2001, 2004)).toBe(true);
    expect(isSameKindOfWork(91, 1001)).toBe(false);
    expect(isSameKindOfWork(0, 0)).toBe(false);
  });

  it('names who already does that kind of work', () => {
    const workers = [worker(1, 1001), worker(2, 91), worker(3, 0)];
    expect(kindTakenBy(workers, 3, 95)?.slot).toBe(2);
    expect(kindTakenBy(workers, 3, 1004)?.slot).toBe(1);
    expect(kindTakenBy(workers, 3, 2001)).toBeNull();
    // The fighter may switch monsters.
    expect(kindTakenBy(workers, 2, 95)).toBeNull();
  });

  it('holds a drop against the fighter, not slot 1', () => {
    const workers = [worker(1, 5003), worker(2, 0), worker(3, 92)];
    expect(lootOwner(workers, 2)?.slot).toBe(3);
    expect(lootOwner([worker(1, 1001), worker(2, 0)], 2)?.slot).toBe(2);
  });

  it('reads each slot from its own fields', () => {
    const snap = {
      TownHallLevel: 5,
      Slot1_CharacterId: 'a', Slot2_CharacterId: 'b', Slot3_CharacterId: 'c',
      Slot1_RaceId: 1, Slot2_RaceId: 1, Slot3_RaceId: 1,
      ActiveActivityId: 1001, Slot2ActivityId: 0, Slot3ActivityId: 91,
      ActivityHaltReason: 0, Slot2ActivityHaltReason: 0, Slot3ActivityHaltReason: 0,
      PlayerHp: 10, PlayerMaxHp: 20, GatheringProgressTicks: 3, RequiredProgressTicks: 9,
      Slot3PlayerHp: 400, Slot3PlayerMaxHp: 900, Slot3MonsterId: 91, Slot3MonsterHp: 50, Slot3MonsterMaxHp: 60,
      Slot3WorkProgressTicks: 0, Slot3WorkRequiredTicks: 0,
    };
    expect(slotBars(snap, 1)).toMatchObject({ playerHp: 10, playerMaxHp: 20, progressTicks: 3, requiredTicks: 9 });
    expect(slotBars(snap, 3)).toMatchObject({ playerHp: 400, playerMaxHp: 900, monsterId: 91, monsterHp: 50, monsterMaxHp: 60 });
  });
});

// 2026-10-08: the rebirth confirmation word, read from RebirthRules.cs so the
// panel and the endpoint cannot ask for different words.
describe('rebirth confirmation word', () => {
  it('is the server word', () => {
    const rules = readFileSync(join(here, '..', '..', 'server', 'FolkIdle.Server', 'Engine', 'RebirthRules.cs'), 'utf8');
    const match = rules.match(/ConfirmationWord = "([A-Z]+)"/);
    expect(match?.[1]).toBe(REBIRTH_CONFIRMATION_WORD);
  });
});
