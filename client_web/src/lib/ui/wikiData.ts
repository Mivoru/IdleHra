// Modul: the Wiki's facts, in one place, and the ledger that says what is
// documented at all.
//
// Two rules govern everything in this file.
//
// FIRST: nothing here is invented. Every number and every table is either
// imported from a module that already mirrors the server (commands.ts), read
// live from an endpoint by a component, or copied from a named server file and
// GUARDED BY tests/wiki.test.ts, which reads the C# and compares. A wiki that
// quietly disagrees with the game is worse than no wiki, because a player
// trusts it and then loses an evening to a price that changed.
//
// The guard is not theoretical. `commands.ts`'s `villageCostLabel` carries a
// display copy of VillageManagementEngine.TierMaterials' ore column and it is
// WRONG for tiers 1, 2 and 4 - it names malachite / hematite / cobalt, the RARE
// ores, where the server charges copper / iron / silver. Nothing held them
// together, so nothing said so. VILLAGE_TIER_MATERIALS below is the server's
// own table and the test compares it entry by entry.
//
// SECOND: the coverage ledger at the bottom names every screen the game has.
// A screen is either documented, with the tab that documents it, or explicitly
// marked as not needing a page, with a reason. The test asserts the ledger and
// `src/routes/*.svelte` are the same set, so a new screen cannot ship
// undocumented and unnoticed.

import { villageGoldCost, villageMaterialCost } from '../net/commands';

// ---------------------------------------------------------------------------
// Village
// ---------------------------------------------------------------------------

/**
 * VillageManagementEngine.TierMaterials, exactly.
 *
 * ONE PAIR PER REGION, COMMON AND RARE. The commons (copper, iron, sulfur,
 * silver, darksteel) are what mining pays out and what every upgrade is priced
 * in; the rares (malachite, hematite, obsidian, cobalt, astralite) are the 10%
 * share, and the Crafting Workshop's extra cost.
 *
 * The ore art is drawn as ingots on purpose - there is no smelting step in this
 * game, so an "ore" and a "bar" are the same object.
 */
export const VILLAGE_TIER_MATERIALS: readonly {
  /** Building levels this tier covers, inclusive. */
  levels: string;
  log: string;
  ore: string;
  rareLog: string;
  rareOre: string;
}[] = [
  { levels: '0-4',   log: 'birch_log',     ore: 'copper_ore',    rareLog: 'golden_birch_log',     rareOre: 'malachite_ore' },
  { levels: '5-9',   log: 'willow_log',    ore: 'iron_ore',      rareLog: 'golden_willow_log',    rareOre: 'hematite_ore' },
  { levels: '10-14', log: 'acacia_log',    ore: 'sulfur_ore',    rareLog: 'golden_acacia_log',    rareOre: 'obsidian_ore' },
  { levels: '15-19', log: 'frostpine_log', ore: 'silver_ore',    rareLog: 'golden_frostpine_log', rareOre: 'cobalt_ore' },
  { levels: '20+',   log: 'ebon_log',      ore: 'darksteel_ore', rareLog: 'golden_ebon_log',      rareOre: 'astralite_ore' },
];

/** GetTierMaterials: `level / 5`, clamped to the last band. */
export function villageTierIndex(currentLevel: number): number {
  return Math.min(Math.max(Math.floor(currentLevel / 5), 0), VILLAGE_TIER_MATERIALS.length - 1);
}

/** VillageManagementEngine.GetMaxBuildingLevelCeiling. */
export function townHallCeiling(townHallLevel: number): number {
  return 2 + townHallLevel * 2;
}

/** VillageManagementEngine.GetTownHallGoldRatePerHour. */
export function townHallGoldPerHour(townHallLevel: number): number {
  if (townHallLevel <= 1) return 50;
  if (townHallLevel === 2) return 150;
  if (townHallLevel === 3) return 450;
  if (townHallLevel === 4) return 1200;
  return 3000;
}

/** VillageManagementEngine.CalculateUpgradeDurationSeconds - cost/10, floor 30. */
export function villageUpgradeSeconds(materialCost: number): number {
  return Math.max(30, Math.floor(materialCost / 10));
}

/** CalculateWarehouseMaxStorage - 1,000 per level, per material. */
export const WAREHOUSE_PER_LEVEL = 1000;

/** VillageManagementEngine.MaxStructuralBuildingLevel. */
export const STRUCTURAL_MAX_LEVEL = 5;

/** CharacterSlotEngine: slot 2 at Town Hall 3, slot 3 at Town Hall 5. */
export const SLOT2_TOWN_HALL = 3;
export const SLOT3_TOWN_HALL = 5;

/** VillageManagementEngine.RareYieldPercent. */
export const VILLAGE_RARE_YIELD_PCT = 10;

export type VillageCostKind = 'service' | 'production' | 'structural';

