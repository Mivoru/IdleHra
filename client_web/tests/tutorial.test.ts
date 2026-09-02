import { describe, it, expect, beforeEach, vi } from 'vitest';
import { nextTutorialStep, TutorialStep } from '../src/lib/stores/tutorialSteps';
import {
  DISCOVERY_IDS,
  DISCOVERY_MOMENTS,
  NO_FACTS,
  findDiscovery,
  nextDiscovery,
  reachedDiscoveries,
  type DiscoveryId,
} from '../src/lib/stores/tutorialDiscoveries';

// Modul: this file used to hold PARITY tests against TutorialStateMachine.cs,
// which the server's csproj links out of client/Assets/Scripts/Engine and
// tests with xUnit. That machine belongs to the UNITY client, which is
// abandoned - so the parity being defended was with a program nobody runs, and
// the port it was defending is gone.
//
// The C# original and its server-side test are left in place: deleting Unity
// code is a separate decision from replacing the web client's onboarding, and
// it is not one to make quietly inside a tutorial change.
//
// What is tested here instead is the thing that now decides what a player is
// told: given a state packet, which step is outstanding and which system has
// just been reached. Both are pure functions of the snapshot, which is exactly
// why they were worth writing this way - no browser, no socket, no store.

/** The fields the tutorial reads, with everything else left off. */
function snapshot(fields: Record<string, number>): any {
  return {
    CurrentLevel: 1,
    EquippedWeaponId: 0,
    Food1_Count: 0,
    Food2_Count: 0,
    Food3_Count: 0,
    ...fields,
  };
}

describe('what a new player is told next', () => {
  it('says nothing before the first packet arrives', () => {
    expect(nextTutorialStep(null)).toBeNull();
  });

  // Modul: THE LARDER FIRST, and this is the regression test for a closed
  // entrance rather than a preference about wording.
  //
  // The fight used to be step one. Measured on a brand-new account against the
  // live server, a character with an empty larder dies to Field Mouse at 29
  // seconds with the monster still on 264 of its 465 HP - so step one could
  // not be completed, and because the steps block each other in order the
  // player never reached the food advice that was sitting in step three.
  //
  // If this test is ever "fixed" by putting combat back in front, read
  // tutorialSteps.ts first: the balance is correct given food, and the food is
  // what the order exists to deliver.
  it('starts with the larder, because the first fight cannot be won without it', () => {
    const prompt = nextTutorialStep(snapshot({}))!;
    expect(prompt.step).toBe(TutorialStep.StockTheLarder);
    expect(prompt.screen).toBe('larder');
    expect(prompt.index).toBe(1);
  });

  it('moves to the fight once there is food to fight on', () => {
    const prompt = nextTutorialStep(snapshot({ Food1_Count: 30 }))!;
    expect(prompt.step).toBe(TutorialStep.WinAFight);
    expect(prompt.screen).toBe('combat');
  });

  it('moves to gear once a level has been earned', () => {
    expect(nextTutorialStep(snapshot({ Food1_Count: 30, CurrentLevel: 2 }))!.step).toBe(
      TutorialStep.EquipADrop,
    );
  });

  it('falls silent when all three are done', () => {
    expect(
      nextTutorialStep(snapshot({ CurrentLevel: 2, EquippedWeaponId: 41, Food1_Count: 30 })),
    ).toBeNull();
  });

  // Modul: THE POINT OF READING STATE RATHER THAN EVENTS. A player who did the
  // first two things in a closed tab - or before any of this shipped - is
  // shown the step they are actually on. The event-driven version had to be
  // watching at the moment each thing happened, and a missed moment was
  // missed for good.
  it('skips ahead for a player who arrives having already done the work', () => {
    expect(nextTutorialStep(snapshot({ CurrentLevel: 40, EquippedWeaponId: 7 }))!.step).toBe(
      TutorialStep.StockTheLarder,
    );
  });

  // Modul: dismissal is NOT tested here. It lives in the store, behind
  // localStorage, and reaching it would drag the whole state store into a node
  // test runner - which is the coupling this split exists to remove.
});

