// GENERATED FILE - DO NOT EDIT BY HAND.
//
// Produced by client_web/scripts/generate-protocol.mjs from the server's own
// `--dump-protocol` output, which comes from the same reflected field plan
// PacketJsonCodec uses to encode. These types therefore cannot describe a
// packet the server does not actually send.
//
// Regenerate with:  npm run generate:protocol
// Verify in CI with: node scripts/generate-protocol.mjs --check

export const TYPE_PROPERTY = "type" as const;
export const MODE_PROPERTY = "mode" as const;

/** The `type` discriminator carried by every packet on this wire. */
export const PacketType = {
  AuthHandshake: "AuthHandshake",
  ClientCommand: "ClientCommand",
  StateUpdate: "StateUpdate",
  RequestChatMessage: "RequestChatMessage",
  ResponseChatMessage: "ResponseChatMessage",
  ResponseLootDrop: "ResponseLootDrop",
} as const;

export type PacketTypeName = (typeof PacketType)[keyof typeof PacketType];

/** AuthHandshakePacket - 530 bytes on the binary wire. */
export interface AuthHandshake {
  readonly type: typeof PacketType.AuthHandshake;
  JwtToken: string;
  JwtTokenLength: number;
  AssetHash: number;
  PlatformSignature: number;
}

/** ClientCommandPacket - 359 bytes on the binary wire. */
export interface ClientCommand {
  readonly type: typeof PacketType.ClientCommand;
  Command: number;
  TargetId: number;
  SecondaryId: number;
  TertiaryId: number;
  LimitPrice: number;
  IsBuy: number;
  QualityTier: number;
  TargetGuid: string;
  SecondaryGuid: string;
  LogicEpochCounter: number;
  TargetUnlockId: number;
  RequestedSlotIndex: number;
  MaterialId: number;
  DepositQuantity: number;
  MatchId: number;
  ClientPredictedTurnCounter: number;
  TargetPlayerId: number;
  MentorshipRole: number;
  TargetBuildingId: number;
  TargetVillagerSlot: number;
  ChallengeId: number;
  ChallengeVerificationHash: number;
  TargetedBossId: number;
  ClientPredictedDamage: number;
  DeviceTokenBytes: string;
  TargetPlatformFamily: number;
  PushReserved0: number;
  ConfirmationHash: number;
  TargetLanguageId: number;
  ComplianceReserved0: number;
  ComplianceReserved1: number;
  ComplianceReserved2: number;
  ChronoSecondsRequested: number;
  TargetSlotIndex: number;
  RawTransactionReceipt: string;
  TargetProductIdHash: number;
  ActiveUiContextBitmask: number;
  TargetRecipeId: number;
  CraftingSlotIndex: number;
  TargetAchievementId: number;
  MigrationToken: number;
  ConsumableItemId: number;
  ConsumableSlotTarget: number;
  TargetMilestoneIndex: number;
  ChronoWarpDurationSeconds: number;
  ChronoTargetSlot: number;
  RequestedSpeedMultiplier: number | string;
  TargetMatchUuid: string;
  TelemetryEventCount: number;
  NetworkDiagnosticsToken: number;
  RerollOperationKind: number;
  RerollAutoMaxAttempts: number;
  RerollStopMinRarity: number;
  RerollStopAffixIndex: number;
}