/**
 * What each building is, what its level actually changes, and how it is paid
 * for. Sourced building by building rather than restated from the Village
 * screen's own blurbs, two of which describe rules the server does not have.
 */
export const VILLAGE_BUILDINGS: readonly {
  id: number;
  name: string;
  costKind: VillageCostKind;
  cap: number | null;
  /** What raising it changes, and where that is enforced. */
  effect: string;
  source: string;
}[] = [
  {
    id: 9,
    name: 'Town Hall',
    costKind: 'structural',
    cap: STRUCTURAL_MAX_LEVEL,
    effect:
      'Sets the level ceiling every other building may reach (2 + 2 per level, so 2 at level 0 and 12 at level 5), unlocks the second character slot at level 3 and the third at level 5, and trickles gold on its own: 50/h up to level 1, then 150, 450, 1,200 and 3,000.',
    source: 'GetMaxBuildingLevelCeiling, CharacterSlotEngine, GetTownHallGoldRatePerHour',
  },
  {
    id: 10,
    name: 'Crafting Workshop',
    costKind: 'structural',
    cap: STRUCTURAL_MAX_LEVEL,
    effect:
      'The only building that also costs rare logs - a tenth of the material price, minimum one. Its level is the one building level nothing else reads today: crafted equipment always comes out Normal and is raised at the Forge instead.',
    source: 'VillageManagementEngine, CraftingEngine.GrantCraftedOutputAsync',
  },
  {
    id: 1,
    name: 'Forge',
    costKind: 'service',
    cap: null,
    effect:
      'Its level is the rarity ceiling for fusion. A level 5 Forge fuses up to rarity 5 and refuses anything above, which is the single most common reason a fusion is turned down.',
    source: 'ForgeSplicingEngine, ClientCommandValidator.ValidateForgeSplicingRequest',
  },
  {
    id: 2,
    name: 'Inn',
    costKind: 'service',
    cap: null,
    effect:
      'The whole of your gene pool. Newcomers arrive every 48h minus 2h per level (floor 24h), the village holds 6 + level of them (cap 16), and their aptitudes roll 2 + up to the Inn level (ceiling 20).',
    source: 'VillagerArrivalRules, BreedingAptitudes.RollVillager',
  },
  {
    id: 3,
    name: 'Breeding Grounds',
    costKind: 'service',
    cap: null,
    effect:
      'Level 1 or better is required to breed at all - below that the server refuses the command. Nothing above level 1 has an additional effect today.',
    source: 'BreedingEngine.ExecuteBreedingAsync',
  },
  {
    id: 5,
    name: 'Lumberjack',
    costKind: 'production',
    cap: null,
    effect:
      'Produces (level + 1) x 100 logs an hour while you are away, and speeds up your own woodcutting by 5% a level. A tenth of the output arrives as the tier\'s rare log.',
    source: 'OfflineSimulationEngine.GrantVillagePassiveProductionAsync, GatheringToolEngine',
  },
  {
    id: 7,
    name: 'Mine',
    costKind: 'production',
    cap: null,
    effect:
      'The same, for ore: (level + 1) x 100 an hour, +5% mining speed a level, a tenth of it the tier\'s rare ore.',
    source: 'OfflineSimulationEngine.GrantVillagePassiveProductionAsync, GatheringToolEngine',
  },
  {
    id: 8,
    name: 'Warehouse',
    costKind: 'production',
    cap: null,
    effect:
      'Caps what the Lumberjack and the Mine may stockpile while you are away: 1,000 per level, per material. At level 0 they bank nothing at all.',
    source: 'CalculateWarehouseMaxStorage',
  },
];

/** Every building costs logs and ore. Only structural ones pay no gold. */
export function villageCostRow(costKind: VillageCostKind, currentLevel: number) {
  const materials = villageMaterialCost(currentLevel);
  const tier = VILLAGE_TIER_MATERIALS[villageTierIndex(currentLevel)];
  return {
    level: `${currentLevel} → ${currentLevel + 1}`,
    gold: costKind === 'structural' ? 0 : villageGoldCost(currentLevel),
    materials,
    log: tier.log,
    ore: tier.ore,
    rareLog: costKind === 'structural' ? Math.max(1, Math.floor(materials / 10)) : 0,
    rareLogId: tier.rareLog,
    seconds: villageUpgradeSeconds(materials),
  };
}

// ---------------------------------------------------------------------------
// The Forge, and rerolling
// ---------------------------------------------------------------------------

/**
 * AffixRegistry.CalculateRerollGoldCost - a FLAT PER-REGION table, indexed by
 * the item's own RegionTier (1-5), not by its fourteen-step rarity.
 *
 * There used to be a `100 * 1.35^tier` item-tier curve with a 1.35 streak
 * multiplier on top; both are gone, and any document still describing them is
 * stale. Measured at item tier 7 the streak multiplier made twenty consecutive
 * attempts cost 13.5 million gold against roughly 564,000 earned across a whole
 * levels 1-100 playthrough, which priced its own headline outcome out of the
 * game.
 */
