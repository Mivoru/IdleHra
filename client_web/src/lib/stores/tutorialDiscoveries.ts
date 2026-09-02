// Modul: TIER TWO - the discovery moments, and nothing else in the file.
//
// Tier one (tutorialSteps.ts) gives a new player three INSTRUCTIONS: do this,
// then this, then this. It stops after three, and the task board's audit of
// what teaches what came back with a long list of nothing: gathering,
// crafting, tools and their slots, the village and the Town Hall ceiling,
// region unlocking via bosses, the forge, affix rerolls, the market, guilds
// and buffs, breeding, the skill tree, deeds and Seals, the Hall of Ancestors,
// inheritance, the world boss.
//
// These are EXPLANATIONS rather than instructions. Each one fires once, the
// first time the player actually reaches the system, and says what it is for.
// The player is never blocked and never has to acknowledge one.
//
// The rule tier one got right is kept verbatim: a moment is a PREDICATE OVER
// THE STATE PACKET. That is what makes it self-healing - a player who unlocked
// region 2 in a closed tab is still told about region 2, because the check is
// "is this true now", not "did I see it happen". It is also why this file
// imports no stores, touches no browser API and needs no network: the whole
// table is testable in a node runner.
//
// The full step list, the reasoning behind each predicate, and the four
// triggers the packet CANNOT express are in docs/onboarding_steps.md.
import type { StateUpdate } from '../net/protocol.generated';

/**
 * The facts a moment needs that are NOT on the state packet.
 *
 * Modul: exactly one of these exists, and it exists under protest. There is no
 * guild id on StateUpdate at all - GuildCombatVanguardPoints and its siblings
 * are written only during a war, and GuildLogisticsLevel only by a depot
 * notification, so every guild field on the wire reads zero for a member who
 * joined five minutes ago. Adding one would cost a layout-guard change on a
 * packet already near its 800-byte ceiling, to carry a boolean the statistics
 * endpoint already answers and GuildOps.svelte already asks it for.
 *
 * Passed IN rather than fetched here so the predicates stay pure.
 */
export interface OnboardingFacts {
  /** fetchStatistics().GuildName !== '' - see above. */
  hasGuild: boolean;
}

export const NO_FACTS: OnboardingFacts = { hasGuild: false };

export type DiscoveryId =
  | 'gathering'
  | 'backpack_full'
  | 'crafting'
  | 'tools'
  | 'skills'
  | 'village'
  | 'region2'
  | 'market'
  | 'forge'
  | 'town_hall'
  | 'guild'
  | 'breeding'
  | 'first_child'
  | 'world_boss'
  | 'deeds'
  | 'ancestors'
  | 'inheritance';

export interface DiscoveryMoment {
  id: DiscoveryId;
  /** The system in one word, for the Settings list. */
  system: string;
  /** A nav key from lib/ui/screens.ts - where the real control lives. */
  screen: string;
  title: string;
  body: string;
}

interface DiscoveryRule extends DiscoveryMoment {
  /** True once the player has reached this system. */
  reached: (s: StateUpdate, facts: OnboardingFacts) => boolean;
}

/** WorldBossEventState. Mirrors BossEventState in net/commands.ts. */
const BOSS_ACTIVE = 1;

/** AGE_PHASES in ui/slots.ts is Child / Adult / Veteran / Elder. */
const AGE_PHASE_VETERAN = 2;

/** inheritanceUpgradeCost(0) - the cheapest thing that screen can sell. */
const CHEAPEST_INHERITANCE_LEVEL = 40;

/**
 * Modul: VillageManagementEngine.GetMaxBuildingLevelCeiling, mirrored.
 * Every non-structural building is refused past 2 + TownHallLevel * 2, which
 * is the single least discoverable rule in the game - the upgrade button just
 * says no. A level 0 Town Hall permits level 2.
 */
function townHallCeiling(townHallLevel: number): number {
  return 2 + Number(townHallLevel) * 2;
}

/** The buildings that ceiling actually caps - not the two structural ones. */
function highestCappedBuilding(s: StateUpdate): number {
  return Math.max(
    Number(s.ForgeLevel),
    Number(s.InnLevel),
    Number(s.BreedingLevel),
    Number(s.LumberjackLevel),
    Number(s.MineLevel),
    Number(s.WarehouseLevel),
  );
}

/**
 * The table. Ordered roughly by when a player meets each system, because when
 * several are pending at once the earliest one wins and the rest wait.
 */
