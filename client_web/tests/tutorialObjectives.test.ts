import { describe, it, expect } from 'vitest';
import {
  ALL_OBJECTIVES,
  nextObjective,
  type ObjectiveId,
} from '../src/lib/stores/tutorialObjectives';
import { NO_FACTS } from '../src/lib/stores/tutorialDiscoveries';
import { DISCOVERY_IDS } from '../src/lib/stores/tutorialDiscoveries';
import { SCREEN_KEYS } from '../src/lib/ui/screens';

/*
  TIER THREE - "what should I do now", which is the question neither working
  tier answers.

  Tier one stops after three steps, about ten minutes in. Tier two is REACTIVE:
  it explains a system the first time the player reaches it, and can say nothing
  at all about one they have not found. The gap between them is the reported
  problem, and this table is the mirror image of tier two - it fires when a
  player is READY for something and has not done it.

  Every rule gets both halves here: it must fire on the packet that should
  trigger it, and it must NOT fire on one that should not. A predicate that is
  never false is a panel that never goes away.
*/

/** The fields the objectives read, with everything else left at a fresh account's value. */
function snapshot(fields: Record<string, number>): any {
  return {
    CurrentLevel: 1,
    UnspentAttributePoints: 0,
    AvailableSkillPoints: 0,
    HighestUnlockedRegion: 1,
    Gold: 0,
    ForgeLevel: 0,
    EquippedWeaponId: 0,
    TownHallLevel: 0,
    LumberjackLevel: 0,
    InventoryCapacity: 100,
    InventorySpaceRemaining: 100,
    ...fields,
  };
}

const NONE: ReadonlySet<string> = new Set<string>();

function idsDueOn(fields: Record<string, number>, facts = NO_FACTS): ObjectiveId[] {
  const due: ObjectiveId[] = [];
  const seen = new Set<string>();
  for (;;) {
    const objective = nextObjective(snapshot(fields), facts, seen);
    if (!objective) return due;
    due.push(objective.id);
    seen.add(objective.id);
  }
}