export const REROLL_GOLD_BY_REGION: readonly number[] = [0, 1000, 2000, 4000, 5000, 10000];

/** AffixRegistry.MaxAffixCount, and RarityTier.GetAffixCount's bands. */
export const AFFIX_COUNT_BANDS: readonly { tiers: string; count: number }[] = [
  { tiers: 'T1-T3', count: 1 },
  { tiers: 'T4-T6', count: 2 },
  { tiers: 'T7-T9', count: 3 },
  { tiers: 'T10-T12', count: 4 },
  { tiers: 'T13-T14', count: 5 },
];

/**
 * The five-step AFFIX rarity, which is a different axis from the fourteen-step
 * item rarity - the item's tier decides HOW MANY affixes it carries, the
 * affix's own rarity decides how BIG each one is.
 *
 * Weights are AffixRegistry._rarityWeightsPerMille; the multiplier is
 * 1.6^(rarity-1); the upgrade price is CalculateRarityUpgradeDiamondCost,
 * `5 * 3.4^(step-1)` floored.
 */
export const AFFIX_RARITIES: readonly {
  rarity: number;
  name: string;
  weightPerMille: number;
  /** Magnitude multiplier against a Common roll. */
  multiplier: number;
  /** Diamonds to step UP from this rarity, or 0 at the top. */
  upgradeDiamonds: number;
}[] = [
  { rarity: 1, name: 'Common', weightPerMille: 520, multiplier: 1, upgradeDiamonds: 5 },
  { rarity: 2, name: 'Uncommon', weightPerMille: 280, multiplier: 1.6, upgradeDiamonds: 17 },
  { rarity: 3, name: 'Rare', weightPerMille: 150, multiplier: 2.56, upgradeDiamonds: 57 },
  { rarity: 4, name: 'Epic', weightPerMille: 40, multiplier: 4.096, upgradeDiamonds: 196 },
  { rarity: 5, name: 'Legendary', weightPerMille: 10, multiplier: 6.5536, upgradeDiamonds: 0 },
];

/** AffixRegistry's definition table, by the slots each affix is legal on. */
export const AFFIX_POOL: readonly { id: string; label: string; slots: string }[] = [
  { id: 'flat_hp', label: 'Flat health', slots: 'Helmet, Chest, Leggings, Boots, Amulet' },
  { id: 'flat_armor', label: 'Flat armour', slots: 'Helmet, Chest, Leggings, Boots, Amulet' },
  { id: 'gather_speed_pct', label: 'Gathering speed', slots: 'Tools only' },
  { id: 'gather_yield_pct', label: 'Gathering yield', slots: 'Tools only' },
  { id: 'gather_rare_find_pct', label: 'Rare find', slots: 'Tools only' },
  { id: 'melee_dmg_pct', label: 'Melee damage', slots: 'Weapon' },
  { id: 'range_dmg_pct', label: 'Ranged damage', slots: 'Weapon' },
  { id: 'magic_dmg_pct', label: 'Magic damage', slots: 'Weapon' },
  { id: 'attack_speed_pct', label: 'Attack speed', slots: 'Weapon, Gloves, Boots' },
  { id: 'crit_chance_pct', label: 'Crit chance', slots: 'Weapon, Helmet' },
  { id: 'crit_dmg_pct', label: 'Crit damage', slots: 'Weapon' },
  { id: 'lifesteal_pct', label: 'Lifesteal', slots: 'Weapon' },
  { id: 'armor_pen_flat', label: 'Armour penetration', slots: 'Weapon, Gloves' },
  { id: 'dodge_chance_pct', label: 'Dodge chance', slots: 'Boots, Helmet, Leggings' },
  { id: 'block_chance_pct', label: 'Block chance', slots: 'Ring' },
];

// ---------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------

/**
 * GatheringToolEngine.GetToolSpeedBonusPct, tabulated on the server as
 * `100 * (1.35^tier - 1)` and mirrored on the Gathering screen already. The
 * wood names are ContentRegistry.ToolWoodsByTier.
 *
 * Two tiers per region band, so there is always a next tool worth going back to
 * the workshop for rather than one payoff at the end of the game.
 */
