using UnityEngine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    public class VisualSyncProxy : MonoBehaviour
    {
        public WebSocketClient NetworkClient;
        
        public float VisualProgressTicks;
        public float VisualPlayerHp;
        public float VisualMonsterHp;

        // Modul: Combat Arena signals. VisualCurrentMonsterId/
        // VisualActiveAudioTrackId are discrete (no interpolation, matching
        // ApplyVillagePacketState's convention) - ActiveAudioTrackId already
        // encodes activity classification server-side (SimulationEngine sets
        // 1=idle, 2=gathering, 3=combat, 4=world boss via the same
        // TryGetGatheringNode check used everywhere else), so the client
        // does not need its own ContentRegistry copy to know "am I fighting
        // a monster right now." OnMonsterHit/OnPlayerHit fire once per real
        // server tick where the raw (non-interpolated) HP actually dropped
        // for the SAME monster/player instance - computed at packet-arrival
        // time in Update's dequeue loop, not by diffing the smoothed
        // per-frame Lerp output, which would spread one real hit across many
        // frames and fire repeatedly instead of once.
        public int VisualCurrentMonsterId { get; private set; }
        public byte VisualActiveAudioTrackId { get; private set; } = 1;
        public event System.Action OnCombatInstanceChanged;
        public event System.Action<int, bool> OnMonsterHit;
        public event System.Action<int, bool> OnPlayerHit;

        // Client-side visual heuristic only - the real crit roll happens
        // server-side and is not transmitted per-hit, only the resulting HP
        // delta. A hit removing at least this fraction of the target's
        // pre-hit HP in one tick is treated as visually "critical" so the
        // threshold scales naturally across monster tiers instead of using
        // a fixed absolute damage number that would be meaningless from
        // early 69 HP monsters up to endgame millions.
        private const float CriticalHitFraction = 0.20f;
        public float VisualWoodcuttingXp;
        public float VisualMiningXp;
        public float VisualGatheringProgress;
        public float VisualVillagePopulation;
        public float VisualAccumulatedTimeBankMs;
        public double VisualBankedChronoSeconds;
        public bool VisualIsChronoAccelerating;
        public uint VisualChronoEngineStatus { get; private set; }
        public ulong VisualActiveChronoLockExpirationTicks { get; private set; }
        public byte VisualCurrentSimulationSpeedMultiplier { get; private set; }
        public long VisualLogicEpochCounter;
        public ulong VisualLogicalEpochFrameIndex;
        public bool VisualWeaponAffixLocked;
        public bool VisualArmorAffixLocked;
        public int VisualMiningMonolithLevel;
        public int VisualWoodcuttingMonolithLevel;
        public float VisualWorldBossHp;
        public float VisualWorldBossMaxHp;
        public int VisualGlobalEventId;
        public int VisualNotificationQueueStateLength;
        public byte VisualActiveLanguageState = 1;

        public int VisualMaxVillagePopulation;
        public int VisualCurrentToolTier;
        public int VisualInnMaturationBonus;
        public int VisualForgeLevel;
        public int VisualInnLevel;
        public int VisualBreedingLevel;
        public int VisualAcademyLevel;
        public int VisualCurrentPopulationCount;
        public float VisualChildMaturationMs;
        
        public int VisualSlot1AgePhase;
        public int VisualSlot2AgePhase;
        public int VisualSlot3AgePhase;
        public int VisualMentorCount;
        
        public int VisualCompletedAreaFlags;
        public int VisualClaimedAchievementFlags;
        public int VisualHumanMasteryLevel;
        public int VisualVilaMasteryLevel;
        public int VisualDraugrMasteryLevel;

        public long VisualActiveGuildWarId;
        public float VisualWarMultiplier;
        public int VisualGuildCombatPoints;
        public int VisualGuildLogisticsPoints;
        public int VisualGuildSupplyPoints;
        public int VisualEnemyCombatPoints;
        public int VisualEnemyLogisticsPoints;
        public int VisualEnemySupplyPoints;
        public int VisualLegacyShardBalance;
        [System.NonSerialized] public ObfuscatedInt32 VisualLegacyShardCell;
        [System.NonSerialized] public ObfuscatedInt64 VisualGoldCell;
        public byte VisualWorldBossAttemptCount;
        public byte VisualWorldBossEventState;
        public long VisualWorldBossEventEndEpoch;
        public int VisualCitizenMultiSlotsUnlocked;
        public long VisualGuildLogisticsCurrentStock;
        public long VisualGuildLogisticsTargetRequirement;
        public int VisualGuildLogisticsLevel;
        public long VisualCombatSimulationMatchId;
        public int VisualCombatSimulationTurnCounter;
        public int VisualCombatSimulationDamageDelta;
        public int VisualGuildRaidTier;
        public long VisualGuildRaidBossCurrentHp;
        public long VisualGuildRaidBossMaxHp;
        public long VisualActiveMentorPlayerId;
        public double VisualMentorshipExpBonusMultiplier = 1.0;
        public uint VisualPremiumCurrencyBalance { get; private set; }
        public uint VisualEventHorizonTransactionCount { get; private set; }
        public uint VisualTotalItemsCraftedCount { get; private set; }
        public byte VisualCraftingEngineStatus { get; private set; }
        public uint VisualTotalAchievementsClaimedCount { get; private set; }
        public uint VisualActiveMasteryBitmask { get; private set; }
        public uint VisualActiveStatusEffectModifierBitmask { get; private set; }
        public uint VisualRemainingBuffDurationTicks { get; private set; }
        public uint VisualActiveChroniclePassLevel { get; private set; }
        public uint VisualAccumulatedSeasonalXp { get; private set; }
        public uint VisualActiveMatchMmr { get; private set; }
        public uint VisualGlobalNodeRemainingHp { get; private set; }
        public System.Guid VisualActiveMatchId { get; private set; }
        public ulong VisualTotalAnalyticsEventsLoggedCount { get; private set; }
        public uint VisualActiveConnectionThroughput { get; private set; }
        public uint VisualCurrentNodeMemoryLoadMetrics { get; private set; }

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        public int LumberjackLevel { get; private set; }
        public int QuarryLevel { get; private set; }
        public int MineLevel { get; private set; }
        public int WarehouseLevel { get; private set; }
        public long WoodStock { get; private set; }
        public long StoneStock { get; private set; }
        public long IronOreStock { get; private set; }

        // Modul 16: timed upgrade queue - PendingUpgradeBuildingId == 0 means
        // no upgrade is currently in flight for this player's village.
        public int PendingUpgradeBuildingId { get; private set; }
        public long PendingUpgradeCompletesAtEpoch { get; private set; }

        // Modul 16/21: character attributes and equipped gear references.
        public int VisualSTR { get; private set; }
        public int VisualDEX { get; private set; }
        public int VisualCON { get; private set; }
        public int VisualLCK { get; private set; }
        public long VisualEquippedWeaponId { get; private set; }
        public long VisualEquippedArmorId { get; private set; }

        // Active Skill Tree (see server ActiveSkillEngine/StateUpdatePacket).
        // Bitmask/skill points are discrete - assigned straight from the
        // latest packet in both the first-packet and steady-state paths,
        // matching VisualActiveMasteryBitmask/VisualCompletedAreaFlags.
        // Mana and the four cooldown-remaining values are Lerp'd every frame
        // like VisualPlayerHp/VisualWorldBossMaxHp, so UiActionBar's radial
        // cooldown overlay sweeps smoothly between the ~100ms server ticks
        // instead of stepping.
        public uint VisualUnlockedSkillsBitmask;
        public int VisualAvailableSkillPoints;
        public float VisualCurrentMana;
        public float VisualMaxMana;
        public float VisualSkill1CooldownRemainingMs;
        public float VisualSkill2CooldownRemainingMs;
        public float VisualSkill3CooldownRemainingMs;
        public float VisualSkill4CooldownRemainingMs;

        // Modul: edge-detected skill cast result - fires exactly once per
        // real RequestCastSkill the server processed (see
        // LastSkillCastResultTick's doc comment on StateUpdatePacket),
        // never once per packet/frame, mirroring OnMonsterHit/OnPlayerHit's
        // edge-detection precedent above.
        public event System.Action<int, bool> OnSkillCastResult;
        private uint _lastAppliedSkillCastResultTick;

        public event System.Action OnVillageStateUpdated;
        public event System.Action OnGuildStateUpdated;
        public static event System.Action OnCharacterStateUpdated;

        private struct ServerSnapshot
        {
            public StateUpdatePacket Packet;
            public float ReceivedTime;
        }

        private ServerSnapshot _snapshotA;
        private ServerSnapshot _snapshotB;
        private bool _hasReceivedState = false;

        // Modul: adaptive playback delay. TargetTickIntervalSeconds (100ms)
        // is the floor - the server's own tick rate - but a fixed 100ms
        // buffer only tolerates one-off jitter, not a sustained latency
        // increase: if packets keep arriving every ~300ms, a render cursor
        // pinned 100ms behind "now" constantly overtakes the last real
        // snapshot and clamps at t=1 (frozen) for most of each interval,
        // then jumps - exactly the stutter this proxy exists to avoid. Instead
        // the delay tracks an exponential moving average of the actual
        // inter-packet interval, so it grows to stay safely behind the
        // network's real cadence and shrinks back toward the 100ms floor
        // once the network recovers. Pure float arithmetic - zero allocations.
        private const float TargetTickIntervalSeconds = 0.100f;
        private const float MaxPlaybackDelaySeconds = 0.500f;
        private const float IntervalSmoothingFactor = 0.2f;
        private float _emaPacketIntervalSeconds = TargetTickIntervalSeconds;

        public void Update()
        {
            if (NetworkClient == null) return;

            while (NetworkClient.PacketQueue.TryDequeue(out var packet))
            {
                if (!_hasReceivedState)
                {
                    _snapshotA = new ServerSnapshot { Packet = packet, ReceivedTime = Time.time };
                    _snapshotB = _snapshotA;
                    
                    VisualProgressTicks = packet.CurrentProgressTicks;
                    VisualPlayerHp = packet.PlayerHp;
                    VisualMonsterHp = packet.CurrentMonsterHp;
                    VisualWoodcuttingXp = packet.WoodcuttingMasteryXp;
                    VisualMiningXp = packet.MiningMasteryXp;
                    VisualGatheringProgress = packet.GatheringProgressTicks;
                    VisualVillagePopulation = packet.VillagePopulation;
                    VisualAccumulatedTimeBankMs = packet.AccumulatedTimeBankMs;
                    VisualBankedChronoSeconds = packet.VisualBankedChronoSeconds;
                    VisualIsChronoAccelerating = packet.IsChronoAccelerating != 0;
                    VisualChronoEngineStatus = packet.ActiveChronoEngineStatus;
                    VisualActiveChronoLockExpirationTicks = packet.ActiveChronoLockExpirationTicks;
                    VisualCurrentSimulationSpeedMultiplier = packet.CurrentSimulationSpeedMultiplier;
                    VisualLogicEpochCounter = packet.LogicEpochCounter;
                    VisualLogicalEpochFrameIndex = packet.LogicalEpochFrameIndex;
                    VisualWeaponAffixLocked = packet.EquippedWeaponAffixLocked != 0;
                    VisualArmorAffixLocked = packet.EquippedArmorAffixLocked != 0;
                    VisualMiningMonolithLevel = packet.CachedMiningMonolithLevel;
                    VisualWoodcuttingMonolithLevel = packet.CachedWoodcuttingMonolithLevel;
                    VisualWorldBossHp = packet.WorldBossCurrentHp;
                    VisualWorldBossMaxHp = packet.WorldBossMaxHp;
                    VisualGlobalEventId = packet.ActiveEventType;
                    VisualNotificationQueueStateLength = packet.NotificationQueueStateLength;
                    VisualActiveLanguageState = packet.ActiveLanguageState == 0 ? (byte)1 : packet.ActiveLanguageState;
                    VisualActiveConnectionThroughput = packet.VisualActiveConnectionThroughput;
                    VisualCurrentNodeMemoryLoadMetrics = packet.CurrentNodeMemoryLoadMetrics;

                    VisualMaxVillagePopulation = packet.CachedMaxPopulationCapacity;
                    VisualCurrentToolTier = packet.CachedCurrentToolTier;
                    VisualInnMaturationBonus = packet.CachedInnMaturationBonus;
                    VisualForgeLevel = packet.ForgeLevel;
                    VisualInnLevel = packet.InnLevel;
                    VisualBreedingLevel = packet.BreedingLevel;
                    VisualAcademyLevel = packet.AcademyLevel;
                    VisualCurrentPopulationCount = packet.CurrentPopulationCount;
                    VisualVillagePopulation = packet.CurrentPopulationCount;
                    VisualChildMaturationMs = packet.ActiveChildMaturationMs;
                    
                    VisualSlot1AgePhase = packet.Slot1_AgePhase;
                    VisualSlot2AgePhase = packet.Slot2_AgePhase;
                    VisualSlot3AgePhase = packet.Slot3_AgePhase;
                    VisualMentorCount = packet.CachedMentorCount;
                    
                    VisualCompletedAreaFlags = packet.CompletedAreaFlags;
                    VisualClaimedAchievementFlags = packet.ClaimedAchievementFlags;
                    VisualHumanMasteryLevel = packet.HumanMasteryLevel;
                    VisualVilaMasteryLevel = packet.VilaMasteryLevel;
                    VisualDraugrMasteryLevel = packet.DraugrMasteryLevel;
                    VisualLegacyShardBalance = packet.LegacyShardBalance;
                    VisualLegacyShardCell = new ObfuscatedInt32(packet.LegacyShardBalance, ResolveClientXorKey(packet.PlayerId, packet.LogicEpochCounter));
                    VisualGoldCell = new ObfuscatedInt64(packet.Gold, ResolveClientXorKey64(packet.PlayerId, packet.LogicEpochCounter));
                    VisualWorldBossAttemptCount = packet.WorldBossAttemptCount;
                    VisualWorldBossEventState = packet.WorldBossEventState;
                    VisualWorldBossEventEndEpoch = packet.WorldBossEventEndEpoch;
                    VisualCitizenMultiSlotsUnlocked = packet.CitizenMultiSlotsUnlocked;
                    VisualCombatSimulationMatchId = packet.CombatSimulationMatchId;
                    VisualCombatSimulationTurnCounter = packet.CombatSimulationTurnCounter;
                    VisualCombatSimulationDamageDelta = packet.CombatSimulationDamageDelta;
                    VisualActiveMentorPlayerId = packet.ActiveMentorPlayerId;
                    VisualPremiumCurrencyBalance = packet.PremiumCurrencyBalance;
                    VisualEventHorizonTransactionCount = packet.EventHorizonTransactionCount;
                    VisualTotalItemsCraftedCount = packet.TotalItemsCraftedCount;
                    VisualCraftingEngineStatus = packet.CraftingEngineStatus;
                    VisualTotalAchievementsClaimedCount = packet.TotalAchievementsClaimedCount;
                    VisualActiveMasteryBitmask = packet.ActiveMasteryBitmask;
                    VisualActiveStatusEffectModifierBitmask = packet.ActiveStatusEffectModifierBitmask;
                    VisualRemainingBuffDurationTicks = packet.RemainingBuffDurationTicks;
                    VisualActiveChroniclePassLevel = packet.ActiveChroniclePassLevel;
                    VisualAccumulatedSeasonalXp = packet.AccumulatedSeasonalXp;
                    VisualActiveMatchMmr = packet.VisualActiveMatchMmr;
                    VisualGlobalNodeRemainingHp = packet.GlobalNodeRemainingHp;
                    VisualActiveMatchId = packet.ActiveMatchId;
                    VisualTotalAnalyticsEventsLoggedCount = packet.TotalAnalyticsEventsLoggedCount;

                    VisualUnlockedSkillsBitmask = packet.UnlockedSkillsBitmask;
                    VisualAvailableSkillPoints = packet.AvailableSkillPoints;
                    VisualCurrentMana = packet.CurrentMana;
                    VisualMaxMana = packet.MaxMana;
                    VisualSkill1CooldownRemainingMs = packet.Skill1CooldownRemainingMs;
                    VisualSkill2CooldownRemainingMs = packet.Skill2CooldownRemainingMs;
                    VisualSkill3CooldownRemainingMs = packet.Skill3CooldownRemainingMs;
                    VisualSkill4CooldownRemainingMs = packet.Skill4CooldownRemainingMs;

                    ApplyVillagePacketState(in packet);
                    ApplyGuildPacketState(in packet);
                    ApplyCharacterPacketState(in packet);
                    ApplyCombatPacketState(in packet);
                    ApplyLastSkillCastState(in packet);

                    _hasReceivedState = true;
                }
                else
                {
                    float now = Time.time;
                    float observedInterval = now - _snapshotB.ReceivedTime;
                    if (observedInterval > 0f)
                    {
                        _emaPacketIntervalSeconds = Mathf.Lerp(_emaPacketIntervalSeconds, observedInterval, IntervalSmoothingFactor);
                    }

                    StateUpdatePacket previousPacket = _snapshotB.Packet;
                    DetectCombatHits(in previousPacket, in packet);

                    _snapshotA = _snapshotB;
                    _snapshotB = new ServerSnapshot { Packet = packet, ReceivedTime = now };
                }
            }

            if (!_hasReceivedState) return;

            // Adaptive playback delay: at least the server's own tick rate,
            // scaled up toward the observed inter-packet interval (with
            // margin) when the network is running slower than that, capped
            // so a bad connection cannot push visuals arbitrarily far behind
            // real time.
            float playbackDelay = Mathf.Clamp(_emaPacketIntervalSeconds * 1.5f, TargetTickIntervalSeconds, MaxPlaybackDelaySeconds);
            float renderTime = Time.time - playbackDelay;

            if (_snapshotA.ReceivedTime == _snapshotB.ReceivedTime)
            {
                return; // Nothing to interpolate yet
            }

            float timeWindow = _snapshotB.ReceivedTime - _snapshotA.ReceivedTime;
            float t = (renderTime - _snapshotA.ReceivedTime) / timeWindow;

            // Explicitly clamp t to prevent overflow
            t = Mathf.Clamp(t, 0f, 1f);

            VisualProgressTicks = Mathf.Lerp(_snapshotA.Packet.CurrentProgressTicks, _snapshotB.Packet.CurrentProgressTicks, t);
            VisualPlayerHp = Mathf.Lerp(_snapshotA.Packet.PlayerHp, _snapshotB.Packet.PlayerHp, t);
            VisualMonsterHp = Mathf.Lerp(_snapshotA.Packet.CurrentMonsterHp, _snapshotB.Packet.CurrentMonsterHp, t);
            VisualWoodcuttingXp = Mathf.Lerp(_snapshotA.Packet.WoodcuttingMasteryXp, _snapshotB.Packet.WoodcuttingMasteryXp, t);
            VisualMiningXp = Mathf.Lerp(_snapshotA.Packet.MiningMasteryXp, _snapshotB.Packet.MiningMasteryXp, t);
            VisualGatheringProgress = Mathf.Lerp(_snapshotA.Packet.GatheringProgressTicks, _snapshotB.Packet.GatheringProgressTicks, t);
            VisualVillagePopulation = Mathf.Lerp(_snapshotA.Packet.VillagePopulation, _snapshotB.Packet.VillagePopulation, t);
            VisualAccumulatedTimeBankMs = Mathf.Lerp(_snapshotA.Packet.AccumulatedTimeBankMs, _snapshotB.Packet.AccumulatedTimeBankMs, t);
            VisualBankedChronoSeconds = _snapshotB.Packet.VisualBankedChronoSeconds;
            VisualIsChronoAccelerating = _snapshotB.Packet.IsChronoAccelerating != 0;
            VisualChronoEngineStatus = _snapshotB.Packet.ActiveChronoEngineStatus;
            VisualActiveChronoLockExpirationTicks = _snapshotB.Packet.ActiveChronoLockExpirationTicks;
            VisualCurrentSimulationSpeedMultiplier = _snapshotB.Packet.CurrentSimulationSpeedMultiplier;
            VisualLogicEpochCounter = _snapshotB.Packet.LogicEpochCounter;
            VisualLogicalEpochFrameIndex = _snapshotB.Packet.LogicalEpochFrameIndex;

            VisualWeaponAffixLocked = _snapshotB.Packet.EquippedWeaponAffixLocked != 0;
            VisualArmorAffixLocked = _snapshotB.Packet.EquippedArmorAffixLocked != 0;
            VisualMiningMonolithLevel = _snapshotB.Packet.CachedMiningMonolithLevel;
            VisualWoodcuttingMonolithLevel = _snapshotB.Packet.CachedWoodcuttingMonolithLevel;

            VisualWorldBossHp = Mathf.Lerp(_snapshotA.Packet.WorldBossCurrentHp, _snapshotB.Packet.WorldBossCurrentHp, t);
            VisualWorldBossMaxHp = Mathf.Lerp(_snapshotA.Packet.WorldBossMaxHp, _snapshotB.Packet.WorldBossMaxHp, t);
            VisualGlobalEventId = _snapshotB.Packet.ActiveEventType;
            VisualNotificationQueueStateLength = _snapshotB.Packet.NotificationQueueStateLength;
            VisualActiveLanguageState = _snapshotB.Packet.ActiveLanguageState == 0 ? (byte)1 : _snapshotB.Packet.ActiveLanguageState;
            VisualActiveConnectionThroughput = _snapshotB.Packet.VisualActiveConnectionThroughput;
            VisualCurrentNodeMemoryLoadMetrics = _snapshotB.Packet.CurrentNodeMemoryLoadMetrics;

            VisualMaxVillagePopulation = _snapshotB.Packet.CachedMaxPopulationCapacity;
            VisualCurrentToolTier = _snapshotB.Packet.CachedCurrentToolTier;
            VisualInnMaturationBonus = _snapshotB.Packet.CachedInnMaturationBonus;
            VisualForgeLevel = _snapshotB.Packet.ForgeLevel;
            VisualInnLevel = _snapshotB.Packet.InnLevel;
            VisualBreedingLevel = _snapshotB.Packet.BreedingLevel;
            VisualAcademyLevel = _snapshotB.Packet.AcademyLevel;
            VisualCurrentPopulationCount = _snapshotB.Packet.CurrentPopulationCount;
            VisualVillagePopulation = _snapshotB.Packet.CurrentPopulationCount;
            VisualChildMaturationMs = Mathf.Lerp(_snapshotA.Packet.ActiveChildMaturationMs, _snapshotB.Packet.ActiveChildMaturationMs, t);
            
            VisualSlot1AgePhase = _snapshotB.Packet.Slot1_AgePhase;
            VisualSlot2AgePhase = _snapshotB.Packet.Slot2_AgePhase;
            VisualSlot3AgePhase = _snapshotB.Packet.Slot3_AgePhase;
            VisualMentorCount = _snapshotB.Packet.CachedMentorCount;

            VisualCompletedAreaFlags = _snapshotB.Packet.CompletedAreaFlags;
            VisualClaimedAchievementFlags = _snapshotB.Packet.ClaimedAchievementFlags;
            VisualHumanMasteryLevel = _snapshotB.Packet.HumanMasteryLevel;
            VisualVilaMasteryLevel = _snapshotB.Packet.VilaMasteryLevel;
            VisualDraugrMasteryLevel = _snapshotB.Packet.DraugrMasteryLevel;
            VisualLegacyShardBalance = _snapshotB.Packet.LegacyShardBalance;
            VisualLegacyShardCell = new ObfuscatedInt32(_snapshotB.Packet.LegacyShardBalance, ResolveClientXorKey(_snapshotB.Packet.PlayerId, _snapshotB.Packet.LogicEpochCounter));
            VisualGoldCell = new ObfuscatedInt64(_snapshotB.Packet.Gold, ResolveClientXorKey64(_snapshotB.Packet.PlayerId, _snapshotB.Packet.LogicEpochCounter));
            VisualWorldBossAttemptCount = _snapshotB.Packet.WorldBossAttemptCount;
            VisualWorldBossEventState = _snapshotB.Packet.WorldBossEventState;
            VisualWorldBossEventEndEpoch = _snapshotB.Packet.WorldBossEventEndEpoch;
            VisualCitizenMultiSlotsUnlocked = _snapshotB.Packet.CitizenMultiSlotsUnlocked;
            VisualCombatSimulationMatchId = _snapshotB.Packet.CombatSimulationMatchId;
            VisualCombatSimulationTurnCounter = _snapshotB.Packet.CombatSimulationTurnCounter;
            VisualCombatSimulationDamageDelta = _snapshotB.Packet.CombatSimulationDamageDelta;
            VisualMentorshipExpBonusMultiplier = _snapshotB.Packet.MentorshipExpBonusMultiplier;
            VisualPremiumCurrencyBalance = _snapshotB.Packet.PremiumCurrencyBalance;
            VisualEventHorizonTransactionCount = _snapshotB.Packet.EventHorizonTransactionCount;
            VisualTotalItemsCraftedCount = _snapshotB.Packet.TotalItemsCraftedCount;
            VisualCraftingEngineStatus = _snapshotB.Packet.CraftingEngineStatus;
            VisualActiveChroniclePassLevel = _snapshotB.Packet.ActiveChroniclePassLevel;
            VisualAccumulatedSeasonalXp = _snapshotB.Packet.AccumulatedSeasonalXp;
            VisualActiveMatchMmr = _snapshotB.Packet.VisualActiveMatchMmr;
            VisualGlobalNodeRemainingHp = _snapshotB.Packet.GlobalNodeRemainingHp;
            VisualActiveMatchId = _snapshotB.Packet.ActiveMatchId;
            VisualTotalAnalyticsEventsLoggedCount = _snapshotB.Packet.TotalAnalyticsEventsLoggedCount;

            VisualActiveGuildWarId = _snapshotB.Packet.ActiveGuildWarId;
            VisualWarMultiplier = Mathf.Lerp(_snapshotA.Packet.CachedWarMultiplier, _snapshotB.Packet.CachedWarMultiplier, t);
            VisualGuildCombatPoints = _snapshotB.Packet.GuildCombatVanguardPoints;
            VisualGuildLogisticsPoints = _snapshotB.Packet.GuildProductionLogisticsPoints;
            VisualGuildSupplyPoints = _snapshotB.Packet.GuildGatheringSupplyChainPoints;
            VisualEnemyCombatPoints = _snapshotB.Packet.EnemyCombatVanguardPoints;
            VisualEnemyLogisticsPoints = _snapshotB.Packet.EnemyProductionLogisticsPoints;
            VisualEnemySupplyPoints = _snapshotB.Packet.EnemyGatheringSupplyChainPoints;

            VisualUnlockedSkillsBitmask = _snapshotB.Packet.UnlockedSkillsBitmask;
            VisualAvailableSkillPoints = _snapshotB.Packet.AvailableSkillPoints;
            VisualCurrentMana = Mathf.Lerp(_snapshotA.Packet.CurrentMana, _snapshotB.Packet.CurrentMana, t);
            VisualMaxMana = Mathf.Lerp(_snapshotA.Packet.MaxMana, _snapshotB.Packet.MaxMana, t);
            VisualSkill1CooldownRemainingMs = Mathf.Lerp(_snapshotA.Packet.Skill1CooldownRemainingMs, _snapshotB.Packet.Skill1CooldownRemainingMs, t);
            VisualSkill2CooldownRemainingMs = Mathf.Lerp(_snapshotA.Packet.Skill2CooldownRemainingMs, _snapshotB.Packet.Skill2CooldownRemainingMs, t);
            VisualSkill3CooldownRemainingMs = Mathf.Lerp(_snapshotA.Packet.Skill3CooldownRemainingMs, _snapshotB.Packet.Skill3CooldownRemainingMs, t);
            VisualSkill4CooldownRemainingMs = Mathf.Lerp(_snapshotA.Packet.Skill4CooldownRemainingMs, _snapshotB.Packet.Skill4CooldownRemainingMs, t);

            ApplyVillagePacketState(in _snapshotB.Packet);
            ApplyGuildPacketState(in _snapshotB.Packet);
            ApplyCharacterPacketState(in _snapshotB.Packet);
            ApplyCombatPacketState(in _snapshotB.Packet);
            ApplyLastSkillCastState(in _snapshotB.Packet);
        }

        // Modul: discrete combat-instance identity (which monster, which
        // activity classification) - matches ApplyVillagePacketState's
        // pattern exactly: only fires OnCombatInstanceChanged when the
        // monster id or activity classification actually changed, so
        // UiCombatArena can tell "a new fight/target started" apart from
        // "the same fight is still going" without re-deriving that itself
        // every frame.
        private void ApplyCombatPacketState(in StateUpdatePacket packet)
        {
            int monsterId = packet.CurrentMonsterId;
            byte audioTrackId = packet.ActiveAudioTrackId == 0 ? (byte)1 : packet.ActiveAudioTrackId;

            bool changed = monsterId != VisualCurrentMonsterId || audioTrackId != VisualActiveAudioTrackId;
            if (!changed) return;

            VisualCurrentMonsterId = monsterId;
            VisualActiveAudioTrackId = audioTrackId;

            OnCombatInstanceChanged?.Invoke();
        }

        // Modul: edge-detects a newly-resolved skill cast via
        // LastSkillCastResultTick (a per-cast incrementing counter, not a
        // boolean flag - a flag would either miss two casts landing in the
        // same 100ms tick or need its own separate acknowledgement
        // round-trip). Tick 0 means "no cast has ever resolved this
        // session" and is never fired.
        private void ApplyLastSkillCastState(in StateUpdatePacket packet)
        {
            uint resultTick = packet.LastSkillCastResultTick;
            if (resultTick == 0 || resultTick == _lastAppliedSkillCastResultTick) return;

            _lastAppliedSkillCastResultTick = resultTick;
            OnSkillCastResult?.Invoke(packet.LastSkillCastId, packet.LastSkillCastSuccess != 0);
        }

        // Modul: fires OnMonsterHit/OnPlayerHit exactly once per real server
        // tick where the raw HP for the SAME monster/player instance
        // actually dropped between two consecutive packets - called at
        // packet-arrival time (see the dequeue loop above), never from a
        // per-frame comparison against the smoothed Lerp output, which would
        // spread a single real hit across many frames instead of firing once.
        // Guards on CurrentMonsterId matching so a monster respawning after
        // a kill (a fresh HP baseline, not a hit) is never misread as damage.
        private void DetectCombatHits(in StateUpdatePacket previousPacket, in StateUpdatePacket newPacket)
        {
            if (newPacket.CurrentMonsterId == previousPacket.CurrentMonsterId && newPacket.CurrentMonsterHp < previousPacket.CurrentMonsterHp)
            {
                int damage = previousPacket.CurrentMonsterHp - newPacket.CurrentMonsterHp;
                bool isCritical = previousPacket.CurrentMonsterHp > 0 && damage >= previousPacket.CurrentMonsterHp * CriticalHitFraction;
                OnMonsterHit?.Invoke(damage, isCritical);
            }

            if (newPacket.PlayerHp < previousPacket.PlayerHp)
            {
                int damage = previousPacket.PlayerHp - newPacket.PlayerHp;
                bool isCritical = previousPacket.PlayerHp > 0 && damage >= previousPacket.PlayerHp * CriticalHitFraction;
                OnPlayerHit?.Invoke(damage, isCritical);
            }
        }

        // Modul 16: Village Infrastructure Passive Production & Warehouse Caps.
        // Discrete level/stock values only ever come from the latest packet (no
        // interpolation needed), so this is shared by both the first-packet path
        // and the steady-state interpolation path above. Fires OnVillageStateUpdated
        // only when a value actually changed, so subscribed UI never redraws on
        // unrelated packet fields ticking every 10 Hz frame.
        private void ApplyVillagePacketState(in StateUpdatePacket packet)
        {
            int lumberjackLevel = packet.LumberjackLevel;
            int quarryLevel = packet.QuarryLevel;
            int mineLevel = packet.MineLevel;
            int warehouseLevel = packet.WarehouseLevel;
            long woodStock = packet.CachedWoodStock;
            long stoneStock = packet.CachedStoneStock;
            long ironOreStock = packet.CachedIronOreStock;
            int pendingUpgradeBuildingId = packet.PendingUpgradeBuildingId;
            long pendingUpgradeCompletesAtEpoch = packet.PendingUpgradeCompletesAtEpoch;

            bool changed = lumberjackLevel != LumberjackLevel
                || quarryLevel != QuarryLevel
                || mineLevel != MineLevel
                || warehouseLevel != WarehouseLevel
                || woodStock != WoodStock
                || stoneStock != StoneStock
                || ironOreStock != IronOreStock
                || pendingUpgradeBuildingId != PendingUpgradeBuildingId
                || pendingUpgradeCompletesAtEpoch != PendingUpgradeCompletesAtEpoch;

            if (!changed) return;

            LumberjackLevel = lumberjackLevel;
            QuarryLevel = quarryLevel;
            MineLevel = mineLevel;
            WarehouseLevel = warehouseLevel;
            WoodStock = woodStock;
            StoneStock = stoneStock;
            IronOreStock = ironOreStock;
            PendingUpgradeBuildingId = pendingUpgradeBuildingId;
            PendingUpgradeCompletesAtEpoch = pendingUpgradeCompletesAtEpoch;

            OnVillageStateUpdated?.Invoke();
        }

        // Modul 18: Guild Logistics Depot contribution progress and co-op Guild
        // Raid boss state. Same pattern as ApplyVillagePacketState - discrete
        // values only ever come from the latest packet (no interpolation needed),
        // shared by both the first-packet and steady-state interpolation paths
        // above, fires OnGuildStateUpdated only on an actual change. Deliberately
        // scoped to the Logistics Depot + Raid boss fields only; the PvP guild war
        // front-point fields (VisualGuildCombatPoints etc.) are a separate system
        // and unrelated to this event.
        private void ApplyGuildPacketState(in StateUpdatePacket packet)
        {
            int logisticsLevel = packet.GuildLogisticsLevel;
            long logisticsCurrentStock = packet.GuildLogisticsCurrentStock;
            long logisticsTargetRequirement = packet.GuildLogisticsTargetRequirement;
            int raidTier = packet.GuildRaidTier;
            long raidBossCurrentHp = packet.GuildRaidBossCurrentHp;
            long raidBossMaxHp = packet.GuildRaidBossMaxHp;

            bool changed = logisticsLevel != VisualGuildLogisticsLevel
                || logisticsCurrentStock != VisualGuildLogisticsCurrentStock
                || logisticsTargetRequirement != VisualGuildLogisticsTargetRequirement
                || raidTier != VisualGuildRaidTier
                || raidBossCurrentHp != VisualGuildRaidBossCurrentHp
                || raidBossMaxHp != VisualGuildRaidBossMaxHp;

            if (!changed) return;

            VisualGuildLogisticsLevel = logisticsLevel;
            VisualGuildLogisticsCurrentStock = logisticsCurrentStock;
            VisualGuildLogisticsTargetRequirement = logisticsTargetRequirement;
            VisualGuildRaidTier = raidTier;
            VisualGuildRaidBossCurrentHp = raidBossCurrentHp;
            VisualGuildRaidBossMaxHp = raidBossMaxHp;

            OnGuildStateUpdated?.Invoke();
        }

        // Modul 16/21: character attribute/equip change detection. Same pattern
        // as ApplyVillagePacketState/ApplyGuildPacketState - discrete values only
        // ever come from the latest packet, shared by both the first-packet and
        // steady-state interpolation paths above, fires OnCharacterStateUpdated
        // only when a stat or equip slot actually changed.
        private void ApplyCharacterPacketState(in StateUpdatePacket packet)
        {
            int str = packet.STR;
            int dex = packet.DEX;
            int con = packet.CON;
            int lck = packet.LCK;
            long equippedWeaponId = packet.EquippedWeaponId;
            long equippedArmorId = packet.EquippedArmorId;

            bool changed = str != VisualSTR
                || dex != VisualDEX
                || con != VisualCON
                || lck != VisualLCK
                || equippedWeaponId != VisualEquippedWeaponId
                || equippedArmorId != VisualEquippedArmorId;

            if (!changed) return;

            VisualSTR = str;
            VisualDEX = dex;
            VisualCON = con;
            VisualLCK = lck;
            VisualEquippedWeaponId = equippedWeaponId;
            VisualEquippedArmorId = equippedArmorId;

            OnCharacterStateUpdated?.Invoke();
        }

        public int GetLegacyShardBalance()
        {
            return VisualLegacyShardCell.Value;
        }

        public long GetGoldBalance()
        {
            return VisualGoldCell.Value;
        }

        private static int ResolveClientXorKey(long playerId, long epoch)
        {
            long key = playerId ^ (epoch << 17) ^ 0x5F3759DF;
            if (key == 0L) key = 0x5F3759DF;
            return (int)(key & 0x7FFFFFFF);
        }

        private static long ResolveClientXorKey64(long playerId, long epoch)
        {
            long key = playerId ^ (epoch << 23) ^ 0x2545F4914F6CDD1DL;
            if (key == 0L) key = 0x2545F4914F6CDD1DL;
            return key;
        }
    }
}