// ---------------------------------------------------------------------------
// Tier two - the discovery moments
// ---------------------------------------------------------------------------

/**
 * A packet with every field a discovery predicate reads set to a value that
 * fires NOTHING. That is the control: each test below turns exactly one thing
 * on and asserts exactly one moment lights up.
 *
 * Modul: InventoryCapacity is 20 rather than 0 on purpose. The backpack
 * predicate has to survive a packet whose inventory fields have not been
 * populated yet - "capacity zero, space remaining zero" is what an empty
 * struct looks like, and reading it as "your backpack is full" would greet a
 * brand-new player with a warning about a backpack they have not used.
 */
function blank(fields: Record<string, number> = {}): any {
  return {
    // gathering
    WoodcuttingMasteryXp: 0,
    MiningMasteryXp: 0,
    FishingMasteryXp: 0,
    HerbalismMasteryXp: 0,
    // inventory
    InventoryCapacity: 20,
    InventorySpaceRemaining: 20,
    // crafting / tools
    TotalItemsCraftedCount: 0,
    AxeToolTier: 0,
    PickaxeToolTier: 0,
    RodToolTier: 0,
    // progression
    AvailableSkillPoints: 0,
    HighestUnlockedRegion: 1,
    Gold: 0,
    PremiumCurrencyBalance: 0,
    AchievementTierTotal: 0,
    // village
    VillagePopulation: 0,
    CurrentPopulationCount: 0,
    TownHallLevel: 0,
    ForgeLevel: 0,
    InnLevel: 0,
    BreedingLevel: 0,
    LumberjackLevel: 0,
    MineLevel: 0,
    WarehouseLevel: 0,
    // characters
    ActiveChildMaturationMs: 0,
    Slot1_AgePhase: 0,
    Slot2_AgePhase: 0,
    Slot3_AgePhase: 0,
    // world
    WorldBossEventState: 0,
    ...fields,
  };
}

/** Every moment, the packet that should fire it, and the fact set it needs. */
const TRIGGERS: readonly { id: DiscoveryId; fields: Record<string, number>; hasGuild?: boolean }[] =
  [
    { id: 'gathering', fields: { FishingMasteryXp: 1 } },
    { id: 'backpack_full', fields: { InventorySpaceRemaining: 0 } },
    { id: 'crafting', fields: { TotalItemsCraftedCount: 1 } },
    { id: 'tools', fields: { PickaxeToolTier: 1 } },
    { id: 'skills', fields: { AvailableSkillPoints: 1 } },
    { id: 'village', fields: { VillagePopulation: 1 } },
    { id: 'region2', fields: { HighestUnlockedRegion: 2 } },
    { id: 'market', fields: { Gold: 5000 } },
    // Modul: ForgeLevel 1 with TownHallLevel 0 gives a ceiling of 2, which the
    // forge has NOT reached - so this fires 'forge' alone and not 'town_hall'.
    { id: 'forge', fields: { ForgeLevel: 1 } },
    { id: 'town_hall', fields: { InnLevel: 2 } },
    { id: 'guild', fields: {}, hasGuild: true },
    { id: 'breeding', fields: { BreedingLevel: 1 } },
    { id: 'first_child', fields: { ActiveChildMaturationMs: 1 } },
    { id: 'world_boss', fields: { WorldBossEventState: 1 } },
    { id: 'deeds', fields: { AchievementTierTotal: 1 } },
    { id: 'ancestors', fields: { Slot2_AgePhase: 2 } },
    { id: 'inheritance', fields: { PremiumCurrencyBalance: 40 } },
  ];