export const TOOL_TIERS: readonly {
  tier: number;
  wood: string;
  /** The token a tool's BaseId actually starts with - ContentRegistry
   *  .ToolWoodsByTier. Kept beside the readable name because they differ:
   *  "Voidbark" is one word in the catalogue and two in the design list, and a
   *  slug() of the display name would land on nothing. */
  slug: string;
  speedPct: number;
  band: string;
}[] = [
  { tier: 1, wood: 'Birch', slug: 'birch', speedPct: 35, band: 'Sunlit Plains' },
  { tier: 2, wood: 'Golden Birch', slug: 'golden_birch', speedPct: 82, band: 'Sunlit Plains' },
  { tier: 3, wood: 'Willow', slug: 'willow', speedPct: 146, band: 'Whispering Woods' },
  { tier: 4, wood: 'Whisper Willow', slug: 'whisper_willow', speedPct: 232, band: 'Whispering Woods' },
  { tier: 5, wood: 'Acacia', slug: 'acacia', speedPct: 348, band: 'Scorched Wasteland' },
  { tier: 6, wood: 'Ironwood', slug: 'ironwood', speedPct: 505, band: 'Scorched Wasteland' },
  { tier: 7, wood: 'Frostpine', slug: 'frostpine', speedPct: 717, band: 'Frozen Peaks' },
  { tier: 8, wood: 'Glacier Pine', slug: 'glacier_pine', speedPct: 1003, band: 'Frozen Peaks' },
  { tier: 9, wood: 'Ebon', slug: 'ebon', speedPct: 1390, band: 'Shadow Citadel' },
  { tier: 10, wood: 'Voidbark', slug: 'voidbark', speedPct: 1912, band: 'Shadow Citadel' },
];

/** GatheringToolEngine.MasterySpeedPctPerLevel. */
export const MASTERY_SPEED_PCT_PER_LEVEL = 10;
/** GatheringToolEngine.VillageYieldBonusPctPerLevel. */
export const VILLAGE_SPEED_PCT_PER_LEVEL = 5;
/** GatheringToolEngine.MinRequiredTicks - ten ticks is one second. */
export const MIN_GATHER_TICKS = 2;

// ---------------------------------------------------------------------------
// The Long Game
// ---------------------------------------------------------------------------

/** DeedRegistry.Chapters - the titles, the reward, and what each is about. */
export const DEED_CHAPTERS: readonly {
  index: number;
  title: string;
  reward: string;
  about: string;
}[] = [
  {
    index: 1,
    title: 'The Village Road',
    reward: 'A Seal, and a set of Common tools',
    about:
      'The tutorial, written as content: win a fight, wear a weapon, fill the larder, gather 100 wood, craft something, reach level 10. Done in order it has touched every loop the game has.',
  },
  {
    index: 2,
    title: 'Smiths',
    reward: 'A Seal',
    about:
      'Fifty fusions, a rarity 8 item, twenty affix rerolls, two pieces of one set, a level 5 Forge and ten crafts. The Forge is the system a new player is least likely to find alone.',
  },
  {
    index: 3,
    title: 'Hunters',
    reward: 'A Seal',
    about:
      'A hundred kills of every region 1 monster (the counter shows your weakest of the five), a boss, level 40, five thousand kills, region 3, and one region of the codex.',
  },
  {
    index: 4,
    title: 'Stewards',
    reward: 'A Seal',
    about:
      'Twenty village building levels, Warehouse 3, a hundred levels of gathering mastery, Inn 5, fifty crafts and one completed region. The half of the game a combat-first player never opens.',
  },
  {
    index: 5,
    title: 'The Ledger of Legends',
    reward: 'A Seal',
    about:
      'A top-fifty season finish, five pieces of one set, level 100, Malakor down, a child raised and an epic child bred. Nobody finishes this in their first season.',
  },
];

/** DeedRegistry.SkillPointsPerSeal. */
export const SKILL_POINTS_PER_SEAL = 2;

/** HallOfAncestorsRules. Ten base, four buyable, hard cap fourteen. */
export const HALL_BASE_SLOTS = 10;
export const HALL_MAX_SLOTS = 14;
/** NextSlotCostDiamonds - 250, doubling. */
export const HALL_SLOT_COSTS: readonly number[] = [250, 500, 1000, 2000];

/** HallOfAncestorsRules.ChooseSurvivors, in order. */
export const CULL_ORDER: readonly string[] = [
  'Your main character, always - their id is the account’s id, so culling them would break the account rather than lose a character.',
  'Whoever you marked Keep. Nothing outranks a mark but the rule above.',
  'Then the highest aptitude total.',
  'Then an epic mutation.',
  'Then the later generation, and finally a stable tiebreak so a rollover is reproducible.',
];

/** SeasonalRotationEngine, via docs/breeding_model.md section 5. */
export const SEASON_CARRIES: readonly string[] = [
  'The Hall of Ancestors roster, up to its cap - aptitudes, genes, generation, epic marks and recorded parents.',
  'Village buildings, including the Inn and the Breeding Grounds.',
  'Race masteries and unlocked races.',
  'Diamonds, purchased Inheritance levels and purchased Hall slots.',
  'Seals, and the +2 permanent skill points each one pays every season.',
  'Your best season rank, and any paid respec grants.',
];

export const SEASON_LOST: readonly string[] = [
  'Every character’s level - everyone is reset to level 1 and to Adult.',
  'All gear, all gold and every other material.',
  'The market and the chronicle pass.',
  'Every point spent in the skill tree.',
  'The entire gene pool - newcomers and elders alike, along with the arrival clock and the escalating feast price.',
];