const DISCOVERIES: readonly DiscoveryRule[] = [
  {
    id: 'gathering',
    system: 'Gathering',
    screen: 'gathering',
    title: 'Gathering has its own levels',
    // Modul: all four masteries summed. Which profession a player tries first
    // is not knowable, and keying this on woodcutting alone would leave a
    // fisherman untaught - the same defect the larder step had when it read
    // Food1_Count and ignored the other two slots.
    reached: (s) =>
      Number(s.WoodcuttingMasteryXp) +
        Number(s.MiningMasteryXp) +
        Number(s.FishingMasteryXp) +
        Number(s.HerbalismMasteryXp) >
      0,
    body:
      'Each profession levels its own mastery, and a higher mastery is a faster tick. ' +
      'Woodcutting, mining, fishing and herbalism are tracked separately, so levelling one ' +
      'does nothing for the others.',
  },
  {
    id: 'backpack_full',
    system: 'Inventory',
    screen: 'chest',
    title: 'Your backpack is full',
    // Modul: a full backpack was a DEAD END for a long time - drops stop
    // arriving and nothing says so. This is the one moment here that is a
    // warning rather than an introduction, which is why it sits this early.
    reached: (s) => Number(s.InventoryCapacity) > 0 && Number(s.InventorySpaceRemaining) <= 0,
    body:
      'A full backpack stops drops arriving at all - they are discarded, not queued. ' +
      'Sell or scrap from the Chest to make room; nothing will warn you again.',
  },
  {
    id: 'crafting',
    system: 'Crafting',
    screen: 'crafting',
    title: 'Crafting is a job, not a purchase',
    reached: (s) => Number(s.TotalItemsCraftedCount) >= 1,
    body:
      'A character works a recipe over time, the same way it fights or gathers. ' +
      'Craft x10 queues ten of the same recipe in one go, which is the only sane way to ' +
      'make anything in quantity.',
  },
  {
    id: 'tools',
    system: 'Tools',
    screen: 'character',
    title: 'A tool is equipment',
    // Modul: ELEVEN SLOTS. 0-7 are combat, 8 Axe, 9 Pickaxe, 10 Rod. Every
    // list in this repo that stopped at eight has been a bug, and a worn tool
    // rendering as an empty slot is the exact confusion this explains.
    reached: (s) => Number(s.AxeToolTier) + Number(s.PickaxeToolTier) + Number(s.RodToolTier) > 0,
    body:
      'An axe, a pickaxe and a rod go in slots 8, 9 and 10 on the paper doll, past the eight ' +
      'combat slots. A worn tool is what makes gathering fast - one sitting in your backpack ' +
      'does nothing at all.',
  },
  {
    id: 'skills',
    system: 'Skill tree',
    screen: 'skills',
    title: 'You have a skill point',
    reached: (s) => Number(s.AvailableSkillPoints) >= 1,
    body:
      'The Skill Tree is where levels turn into power. Your first respec is free and every ' +
      'one after that is paid, so spend the early points on what you are actually doing.',
  },
  {
    id: 'village',
    system: 'Village',
    screen: 'village',
    title: 'Villagers have arrived',
    reached: (s) => Number(s.VillagePopulation) >= 1 || Number(s.CurrentPopulationCount) >= 1,
    body:
      'Villagers work the production buildings, which earn wood and ore while you are away. ' +
      'The Inn is what houses them, and the Inn also feeds the gene pool you breed from.',
  },
  {
    id: 'region2',
    system: 'Regions',
    screen: 'combat',
    title: 'A new region is open',
    // Modul: HighestUnlockedRegion, not DefeatedRegionBossMask. The mask says
    // a boss died; the unlock is the thing the player noticed happening.
    reached: (s) => Number(s.HighestUnlockedRegion) >= 2,
    body:
      'Regions are unlocked by killing the BOSS of the previous one, not by levelling. ' +
      'If the next region looks locked no matter how high you get, an unbeaten boss is why.',
  },
  {
    id: 'market',
    system: 'Market',
    screen: 'market',
    title: 'Gold worth spending',
    // Modul: market activity is entirely REST, so "has listed" and "has
    // bought" are both unavailable here. Wealth is the honest packet-side
    // stand-in, and it is also the moment the screen becomes interesting.
    reached: (s) => Number(s.Gold) >= 5000,
    body:
      'The Market is gear other players listed, at prices they set. The seller pays a ' +
      'wealth-scaled burn out of the sale, so what you pay is not what they keep - which is ' +
      'why listings undercut each other less than you would expect.',
  },
  {
    id: 'forge',
    system: 'Forge',
    screen: 'forge',
    title: 'The Forge fuses and rerolls',
    reached: (s) => Number(s.ForgeLevel) >= 1,
    body:
      'Fusion turns two items into one better item; a reroll changes the affixes on a single ' +
      'item, and you can lock the affix you want to keep. The Forge BUILDING level is the ' +
      'rarity ceiling: a level 5 Forge cannot fuse past rarity 5.',
  },
  {
    id: 'town_hall',
    system: 'Town Hall',
    screen: 'village',
    title: 'A building hit the Town Hall ceiling',
    // Modul: fires on the ceiling being REACHED, not on the Town Hall
    // existing. A fresh account has TownHallLevel 0 and a ceiling of 2, so
    // "the Town Hall exists" would fire on the very first packet and teach
    // nothing. The moment worth explaining is the one where an upgrade is
    // silently refused.
    reached: (s) => highestCappedBuilding(s) >= townHallCeiling(Number(s.TownHallLevel)),
    body:
      'Every other building is capped at 2 + (Town Hall level x 2), so upgrades stop being ' +
      'offered until the Town Hall itself grows. It is the only building on the critical ' +
      'path, and it costs logs and ore rather than gold.',
  },
  {
    id: 'guild',
    system: 'Guilds',
    screen: 'guildops',
    title: 'You are in a guild',
    reached: (_s, facts) => facts.hasGuild,
    body:
      'Donating materials to the guild depot raises the buff tiers for the whole guild, and ' +
      'an active buff applies to every member - including you while you are offline. ' +
      'Donations are the whole point of a guild; the roster is just who is doing it.',
  },
  {
    id: 'breeding',
    system: 'Breeding',
    screen: 'breeding',
    title: 'The Breeding Grounds are built',
    reached: (s) => Number(s.BreedingLevel) >= 1,
    body:
      'Pair two of your characters, or one of them with a villager, and the child inherits ' +
      'their aptitudes. Rarer races come out of exactly this - breeding is the only way to ' +
      'get one.',
  },
  {
    id: 'first_child',
    system: 'Breeding',
    screen: 'breeding',
    title: 'A child is on the way',
    // Modul: the maturation timer, not Slot2_CharacterId. A non-empty
    // character id is a lasting fact that would collide with 'breeding'; the
    // timer is the actual moment of breeding.
    reached: (s) => Number(s.ActiveChildMaturationMs) > 0,
    body:
      'It is playable once it grows up, and it occupies one of your character slots until ' +
      'then. A higher Inn level shortens the wait.',
  },
  {
    id: 'world_boss',
    system: 'World Boss',
    screen: 'worldboss',
    title: 'A world boss is up',
    reached: (s) => Number(s.WorldBossEventState) === BOSS_ACTIVE,
    body:
      'Everyone on the server hits the same health bar and rewards scale with your share. ' +
      'Attempts are limited per encounter, and an attack made with an empty larder is thrown ' +
      'away silently - stock it first.',
  },
  {
    id: 'deeds',
    system: 'Deeds & Seals',
    screen: 'progression',
    title: 'The Book of Deeds is running',
    reached: (s) => Number(s.AchievementTierTotal) >= 1,
    body:
      'It tracks what you have done across every season you ever play. Finishing a chapter ' +
      'earns a Seal, and each Seal is +2 skill points every season from then on - permanently, ' +
      'through every reset.',
  },
  {
    id: 'ancestors',
    system: 'Hall of Ancestors',
    screen: 'ancestors',
    title: 'Your character is ageing',
    // Modul: age phase, not "a second character exists". AGE_PHASES is
    // Child / Adult / Veteran / Elder, so >= 2 is "someone is getting old",
    // which is when the rollover cull stops being abstract.
    reached: (s) =>
      Math.max(Number(s.Slot1_AgePhase), Number(s.Slot2_AgePhase), Number(s.Slot3_AgePhase)) >=
      AGE_PHASE_VETERAN,
    body:
      'Levels, gear, gold and the village all reset at the end of a season. The Hall of ' +
      'Ancestors is the short list of people who carry through it, and anyone you have not ' +
      'marked is culled at the rollover.',
  },
  {
    id: 'inheritance',
    system: 'Inheritance',
    screen: 'inheritance',
    title: 'Diamonds buy something permanent',
    // Modul: gated on being able to AFFORD one. Telling a player about a shop
    // they cannot buy from is how the old step two - "craft something", on an
    // account with no materials - became an instruction nobody could follow.
    reached: (s) => Number(s.PremiumCurrencyBalance) >= CHEAPEST_INHERITANCE_LEVEL,
    body:
      'Inheritance bonuses are bought with diamonds and are the one thing a season reset ' +
      'leaves completely untouched. Everything else you own is temporary.',
  },
];