describe('discovery moments: each fires on its own trigger and nothing else', () => {
  it('a blank packet reaches nothing at all', () => {
    expect(reachedDiscoveries(blank(), NO_FACTS)).toEqual([]);
    expect(nextDiscovery(blank(), NO_FACTS, new Set())).toBeNull();
  });

  it('says nothing before the first packet arrives', () => {
    expect(nextDiscovery(null, NO_FACTS, new Set())).toBeNull();
    expect(reachedDiscoveries(null)).toEqual([]);
  });

  // Modul: the table is checked for COMPLETENESS, not just for the entries
  // somebody remembered to test. A moment added without a trigger row fails
  // here rather than shipping untested.
  it('every moment in the table has a trigger under test', () => {
    expect(TRIGGERS.map((t) => t.id).sort()).toEqual([...DISCOVERY_IDS].sort());
  });

  for (const trigger of TRIGGERS) {
    it(`${trigger.id} fires on its own trigger, and only it`, () => {
      const facts = { hasGuild: trigger.hasGuild ?? false };
      const reached = reachedDiscoveries(blank(trigger.fields), facts);
      expect(reached).toEqual([trigger.id]);
    });

    it(`${trigger.id} does not fire on a packet that has not reached it`, () => {
      const reached = reachedDiscoveries(blank(), { hasGuild: false });
      expect(reached).not.toContain(trigger.id);
    });
  }
});

describe('the Town Hall ceiling, which is the least obvious rule in the game', () => {
  // 2 + TownHallLevel * 2, mirrored from VillageManagementEngine.
  it('does not fire while a building is still below the ceiling', () => {
    expect(reachedDiscoveries(blank({ InnLevel: 1, TownHallLevel: 0 }), NO_FACTS)).not.toContain(
      'town_hall',
    );
  });

  it('fires the moment a building reaches it', () => {
    expect(reachedDiscoveries(blank({ InnLevel: 2, TownHallLevel: 0 }), NO_FACTS)).toContain(
      'town_hall',
    );
  });

  it('stops firing again once the Town Hall is raised', () => {
    expect(reachedDiscoveries(blank({ InnLevel: 2, TownHallLevel: 1 }), NO_FACTS)).not.toContain(
      'town_hall',
    );
  });

  // Modul: the ceiling caps the SERVICE and PRODUCTION buildings, not the two
  // structural ones. A Crafting Workshop at level 4 is legal on a level 0 Town
  // Hall, and counting it would fire this on a village that is working fine.
  it('ignores the structural buildings, which the ceiling does not cap', () => {
    expect(
      reachedDiscoveries(blank({ CraftingWorkshopLevel: 4, TownHallLevel: 0 }), NO_FACTS),
    ).not.toContain('town_hall');
  });
});

describe('choosing which one to show', () => {
  it('hands over the earliest reached moment that has not been seen', () => {
    // Reached: gathering (first in the table) and market (much later).
    const packet = blank({ FishingMasteryXp: 10, Gold: 9000 });
    expect(nextDiscovery(packet, NO_FACTS, new Set())!.id).toBe('gathering');
  });

  it('moves on to the next once one has been acknowledged', () => {
    const packet = blank({ FishingMasteryXp: 10, Gold: 9000 });
    expect(nextDiscovery(packet, NO_FACTS, new Set(['gathering']))!.id).toBe('market');
  });

  it('falls silent once everything reached has been seen', () => {
    const packet = blank({ FishingMasteryXp: 10, Gold: 9000 });
    const seen = new Set(['gathering', 'market']);
    expect(nextDiscovery(packet, NO_FACTS, seen)).toBeNull();
  });

  // Modul: THE SAME SELF-HEALING PROPERTY AS TIER ONE. The predicate is
  // re-evaluated on every packet, so a system reached while the tab was closed
  // is still explained when the player comes back. Nothing here depends on
  // having watched the transition happen.
  it('explains a system reached while the client was not running', () => {
    const away = blank({ HighestUnlockedRegion: 4, ForgeLevel: 3, TownHallLevel: 2 });
    expect(nextDiscovery(away, NO_FACTS, new Set())!.id).toBe('region2');
  });

  it('never offers a moment the player has not reached, however long the seen-set is', () => {
    expect(nextDiscovery(blank(), NO_FACTS, new Set())).toBeNull();
  });
});