// ---------------------------------------------------------------------------
// Guilds
// ---------------------------------------------------------------------------

/**
 * GuildContributionEngine.BuffTierMaterials. The same five pairs the village
 * and the gathering loot tables use - the guild is not a separate economy.
 */
export const GUILD_BUFF_TIERS: readonly {
  tier: number;
  region: string;
  commonWood: string;
  rareWood: string;
  commonOre: string;
  rareOre: string;
}[] = [
  { tier: 1, region: 'Sunlit Plains', commonWood: 'birch_log', rareWood: 'golden_birch_log', commonOre: 'copper_ore', rareOre: 'malachite_ore' },
  { tier: 2, region: 'Whispering Woods', commonWood: 'willow_log', rareWood: 'golden_willow_log', commonOre: 'iron_ore', rareOre: 'hematite_ore' },
  { tier: 3, region: 'Scorched Wasteland', commonWood: 'acacia_log', rareWood: 'golden_acacia_log', commonOre: 'sulfur_ore', rareOre: 'obsidian_ore' },
  { tier: 4, region: 'Frozen Peaks', commonWood: 'frostpine_log', rareWood: 'golden_frostpine_log', commonOre: 'silver_ore', rareOre: 'cobalt_ore' },
  { tier: 5, region: 'Shadow Citadel', commonWood: 'ebon_log', rareWood: 'golden_ebon_log', commonOre: 'darksteel_ore', rareOre: 'astralite_ore' },
];

/** GuildContributionEngine.BuffMaterialCostPerType - of EACH, so double this. */
export const GUILD_BUFF_COST_PER_MATERIAL = 25_000;

/** SimulationEngine reads each of these as `tier * 2` percent. */
export const GUILD_BUFF_TYPES: readonly { type: string; label: string; what: string }[] = [
  { type: 'Exp', label: 'Experience', what: '+2% experience per tier' },
  { type: 'Gold', label: 'Gold', what: '+2% gold from kills per tier' },
  { type: 'DropRate', label: 'Drop rate', what: '+2 points of loot luck per tier' },
  { type: 'Damage', label: 'Damage', what: '+2% damage per tier' },
];

/** GuildRecord.MinTaxRatePct / MaxTaxRatePct. */
export const GUILD_TAX_MIN_PCT = 5;
export const GUILD_TAX_MAX_PCT = 20;

// ---------------------------------------------------------------------------
// Market, mail, the world boss and the daily bonus
// ---------------------------------------------------------------------------

/** MarketEscrowEngine: the seller's fee bracket, by the gold they are holding. */
export const MARKET_FEE_BRACKETS: readonly { wealth: string; feePct: number }[] = [
  { wealth: 'under 500,000 gold', feePct: 5 },
  { wealth: '500,000 to 5,000,000', feePct: 8 },
  { wealth: 'over 5,000,000', feePct: 15 },
];

/** WorldBossEngine. */
export const WORLD_BOSS_HP = 50_000_000;
export const WORLD_BOSS_ATTEMPTS = 3;
export const WORLD_BOSS_DAMAGE_FLOOR = 1000;
export const WORLD_BOSS_DAMAGE_CEILING = 100_000_000;
export const WORLD_BOSS_MAILBOX_LIMIT = 50;

/** The percentile brackets AwardRewards pays into the mailbox. */
export const WORLD_BOSS_REWARDS: readonly { bracket: string; tokens: number; gold: number }[] = [
  { bracket: 'Top 1%', tokens: 10, gold: 250_000 },
  { bracket: 'Top 10%', tokens: 6, gold: 100_000 },
  { bracket: 'Top 50%', tokens: 3, gold: 50_000 },
  { bracket: 'Everyone else who landed a hit', tokens: 1, gold: 10_000 },
];

/**
 * DailyLoginRewardEngine.GoldRewardMatrices - three weekly rotations, chosen by
 * the UTC week number, so everybody on the server is on the same one.
 */
export const DAILY_LOGIN_MATRICES: readonly (readonly number[])[] = [
  [500, 1000, 1500, 2500, 4000, 6000, 10000],
  [4000, 3000, 2500, 2500, 3500, 4000, 6000],
  [1000, 2000, 6000, 2000, 2500, 4000, 8000],
];

/** DailyLoginRewardEngine.PremiumDiamondsOnDay7Completion. */
export const DAILY_LOGIN_DAY7_DIAMONDS = 100;