/** StateUpdatePacket - 775 bytes on the binary wire. */
export interface StateUpdate {
  readonly type: typeof PacketType.StateUpdate;
  PlayerId: number;
  ActiveActivityId: number;
  CurrentProgressTicks: number;
  RequiredProgressTicks: number;
  InventorySpaceRemaining: number;
  InventoryCapacity: number;
  Food1_ItemId: number;
  Food1_Count: number;
  Food2_ItemId: number;
  Food2_Count: number;
  Food3_ItemId: number;
  Food3_Count: number;
  ActivityHaltReason: number;
  CurrentMonsterId: number;
  CurrentMonsterHp: number;
  PlayerHp: number;
  Quarantine_Active: number;
  CurrentLevel: number;
  CurrentXp: number;
  Slot1_CharacterId: string;
  Slot1_AgeTicks: number;
  Slot1_AgePhase: number;
  Slot2_CharacterId: string;
  Slot2_AgeTicks: number;
  Slot2_AgePhase: number;
  Slot3_CharacterId: string;
  Slot3_AgeTicks: number;
  Slot3_AgePhase: number;
  Slot1_RaceId: number;
  Slot2_RaceId: number;
  Slot3_RaceId: number;
  CachedMentorCount: number;
  WoodcuttingMasteryXp: number;
  WoodcuttingMasteryLevel: number;
  MiningMasteryXp: number;
  MiningMasteryLevel: number;
  FishingMasteryXp: number;
  FishingMasteryLevel: number;
  HerbalismMasteryXp: number;
  HerbalismMasteryLevel: number;
  GatheringProgressTicks: number;
  CompletedAreaFlags: number;
  HighestLocationReached: number;
  HighestUnlockedRegion: number;
  HumanMasteryLevel: number;
  VilaMasteryLevel: number;
  DraugrMasteryLevel: number;
  VillagePopulation: number;
  AccumulatedTimeBankMs: number;
  BankedChronoSeconds: number | string;
  IsChronoAccelerating: number;
  AutoEatThreshold: number;
  STR: number;
  DEX: number;
  CON: number;
  LCK: number;
  EquippedWeaponId: number;
  EquippedWeaponAffixLocked: number;
  EquippedChestId: number;
  EquippedHelmetId: number;
  EquippedGlovesId: number;
  EquippedBootsId: number;
  EquippedAmuletId: number;
  EquippedRingId: number;
  UnlockedRaceBitmask: number;
  Inherit_Damage: number;
  Inherit_MaxHp: number;
  Inherit_XpGain: number;
  Inherit_GoldGain: number;
  Inherit_GatheringYield: number;
  Inherit_LootLuck: number;
  Slot2ActivityId: number;
  Slot3ActivityId: number;
  Slot2ActivityHaltReason: number;
  Slot3ActivityHaltReason: number;
  EquippedArmorAffixLocked: number;
  EquippedLeggingsId: number;
  EquippedLeggingsAffixLocked: number;
  CachedMiningMonolithLevel: number;
  CachedWoodcuttingMonolithLevel: number;
  ActiveOffensivePotionId: number;
  OffensivePotionDurationMs: number;
  ActiveDefensivePotionId: number;
  DefensivePotionDurationMs: number;
  WorldBossMaxHp: number;
  WorldBossCurrentHp: number;
  ActiveEventType: number;
  IsFreshAccount: number;
  PlayerAccuracyRating: number;
  PlayerArmorRating: number;
  PlayerBlockStrengthPct: number | string;
  CommandResult0_Code: number;
  CommandResult0_Tick: number;
  CommandResult1_Code: number;
  CommandResult1_Tick: number;
  CommandResult2_Code: number;
  CommandResult2_Tick: number;
  CommandResult3_Code: number;
  CommandResult3_Tick: number;
  CachedCurrentToolTier: number;
  AxeToolTier: number;
  PickaxeToolTier: number;
  RodToolTier: number;
  ToolGatherSpeedPct: number;
  ToolGatherYieldPct: number;
  ToolRareFindPct: number;
  CachedMaxPopulationCapacity: number;
  CachedInnMaturationBonus: number;
  ActiveChildMaturationMs: number;
  ActiveGuildWarId: number;
  CachedWarMultiplier: number | string;
  GuildCombatVanguardPoints: number;
  GuildProductionLogisticsPoints: number;
  GuildGatheringSupplyChainPoints: number;
  EnemyCombatVanguardPoints: number;
  EnemyProductionLogisticsPoints: number;
  EnemyGatheringSupplyChainPoints: number;
  LogicEpochCounter: number;
  LegacyShardBalance: number;
  CitizenMultiSlotsUnlocked: number;
  GuildLogisticsCurrentStock: number;
  GuildLogisticsTargetRequirement: number;
  CombatSimulationMatchId: number;
  CombatSimulationTurnCounter: number;
  CombatSimulationDamageDelta: number;
  ActiveMentorPlayerId: number;
  MentorshipExpBonusMultiplier: number | string;
  ForgeLevel: number;
  InnLevel: number;
  BreedingLevel: number;
  AcademyLevel: number;
  CurrentPopulationCount: number;
  ActiveChallengeSeed: number;
  ActiveLanguageState: number;
  CurrentSimulationSpeedMultiplier: number;
  PremiumCurrencyBalance: number;
  ActiveAudioTrackId: number;
  TotalItemsCraftedCount: number;
  ActiveStatusEffectModifierBitmask: number;
  RemainingBuffDurationTicks: number;
  VisualBankedChronoSeconds: number;
  ActiveChronoLockExpirationTicks: number;
  GlobalNodeRemainingHp: number;
  NetworkDiagnosticsToken: number;
  Gold: number;
  WorldBossAttemptCount: number;
  WorldBossEventState: number;
  WorldBossEventEndEpoch: number;
  GuildLogisticsLevel: number;
  GuildRaidTier: number;
  GuildRaidBossCurrentHp: number;
  GuildRaidBossMaxHp: number;
  LumberjackLevel: number;
  QuarryLevel: number;
  MineLevel: number;
  WarehouseLevel: number;
  CachedWoodStock: number;
  CachedStoneStock: number;
  CachedIronOreStock: number;
  TownHallLevel: number;
  CraftingWorkshopLevel: number;
  LegacyPerksBitmask: number;
  PendingUpgradeBuildingId: number;
  PendingUpgradeCompletesAtEpoch: number;
  UnlockedSkillsBitmask: number;
  CurrentMana: number;
  MaxMana: number;
  AvailableSkillPoints: number;
  Skill1CooldownRemainingMs: number;
  Skill2CooldownRemainingMs: number;
  Skill3CooldownRemainingMs: number;
  Skill4CooldownRemainingMs: number;
  LastSkillCastId: number;
  LastSkillCastSuccess: number;
  LastSkillCastResultTick: number;
  OfflineElapsedSeconds: number;
  OfflineGoldEarned: number;
  OfflineSlot1Gold: number;
  OfflineSlot1Xp: number;
  OfflineSlot1Drops: number;
  OfflineSlot2Gold: number;
  OfflineSlot2Xp: number;
  OfflineSlot2Drops: number;
  OfflineSlot3Gold: number;
  OfflineSlot3Xp: number;
  OfflineSlot3Drops: number;
  OfflineXpEarned: number;
  OfflineMaterialDropsGranted: number;
  OfflineSummaryTick: number;
  TicksSinceLastFlush: number;
}

