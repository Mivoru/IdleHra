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
  friends: ['social', 'friends'] as const,
  achievements: ['meta', 'achievements'] as const,
  loginBonus: ['meta', 'loginBonus'] as const,
  leaderboard: ['meta', 'leaderboard'] as const,
  guildLeaderboard: ['meta', 'leaderboard', 'guilds'] as const,
  codex: ['meta', 'codex'] as const,
  metadata: ['meta', 'metadata'] as const,
  breedingRoster: ['meta', 'breeding'] as const,
  breedingPreview: (a: string, b: string) => ['meta', 'breeding', 'preview', a, b] as const,
  storeCatalog: ['shop', 'catalog'] as const,
  raceMastery: ['meta', 'raceMastery'] as const,
  guilds: ['social', 'guilds'] as const,
  guildRoster: ['social', 'guild', 'roster'] as const,
  guildApplications: ['social', 'guild', 'applications'] as const,
  playerNames: (ids: number[]) => ['social', 'names', ids.join(',')] as const,
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

// ---------------------------------------------------------------------------
// Social
// ---------------------------------------------------------------------------

export interface FriendEntry {
  PlayerId: number;
  Username: string;
  Level: number;
  IsBlocked: boolean;
  IsOnline: boolean;
}

export function fetchFriends(): Promise<FriendEntry[]> {
  return authedGet<FriendEntry[]>('/api/v1/friends/list');
}

/** Username to numeric id. The relationship commands take ids, not names. */
export function resolvePlayer(username: string): Promise<{ PlayerId: number }> {
  return authedGet<{ PlayerId: number }>(
    `/api/v1/players/resolve?username=${encodeURIComponent(username)}`,
  );
}

export interface PlayerName {
  PlayerId: number;
  Username: string;
}

/**
 * Batched on purpose. ResponseChatMessagePacket has no room for a name, so
 * every social surface carries a raw numeric SenderPlayerId - a chat log
 * resolves ONE request for every id it is displaying, not one per row.
 */
export function fetchPlayerNames(ids: number[]): Promise<PlayerName[]> {
  if (ids.length === 0) return Promise.resolve([]);
  return authedGet<PlayerName[]>(`/api/v1/players/names?ids=${ids.join(',')}`);
}

export interface GuildDirectoryEntry {
  GuildId: number;
  Name: string;
  CurrentTier: number;
  ActiveMembers: number;
  MaxMembers: number;
  GuildMMR: number;
  TaxRatePct: number;
  /** Server-side JoinType; anything non-zero requires an application. */
  JoinType: number;
  MinApplicationLevel: number;
}

export function fetchGuilds(): Promise<GuildDirectoryEntry[]> {
  return authedGet<GuildDirectoryEntry[]>('/api/v1/guilds/list');
}

// Modul: the roster carries NO USERNAME - only PlayerId, Role,
// ContributionPoints and IsOnline. Names come from /api/v1/players/names, the
// same batched resolver chat uses, because the same constraint applies: this
// wire identifies players numerically and names are looked up separately.
export interface GuildMember {
  PlayerId: number;
  Role: number;
  ContributionPoints: number;
  IsOnline: boolean;
}

export function fetchGuildRoster(): Promise<GuildMember[]> {
  return authedGet<GuildMember[]>('/api/v1/guild/roster');
}

export interface GuildApplication {
  Id: number;
  PlayerId: number;
  Username: string;
  ApplicantLevel: number;
  CreatedAtEpoch: number;
}

/** Leader-only; returns an empty list for anyone else rather than a 403. */
export function fetchGuildApplications(): Promise<GuildApplication[]> {
  return authedGet<GuildApplication[]>('/api/v1/guild/applications/pending');
}

// ---------------------------------------------------------------------------
// Meta and progression
// ---------------------------------------------------------------------------

export interface AchievementEntry {
  AchievementId: number;
  CurrentProgress: number;
  CompletedTier: number;
  NextTierTarget: number;
  NextTierReward: number;
  IsClaimed: boolean;
}

export function fetchAchievements(): Promise<AchievementEntry[]> {
  return authedGet<AchievementEntry[]>('/api/v1/achievements/snapshot');
}

export interface LoginBonusState {
  CurrentStreakDay: number;
  CreditedToday: boolean;
  WeeklyGoldSchedule: number[];
  Day7DiamondBonus: number;
}

