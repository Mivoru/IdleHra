// Modul: the REST surface, typed in one place.
//
// The Unity client grew 24 separate hand-written caches because it had no
// data-fetching library - each one its own timer, its own staleness rule and
// its own invalidation bug. The port plan's answer is TanStack Query, so this
// file is deliberately ONLY the typed fetchers and their query keys; caching,
// deduplication, retry and invalidation all belong to the query client and are
// never reimplemented here.

import { authedGet, authedPost } from './auth';

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
  friends: ['social', 'friends'] as const,
  onlineStats: ['stats', 'online'] as const,
  achievements: ['meta', 'achievements'] as const,
  villageNewcomers: ['village', 'newcomers'] as const,
  loginBonus: ['meta', 'loginBonus'] as const,
  leaderboard: ['meta', 'leaderboard'] as const,
  guildLeaderboard: ['meta', 'leaderboard', 'guilds'] as const,
  codex: ['meta', 'codex'] as const,
  metadata: ['meta', 'metadata'] as const,
  breedingRoster: ['meta', 'breeding'] as const,
  ancestorsHall: ['meta', 'ancestors'] as const,
  deeds: ['meta', 'deeds'] as const,
  breedingPreview: (a: string, b: string) => ['meta', 'breeding', 'preview', a, b] as const,
  villagerBreedingPreview: (heroId: string, newcomerId: number) =>
    ['meta', 'breeding', 'preview', 'village', heroId, newcomerId] as const,
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
  mailbox: ['player', 'mailbox'] as const,
  guildLogistics: ['social', 'guild', 'logistics'] as const,
  guildDepot: ['social', 'guild', 'depot'] as const,
  guildShardMatch: ['social', 'guild', 'shardMatch'] as const,
  codexRegions: ['meta', 'codex', 'regions'] as const,
  storefront: ['shop', 'storefront'] as const,
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
  /** Which of the seven equipment slots it is worn in, or -1. Sent by the
   *  server rather than re-derived from the BaseItemId: the two disagreed for
   *  four of the seven pieces, so a fully equipped character rendered three
   *  filled slots and four empty ones. */
  EquippedInSlotIndex: number;
  Affixes: AffixMap;
  IsAffixLocked: boolean;
}