describe('the objective track', () => {
  it('says nothing before the first packet arrives', () => {
    expect(nextObjective(null, NO_FACTS, NONE)).toBeNull();
  });

  it('asks nothing of a brand-new account, because tier one owns that player', () => {
    // Modul: a fresh account must fall through this table entirely. Tier one is
    // three ordered instructions and the entrance was closed once already by
    // asking a new player for something they could not do; tier three arriving
    // over the top of it would be the same defect wearing a different panel.
    expect(idsDueOn({})).toEqual([]);
  });

  it('puts unplaced attribute points first, because that is power already owned', () => {
    const objective = nextObjective(snapshot({ UnspentAttributePoints: 7 }), NO_FACTS, NONE)!;
    expect(objective.id).toBe('place_attribute_points');
    expect(objective.screen).toBe('character');
  });

  it('ignores a single stray attribute point', () => {
    // A level pays seven. One point is not worth a panel.
    expect(idsDueOn({ UnspentAttributePoints: 1 })).not.toContain('place_attribute_points');
    expect(idsDueOn({ UnspentAttributePoints: 7 })).toContain('place_attribute_points');
  });

  it('names the region boss once the player is strong enough and has not passed it', () => {
    expect(idsDueOn({ CurrentLevel: 8 })).toContain('first_region_boss');
    // Already through: never mentioned again.
    expect(idsDueOn({ CurrentLevel: 8, HighestUnlockedRegion: 2 })).not.toContain('first_region_boss');
    // Not ready yet.
    expect(idsDueOn({ CurrentLevel: 3 })).not.toContain('first_region_boss');
  });

  it('offers the Delve only once there is real gold to burn', () => {
    // Modul: THE DELVE HAD NOTHING TEACHING IT. It shipped as task 11 with no
    // tier-two moment, because tier two fires on reaching a system and there is
    // no way to "reach" a screen nobody has told you about. This is the entry
    // that closes that, and it is the clearest example of what tier three is
    // for.
    expect(idsDueOn({ Gold: 1000 })).not.toContain('try_the_delve');
    expect(idsDueOn({ Gold: 25_000 })).toContain('try_the_delve');
  });

  it('mentions the guild only to someone who is not in one', () => {
    expect(idsDueOn({ CurrentLevel: 10 }, { hasGuild: false })).toContain('join_a_guild');
    expect(idsDueOn({ CurrentLevel: 10 }, { hasGuild: true })).not.toContain('join_a_guild');
  });

  it('raises the market when the bags are nearly full, not when they are empty', () => {
    expect(idsDueOn({ InventorySpaceRemaining: 100 })).not.toContain('sell_on_the_market');
    expect(idsDueOn({ InventorySpaceRemaining: 4 })).toContain('sell_on_the_market');
  });

  it('names the Town Hall only when it is the thing in the way', () => {
    // Nothing built yet - not the Hall's fault.
    expect(idsDueOn({ TownHallLevel: 0, LumberjackLevel: 0 })).not.toContain('raise_the_town_hall');
    // A building pinned at the ceiling: now it is.
    expect(idsDueOn({ TownHallLevel: 2, LumberjackLevel: 2 })).toContain('raise_the_town_hall');
    // Room left below the ceiling.
    expect(idsDueOn({ TownHallLevel: 5, LumberjackLevel: 2 })).not.toContain('raise_the_town_hall');
  });

  it('mentions the Forge only once one exists and there is something to reroll', () => {
    expect(idsDueOn({ ForgeLevel: 1 })).not.toContain('reroll_an_affix');
    expect(idsDueOn({ ForgeLevel: 1, EquippedWeaponId: 12 })).toContain('reroll_an_affix');
  });

  it('hands over the next objective once one is dismissed', () => {
    const fields = { UnspentAttributePoints: 7, AvailableSkillPoints: 2 };
    const first = nextObjective(snapshot(fields), NO_FACTS, NONE)!;
    expect(first.id).toBe('place_attribute_points');

    const second = nextObjective(snapshot(fields), NO_FACTS, new Set([first.id]))!;
    expect(second.id).toBe('spend_skill_points');
  });

  it('goes quiet once everything due has been acknowledged', () => {
    const fields = { UnspentAttributePoints: 7, AvailableSkillPoints: 2, CurrentLevel: 12, Gold: 50_000 };
    const seen = new Set<string>(idsDueOn(fields));
    expect(nextObjective(snapshot(fields), NO_FACTS, seen)).toBeNull();
  });
});

describe('the objective table itself', () => {
  it('points every objective at a screen that exists', () => {
    // Modul: a nav key that does not exist sends the player nowhere and
    // announces nothing - the same class as the screen list that went stale in
    // three separate checkers. SCREEN_KEYS is the authority.
    for (const objective of ALL_OBJECTIVES) {
      expect(SCREEN_KEYS).toContain(objective.screen as (typeof SCREEN_KEYS)[number]);
    }
  });

  it('gives every objective a distinct id', () => {
    const ids = ALL_OBJECTIVES.map((o) => o.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('never reuses a discovery id', () => {
    // Modul: BOTH TIERS SHARE ONE SEEN-SET, keyed by id in localStorage. A
    // collision would mean dismissing one silently dismissed the other, which
    // is invisible until a player complains that a panel never appeared.
    for (const objective of ALL_OBJECTIVES) {
      expect(DISCOVERY_IDS).not.toContain(objective.id as never);
    }
  });

  it('says something in every objective, and says why it matters', () => {
    for (const objective of ALL_OBJECTIVES) {
      expect(objective.title.length).toBeGreaterThan(8);
      // The risk with a permanent track is nagging, and the cure is that each
      // entry explains the payoff rather than just naming a screen.
      expect(objective.body.length).toBeGreaterThan(60);
      expect(objective.system.length).toBeGreaterThan(0);
    }
  });
});