describe('the table itself', () => {
  it('carries no predicate into the exported list', () => {
    for (const moment of DISCOVERY_MOMENTS) {
      expect(Object.keys(moment).sort()).toEqual(['body', 'id', 'screen', 'system', 'title']);
    }
  });

  it('has a unique id per moment', () => {
    expect(new Set(DISCOVERY_IDS).size).toBe(DISCOVERY_IDS.length);
  });

  // Modul: every moment has to be able to POINT SOMEWHERE. A screen key that
  // does not exist in the nav is a "Take me there" button that does nothing,
  // which is this repo's dominant defect class in miniature.
  it('points every moment at a real screen', () => {
    const screens = new Set([
      'hub', 'combat', 'gathering', 'worldboss', 'boosts', 'character', 'chest', 'larder',
      'crafting', 'forge', 'market', 'social', 'guildops', 'mailbox', 'leaderboards',
      'breeding', 'ancestors', 'inheritance', 'village', 'skills', 'progression', 'codex',
      'store', 'settings', 'wiki',
    ]);
    for (const moment of DISCOVERY_MOMENTS) {
      expect(screens.has(moment.screen), `${moment.id} -> ${moment.screen}`).toBe(true);
    }
  });

  it('can be looked up by id, and refuses an unknown one', () => {
    expect(findDiscovery('forge')?.system).toBe('Forge');
    expect(findDiscovery('not_a_moment')).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Seen-state: survives a reload, survives a season, does not bury a veteran
// ---------------------------------------------------------------------------

/** A localStorage good enough for the three things the module does with it. */
function installStorage(): Map<string, string> {
  const backing = new Map<string, string>();
  (globalThis as any).localStorage = {
    getItem: (k: string) => (backing.has(k) ? backing.get(k)! : null),
    setItem: (k: string, v: string) => void backing.set(k, v),
    removeItem: (k: string) => void backing.delete(k),
  };
  return backing;
}

async function freshSeenModule() {
  // A fresh module instance per call: the module holds the active account and
  // its seen-set for the lifetime of a page, which is correct in a browser and
  // would leak between tests here. resetModules is also how a RELOAD is
  // simulated below - same storage, brand-new module state.
  vi.resetModules();
  return await import('../src/lib/stores/tutorialSeen');
}

describe('which explanations have been shown', () => {
  let backing: Map<string, string>;

  beforeEach(() => {
    backing = installStorage();
  });

  it('reports a first-ever adoption, and only the first time', async () => {
    const seen = await freshSeenModule();
    expect(seen.adoptPlayer(1234)).toBe(true);
    expect(seen.adoptPlayer(1234)).toBe(false);
  });

  it('persists a mark under the player id and reads it back', async () => {
    const first = await freshSeenModule();
    first.adoptPlayer(1234);
    first.markAllSeen([]);
    first.markSeen('forge');

    // A reload: storage already holds the key, so this is not a first-ever
    // adoption and nothing is baselined over the top of it.
    const reload = await freshSeenModule();
    expect(reload.adoptPlayer(1234)).toBe(false);
    expect(backing.has('folkidle.onboardingSeen.1234')).toBe(true);
    expect(JSON.parse(backing.get('folkidle.onboardingSeen.1234')!)).toContain('forge');
  });

  // Modul: THE BASELINE WRITE HAPPENS EVEN WHEN IT IS EMPTY. Without it the
  // storage key stays absent, the next reload baselines a SECOND time, and
  // everything the player reached in between is silently marked as already
  // explained. This is the assertion that pins that.
  it('writes an empty baseline so it cannot happen twice', async () => {
    const seen = await freshSeenModule();
    expect(seen.adoptPlayer(77)).toBe(true);
    seen.markAllSeen([]);
    expect(backing.get('folkidle.onboardingSeen.77')).toBe('[]');

    const reload = await freshSeenModule();
    expect(reload.adoptPlayer(77)).toBe(false);
  });

  it('keeps two accounts on one browser apart', async () => {
    const seen = await freshSeenModule();
    seen.adoptPlayer(1);
    seen.markSeen('forge');
    expect(seen.adoptPlayer(2)).toBe(true);
    seen.markAllSeen([]);
    expect(JSON.parse(backing.get('folkidle.onboardingSeen.2')!)).toEqual([]);
    expect(JSON.parse(backing.get('folkidle.onboardingSeen.1')!)).toEqual(['forge']);
  });

  it('forgets one, and forgets all', async () => {
    const seen = await freshSeenModule();
    seen.adoptPlayer(9);
    seen.markAllSeen(['forge', 'market', 'deeds']);
    seen.forgetSeen('market');
    expect(JSON.parse(backing.get('folkidle.onboardingSeen.9')!).sort()).toEqual([
      'deeds',
      'forge',
    ]);
    seen.forgetAllSeen();
    expect(JSON.parse(backing.get('folkidle.onboardingSeen.9')!)).toEqual([]);
  });

  it('survives a browser that refuses storage entirely', async () => {
    (globalThis as any).localStorage = {
      getItem() {
        throw new Error('denied');
      },
      setItem() {
        throw new Error('denied');
      },
      removeItem() {
        throw new Error('denied');
      },
    };
    const seen = await freshSeenModule();
    // Reads as "nothing stored", which re-teaches at worst - the harmless
    // direction to fail.
    expect(seen.adoptPlayer(5)).toBe(true);
    expect(() => seen.markSeen('forge')).not.toThrow();
  });
});

describe('a veteran is not buried, and a season reset does not re-teach', () => {
  beforeEach(() => {
    installStorage();
  });

  // Modul: the naive rule - "true and unseen" with no baseline - queues every
  // moment at once for a player who has been at this for weeks and then clears
  // their browser. Fifteen explanations in a row is worse than none.
  it('baselines everything an established account has already passed', async () => {
    const seen = await freshSeenModule();
    const veteran = blank({
      FishingMasteryXp: 90_000,
      TotalItemsCraftedCount: 400,
      AxeToolTier: 4,
      HighestUnlockedRegion: 5,
      Gold: 2_000_000,
      ForgeLevel: 5,
      InnLevel: 6,
      TownHallLevel: 3,
      AchievementTierTotal: 30,
    });
    expect(seen.adoptPlayer(4242)).toBe(true);
    seen.markAllSeen(reachedDiscoveries(veteran, { hasGuild: true }));

    // Nothing left to say about anything they have already done...
    const stored: Set<string> = new Set(
      JSON.parse((globalThis as any).localStorage.getItem('folkidle.onboardingSeen.4242')),
    );
    expect(nextDiscovery(veteran, { hasGuild: true }, stored)).toBeNull();

    // ...but a system they have NOT reached is still explained when they do.
    const later = { ...veteran, ActiveChildMaturationMs: 5000 };
    expect(nextDiscovery(later, { hasGuild: true }, stored)!.id).toBe('first_child');
  });

  // Modul: THE SEASON RESET. Levels, gear, gold and the village all go back to
  // nothing, so every predicate goes false and then true again. The seen-set
  // is attached to the ACCOUNT rather than to the season, so nothing fires
  // twice - which is the whole reason it stores "seen" and not "progress".
  it('says nothing a second time when a season resets the world', async () => {
    const seen = await freshSeenModule();
    seen.adoptPlayer(808);
    seen.markAllSeen([]);

    const seasonOne = blank({ HighestUnlockedRegion: 3, ForgeLevel: 2, Gold: 40_000 });
    let stored = new Set<string>();
    let cue = nextDiscovery(seasonOne, NO_FACTS, stored);
    while (cue) {
      seen.markSeen(cue.id);
      stored.add(cue.id);
      cue = nextDiscovery(seasonOne, NO_FACTS, stored);
    }
    expect(stored.size).toBeGreaterThan(0);

    // The rollover: back to region 1, no buildings, no gold.
    const seasonTwo = blank();
    expect(nextDiscovery(seasonTwo, NO_FACTS, stored)).toBeNull();

    // And climbing back up teaches none of it again.
    expect(nextDiscovery(seasonOne, NO_FACTS, stored)).toBeNull();
  });
});