/** AchievementMilestones. Four achievements; three of them tier I-IV. */
export const ACHIEVEMENTS: readonly {
  name: string;
  metric: string;
  thresholds: string;
  rewards: string;
}[] = [
  {
    name: 'Monster hunter',
    metric: 'Monsters killed',
    thresholds: '10,000',
    rewards: '500 diamonds, claimed by hand on the Progression screen',
  },
  {
    name: 'Treasury',
    metric: 'Gold held at once',
    thresholds: '100k / 5M / 100M / 2.5B',
    rewards: '10 / 50 / 250 / 1,000 diamonds',
  },
  {
    name: 'Forging',
    metric: 'Fusions, then the highest rarity ever fused',
    thresholds: '50 / 500 fusions, then rarity 10 / rarity 14',
    rewards: '15 / 75 / 200 / 1,500 diamonds',
  },
  {
    name: 'Logistics',
    metric: 'Material gathered',
    thresholds: '10k / 100k / 1M / 10M',
    rewards: '10 / 50 / 200 / 800 diamonds, and +1 / +2 / +4 / +8% gathering speed permanently',
  },
];

// ---------------------------------------------------------------------------
// The coverage ledger
// ---------------------------------------------------------------------------

export type CoverageStatus = 'documented' | 'no-page-needed' | 'removed';

export interface ScreenCoverage {
  /** The file under src/routes, without the extension. */
  screen: string;
  label: string;
  status: CoverageStatus;
  /** The wiki tab that documents it, for `documented` rows. */
  tab?: string;
  note: string;
}

/**
 * Every screen the game has, and where it is written down.
 *
 * tests/wiki.test.ts asserts this is exactly the set of `src/routes/*.svelte`,
 * so adding a screen without deciding whether it needs a page fails the suite
 * rather than being noticed a season later.
 */
export const SCREEN_COVERAGE: readonly ScreenCoverage[] = [
  { screen: 'Hub', label: 'Hub', status: 'no-page-needed', note: 'The menu itself. Documenting a list of links is documenting this wiki’s own table of contents.' },
  { screen: 'Login', label: 'Sign in', status: 'no-page-needed', note: 'Happens before the game; nothing about it is a rule a player can play around.' },
  { screen: 'Settings', label: 'Settings', status: 'no-page-needed', note: 'Volume, theme and account options. Every control says what it does on the screen.' },
  { screen: 'Wiki', label: 'Wiki', status: 'no-page-needed', note: 'This. A wiki page about the wiki would be a table of contents for the table of contents.' },
  { screen: 'Boosts', label: 'Chrono bank', status: 'removed', note: 'The banked-seconds system was deleted on 2026-09-02; the screen keeps its consumables and buffs. Nothing to document.' },

  { screen: 'Combat', label: 'Combat', status: 'documented', tab: 'combat', note: 'Attributes, the damage model, auto-eat, death and halt reasons.' },
  { screen: 'Larder', label: 'Larder', status: 'documented', tab: 'combat', note: 'Auto-eat and what counts as food.' },
  { screen: 'Character', label: 'Character', status: 'documented', tab: 'combat', note: 'The eleven equipment slots and armour set bonuses.' },
  { screen: 'Progression', label: 'Progression', status: 'documented', tab: 'skills', note: 'Skill points, the three rings, respec - and the Book of Deeds, which is on the Long Game page.' },
  { screen: 'Chest', label: 'Chest', status: 'documented', tab: 'items', note: 'Storage, selling and discarding; the item database is here too.' },
  { screen: 'Forge', label: 'Forge', status: 'documented', tab: 'forge', note: 'Fusion, the Forge level ceiling, and affix rerolls with their per-region price.' },
  { screen: 'Codex', label: 'Codex', status: 'documented', tab: 'map', note: 'Per-monster kill records and region completion.' },
  { screen: 'Gathering', label: 'Gathering', status: 'documented', tab: 'gathering', note: 'Nodes, mastery, and the tools that accelerate them.' },
  { screen: 'Crafting', label: 'Crafting', status: 'documented', tab: 'crafting', note: 'The live recipe list, read from the same endpoint the screen uses.' },
  { screen: 'Village', label: 'Village', status: 'documented', tab: 'village', note: 'Every building, the Town Hall ceiling, the tier materials and the generated cost table.' },
  { screen: 'Breeding', label: 'Breeding', status: 'documented', tab: 'breeding', note: 'Aptitudes, genes, the two pairings and what a child inherits.' },
  { screen: 'Ancestors', label: 'Hall of Ancestors', status: 'documented', tab: 'longgame', note: 'The roster, the cap, fielding, and who survives the cull.' },
  { screen: 'Inheritance', label: 'Inheritance', status: 'documented', tab: 'longgame', note: 'The six permanent bonuses diamonds buy, and their cost curve.' },
  { screen: 'Leaderboards', label: 'Leaderboards', status: 'documented', tab: 'longgame', note: 'Seasonal ranking and what a placement is worth.' },
  { screen: 'GuildOps', label: 'Guild', status: 'documented', tab: 'guilds', note: 'Roles, the depot, buff tiers and the guild tax.' },
  { screen: 'Social', label: 'Friends', status: 'documented', tab: 'guilds', note: 'Friends, blocking and profiles.' },
  { screen: 'Chat', label: 'Chat', status: 'documented', tab: 'guilds', note: 'The three channels and what each reaches.' },
  { screen: 'Market', label: 'Market', status: 'documented', tab: 'economy', note: 'Listings, the wealth-scaled seller fee and the region gate on buying.' },
  { screen: 'Mailbox', label: 'Mailbox', status: 'documented', tab: 'economy', note: 'Where world boss rewards and undeliverable market goods land.' },
  { screen: 'Store', label: 'Store', status: 'documented', tab: 'economy', note: 'Diamonds, and the two sinks worth spending them on.' },
  { screen: 'WorldBoss', label: 'World boss', status: 'documented', tab: 'events', note: 'The shared encounter, the three attempts and the percentile rewards.' },
];

