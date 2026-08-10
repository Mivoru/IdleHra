// Modul: the content mirror. Descendant of ClientContentRegistry (502 lines),
// which read the same files off StreamingAssets; the web build fetches them
// from /gamedata over HTTP instead, so both clients read the same bytes rather
// than each shipping a copy.

import { GAMEDATA_BASE } from './config';

export interface MonsterDefinition {
  Id: number;
  Name: string;
  EnemyId: string;
  MaxHp: number;
  AttackPower: number;
  AttackIntervalMs: number;
  BaseGoldReward: number;
  BaseXpReward: number;
  RegionTier: number;
  Armor: number;
  DodgeRating: number;
  LootTableId: number;
}

export interface ItemDefinition {
  Id: number;
  BaseId: string;
  RegionTier: number;
  BaseValueGold: number;
  FlatAttackPower: number;
  FlatDefenseRating: number;
}

// Modul: THE canonical monster set is ids 91-115 - five regions of four
// monsters plus a boss. monsters.json contains 115 entries, so the extra 90
// are not regions and must never be presented as such, and the region a
// monster belongs to is read from this range rather than derived from
// RegionTier (which does not partition the file the way it looks like it
// should). This is content canon, recorded because deriving it wrongly has
// already caused bugs.
export const FIRST_CANONICAL_MONSTER_ID = 91;
export const LAST_CANONICAL_MONSTER_ID = 115;
export const MONSTERS_PER_REGION = 5;
export const REGION_COUNT = 5;

export interface GatheringNodeDefinition {
  ActivityId: number;
  /** 0 Woodcutting, 1 Mining, 2 Fishing, 3 Herbalism. */
  ProfessionType: number;
  BaseTickThreshold: number;
  BaseMasteryXpReward: number;
}

export interface ContentRegistry {
  monsters: Map<number, MonsterDefinition>;
  items: Map<number, ItemDefinition>;
  /** Reverse of `items`, because commands carry numeric ids and REST carries BaseIds. */
  itemsByBaseId: Map<string, ItemDefinition>;
  gatheringNodes: GatheringNodeDefinition[];
  /** The 25 canonical monsters, grouped into the five regions, in order. */
  regions: MonsterDefinition[][];
}

/**
 * Food carries no flag in items.json; the "_food" suffix on its BaseId is the
 * only marker, and all 14 cooked items use it. Recorded as a convention being
 * relied on rather than a fact being read - if a food item ever ships without
 * it, the larder simply will not offer it and nothing will say why.
 */
/**
 * The ten fish, by name.
 *
 * Modul: food used to be "anything with _food in its BaseId", which no raw
 * fish carries - so a player could fish all day, watch the catch land in the
 * chest, and be told by the larder that they had no food. Cooking is not in
 * the design list, so a fish IS the meal.
 *
 * The server derives this from the fishing loot tables ("anything a fishing
 * node drops"); this is the same set written out, because the client is never
 * sent a loot table. ContentRegistryFishTests asserts the server's set is
 * exactly these ten, so the two cannot drift apart quietly.
 */
export const RAW_FISH_BASE_IDS: readonly string[] = [
  'sunlit_perch',
  'shimmering_trout',
  'moss_bass',
  'ancient_eel',
  'lava_carp',
  'hellfire_salmon',
  'frost_cod',
  'glacier_halibut',
  'void_ray',
  'spectral_lanternfish',
];

export function isFood(baseItemId: string): boolean {
  return baseItemId.includes('_food') || RAW_FISH_BASE_IDS.includes(baseItemId);
}

/**
 * Consumable classification, by the SAME BaseId markers ConsumableEngine uses
 * server-side.
 *
 * Deliberately not a hand-written id list. There was one of those on the
 * server - AlchemyCompendium's seven legacy ids - and because the eight real
 * consumables (items.json 372-379) were added to the engine but not to that
 * set, eating a Roasted Perch FORCE-DISCONNECTED the player. Reading the same
 * markers the engine reads means this cannot drift the same way.
 *
 * Note "_food_consumable" also contains "_food", so `isFood` above is true for
 * these too - the two answer different questions (larder stocking versus
 * on-demand use) and the overlap is intentional.
 */
export type ConsumableKind = 'food' | 'offensive' | 'defensive';

export function consumableKind(baseItemId: string): ConsumableKind | null {
  if (!baseItemId.includes('_consumable')) return null;
  if (baseItemId.includes('_offensive_potion')) return 'offensive';
  if (baseItemId.includes('_defensive_potion')) return 'defensive';
  if (baseItemId.includes('_food')) return 'food';
  return null;
}

async function fetchJson<T>(fileName: string): Promise<T> {
  const response = await fetch(`${GAMEDATA_BASE}/${fileName}`);
  if (!response.ok) {
    throw new Error(`content load failed: ${fileName} (HTTP ${response.status})`);
  }
  // These files are written with a UTF-8 BOM. Response.json() decodes with a
  // TextDecoder that strips it, so this works - but it is the reason not to
  // "optimise" this into response.text() + JSON.parse, which would throw on
  // the BOM.
  return (await response.json()) as T;
}

let cached: ContentRegistry | null = null;

