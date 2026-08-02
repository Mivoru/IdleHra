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
export function isFood(baseItemId: string): boolean {
  return baseItemId.includes('_food');
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

/** Turns "cooked_mud_carp_t3_food" into "Cooked Mud Carp T3 Food". */
export function prettifyBaseId(baseId: string): string {
  return baseId
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
