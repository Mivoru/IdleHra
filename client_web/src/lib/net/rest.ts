// Modul: the REST surface, typed in one place.
//
// The Unity client grew 24 separate hand-written caches because it had no
// data-fetching library - each one its own timer, its own staleness rule and
// its own invalidation bug. The port plan's answer is TanStack Query, so this
// file is deliberately ONLY the typed fetchers and their query keys; caching,
// deduplication, retry and invalidation all belong to the query client and are
// never reimplemented here.

import { authedGet } from './auth';

// ---------------------------------------------------------------------------
// Query keys
// ---------------------------------------------------------------------------

// Centralised so an invalidation cannot miss a cache by spelling its key
// differently at the call site - the exact failure mode 24 hand-rolled caches
// kept producing.
export const queryKeys = {
  inventory: ['player', 'inventory'] as const,
  statistics: ['player', 'statistics'] as const,
  monsterLoot: (monsterId: number) => ['monsters', 'loot', monsterId] as const,
  bank: ['player', 'bank'] as const,
  forge: ['player', 'forge'] as const,
  recipes: ['crafting', 'recipes'] as const,
  market: (baseItemId: string, qualityTier: number, pageIndex: number) =>
    ['market', 'listings', baseItemId, qualityTier, pageIndex] as const,
};

// ---------------------------------------------------------------------------
// /api/v1/player/inventory
// ---------------------------------------------------------------------------

/** Affix magnitudes are whole points for flat affixes, tenths of a percent otherwise. */
export type AffixMap = Record<string, number>;

export interface InventoryEquipment {
  Id: number;
  BaseItemId: string;
  QualityTier: number;
  IsEquipped: boolean;
  /** Which character slot (0-2) wears this, or -1 if it is merely carried. */
  EquippedByCharacterSlot: number;
  Affixes: AffixMap;
  IsAffixLocked: boolean;
}

export interface InventoryStack {
  ItemId: string;
  BackpackQuantity: number;
  StashQuantity: number;
}

export interface InventorySnapshot {
  BackpackSlotsUsed: number;
  MaxStackQuantity: number;
  Equipment: InventoryEquipment[];
  Stacks: InventoryStack[];
}

export function fetchInventory(): Promise<InventorySnapshot> {
  return authedGet<InventorySnapshot>('/api/v1/player/inventory');
}

// ---------------------------------------------------------------------------
// /api/v1/player/statistics
// ---------------------------------------------------------------------------

export interface VillagerSlot {
  SlotIndex: number;
  IsActive: boolean;
  EfficiencyModifier: number;
}

export interface PlayerStatistics {
  Level: number;
  Xp: number;
  Gold: number;
  PremiumDiamonds: number;
  LoginStreakDays: number;
  AchievementsClaimedCount: number;
  RegionsCompletedCount: number;
  CharacterCount: number;
  AvailableSkillPoints: number;
  GuildName: string;
  Villagers: VillagerSlot[];
  TotalKills: number;
  BossesSlain: number;
  TotalItemsCrafted: number;
  TotalDeaths: number;
  TotalPlayTimeSeconds: number;
}

export function fetchStatistics(): Promise<PlayerStatistics> {
  return authedGet<PlayerStatistics>('/api/v1/player/statistics');
}

// ---------------------------------------------------------------------------
// /api/v1/bank/list
// ---------------------------------------------------------------------------

// Modul: `Id` here is the BANK ROW id, not the equipment instance id it came
// from. WithdrawFromBank addresses this row, so the distinction is load-bearing
// rather than incidental.
export interface BankEntry {
  Id: number;
  BaseItemId: string;
  QualityTier: number;
  IsAffixLocked: boolean;
}

export function fetchBank(): Promise<BankEntry[]> {
  return authedGet<BankEntry[]>('/api/v1/bank/list');
}

// ---------------------------------------------------------------------------
// /api/v1/crafting/recipes
// ---------------------------------------------------------------------------

/**
 * `Mat1CurrentStock` / `Mat2CurrentStock` are the UNIFIED backpack+stash
 * balance - exactly what a craft will actually spend - so an affordable-looking
 * recipe really is affordable. Reporting one tier while spending from two is
 * the bug this shape exists to prevent.
 */
export interface CraftingRecipe {
  ResultItemId: number;
  ResultBaseItemId: string;
  ProfessionType: number;
  RequiredLevel: number;
  CraftingTimeMs: number;
  Mat1Id: number;
  Mat1BaseItemId: string;
  Mat1Count: number;
  Mat1CurrentStock: number;
  Mat2Id: number;
  Mat2BaseItemId: string;
  Mat2Count: number;
  Mat2CurrentStock: number;
}

export interface CraftingRecipeSnapshot {
  PlayerLevel: number;
  Recipes: CraftingRecipe[];
}

export function fetchRecipes(): Promise<CraftingRecipeSnapshot> {
  return authedGet<CraftingRecipeSnapshot>('/api/v1/crafting/recipes');
}

// ---------------------------------------------------------------------------
// /api/v1/forge/inventory
// ---------------------------------------------------------------------------

export interface ForgeEquipment {
  Id: number;
  BaseItemId: string;
  QualityTier: number;
  IsAffixLocked: boolean;
  Affixes: AffixMap;
}

export interface ForgeRecipe {
  RecipeId: number;
  ResultBaseItemId: string;
  TierIndex: number;
  MaterialName: string;
  MaterialCost: number;
  CurrentMaterialStock: number;
}

export interface ForgeSnapshot {
  OwnedEquipment: ForgeEquipment[];
  Recipes: ForgeRecipe[];
}

export function fetchForge(): Promise<ForgeSnapshot> {
  return authedGet<ForgeSnapshot>('/api/v1/forge/inventory');
}

// ---------------------------------------------------------------------------
// /api/v1/market/listings
// ---------------------------------------------------------------------------

export interface MarketListing {
  OrderId: number;
  BaseItemId: string;
  QualityTier: number;
  Price: number;
  CreatedAtEpoch: number;
}

/**
 * `baseItemId` is REQUIRED - the endpoint 400s without one, so this is a
 * search rather than a browse. There is no "show me everything" query.
 */
export function fetchMarketListings(
  baseItemId: string,
  qualityTier: number,
  pageIndex = 0,
  pageSize = 20,
): Promise<MarketListing[]> {
  const query = new URLSearchParams({
    baseItemId,
    qualityTier: String(qualityTier),
    pageIndex: String(pageIndex),
    pageSize: String(pageSize),
  });
  return authedGet<MarketListing[]>(`/api/v1/market/listings?${query}`);
}