/** RequestChatMessagePacket - 139 bytes on the binary wire. */
export interface RequestChatMessage {
  readonly type: typeof PacketType.RequestChatMessage;
  MessageLength: number;
  ChannelType: number;
  TargetPlayerId: number;
  MessageText: string;
}

/** ResponseChatMessagePacket - 147 bytes on the binary wire. */
export interface ResponseChatMessage {
  readonly type: typeof PacketType.ResponseChatMessage;
  SenderPlayerId: number;
  TimestampEpochMs: number;
  MessageLength: number;
  ChannelType: number;
  MessageText: string;
}

/** ResponseLootDropPacket - 22 bytes on the binary wire. */
export interface ResponseLootDrop {
  readonly type: typeof PacketType.ResponseLootDrop;
  PlayerId: number;
  ItemId: number;
  Quantity: number;
  MonsterId: number;
  QualityTier: number;
  DropKind: number;
}

/** Fields a client fills in; everything omitted defaults to zero server-side. */
export type AuthHandshakeDraft = Partial<Omit<AuthHandshake, 'type'>>;
export type ClientCommandDraft = Partial<Omit<ClientCommand, 'type'>>;
export type StateUpdateDraft = Partial<Omit<StateUpdate, 'type'>>;
export type RequestChatMessageDraft = Partial<Omit<RequestChatMessage, 'type'>>;
export type ResponseChatMessageDraft = Partial<Omit<ResponseChatMessage, 'type'>>;
export type ResponseLootDropDraft = Partial<Omit<ResponseLootDrop, 'type'>>;

