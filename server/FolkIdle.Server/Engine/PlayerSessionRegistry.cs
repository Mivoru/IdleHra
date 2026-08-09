using System.Collections.Concurrent;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

    // Modul 16/21: EquipmentSlotEngine's equip/unequip handlers run on a
    // background Task.Run thread with no ref access to the live TickStatePayload,
    // so the resulting slot state (plus pre-computed, allocation-free-to-read
    // affix totals for StatsCalculator) is reported back through this queue.
    // Modul: race unlock feedback. One newly granted race for one player.
    // Unmanaged, like every other notification on these queues, so draining it
    // on the tick thread allocates nothing.
    public struct RaceUnlockNotification
    {
        public long PlayerId;
        public byte RaceId;
    }

    public struct EquipmentSlotUpdateNotification
    {
        public long PlayerId;

        // Modul: per-character equipment. WHICH character changed. Equipment
        // used to be account-wide, so the tick thread could apply an update
        // without asking - now it has to route the change to the right slot's
        // register, or equipping a helmet on the miner would re-stat the
        // swordsman.
        public System.Guid CharacterId;

        public long EquippedWeaponId;
        public long EquippedHelmetId;
        public long EquippedChestId;
        public long EquippedGlovesId;
        public long EquippedLeggingsId;
        public long EquippedBootsId;
        public long EquippedAmuletId;
        public long EquippedRingId;

        // Modul: which of the three weapon families, for the hit effect. See
        // EquipmentSlotEngine.ResolveWeaponKind - the client cannot work it out
        // from an instance id without fetching an inventory.
        public byte EquippedWeaponKind;

        // Modul: the tool loadout, resolved with the rest of the gear.
        // The tick needs a tier and three percentages, not three instance ids -
        // and recomputing them from the database on a 10Hz path is exactly what
        // this notification exists to avoid.
        public byte AxeToolTier;
        public byte PickaxeToolTier;
        public byte RodToolTier;
        public ushort ToolGatherSpeedPct;
        public ushort ToolGatherYieldPct;
        public ushort ToolRareFindPct;
        // Modul: Affix System Unification. Was four loose ints, which could
        // only carry four of the GDD's twelve affixes - the other eight had
        // nowhere to go and silently contributed nothing.
        public EquippedAffixTotals AffixTotals;

        // Modul: Architecture Overhaul, Part 4. Per-slot SetId, so the
        // tick thread can cache them onto TickStatePayload for
        // SetBonusEngine.Evaluate at recalculation time.
        //
        // Modul: seven-slot set bonuses. Was three loose ints that collapsed
        // four armour slots into one - see EquippedSetIds.
        public EquippedSetIds SetIds;
    }

    // Modul: GuildManagementEngine's create/join/leave/kick handlers run on
    // background threads and commit membership to the database first; this
    // notification is how the tick thread learns a live player's GuildId
    // changed mid-session so it can update the tick-thread-owned
    // _guildMembersIndex and the player's TickStatePayload.GuildId, then
    // push a ReloadState packet. Before this queue existed, GuildId was
    // load-once-at-login and never changed for a session's lifetime.
    public struct GuildMembershipChangeNotification
    {
        public long PlayerId;
        public long OldGuildId;
        public long NewGuildId;
    }

    // Modul: generic client error-feedback channel - the engines that
    // reject a market/forge/reroll/guild-contribution request run on
    // background Task.Run threads (via SafeDispatchAsync) and cannot write
    // TickStatePayload's CommandResultSlot0-3 ring buffer directly (it is
    // a struct field on the tick-thread-owned dictionary, not reachable
    // by reference from another thread); they enqueue this notification
    // instead, and the tick thread drains it into the payload the same
    // way every other cross-thread report in this codebase works (see
    // GuildMembershipChangeNotification for the identical pattern).
    // Modul: larder. LarderEngine does its work on a background dispatch task
    // inside a Serializable transaction, but the live TickStatePayload belongs
    // to the 10Hz tick thread alone. This carries the committed slot contents
    // back to it - the same hand-off shape as ActivityChangeNotification.
    public struct LarderSlotUpdateNotification
    {
        public long PlayerId;
        // 0-based, matching the wire. The tick-thread drain maps it onto the
        // 1-based Food1/Food2/Food3 payload fields.
        public int SlotIndex;
        public int ItemId;
        public int Count;
    }

    public struct CommandResultNotification
    {
        public long PlayerId;
        public byte ResultCode;
    }

    public struct MailClaimRequest
    {
        public long PlayerId;
        public long MailId;
        public long GoldAttachment;
        public bool HasItem;
    }

    /// <summary>
    /// A recount of how many backpack slots a player occupies, handed to the
    /// tick thread after an operation that changed it.
    ///
    /// THIS EXISTS BECAUSE A FULL BACKPACK WAS UNRECOVERABLE.
    ///
    /// InventorySpaceRemaining was only ever refreshed from two places: the
    /// session load, and a loot drop carrying a census. ProcessSubTick returns
    /// immediately when the value is 0, so no combat ran, so no loot dropped,
    /// so no census arrived - and depositing to the bank, claiming mail or
    /// selling on the market all changed the database without touching the
    /// live payload. The player freed slots, watched the count stay at 0, and
    /// had no way back except reconnecting.
    ///
    /// Every path that adds or removes a backpack item now enqueues one of
    /// these. Same hand-off shape as CombatLootDropQueue: an unmanaged struct
    /// in a lock-free queue, drained by the thread that owns the payload.
    /// </summary>
    public struct InventoryCensusNotification
    {
        public long PlayerId;
        public int OccupiedSlots;
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
        // Units produced by this craft. Commodity crafts batch (quantity N in one
        // notification); equipment crafts are always 1.
        public int Quantity;
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

        // Modul 16: passive-production buildings, extended in this pass so an
        // UpgradeBuilding command against Lumberjack/Quarry/Mine/Warehouse
        // replicates immediately instead of only refreshing at next login.
        public byte LumberjackLevel;
        public byte QuarryLevel;
        public byte MineLevel;
        public byte WarehouseLevel;

        // Modul: Play Mode audit fix - see StateUpdatePacket's own comment.
        public byte TownHallLevel;
        public byte CraftingWorkshopLevel;

        // Modul 16: timed upgrade queue - PendingUpgradeBuildingId == 0 means
        // no upgrade is currently in flight for this player's village (only
        // one building may be queued at a time, see
        // VillageManagementEngine.ExecuteUpgradeBuildingAsync).
        public byte PendingUpgradeBuildingId;
        public long PendingUpgradeCompletesAtEpoch;
    }

    public struct MentorshipUpdateNotification
    {
        public long PlayerId;
    }

    // Modul 13.4.3: newly-completed regions from this Codex processing batch
    // only (see CodexEngine.ExecuteAsync) - CompletedRegionFlags is OR'd into
    // TickStatePayload.CompletedAreaFlags on drain, never assigned outright, so
    // regions completed earlier this session are preserved.
    public struct RegionCompletionNotification
    {
        public long PlayerId;
        public int CompletedRegionFlags;
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

    // Modul: cross-shard guild war. Carries the result of a shard attack back
    // to the tick thread. The mesh call is a network round trip and used to run
    // synchronously inside the 10 Hz loop, blocking every player's simulation
    // on one player's request.
    //
    // SecurityViolationStatus is threaded back rather than acted on off-thread:
    // terminating a session is tick-thread work, and the drain is the only place
    // that can safely touch the payload. 0 means the attack was accepted.
    public struct ShardAttackResultNotification
    {
        public long PlayerId;
        public uint ProcessingStatus;
        public System.Guid MatchUuid;
        public long GlobalNodeRemainingHp;
        public int ActiveMatchMmr;
    }

    public struct LegacyStoreUpdateNotification
    {
        public long PlayerId;
        public int LegacyShardBalance;
        public int CitizenMultiSlotsUnlocked;

        // Modul: only the Prestige perk-purchase path (LegacyStoreEngine.
        // ExecutePerkPurchaseAsync) populates LegacyPerks and sets this
        // true - the citizen-slot path leaves both at their default (0/
        // false), so the drain in SimulationEngine must check this flag
        // before overwriting CachedLegacyPerks, or every citizen-slot
        // purchase would silently zero out a player's purchased perk ranks.
        public long LegacyPerks;
        public bool HasLegacyPerksUpdate;
    }

    // Modul: CommandType.SyncBillingStatus's result - the client's live
    // TickStatePayload.PremiumCurrency only ever changes through explicit
    // in-tick mutation (see SetPremiumCurrency call sites), so a purchase
    // verified out-of-band through the REST /api/v1/billing/verify endpoint
    // (which writes PlayerRecords.PremiumDiamonds directly to the database
    // and never touches the in-memory active-player payload) would
    // otherwise stay invisible to an already-connected session until its
    // next full ReloadState. This notification carries the DB-authoritative
    // balance back onto the live payload on demand instead.
    public struct BillingSyncNotification
    {
        public long PlayerId;
        public int PremiumDiamondsBalance;
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

        // Modul 13.4.3: unix-epoch-seconds until which this player's character
        // XP generation is reduced by 20 percent, set on early contract
        // termination (see MentorshipEngine.ExecuteTerminateMentorshipAsync). 0
        // on the "contract established" path (no penalty).
        public long XpPenaltyExpiresEpoch;
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
        public ConcurrentQueue<EquipmentSlotUpdateNotification> EquipmentSlotUpdateQueue { get; } = new();

        // Modul: race unlock feedback. CodexEngine grants races from a
        // background loop, off the tick thread, so the live payload learns
        // about it the same way it learns about every other off-thread result.
        public ConcurrentQueue<RaceUnlockNotification> RaceUnlockQueue { get; } = new();
        public ConcurrentQueue<MailClaimRequest> MailClaimRequestQueue { get; } = new();
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
        public ConcurrentQueue<ShardAttackResultNotification> ShardAttackResultQueue { get; } = new();
        public ConcurrentQueue<LegacyStoreUpdateNotification> LegacyStoreUpdateQueue { get; } = new();
        public ConcurrentQueue<GuildLogisticsDepotUpdateNotification> GuildLogisticsDepotUpdateQueue { get; } = new();
        public ConcurrentQueue<GuildCombatSimulationUpdateNotification> GuildCombatSimulationUpdateQueue { get; } = new();
        public ConcurrentQueue<GuildRaidBossUpdateNotification> GuildRaidBossUpdateQueue { get; } = new();
        public ConcurrentQueue<MentorshipContractUpdateNotification> MentorshipContractUpdateQueue { get; } = new();
        public ConcurrentQueue<CodexMultiplierUpdateNotification> CodexMultiplierUpdateQueue { get; } = new();
        public ConcurrentQueue<RegionCompletionNotification> RegionCompletionUpdateQueue { get; } = new();
        public ConcurrentQueue<CombatLootDropNotification> CombatLootDropQueue { get; } = new();

        // Modul: see InventoryCensusNotification. A full backpack used to be a
        // dead end, because the only thing that recomputed free space was a
        // loot drop and loot drops need free space.
        public ConcurrentQueue<InventoryCensusNotification> InventoryCensusQueue { get; } = new();

        // Modul: Deploy activation fix. ChangeCharacterActivityAsync runs on
        // a background dispatch task (it does real DB work inside a
        // Serializable transaction), but the live TickStatePayload is owned
        // exclusively by the 10Hz tick thread and must never be mutated from
        // anywhere else. This is the established hand-off for exactly that
        // situation - the same shape as CombatLootDropQueue and
        // RegionCompletionUpdateQueue above. An unmanaged struct in a
        // lock-free queue, so the tick-thread drain allocates nothing.
        public ConcurrentQueue<ActivityChangeNotification> ActivityChangeQueue { get; } = new();

        // Modul: a payload re-read from the database, waiting to be applied by
        // the thread that owns _activePlayers.
        //
        // ReloadState has to go to the database, which the 10Hz tick must not
        // wait on - so the read happens on a task and the RESULT comes back
        // here. Writing the dictionary from that task instead was a data race
        // against the tick iterating it, and it cost a player their fight: the
        // symptom was "deployed to Wild Boar, but nothing is happening".
        public ConcurrentQueue<TickStatePayload> StateReloadQueue { get; } = new();

        // Modul: larder - see LarderSlotUpdateNotification.
        public ConcurrentQueue<LarderSlotUpdateNotification> LarderSlotUpdateQueue { get; } = new();

        // Modul: Guild War scoreboard sync - see
        // GuildWarScoreboardNotification for what was missing.
        public ConcurrentQueue<GuildWarScoreboardNotification> GuildWarScoreboardQueue { get; } = new();

        // Modul: Loot Event Feed. Deliberately a SECOND queue rather than
        // reusing CombatLootDropQueue above. That one is drained by
        // SimulationEngine's tick thread purely to decrement
        // InventorySpaceRemaining; this one is drained by
        // NetworkBroadcastSystem to push a ResponseLootDropPacket to the
        // owning player's socket. A single queue cannot serve both, since
        // whichever consumer dequeued an entry first would consume it.
        public ConcurrentQueue<FolkIdle.Server.Network.ResponseLootDropPacket> OutboundLootDropQueue { get; } = new();
        public ConcurrentQueue<GuildMembershipChangeNotification> GuildMembershipChangeQueue { get; } = new();
        public ConcurrentQueue<CommandResultNotification> CommandResultQueue { get; } = new();
        public ConcurrentQueue<BillingSyncNotification> BillingSyncQueue { get; } = new();

        // Modul: inheritance stats - a committed purchase reaching the live payload.
        public ConcurrentQueue<InheritanceSyncNotification> InheritanceSyncQueue { get; } = new();

        // Modul: skill tree. Same pattern and the same reason - the tick thread
        // owns the payload, so a level bought over HTTP arrives here rather
        // than being written across threads.
        public ConcurrentQueue<SkillTreeSyncNotification> SkillTreeSyncQueue { get; } = new();

        // Modul: single shared enqueue point for the generic client
        // error-feedback channel, called from every engine that rejects a
        // market/forge/reroll/guild-contribution request (MarketEscrowEngine,
        // ForgeSplicingEngine, AffixRerollEngine, GuildContributionEngine)
        // instead of each duplicating the same three-line notification
        // construction. Callers holding an optional/nullable
        // PlayerSessionRegistry reference use the null-conditional
        // operator (playerRegistry?.EnqueueCommandResult(...)) so a
        // registry-less construction (some test fixtures) degrades to a
        // safe no-op rather than a null-reference exception.
        //
        // Modul: Full-Stack Production Hardening Phase 3, Part 5. This
        // method still only enqueues - it is called from arbitrary
        // background SafeDispatchAsync threads (market/forge/reroll/guild
        // engines) with no safe ref access to TickStatePayload, so the
        // actual circular 4-slot ring-buffer append (advancing
        // CommandResultRingWriteIndex, overwriting the oldest slot) happens
        // in SimulationEngine's single-threaded tick-loop drain of
        // CommandResultQueue, the only place that legitimately holds a ref
        // into _activePlayers.
        public void EnqueueCommandResult(long playerId, byte resultCode)
        {
            CommandResultQueue.Enqueue(new CommandResultNotification { PlayerId = playerId, ResultCode = resultCode });
        }

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
