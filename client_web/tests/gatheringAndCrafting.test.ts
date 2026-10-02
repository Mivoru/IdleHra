import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { nodeYieldBaseId, nodeRareBaseId, masteryXpForLevel } from '../src/lib/ui/gatheringNodes';
import { workersOf, workerName, describeJob, type WorkerSnapshot } from '../src/lib/ui/workers';
import { recipeGroup, toolEffect, firstStepLine, affordableUnits } from '../src/lib/ui/craftingCards';
import { EMPTY_GUID } from '../src/lib/net/commands';
import type { CraftingRecipe } from '../src/lib/net/rest';

const here = dirname(fileURLToPath(import.meta.url));
const serverRoot = join(here, '..', '..', 'server', 'FolkIdle.Server');

describe('what a gathering node yields (task 101)', () => {
  // Modul: the client is never sent a loot table, so the node -> material
  // names are a mirror. ContentRegistry comments every gathering row as
  // "index N: <base id> - <Place> (<Profession>, common|rare)" and the
  // segment table maps node N001-N005 to rows 77-106 in that order. Read both
  // and compare every node.
  it('matches the server loot table for all fifteen nodes', () => {
    const source = readFileSync(join(serverRoot, 'Engine', 'ContentRegistry.cs'), 'utf8');
    const rows = new Map<number, string>();
    for (const m of source.matchAll(/index (\d+): ([a-z_]+) - [A-Za-z ]+ \((Woodcutting|Mining|Fishing), (common|rare)\)/g)) {
      rows.set(Number(m[1]), m[2]);
    }
    const segments = new Map<number, number>();
    for (const m of source.matchAll(/\{ ([123]00[1-5]), \((\d+), 2\) \}/g)) {
      segments.set(Number(m[1]), Number(m[2]));
    }
    expect(segments.size).toBe(15);

    for (const [node, first] of segments) {
      expect(nodeYieldBaseId(node), `node ${node} common`).toBe(rows.get(first));
      expect(nodeRareBaseId(node), `node ${node} rare`).toBe(rows.get(first + 1));
    }
  });

  it('mastery needs 50 * (level + 1)^2 xp, as SimulationEngine.MasteryXpForLevel', () => {
    const source = readFileSync(join(serverRoot, 'Domain', 'Combat', 'SimulationEngine.cs'), 'utf8');
    expect(source).toMatch(/return 50L \* \(level \+ 1L\) \* \(level \+ 1L\);/);
    expect(masteryXpForLevel(0)).toBe(50);
    expect(masteryXpForLevel(9)).toBe(5000);
  });
});

function snapshot(overrides: Partial<WorkerSnapshot> = {}): WorkerSnapshot {
  return {
    TownHallLevel: 5,
    Slot1_CharacterId: 'a',
    Slot2_CharacterId: 'b',
    Slot3_CharacterId: EMPTY_GUID,
    Slot1_RaceId: 1,
    Slot2_RaceId: 2,
    Slot3_RaceId: 0,
    ActiveActivityId: 2001,
    Slot2ActivityId: 0,
    Slot3ActivityId: 0,
    ActivityHaltReason: 0,
    Slot2ActivityHaltReason: 1,
    Slot3ActivityHaltReason: 0,
    ...overrides,
  };
}

describe('workers', () => {
  it('lists unlocked, occupied slots only', () => {
    expect(workersOf(snapshot()).map((w) => w.slot)).toEqual([1, 2]);
    // Slot 2 opens at Town Hall 3.
    expect(workersOf(snapshot({ TownHallLevel: 1 })).map((w) => w.slot)).toEqual([1]);
    expect(workersOf(null)).toEqual([]);
  });

  it('names a person, then their race, then their slot', () => {
    const [one, two] = workersOf(snapshot());
    expect(workerName(one, new Map([['a', 'Aila']]))).toBe('Aila');
    expect(workerName(two, new Map())).not.toBe('');
    expect(workerName({ ...two, raceId: 0 }, new Map())).toBe('Slot 2');
  });

  it('says what a job IS, not only where', () => {
    expect(describeJob(2001, 0)).toBe('Mining Copper Ore - Sunlit Plains');
    expect(describeJob(3002, 0)).toBe('Fishing Moss Bass - Whispering Woods');
    expect(describeJob(0, 1)).toBe('Idle - Out of food');
    expect(describeJob(0, 0)).toBe('Idle');
    expect(describeJob(95, 0, { monsterName: () => 'Wolf' })).toBe('Fighting Wolf');
    expect(describeJob(5003, 0, { recipeName: () => 'Birch Axe' })).toBe('Crafting Birch Axe');
  });
});

function recipe(overrides: Partial<CraftingRecipe> = {}): CraftingRecipe {
  return {
    ResultItemId: 1,
    ResultBaseItemId: 'birch_axe_tool',
    ProfessionType: 5,
    RequiredLevel: 1,
    CraftingTimeMs: 3000,
    Mat1Id: 10,
    Mat1BaseItemId: 'birch_log',
    Mat1Count: 20,
    Mat1CurrentStock: 0,
    Mat2Id: 11,
    Mat2BaseItemId: 'copper_ore',
    Mat2Count: 10,
    Mat2CurrentStock: 0,
    ...overrides,
  };
}

describe('crafting cards', () => {
  it('sorts locked apart from missing materials', () => {
    expect(recipeGroup(recipe(), 1)).toBe('missing');
    expect(recipeGroup(recipe({ RequiredLevel: 5 }), 1)).toBe('locked');
    expect(recipeGroup(recipe({ Mat1CurrentStock: 40, Mat2CurrentStock: 10 }), 1)).toBe('ready');
    expect(affordableUnits(recipe({ Mat1CurrentStock: 40, Mat2CurrentStock: 25 }), 10)).toBe(2);
  });

  it('names the slot and the effect of a tool, longest wood first', () => {
    expect(toolEffect('birch_axe_tool')).toEqual({ slot: 'Axe', effect: '+35% woodcutting speed' });
    expect(toolEffect('golden_birch_pickaxe_tool')).toEqual({ slot: 'Pickaxe', effect: '+82% mining speed' });
    expect(toolEffect('whisper_willow_fishing_rod_tool')?.slot).toBe('Rod');
    expect(toolEffect('whisper_willow_fishing_rod_tool')?.effect).toBe('+232% fishing speed');
    expect(toolEffect('eq_iron_sword_melee_weapon_slot_base')).toBeNull();
  });

  it('tells an empty-handed player what the first tools need', () => {
    expect(firstStepLine([recipe({ RequiredLevel: 3 }), recipe()], 1)).toBe(
      'Nothing craftable yet - the first tools need Birch Log and Copper Ore.',
    );
  });
});