export async function loadContent(): Promise<ContentRegistry> {
  if (cached !== null) return cached;

  const [monsterList, itemList, gatheringNodes] = await Promise.all([
    fetchJson<MonsterDefinition[]>('monsters.json'),
    fetchJson<ItemDefinition[]>('items.json'),
    fetchJson<GatheringNodeDefinition[]>('gathering_nodes.json'),
  ]);

  const monsters = new Map(monsterList.map((m) => [m.Id, m]));
  const items = new Map(itemList.map((i) => [i.Id, i]));
  const itemsByBaseId = new Map(itemList.map((i) => [i.BaseId, i]));

  const regions: MonsterDefinition[][] = [];
  for (let region = 0; region < REGION_COUNT; region++) {
    const start = FIRST_CANONICAL_MONSTER_ID + region * MONSTERS_PER_REGION;
    const group: MonsterDefinition[] = [];
    for (let offset = 0; offset < MONSTERS_PER_REGION; offset++) {
      const monster = monsters.get(start + offset);
      if (monster) group.push(monster);
    }
    regions.push(group);
  }

  cached = { monsters, items, itemsByBaseId, gatheringNodes, regions };
  return cached;
}

/** A drop-preview row from /api/v1/monsters/loot. */
export interface MonsterLootEntry {
  ItemId: number;
  BaseItemId: string;
  ChancePct: number;
  MinQuantity: number;
  MaxQuantity: number;
  IsEquipment: boolean;
}

// Modul: item names. NOT ONE of the 437 items in items.json carries a Name
// field - monsters do, items never have - so every name a player reads is
// derived from the BaseId here. That was fine while the BaseId was just words,
// and stopped being fine once it also had to encode structure: a shield read
// as "Eq Linen Buckler Helper Offhand Base", because "_helper_offhand_" is the
// marker EquipmentSlotEngine.ResolveSlotIndex matches on and "eq_"/"_base" are
// scaffolding. The name was always in there; it was wearing the plumbing.
//
// These are stripped as WHOLE SUFFIXES, longest first, and exactly one match
// is removed. That precision is the entire difficulty, because the plumbing
// words also occur in real names:
//
//   eq_obsidian_gloves_gloves_armor_slot_base   -> Obsidian Gloves
//   runed_boots_boots_armor_slot_base           -> Runed Boots
//   gilded_round_shield_shield_slot_base        -> Gilded Round Shield
//
// Anything that strips a trailing RUN of known words turns those three into
// "Obsidian", "Runed" and "Gilded Round". Note the two shapes that produce the
// doubling: armour names the slot separately (<name>_<slot>_armor_slot_base),
// while a shield's family word IS its slot marker (<name>_shield_slot_base) -
// so the same visible doubling needs different cuts, which is why these are
// enumerated per family rather than generalised.
//
// Verified against all 437 BaseIds.
//
// "ranged" is listed beside "range" because the catalogue authored both, and
// "structural" because one region-10 weapon uses it where every other weapon
// names a damage class - it sits in the class position, so it is scaffolding
// here even though it is scaffolding of one.
const STRUCTURAL_SUFFIXES: readonly RegExp[] = [
  /_(?:helmet|chest|gloves|boots|leggings)_armor_slot_base$/,
  /_(?:melee|ranged|range|blunt|magic|structural)_weapon_slot_base$/,
  /_unique_regional_boss_material$/,
  /_ultimate_mythic_upgrade_material$/,
  /_(?:offensive|defensive)_potion_consumable$/,
  /_guaranteed_currency_payout$/,
  /_rare_crafting_ingredient$/,
  /_raw_cooking_ingredient$/,
  /_raw_fishing_material$/,
  /_helper_offhand_base$/,
  /_alchemy_ingredient$/,
  /_crafting_material$/,
  /_ring_1\/2_slot_base$/,
  /_alchemy_material$/,
  /_amulet_slot_base$/,
  /_shield_slot_base$/,
  /_armor_slot_base$/,
  /_weapon_slot_base$/,
  /_t\d+_food$/,
  /_material$/,
  /_food$/,
  /_tool$/,
  /_base$/,
];

/** Turns "eq_linen_buckler_helper_offhand_base" into "Linen Buckler". */
export function prettifyBaseId(baseId: string): string {
  let stem = baseId.replace(/^eq_/, '');

  for (const suffix of STRUCTURAL_SUFFIXES) {
    const stripped = stem.replace(suffix, '');
    if (stripped !== stem) {
      stem = stripped;
      break;
    }
  }

  return stem
    .split('_')
    .filter((part) => part.length > 0)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

export function itemName(registry: ContentRegistry | null, itemId: number): string {
  const item = registry?.items.get(itemId);
  return item ? prettifyBaseId(item.BaseId) : `Item #${itemId}`;
}

export function monsterName(registry: ContentRegistry | null, monsterId: number): string {
  return registry?.monsters.get(monsterId)?.Name ?? `Monster #${monsterId}`;
}

export function getArmourFamily(baseItemId: string): string {
  if (!baseItemId || !baseItemId.startsWith('eq_')) return '';
  const parts = baseItemId.split('_');
  if (parts.length < 3) return '';
  const raw = parts[1];
  return raw === 'dreadnought' ? 'dread' : raw;
}