// ---------------------------------------------------------------------------
// Search
// ---------------------------------------------------------------------------

export interface WikiSearchEntry {
  tab: string;
  /** The id of a heading inside that tab, for scrolling. */
  anchor: string;
  title: string;
  /** Extra words that should match, beyond the title. */
  keywords: string;
}

/**
 * What search looks through.
 *
 * A hand-kept index rather than a scan of the rendered page, because only the
 * active tab is in the DOM - searching what is rendered would only ever find
 * the section the player is already reading, which is the one thing they do
 * not need help finding.
 */
export const WIKI_SEARCH_INDEX: readonly WikiSearchEntry[] = [
  { tab: 'basics', anchor: 'the-loop', title: 'The core loop', keywords: 'start beginning new player what to do first idle offline' },
  { tab: 'basics', anchor: 'offline', title: 'Offline progression', keywords: 'away logged out sleep twelve hours cap summary' },
  { tab: 'basics', anchor: 'halts', title: 'Why a character stopped', keywords: 'halt idle stuck nothing happening out of food died quarantine' },
  { tab: 'basics', anchor: 'currencies', title: 'Gold, diamonds and materials', keywords: 'currency premium money commodity' },

  { tab: 'combat', anchor: 'attributes', title: 'STR, DEX, CON and LCK', keywords: 'strength dexterity constitution luck stats attributes armour penetration accuracy' },
  { tab: 'combat', anchor: 'damage', title: 'How a hit is resolved', keywords: 'damage armour mitigation crit dodge block lifesteal' },
  { tab: 'combat', anchor: 'autoeat', title: 'Auto-eat and the larder', keywords: 'food fish heal threshold larder starve' },
  { tab: 'combat', anchor: 'slots', title: 'The eleven equipment slots', keywords: 'weapon helmet chest gloves leggings boots amulet ring axe pickaxe rod tools paper doll offhand shield' },
  { tab: 'combat', anchor: 'sets', title: 'Armour set bonuses', keywords: 'set family two three five pieces matching' },

  { tab: 'skills', anchor: 'rings', title: 'Roots, boughs and crowns', keywords: 'skill tree points respec exclusive branch' },
  { tab: 'skills', anchor: 'nodes', title: 'Every skill node', keywords: 'fortune giantslayer precision cruelty insight plenty rarity relentless bloodthirst harvest golden fleece' },

  { tab: 'items', anchor: 'rarity', title: 'The fourteen rarity tiers', keywords: 'normal common uncommon rare epic legendary mythic relic ancient divine demonic godly transcendent' },
  { tab: 'items', anchor: 'droprates', title: 'Drop chances and luck', keywords: 'drop rate luck lck calculator odds equipment 15%' },
  { tab: 'items', anchor: 'database', title: 'Item database', keywords: 'search items catalogue browse' },
  { tab: 'items', anchor: 'namespaces', title: 'Three kinds of material', keywords: 'commodity slug crafting material namespace copper ore stranded' },

  { tab: 'forge', anchor: 'fusion', title: 'Fusion', keywords: 'forge fuse combine sacrifice rarity ceiling level' },
  { tab: 'forge', anchor: 'reroll', title: 'Affix rerolls and what they cost', keywords: 'reroll affix price region gold 1000 2000 4000 5000 10000 lock' },
  { tab: 'forge', anchor: 'affixrarity', title: 'Affix rarity', keywords: 'common uncommon rare epic legendary magnitude multiplier diamonds upgrade' },
  { tab: 'forge', anchor: 'affixpool', title: 'Which affixes roll where', keywords: 'affix pool slots weapon armour tool stacking' },

  { tab: 'map', anchor: 'regions', title: 'The five regions', keywords: 'sunlit plains whispering woods scorched wasteland frozen peaks shadow citadel' },
  { tab: 'map', anchor: 'unlock', title: 'Unlocking a region', keywords: 'boss gate progression wear gear region tier equip refused' },
  { tab: 'map', anchor: 'races', title: 'Races and which boss unlocks them', keywords: 'human vila draugr kobold vodnik moosleute race mastery' },
  { tab: 'map', anchor: 'monsters', title: 'Every monster and its drops', keywords: 'monster drops loot table hp armour dodge codex' },

  { tab: 'gathering', anchor: 'professions', title: 'The gathering professions', keywords: 'woodcutting mining fishing herbalism node activity' },
  { tab: 'gathering', anchor: 'speed', title: 'What makes gathering faster', keywords: 'mastery tool village lumberjack mine speed ticks affix' },
  { tab: 'gathering', anchor: 'tools', title: 'Tools are equipment', keywords: 'axe pickaxe rod tool tier wood birch void bark slots 8 9 10' },
  { tab: 'gathering', anchor: 'nodes', title: 'Node list', keywords: 'nodes tick threshold location' },

  { tab: 'crafting', anchor: 'bench', title: 'How crafting works', keywords: 'craft recipe batch ten workshop rarity normal' },
  { tab: 'crafting', anchor: 'recipes', title: 'Every recipe', keywords: 'recipes materials tools smelting equipment cooking alchemy' },

  { tab: 'village', anchor: 'townhall', title: 'The Town Hall ceiling', keywords: 'town hall ceiling character slots gold per hour structural' },
  { tab: 'village', anchor: 'buildings', title: 'What each building does', keywords: 'forge inn breeding grounds lumberjack mine warehouse crafting workshop' },
  { tab: 'village', anchor: 'materials', title: 'Tier materials', keywords: 'logs ore copper iron sulfur silver darksteel malachite hematite obsidian cobalt astralite' },
  { tab: 'village', anchor: 'costs', title: 'Upgrade costs, level by level', keywords: 'cost gold materials price upgrade time duration' },

  { tab: 'breeding', anchor: 'words', title: 'The words breeding uses', keywords: 'aptitude bloodline gene copy ancestor cull elder gene pool fielding terminology' },
  { tab: 'breeding', anchor: 'requirements', title: 'What you need to breed', keywords: 'level 50 adult breeding grounds gold cooldown' },
  { tab: 'breeding', anchor: 'pairings', title: 'The two pairings', keywords: 'hero newcomer villager inbred related sibling' },
  { tab: 'breeding', anchor: 'inherits', title: 'What a child inherits', keywords: 'aptitude drift epic mutation genes dominant recessive band preview' },
  { tab: 'breeding', anchor: 'genepool', title: 'The Inn and the gene pool', keywords: 'newcomer arrival interval capacity feast elder marry' },

  { tab: 'longgame', anchor: 'season', title: 'What a season resets', keywords: 'rollover reset carries lost season end wipe' },
  { tab: 'longgame', anchor: 'deeds', title: 'The Book of Deeds and Seals', keywords: 'deeds chapters seals skill points permanent' },
  { tab: 'longgame', anchor: 'hall', title: 'The Hall of Ancestors and the cull', keywords: 'ancestors slots keep mark cull survive field roster' },
  { tab: 'longgame', anchor: 'inheritance', title: 'Inheritance', keywords: 'diamonds permanent damage health experience gold gathering luck 2% 20 levels' },
  { tab: 'longgame', anchor: 'leaderboard', title: 'Season rank', keywords: 'leaderboard rank top fifty placement' },

  { tab: 'guilds', anchor: 'roles', title: 'Roles', keywords: 'leader officer member kick promote demote application' },
  { tab: 'guilds', anchor: 'depot', title: 'The depot and donations', keywords: 'donate depot contribution logs ore guild tier' },
  { tab: 'guilds', anchor: 'buffs', title: 'Guild buffs', keywords: 'buff tier experience gold drop rate damage common rare 25000 duration' },
  { tab: 'guilds', anchor: 'tax', title: 'The guild tax', keywords: 'tax market sale cut treasury 5% 20%' },
  { tab: 'guilds', anchor: 'chat', title: 'Chat channels', keywords: 'world guild private whisper channel' },

  { tab: 'economy', anchor: 'market', title: 'The market', keywords: 'sell buy listing price fee bracket escrow region gate' },
  { tab: 'economy', anchor: 'mailbox', title: 'The mailbox', keywords: 'mail claim attachment full fifty lost reward' },
  { tab: 'economy', anchor: 'diamonds', title: 'Diamonds', keywords: 'premium currency store purchase inheritance hall slots affix rarity' },

  { tab: 'events', anchor: 'worldboss', title: 'The world boss', keywords: 'perun avatar shared hp attempts percentile token reward' },
  { tab: 'events', anchor: 'daily', title: 'The daily bonus', keywords: 'login streak seven days gold diamonds utc midnight' },
  { tab: 'events', anchor: 'achievements', title: 'Achievements', keywords: 'treasury forging logistics tiers diamonds claim' },

  { tab: 'screens', anchor: 'ledger', title: 'Screen index', keywords: 'coverage every screen documented index where is' },
];