export interface InventoryStack {
  ItemId: string;
  /** How many the player has. One store - see InventoryStackResponse. */
  Quantity: number;
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

export interface OnlineStats {
  OnlineCount: number;
}

export async function fetchOnlineStats() {
  const res = await fetch('/api/v1/stats/online');
  if (!res.ok) throw new Error('Failed to fetch online stats');
  return res.json() as Promise<OnlineStats>;
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

export interface MarketBrowseFilters {
  /** Substring, not an exact id. Empty means every item. */
  baseItemId?: string;
  /** Equipment slot indices to include. Empty or omitted means every slot. */
  slotIndexes?: number[];
  /** Region tiers 1-5 (the LOCATION the gear belongs to, not its rarity). */
  tiers?: number[];
  minQualityTier?: number;
  maxQualityTier?: number;
  sortBy?: 'price' | 'rarity' | 'name';
  descending?: boolean;
  pageIndex?: number;
  pageSize?: number;
}

export interface MarketBrowsePage {
  Listings: MarketListing[];
  TotalCount: number;
  PageIndex: number;
  PageSize: number;
}

/**
 * Every filter is optional. The endpoint used to 400 without an exact
 * BaseItemId AND match QualityTier exactly, so the only question a player could
 * ask was "is this precise item at this precise rarity for sale" - which nobody
 * can ask about a marketplace they have never seen. No filters returns the
 * whole book, paginated.
 */
export function fetchMarketListings(filters: MarketBrowseFilters = {}): Promise<MarketBrowsePage> {
  const query = new URLSearchParams({
    baseItemId: filters.baseItemId ?? '',
    slotIndexes: (filters.slotIndexes ?? []).join(','),
    tiers: (filters.tiers ?? []).join(','),
    minQualityTier: String(filters.minQualityTier ?? 0),
    maxQualityTier: String(filters.maxQualityTier ?? 13),
    sortBy: filters.sortBy ?? 'price',
    descending: filters.descending ? '1' : '0',
    pageIndex: String(filters.pageIndex ?? 0),
    pageSize: String(filters.pageSize ?? 24),
  });
  return authedGet<MarketBrowsePage>(`/api/v1/market/listings?${query}`);
}

export interface MarketPricePoint {
  Epoch: number;
  Price: number;
}

export interface MarketPriceHistory {
  BaseItemId: string;
  QualityTier: number;
  LastPrice: number;
  TradeCount: number;
  AveragePrice: number;
  LowPrice: number;
  HighPrice: number;
  /** Null where nothing traded before that window opened - "unknown", not "unchanged". */
  ChangeDayPct: number | null;
  ChangeWeekPct: number | null;
  ChangeMonthPct: number | null;
  /** Oldest first, ready to plot. */
  Points: MarketPricePoint[];
  /** The seller's own wealth-scaled burn and their guild's cut, both percent. */
  FeePct: number;
  GuildTaxPct: number;
}

/**
 * What this item has actually been selling for, over the last thirty days.
 *
 * Reads the trade archive, which has recorded every completed sale with a
 * timestamp since the market shipped - so the history is real from the first
 * request rather than starting to accumulate from today.
 */
export function fetchMarketPriceHistory(baseItemId: string, qualityTier: number): Promise<MarketPriceHistory> {
  const query = new URLSearchParams({ baseItemId, qualityTier: String(qualityTier) });
  return authedGet<MarketPriceHistory>(`/api/v1/market/history?${query}`);
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

/**
 * Approve or reject a pending application.
 *
 * `Success: false` is a NORMAL answer, not an error - ApproveApplicationAsync
 * returns it when the caller is not the leader, when the guild is full, or
 * when the application was already handled by someone else. The HTTP status is
 * 200 in every one of those cases, so a caller that only checks the status
 * reports a rejection as a success.
 *
 * The request property is `applicationId` in CAMEL CASE while the response
 * DTO is PascalCase, and the two support/billing endpoints nearby use
 * PascalCase for their requests. There is no rule to infer here - each body
 * shape was read off its own handler, and a plausible-looking PascalCase
 * `ApplicationId` gets a 400 with no hint as to why.
 */
export interface GuildApplicationActionResult {
  Success: boolean;
}

export function approveGuildApplication(applicationId: number): Promise<GuildApplicationActionResult | null> {
  return authedPost<GuildApplicationActionResult>('/api/v1/guild/applications/approve', {
    applicationId,
  });
}

export function rejectGuildApplication(applicationId: number): Promise<GuildApplicationActionResult | null> {
  return authedPost<GuildApplicationActionResult>('/api/v1/guild/applications/reject', {
    applicationId,
  });
}

export async function kickGuildMember(targetPlayerId: number): Promise<void> {
  await authedPost('/api/v1/guilds/kick', { targetPlayerId });
}

export async function promoteGuildMember(targetPlayerId: number): Promise<void> {
  await authedPost('/api/v1/guilds/promote', { targetPlayerId });
}

export async function demoteGuildMember(targetPlayerId: number): Promise<void> {
  await authedPost('/api/v1/guilds/demote', { targetPlayerId });
}

// ---------------------------------------------------------------------------
// /api/v1/guild/logistics/snapshot
// ---------------------------------------------------------------------------

/**
 * The guild depot's per-material stock against its requirement.
 *
 * NEVER APPEND A QUERY STRING TO THIS URL. The handler treats ANY query as
 * tampering: it calls ForceDisconnect on the player's live WebSocket session
 * and answers 403. So a harmless-looking cache-buster (`?t=${Date.now()}`) -
 * the reflex fix when a snapshot looks stale - drops the player out of the
 * game. The storefront endpoint below behaves identically, via
 * ValidateStorefrontQuery.
 *
 * A player with no guild gets 200 and an empty list, not an error, so there is
 * nothing to gate on GuildId here.
 */
export interface GuildLogisticsEntry {
  MaterialId: number;
  CurrentStock: number;
  TargetRequirement: number;
}

export function fetchGuildLogistics(): Promise<GuildLogisticsEntry[]> {
  return authedGet<GuildLogisticsEntry[]>('/api/v1/guild/logistics/snapshot');
}

// ---------------------------------------------------------------------------
// /api/v1/guild/shard-match
// ---------------------------------------------------------------------------

/**
 * The cross-shard match this guild is committed to, or null.
 *
 * This exists for one reason: `SubmitShardAttack` is refused - BY
 * DISCONNECTING - when its TargetMatchUuid disagrees with the match the server
 * already has the player committed to, and that id lived only in the server's
 * tick state. Without this endpoint the only way to send the command was to
 * guess, so the web client shipped the screen with the button missing.
 *
 * Null is a NORMAL answer, not an error: no guild, or a guild with no running
 * match, both return 200 with a null body.
 */
export interface GuildShardMatch {
  MatchUuid: string;
  ActiveMatchMmr: number;
  GlobalNodeRemainingHp: number;
  /** False means this guild is defending rather than attacking. */
  IsAttacker: boolean;
}

export function fetchGuildShardMatch(): Promise<GuildShardMatch | null> {
  return authedGet<GuildShardMatch | null>('/api/v1/guild/shard-match');
}

// ---------------------------------------------------------------------------
// The village chest
// ---------------------------------------------------------------------------

/**
 * Selling or binning from the chest.
 *
 * `Success: false` arrives with HTTP 200 - the item was already gone, or the
 * quantity was nonsense. Reason names which. Same shape as the guild
 * application endpoints, and the same trap: a caller that only checks the
 * status reports a failure as a sale.
 */
export interface ChestActionResult {
  Success: boolean;
  GoldGained: number;
  Reason: string;
}

export function sellFromChest(target: { equipmentId: number } | { itemId: string; quantity: number }) {
  return authedPost<ChestActionResult>('/api/v1/chest/sell', target);
}

export function discardFromChest(target: { equipmentId: number } | { itemId: string; quantity: number }) {
  return authedPost<ChestActionResult>('/api/v1/chest/discard', target);
}

// ---------------------------------------------------------------------------
// /api/v1/mailbox/list
// ---------------------------------------------------------------------------

/**
 * Unclaimed mail. The server already filters out claimed and pending rows, so
 * everything returned here is actionable - there is no "read" state to model.
 *
 * `Id` is the MAIL ROW id and is what ClaimMailItem takes. BaseItemId is a
 * string identifier, not a numeric definition id, matching the inventory.
 */
export interface MailboxEntry {
  Id: number;
  BaseItemId: string;
  QualityTier: number;
  Quantity: number;
  GoldAttachment: number;
  HasEquipmentAttachment: boolean;
  SenderName: string | null;
  MessageText: string | null;
  /** Unix seconds. */
  ReceivedTimestamp: number;
}

export function fetchMailbox(): Promise<MailboxEntry[]> {
  return authedGet<MailboxEntry[]>('/api/v1/mailbox/list');
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

/**
 * The village gene pool: who lives there, and how many more will fit.
 *
 * The CAP comes from the server rather than being derived here, because
 * "11 / 14" is the number that makes the keep-or-turn-away decision legible
 * and mirroring VillagerArrivalRules.PopulationCapFor into TypeScript for one
 * label would be a tenth copy of a server rule.
 */
export interface VillageNewcomer {
  Id: number;
  RaceId: number;
  IsFemale: boolean;
  AptitudeStrength: number;
  AptitudeSkill: number;
  AptitudeEndurance: number;
  AptitudeFortune: number;
  ArrivedAtEpoch: number;
  IsElder: boolean;
}

export interface VillageNewcomersSnapshot {
  InnLevel: number;
  PopulationCap: number;
  IntervalSeconds: number;

  /**
   * What throwing a feast costs right now. Escalates 1.6x per recruitment
   * within a season off a counter only the server has, so this cannot be
   * computed client-side and a button that guessed would eventually quote the
   * wrong number.
   */
  RecruitCostGold: number;

  /** Why a feast is refused, or "" when it is not. Comes from the same
   * function the command itself runs, so a disabled button and a rolled-back
   * command can never disagree about why. */
  RecruitBlockedReason: string;

  Newcomers: VillageNewcomer[];
}

export function fetchVillageNewcomers(): Promise<VillageNewcomersSnapshot> {
  return authedGet<VillageNewcomersSnapshot>('/api/v1/village/newcomers');
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

  // The second and third ranking keys. The board sorts by level, then by the
  // hardest monster a player has ever put down, then by kills of it - so it has
  // to be able to show them.
  HardestMonsterId: number;
  HardestMonsterName: string;
  KillsOfHardest: number;
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

  // Modul: hero x villager. A pair needs one of each and the same race, and
  // the aptitudes are what the pairing is chosen FOR - all three were missing
  // from this roster, so the village pairing screen could not filter or
  // compare anything.
  IsFemale: boolean;
  AptitudeStrength: number;
  AptitudeSkill: number;
  AptitudeEndurance: number;
  AptitudeFortune: number;

  LocusRaceDominant: number;
  LocusRaceRecessive: number;
}

export function fetchBreedingRoster(): Promise<BreedingCandidate[]> {
  return authedGet<BreedingCandidate[]>('/api/v1/breeding/roster');
}

/**
 * A member of the Hall of Ancestors: everybody the account owns, and what they
 * carry. The breeding roster answers "who can I pair"; this answers "who
 * carries into next season, and where do they stand".
 */
export interface HallMember {
  CharacterId: string;
  Level: number;
  AgePhase: number;
  IsFemale: boolean;
  SlotIndex: number;

  /** Which of the three playable slots they occupy, or -1 for benched. */
  PlayableSlot: number;

  RaceId: number;
  GenerationIndex: number;
  IsEpicMutation: boolean;
  IsInbred: boolean;

  /** Marked by the player to carry through the rollover. */
  IsKept: boolean;

  /**
   * Whether the cull would keep them if the season ended NOW. A cap that only
   * reveals what it did after a rollover has already deleted somebody is not a
   * decision, it is a surprise.
   */
  WouldCarry: boolean;

  IsMainCharacter: boolean;
  AptitudeStrength: number;
  AptitudeSkill: number;
  AptitudeEndurance: number;
  AptitudeFortune: number;

  /** "" for an unknown parent - a founder and a villager's child both have
   * one, and neither is an error. */
  ParentPaternalId: string;
  ParentMaternalId: string;
}

export interface HallSnapshot {
  Cap: number;
  MaxCap: number;
  SlotsPurchased: number;
  NextSlotCostDiamonds: number;
  Diamonds: number;
  PlayableSlots: number;
  Members: HallMember[];
}

export function fetchAncestorsHall(): Promise<HallSnapshot> {
  return authedGet<HallSnapshot>('/api/v1/ancestors/hall');
}

/**
 * The Book of Deeds, as the SERVER sees it.
 *
 * The chapter definitions live on the server rather than here, and that is not
 * a preference: completing a chapter awards a Seal, a Seal grants +2 permanent
 * skill points every season forever, and a client that decided when it had
 * earned one could award itself the whole tree. So this renders an answer, it
 * does not compute one.
 */
export interface DeedEntry {
  Id: string;
  Title: string;
  Body: string;
  Screen: string;
  Target: number;
  Current: number;
  Done: boolean;
}

export interface DeedChapterEntry {
  Index: number;
  Title: string;
  Reward: string;
  /** A chapter opens when the one before it completes. */
  IsOpen: boolean;
  IsComplete: boolean;
  HasSeal: boolean;
  Deeds: DeedEntry[];
}

export interface DeedsSnapshot {
  SealsEarnedMask: number;
  SealCount: number;
  SkillPointsFromSeals: number;
  SkillPointsPerSeal: number;
  /** Chapters sealed by THIS request, so the moment can be celebrated rather
   * than noticed as a number that changed. */
  NewlySealedMask: number;
  Chapters: DeedChapterEntry[];
}

export function fetchDeeds(): Promise<DeedsSnapshot> {
  return authedGet<DeedsSnapshot>('/api/v1/deeds/snapshot');
}

export interface GeneLocusPreview {
  LocusName: string;
  ParentPaternalDominant: number;
  ParentMaternalDominant: number;
  PredictedMinDominant: number;
  PredictedMaxDominant: number;
  MutationChancePct: number;
}

/**
 * The band a single aptitude can land in. Exact, not sampled - a child takes
 * each aptitude from one parent and mutation moves it by at most one, so the
 * reachable range is bounded. See BreedingAptitudes.PreviewOne.
 *
 * The 5% epic roll's +1 is NOT in these numbers; it would widen every band by
 * one to describe something that almost never happens.
 */
export interface AptitudePreview {
  AptitudeName: string;
  ParentHero: number;
  ParentPartner: number;
  PredictedMin: number;
  PredictedMax: number;
}

export interface BreedingPreview {
  IsEligible: boolean;
  IneligibleReason: string;
  IsInbredRisk: boolean;
  BreedingCostGold: number;
  HasSufficientGold: boolean;
  Loci: GeneLocusPreview[];
  Aptitudes: AptitudePreview[];
}

export function fetchBreedingPreview(paternalId: string, maternalId: string): Promise<BreedingPreview> {
  const query = new URLSearchParams({ paternalId, maternalId });
  return authedGet<BreedingPreview>(`/api/v1/breeding/preview?${query}`);
}

/**
 * The same question for THE standard pair: one of your heroes and somebody
 * from the village. A separate endpoint because the partner is a
 * village_newcomers row rather than a character.
 */
export function fetchVillagerBreedingPreview(
  heroId: string,
  newcomerId: number,
): Promise<BreedingPreview> {
  const query = new URLSearchParams({ heroId, newcomerId: String(newcomerId) });
  return authedGet<BreedingPreview>(`/api/v1/breeding/village-preview?${query}`);
}

// Modul: the catalog carries NO PRICE - only the product id and how many
// diamonds it grants. Real money pricing lives in the storefront below, which
// is a separate, personalised endpoint; nothing built from THIS list may
// present a currency amount.
export interface StoreCatalogEntry {
  ProductId: string;
  DiamondAmount: number;
}

export function fetchStoreCatalog(): Promise<StoreCatalogEntry[]> {
  return authedGet<StoreCatalogEntry[]>('/api/v1/store/catalog');
}

// ---------------------------------------------------------------------------
// /api/v1/storefront/listings
// ---------------------------------------------------------------------------

/**
 * The PERSONALISED storefront. This is not the same list for every player:
 * requesting it runs StorefrontSegmentationEngine, which sorts the player into
 * a cohort by lifetime spend, account age and days since their last purchase,
 * and returns only that cohort's listings.
 *
 * Three consequences the UI has to respect. Fetching this WRITES - it upserts
 * a PlayerSegmentationProfile row - so it must not be polled on a timer or
 * refetched on window focus the way a read-only snapshot can be. Two players
 * comparing screens will legitimately see different prices, so the screen
 * should never describe a listing as "the" price. And as with the guild depot
 * above, ANY query string force-disconnects the player's game session.
 *
 * `PriceInCents` is real money. Nothing in this client can complete such a
 * purchase - that needs a platform store - so prices are shown as information
 * and the buy path stays disabled rather than pretending.
 */
export interface StorefrontListing {
  ListingId: number;
  ProductIdentifier: string;
  DiamondPackageYield: number;
  PriceInCents: number;
}

export function fetchStorefront(): Promise<StorefrontListing[]> {
  return authedGet<StorefrontListing[]>('/api/v1/storefront/listings');
}

// ---------------------------------------------------------------------------
// /api/v1/codex/regions
// ---------------------------------------------------------------------------

/**
 * Per-region kill progress toward completion, and the loot-luck bonus a
 * completed region grants permanently.
 *
 * The server loops regions 1-10 and only emits those that actually exist in
 * the content tables, so the list is shorter than ten - never index it by
 * region number.
 */
export interface RegionProgress {
  RegionId: number;
  CurrentKills: number;
  RequiredKills: number;
  IsCompleted: boolean;
  LootLuckBonusPct: number;
}

export function fetchCodexRegions(): Promise<RegionProgress[]> {
  return authedGet<RegionProgress[]>('/api/v1/codex/regions');
}

// ---------------------------------------------------------------------------
// /api/v1/achievements/state
// ---------------------------------------------------------------------------

/**
 * The claim BITMASKS, as opposed to /achievements/snapshot's per-achievement
 * progress rows. Both exist because they answer different questions: the
 * snapshot says how far along each achievement is, this says which rewards
 * have been taken.
 *
 * ClaimedMilestonesBitmask is a C# ulong. Fifty battle-pass milestones exist,
 * so real values stay under 2^50 and survive JSON's double - but it would
 * silently lose precision if the pass ever grew past 53 milestones, which is
 * worth knowing before anyone adds one.
 */
export interface AchievementsState {
  ClaimedAchievementFlags: number;
  TotalAchievementsClaimedCount: number;
  ClaimedMilestonesBitmask: number;
}

export function fetchAchievementsState(): Promise<AchievementsState> {
  return authedGet<AchievementsState>('/api/v1/achievements/state');
}

// ---------------------------------------------------------------------------
// /api/v1/support/tickets/create
// ---------------------------------------------------------------------------

/**
 * Sends a diagnostic trace with a support request.
 *
 * WHAT THIS ACTUALLY DOES TODAY: the handler reads the TraceLog property,
 * writes one line to the server console, and returns 200. It does not store a
 * ticket, does not assign an id, and returns no body. So the UI must not
 * promise a reply or show a reference number - a 200 means "the server
 * received it", nothing more, and saying more would be a lie the player only
 * discovers by waiting for an answer that never comes.
 *
 * Scrubbing is the CLIENT'S job by the server's own design decision, so
 * `scrubTrace` runs before anything leaves the browser.
 */
export function submitSupportTicket(traceLog: string): Promise<null> {
  return authedPost<null>('/api/v1/support/tickets/create', {
    TraceLog: scrubTrace(traceLog),
  }).then(() => null);
}

/**
 * Removes the obvious personal identifiers before a trace leaves the browser.
 *
 * Deliberately conservative and deliberately not clever: it strips bearer
 * tokens, email addresses and anything that looks like a long opaque id. It is
 * a reduction of risk, not a guarantee of anonymity, and the UI says so rather
 * than implying the log is safe.
 */
export function scrubTrace(traceLog: string): string {
  return traceLog
    .replace(/Bearer\s+[A-Za-z0-9._~+/-]+=*/gi, 'Bearer [redacted]')
    .replace(/[\w.+-]+@[\w-]+\.[\w.-]+/g, '[email]')
    .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, '[uuid]')
    .slice(0, 16000);
}

// ---------------------------------------------------------------------------
// Admin Endpoints
// ---------------------------------------------------------------------------

export interface AdminStatus {
  isAdmin: boolean;
  profanityEnabled: boolean;
}

export function fetchAdminStatus(): Promise<AdminStatus> {
  return authedGet<AdminStatus>('/api/v1/admin/status');
}

export function adminToggleProfanity(enabled: boolean): Promise<null> {
  return authedPost<null>('/api/v1/admin/profanity', { enabled });
}

export function adminAnnounce(text: string): Promise<null> {
  return authedPost<null>('/api/v1/admin/announce', { text });
}

export function adminBan(username: string): Promise<null> {
  return authedPost<null>(`/api/v1/admin/ban?username=${encodeURIComponent(username)}`, {});
}

export function adminUnban(username: string): Promise<null> {
  return authedPost<null>(`/api/v1/admin/unban?username=${encodeURIComponent(username)}`, {});
}

export interface AdminMailRequest {
  TargetUsername: string | null;
  BaseItemId: string | null;
  QualityTier: number;
  Quantity: number;
  Gold: number;
  SenderName: string | null;
  MessageText: string | null;
}

export function adminSendMail(req: AdminMailRequest): Promise<null> {
  return authedPost<null>('/api/v1/admin/mail', req);
}

export interface GuildDepotResponse {
  Balances: Record<number, number>;
  Leaderboard: GuildMemberInfo[];
  ActiveBuffs: GuildActiveBuffInfo[];
}

export interface GuildMemberInfo {
  PlayerId: number;
  Name: string;
  WeeklyContributionPoints: number;
}

export interface GuildActiveBuffInfo {
  BuffType: string;
  ExpiresAtEpoch: number;
}

export function fetchGuildDepot(): Promise<GuildDepotResponse> {
  return authedGet<GuildDepotResponse>('/api/v1/guilds/depot');
}

export function donateToGuildDepot(materialId: number, quantity: number): Promise<void> {
  return authedPost<void>('/api/v1/guilds/depot/donate', { MaterialId: materialId, Quantity: quantity }).then(() => {});
}

export function activateGuildBuff(buffType: string): Promise<void> {
  return authedPost<void>('/api/v1/guilds/buffs/activate', { BuffType: buffType }).then(() => {});
}