export function fetchLoginBonus(): Promise<LoginBonusState> {
  return authedGet<LoginBonusState>('/api/v1/login-bonus/state');
}

export interface LeaderboardEntry {
  Rank: number;
  PlayerId: number;
  DisplayName: string;
  Level: number;
  Xp: number;
}

export function fetchLeaderboard(): Promise<LeaderboardEntry[]> {
  return authedGet<LeaderboardEntry[]>('/api/v1/leaderboard/global');
}

/**
 * Modul: the guild board does NOT reuse the player board's shape, despite the
 * port plan asserting it did ("the player leaderboard shape already exists").
 * It returns { Rank, GuildId, Name, GuildTier, GuildMMR } - no DisplayName, no
 * Xp - and reading it as a player row crashes on undefined.
 *
 * Nobody had ever seen this response: the endpoint is implemented and fixed
 * server-side but NO Unity screen has ever called it, which is exactly why the
 * plan's assumption about its shape went unchallenged. It is one of the nine
 * endpoints listed as capability the old client never used, and wiring it here
 * closes that gap rather than inheriting it.
 */
export interface GuildLeaderboardEntry {
  Rank: number;
  GuildId: number;
  Name: string;
  GuildTier: number;
  GuildMMR: number;
}

export function fetchGuildLeaderboard(): Promise<GuildLeaderboardEntry[]> {
  return authedGet<GuildLeaderboardEntry[]>('/api/v1/leaderboard/guilds');
}

export interface CodexEntry {
  MonsterId: number;
  Level: number;
  Kills: number;
  NextLevelKills: number;
}

export function fetchCodex(): Promise<CodexEntry[]> {
  return authedGet<CodexEntry[]>('/api/v1/codex/snapshot');
}

export interface RaceMasteryEntry {
  RaceId: number;
  Level: number;
  Experience: number;
  NextLevelExperience: number;
}

export function fetchRaceMastery(): Promise<RaceMasteryEntry[]> {
  return authedGet<RaceMasteryEntry[]>('/api/v1/mastery/snapshot');
}

// ---------------------------------------------------------------------------
// Season pass, breeding and the store
// ---------------------------------------------------------------------------

// Modul: ChroniclePassLevel and AccumulatedSeasonalXp used to ride on
// StateUpdatePacket and were moved off it - low-frequency metadata does not
// belong on a 10 Hz packet - so this endpoint is their only home.
export interface PlayerMetadata {
  ChroniclePassLevel: number;
  AccumulatedSeasonalXp: number;
  EventHorizonTransactionCount: number;
}

export function fetchMetadata(): Promise<PlayerMetadata> {
  return authedGet<PlayerMetadata>('/api/v1/player/metadata');
}

export interface BreedingCandidate {
  CharacterId: string;
  Level: number;
  AgePhase: number;
  GenerationIndex: number;
  IsBreedingActive: boolean;
  BreedingCooldownEndEpoch: number;
  IsEpicMutation: boolean;
  IsInbred: boolean;
  LocusRaceDominant: number;
  LocusRaceRecessive: number;
}

export function fetchBreedingRoster(): Promise<BreedingCandidate[]> {
  return authedGet<BreedingCandidate[]>('/api/v1/breeding/roster');
}

export interface GeneLocusPreview {
  LocusName: string;
  ParentPaternalDominant: number;
  ParentMaternalDominant: number;
  PredictedMinDominant: number;
  PredictedMaxDominant: number;
  MutationChancePct: number;
}

export interface BreedingPreview {
  IsEligible: boolean;
  IneligibleReason: string;
  IsInbredRisk: boolean;
  BreedingCostGold: number;
  HasSufficientGold: boolean;
  Loci: GeneLocusPreview[];
}

export function fetchBreedingPreview(paternalId: string, maternalId: string): Promise<BreedingPreview> {
  const query = new URLSearchParams({ paternalId, maternalId });
  return authedGet<BreedingPreview>(`/api/v1/breeding/preview?${query}`);
}

// Modul: the catalog carries NO PRICE - only the product id and how many
// diamonds it grants. Real money pricing lives in the storefront, which this
// client does not reach, so nothing here may present a currency amount.
export interface StoreCatalogEntry {
  ProductId: string;
  DiamondAmount: number;
}

export function fetchStoreCatalog(): Promise<StoreCatalogEntry[]> {
  return authedGet<StoreCatalogEntry[]>('/api/v1/store/catalog');
}