/** The moments, without their predicates - for the Settings list. */
export const DISCOVERY_MOMENTS: readonly DiscoveryMoment[] = DISCOVERIES.map(
  ({ reached: _reached, ...moment }) => moment,
);

export const DISCOVERY_IDS: readonly DiscoveryId[] = DISCOVERIES.map((d) => d.id);

export function findDiscovery(id: string): DiscoveryMoment | null {
  return DISCOVERY_MOMENTS.find((m) => m.id === id) ?? null;
}

/** Every moment the player has reached, in table order. */
export function reachedDiscoveries(
  snapshot: StateUpdate | null,
  facts: OnboardingFacts = NO_FACTS,
): DiscoveryId[] {
  if (!snapshot) return [];
  return DISCOVERIES.filter((d) => d.reached(snapshot, facts)).map((d) => d.id);
}

/**
 * The next thing to explain, or null.
 *
 * Reached and not yet seen, earliest in the table first. Pure: the seen-set is
 * handed in rather than read from storage, which is what keeps this testable
 * without a browser.
 */
export function nextDiscovery(
  snapshot: StateUpdate | null,
  facts: OnboardingFacts,
  seen: ReadonlySet<string>,
): DiscoveryMoment | null {
  if (!snapshot) return null;
  for (const rule of DISCOVERIES) {
    if (seen.has(rule.id)) continue;
    if (!rule.reached(snapshot, facts)) continue;
    const { reached: _reached, ...moment } = rule;
    return moment;
  }
  return null;
}
