using System.Collections.Concurrent;
using System.Linq;

namespace FolkIdle.Server.Engine
{
    public struct MarketMatchNotification
    {
        public long PlayerId;
        public long GoldDelta;
        // If buyer receives an item, they get the instance ID.
        public long? NewEquipmentInstanceId; 
    }

    public struct AchievementClaimRequest
    {
        public long PlayerId;
        public uint AchievementId;
        public LiveSessionContext LiveSession;
    }

    // Modul 13: ForgeSplicingEngine.ExecuteFusionAsync runs on a background
    // Task.Run thread with only value-type copies (no ref access to the live
    // TickStatePayload), so a successful upgrade is reported back to the tick
    // thread through this queue rather than mutated directly.
    public struct ForgeUpgradeNotification
    {
        public long PlayerId;
        public int ResultingQualityTier;
    }

    public struct MailClaimRequest
    {
        public long PlayerId;
        public long MailId;
        public long GoldAttachment;
        public bool HasItem;
    }

    public struct BankWithdrawRequest
    {
        public long PlayerId;
        public long BankId;
    }

    public struct BirthNotification
    {
        public long PlayerId;
        public System.Guid ChildCharacterId;
        public long GeneticVector;
    }

    public struct WorldBossAttemptUpdateNotification
    {
        public long PlayerId;
        public byte AttemptCount;
    }

    public struct MasteryUpdateNotification
    {
        public long PlayerId;
        public int RaceId;
        public int MasteryLevel;
    }

    public struct GuildUpdateNotification
    {
        public long GuildId;
        public bool IsMining;
        public int NewLevel;
    }

    public struct CraftingCompletionNotification
    {
        public long PlayerId;
        public int CraftedItemId;
        public int Quantity;
        // if this was an equipment craft, maybe we give gold or something? The prompt says: "Enqueue output items into 'CraftingCompletionQueue'. The 10 Hz engine thread will pull these down and safely adjust inventory balances via 'CollectionsMarshal'".
        // Wait, what if it's equipment? Usually commodities are stackable. Let's just say we pass the commodity update.
    }

    public struct InfrastructureUpdateNotification
    {
        public long PlayerId;
        public byte ForgeLevel;
        public byte InnLevel;
        public byte BreedingLevel;
        public byte AcademyLevel;
        public byte CurrentPopulationCount;
        public int MaxPopulationCapacity;
        public int InnMaturationBonus;
        public int CurrentToolTier;
    }

    public struct MentorshipUpdateNotification
    {
        public long PlayerId;
    }

    public struct QuarantineNotification
    {
        public long PlayerId;
    }

    public struct ChronoAccelerationNotification
    {
        public long PlayerId;
        public double SecondsToAdd;
    }

    public struct LegacyStoreUpdateNotification
    {
        public long PlayerId;
        public int LegacyShardBalance;
        public int CitizenMultiSlotsUnlocked;
    }

    public struct GuildLogisticsDepotUpdateNotification
    {
        public long GuildId;
        public int MaterialId;
        public long CurrentStock;
        public long TargetRequirement;
        public int Level;
    }

    // Co-op PvE guild raid boss update. Distinct from GuildCombatSimulationUpdateNotification,
    // which is the unrelated PvP guild-vs-guild war turn engine.
    public struct GuildRaidBossUpdateNotification
    {
        public long GuildId;
        public int RaidTier;
        public long RaidBossCurrentHp;
        public long RaidBossMaxHp;
    }

    public struct GuildCombatSimulationUpdateNotification
    {
        public long MatchId;
        public long AttackingGuildId;
        public long DefendingGuildId;
        public int TurnCounter;
        public int DamageDelta;
    }

    public struct MentorshipContractUpdateNotification
    {
        public long PlayerId;
        public long MentorPlayerId;
        public double ExpBonusMultiplier;
        public byte ActiveContractCount;
    }

    public struct CodexMultiplierUpdateNotification
    {
        public long PlayerId;
        public float YieldMultiplier;
        public float DamageMultiplier;
    }

    public class PlayerSessionRegistry
    {
        private readonly ConcurrentDictionary<long, bool> _onlinePlayers = new();
        public ConcurrentQueue<MarketMatchNotification> MarketMatchQueue { get; } = new();
        public ConcurrentQueue<AchievementClaimRequest> AchievementClaimQueue { get; } = new();
        public ConcurrentQueue<ForgeUpgradeNotification> ForgeUpgradeQueue { get; } = new();
        public ConcurrentQueue<MailClaimRequest> MailClaimRequestQueue { get; } = new();
        public ConcurrentQueue<BankWithdrawRequest> BankWithdrawRequestQueue { get; } = new();
        public ConcurrentQueue<BirthNotification> BirthNotificationQueue { get; } = new();
        public ConcurrentQueue<WorldBossAttemptUpdateNotification> WorldBossAttemptUpdateQueue { get; } = new();
        public ConcurrentQueue<MasteryUpdateNotification> MasteryUpdateQueue { get; } = new();
        public ConcurrentQueue<long> LoginQueue { get; } = new();
        public ConcurrentQueue<GuildUpdateNotification> GuildUpdateQueue { get; } = new();
        public ConcurrentQueue<CraftingCompletionNotification> CraftingCompletionQueue { get; } = new();
        public ConcurrentQueue<InfrastructureUpdateNotification> InfrastructureUpdateQueue { get; } = new();
        public ConcurrentQueue<MentorshipUpdateNotification> MentorshipUpdateQueue { get; } = new();
        public ConcurrentQueue<QuarantineNotification> QuarantineNotificationQueue { get; } = new();
        public ConcurrentQueue<ChronoAccelerationNotification> ChronoAccelerationQueue { get; } = new();
        public ConcurrentQueue<LegacyStoreUpdateNotification> LegacyStoreUpdateQueue { get; } = new();
        public ConcurrentQueue<GuildLogisticsDepotUpdateNotification> GuildLogisticsDepotUpdateQueue { get; } = new();
        public ConcurrentQueue<GuildCombatSimulationUpdateNotification> GuildCombatSimulationUpdateQueue { get; } = new();
        public ConcurrentQueue<GuildRaidBossUpdateNotification> GuildRaidBossUpdateQueue { get; } = new();
        public ConcurrentQueue<MentorshipContractUpdateNotification> MentorshipContractUpdateQueue { get; } = new();
        public ConcurrentQueue<CodexMultiplierUpdateNotification> CodexMultiplierUpdateQueue { get; } = new();

        public void RegisterPlayer(long playerId)
        {
            _onlinePlayers[playerId] = true;
        }

        public void UnregisterPlayer(long playerId)
        {
            _onlinePlayers.TryRemove(playerId, out _);
        }

        public bool IsPlayerOnline(long playerId)
        {
            return _onlinePlayers.ContainsKey(playerId);
        }

        public int GetOnlinePlayerCount()
        {
            return _onlinePlayers.Count;
        }

        public long[] GetOnlinePlayerIds()
        {
            return _onlinePlayers.Keys.ToArray();
        }

        public void EnqueueGuildUpdate(GuildUpdateNotification notification)
        {
            GuildUpdateQueue.Enqueue(notification);
        }
    }
}