/** The command opcodes. Numbering has deliberate gaps - see CommandType in C#. */
export const CommandType = {
  None: 0,
  ChangeActivity: 1,
  ExecuteForgeFusion: 2,
  PlaceLimitOrder: 3,
  ReloadState: 4,
  ContributeToGuild: 5,
  Logout: 6,
  Login: 7,
  ToggleChronoAcceleration: 8,
  MarketListItem: 9,
  MarketBuyItem: 10,
  ClaimMailItem: 11,
  DepositToBank: 12,
  WithdrawFromBank: 13,
  RerollItemAffix: 14,
  ExecuteBreeding: 15,
  UpdateAutoEatThreshold: 16,
  InitializeCrafting: 18,
  RegisterWorldBossDamage: 19,
  UpgradeTool: 21,
  AssignMentor: 22,
  ContributeToWarSupply: 23,
  ConsumeChronoCore: 24,
  PurchaseLegacyUnlocks: 25,
  DepositGuildMaterial: 26,
  ExecuteCombatTurn: 27,
  EstablishMentorship: 28,
  UpgradeBuilding: 29,
  EvictVillager: 30,
  AntiCheatChallengeResponse: 31,
  AttackWorldBoss: 32,
  RegisterPushToken: 33,
  TriggerGdprPurge: 34,
  SwitchLanguage: 35,
  SubmitPurchaseReceipt: 39,
  SyncBillingStatus: 40,
  ReportUiContextSwitch: 41,
  CraftItem: 42,
  ClaimAchievementReward: 43,
  InitiateNodeMigration: 44,
  ConsumeConsumableAsset: 45,
  ClaimBattlePassReward: 46,
  ActivateChronoBoost: 47,
  ConsumeTimeWarpCore: 48,
  RegisterGuildDefense: 49,
  SubmitShardAttack: 50,
  ReportTelemetryBurst: 51,
  PingNetworkDiagnostics: 52,
  LaunchGuildRaid: 53,
  EquipItem: 54,
  UnequipItem: 55,
  TerminateMentorship: 56,
  RequestUnlockSkill: 57,
  RequestCastSkill: 58,
  PurchaseBattlePass: 59,
  AddFriend: 60,
  RemoveFriend: 61,
  BlockPlayer: 62,
  UnblockPlayer: 63,
  ContributeGuildTreasury: 64,
  StockFoodSlot: 65,
  PurchaseInheritanceLevel: 66,
} as const;

export type CommandTypeName = keyof typeof CommandType;

/** Binary wire sizes, kept for tests that assert the binary path is untouched. */
export const PACKET_BYTE_SIZE = {
  AuthHandshake: 530,
  ClientCommand: 359,
  StateUpdate: 775,
  RequestChatMessage: 139,
  ResponseChatMessage: 147,
  ResponseLootDrop: 22,
} as const;

/** Server-computed challenge answers. See tests/antiCheat.test.ts. */
export const CHALLENGE_VECTORS: readonly {
  seed: number;
  playerId: number;
  logicEpochCounter: number;
  expectedHash: number;
}[] = [
  { seed: 1, playerId: 1, logicEpochCounter: 0, expectedHash: 3894212726 },
  { seed: 2738958700, playerId: 9, logicEpochCounter: 23, expectedHash: 1348711300 },
  { seed: 4294967295, playerId: 1042, logicEpochCounter: 1, expectedHash: 3115502839 },
  { seed: 2147483648, playerId: 7, logicEpochCounter: 4294967295, expectedHash: 805335653 },
  { seed: 305419896, playerId: 4294967298, logicEpochCounter: 5, expectedHash: 675803173 },
  { seed: 1831565813, playerId: 2147483647, logicEpochCounter: 2147483647, expectedHash: 1095946487 },
];

/** Server-computed GDPR confirmation hashes. See tests/antiCheat.test.ts. */
export const GDPR_CONFIRMATION_VECTORS: readonly {
  playerId: number;
  logicEpochCounter: number;
  expectedHash: number;
}[] = [
  { playerId: 1, logicEpochCounter: 0, expectedHash: 161968333 },
  { playerId: 1042, logicEpochCounter: 7, expectedHash: 3835261794 },
  { playerId: 9, logicEpochCounter: 2147483647, expectedHash: 1801647405 },
  { playerId: 7, logicEpochCounter: 4294967295, expectedHash: 2958100973 },
  { playerId: 4294967298, logicEpochCounter: 123456789, expectedHash: 4142868759 },
  { playerId: 2147483647, logicEpochCounter: 1, expectedHash: 1290345934 },
];
