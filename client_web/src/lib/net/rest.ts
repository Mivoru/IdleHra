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
  mastery: ['player', 'mastery'] as const,
  statistics: ['player', 'statistics'] as const,
  monsterLoot: (monsterId: number) => ['monsters', 'loot', monsterId] as const,
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
// /api/v1/mastery/snapshot
// ---------------------------------------------------------------------------

export interface RaceMasteryEntry {
  RaceId: number;
  Level: number;
  Experience: number;
  NextLevelExperience: number;
}

export function fetchMastery(): Promise<RaceMasteryEntry[]> {
  return authedGet<RaceMasteryEntry[]>('/api/v1/mastery/snapshot');
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
