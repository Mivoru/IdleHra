using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    public enum CommandType : byte
    {
        None = 0,
        ChangeActivity = 1,
        ExecuteForgeFusion = 2,
        PlaceLimitOrder = 3,
        ReloadState = 4,
        ContributeToGuild = 5,
        Logout = 6,
        Login = 7,
        ToggleChronoAcceleration = 8,
        MarketListItem = 9,
        MarketBuyItem = 10,
        ClaimMailItem = 11,
        DepositToBank = 12,
        WithdrawFromBank = 13,
        RerollItemAffix = 14,
        ExecuteBreeding = 15,
        UpdateAutoEatThreshold = 16,
        InitializeCrafting = 18,
        RegisterWorldBossDamage = 19,
        LegacyUpgradeBuilding = 20,
        UpgradeTool = 21,
        AssignMentor = 22,
        ContributeToWarSupply = 23,
        ConsumeChronoCore = 24,
        PurchaseLegacyUnlocks = 25,
        DepositGuildMaterial = 26,
        ExecuteCombatTurn = 27,
        EstablishMentorship = 28,
        UpgradeBuilding = 29,
        EvictVillager = 30,
        AntiCheatChallengeResponse = 31,
        AttackWorldBoss = 32,
        RegisterPushToken = 33,
        TriggerGdprPurge = 34,
        SwitchLanguage = 35,
        RequestLeaderboardSlice = 38,
        SubmitPurchaseReceipt = 39,
        SyncBillingStatus = 40,
        ReportUiContextSwitch = 41,
        CraftItem = 42,
        ClaimAchievementReward = 43,
        InitiateNodeMigration = 44,
        ConsumeConsumableAsset = 45,
        ClaimBattlePassReward = 46,
        ActivateChronoBoost = 47,
        ConsumeTimeWarpCore = 48,
        RegisterGuildDefense = 49,
        SubmitShardAttack = 50,
        ReportTelemetryBurst = 51,
        PingNetworkDiagnostics = 52
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ClientCommandPacket
    {
        public CommandType Command;
        public long TargetId;
        public long SecondaryId;
        public long TertiaryId;
        public int LimitPrice;
        public byte IsBuy;
        public int QualityTier;
        public System.Guid TargetGuid;
        public System.Guid SecondaryGuid;
        public long LogicEpochCounter;
        public uint TargetUnlockId;
        public uint RequestedSlotIndex;
        public uint MaterialId;
        public uint DepositQuantity;
        public uint MatchId;
        public uint ClientPredictedTurnCounter;
        public uint TargetPlayerId;
        public uint MentorshipRole;
        public uint TargetBuildingId;
        public uint TargetVillagerSlot;
        public uint ChallengeId;
        public uint ChallengeVerificationHash;
        public uint TargetedBossId;
        public uint ClientPredictedDamage;
        public fixed byte DeviceTokenBytes[64];
        public byte TargetPlatformFamily;
        public byte PushReserved0;
        public uint ConfirmationHash;
        public byte TargetLanguageId;
        public byte ComplianceReserved0;
        public byte ComplianceReserved1;
        public byte ComplianceReserved2;
        public uint ChronoSecondsRequested;
        public uint TargetSlotIndex;
        public fixed byte RawTransactionReceipt[64];
        public uint TargetProductIdHash;
        public uint ActiveUiContextBitmask;
        public uint TargetRecipeId;
        public uint CraftingSlotIndex;
        public uint TargetAchievementId;
        public uint MigrationToken;
        public uint ConsumableItemId;
        public uint ConsumableSlotTarget;
        public uint TargetMilestoneIndex;
        public uint ChronoWarpDurationSeconds;
        public uint ChronoTargetSlot;
        public double RequestedSpeedMultiplier;
        public System.Guid TargetMatchUuid;
        public uint TelemetryEventCount;
        public uint NetworkDiagnosticsToken;
        public uint TelemetryBurstPadding;
        public uint SecurityPadding;
        public fixed byte Sprint70ExpansionPadding[24];
    }
}
