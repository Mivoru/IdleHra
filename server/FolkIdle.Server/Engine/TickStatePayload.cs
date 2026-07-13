namespace FolkIdle.Server.Engine
{
    public struct TickStatePayload
    {
        public long PlayerId;
        public System.Guid AccountId;
        public long ActiveActivityId;
        public int CurrentProgressTicks;
        public int RequiredProgressTicks;
        public int InventorySpaceRemaining;
        public bool IsDirty;
        public int TicksSinceLastFlush;
        public int CurrentLevel;
        public long CurrentXp;
        public int SelectedLineageId;
        
        public System.Guid Slot1_CharacterId;
        public long Slot1_AgeTicks;
        public int Slot1_AgePhase;
        public long Slot1_GeneticVector;

        public System.Guid Slot2_CharacterId;
        public long Slot2_AgeTicks;
        public int Slot2_AgePhase;
        public long Slot2_GeneticVector;

        public System.Guid Slot3_CharacterId;
        public long Slot3_AgeTicks;
        public int Slot3_AgePhase;
        public long Slot3_GeneticVector;
        
        public int CachedMentorCount;
        public long LastLogoutTimestamp;
        public long AccumulatedTimeBankMs;
        public int SpeedMultiplier;

        // Combat mechanics
        public int CurrentMonsterId;
        public int CurrentMonsterHp;
        public int PlayerHp;
        public int CombatTargetTickAccumulator;
        
        public bool IsSuspended;
        public bool Quarantine_Active;

        public long CurrentGold;
        public int PremiumCurrency;
        
        public long LastCommandTimestamp;

        // Gathering mastery
        public int WoodcuttingMasteryXp;
        public int WoodcuttingMasteryLevel;
        public int MiningMasteryXp;
        public int MiningMasteryLevel;
        public int GatheringProgressTicks;
        public int VillagePopulation;

        public int STR;
        public int DEX;
        public int CON;
        public int LCK;

        public int AutoEatThreshold;
        public int Food1_ItemId;
        public int Food1_Count;
        public int Food2_ItemId;
        public int Food2_Count;
        public int Food3_ItemId;
        public int Food3_Count;

        public long EquippedWeaponId;
        public bool EquippedWeaponAffixLocked;
        
        public long EquippedArmorId;
        public bool EquippedArmorAffixLocked;

        public int CachedMiningMonolithLevel;
        public int CachedWoodcuttingMonolithLevel;
        public long GuildId;
        public long ActiveGuildWarId;
        public System.Guid ActiveCrossShardMatchId;
        public int ActiveMatchMmr;
        public long GlobalNodeRemainingHp;
        public float CachedWarMultiplier;
        public int GuildCombatVanguardPoints;
        public int GuildProductionLogisticsPoints;
        public int GuildGatheringSupplyChainPoints;
        public int EnemyCombatVanguardPoints;
        public int EnemyProductionLogisticsPoints;
        public int EnemyGatheringSupplyChainPoints;
        
        // Alchemy Buffs
        public int ActiveOffensivePotionId;
        public int OffensivePotionDurationMs;
        public int ActiveDefensivePotionId;
        public int DefensivePotionDurationMs;

        public long WorldBossMaxHp;
        public long WorldBossCurrentHp;
        public int ActiveGlobalEventId;

        // Village Infrastructure
        public int CachedCurrentToolTier;
        public int CachedMaxPopulationCapacity;
        public int CachedInnMaturationBonus;
        public byte ForgeLevel;
        public byte InnLevel;
        public byte BreedingLevel;
        public byte AcademyLevel;
        public byte CurrentPopulationCount;
        public byte ActiveMentorshipContractCount;

        public int ActiveChildMaturationMs;

        // Codex and Achievements
        public int CompletedAreaFlags;
        public int ClaimedAchievementFlags;
        public uint TotalAchievementsClaimedCount;

        // Race Masteries
        public int HumanMasteryLevel;
        public int VilaMasteryLevel;
        public int DraugrMasteryLevel;
        public long LogicEpochCounter;
        // Chrono-buffer: unmanaged registers — operated on 100% allocation-free on hot-path.
        public double BankedChronoSeconds;
        public bool IsChronoAccelerating;
        public double ActiveChronoSpeedMultiplier;
        public long ActiveChronoLockExpirationTicks;
        public int LegacyShardBalance;
        public int CitizenMultiSlotsUnlocked;
        public long GuildLogisticsCurrentStock;
        public long GuildLogisticsTargetRequirement;
        public long CombatSimulationMatchId;
        public int CombatSimulationTurnCounter;
        public int CombatSimulationDamageDelta;
        public long ActiveMentorPlayerId;
        public double MentorshipExpBonusMultiplier;
        
        public uint NetworkDiagnosticsToken;

        // Redis write-behind session flags. Internal only; never serialized into network packets.
        public bool RequiresRedisFlush;
        public long RedisPendingGoldDelta;
        public FolkIdle.Server.Models.ObfuscatedInt64 ObfuscatedGold;
        public FolkIdle.Server.Models.ObfuscatedInt32 ObfuscatedPremiumCurrency;
        public FolkIdle.Server.Models.ObfuscatedInt32 ObfuscatedLegacyShards;
        public long ObfuscationSessionKey;
        public uint ActiveChallengeSeed;
        public long ActiveChallengeIssuedAtMs;
        public byte ActiveChallengeAnswered;
        public bool IsQuarantined;
        public byte ActiveLanguageState;
        public byte WorldBossAttemptCount;
        public uint ActiveUiContextBitmask;
        public uint ActiveChroniclePassLevel;
        public uint AccumulatedSeasonalXp;

        public void InitializeObfuscation(long sessionKey)
        {
            ObfuscationSessionKey = sessionKey == 0L ? PlayerId ^ 0x5F3759DF5F3759DFL : sessionKey;
            ObfuscatedGold = new FolkIdle.Server.Models.ObfuscatedInt64(CurrentGold, ObfuscationSessionKey);
            ObfuscatedPremiumCurrency = new FolkIdle.Server.Models.ObfuscatedInt32(PremiumCurrency, (int)(ObfuscationSessionKey & 0x7FFFFFFF));
            ObfuscatedLegacyShards = new FolkIdle.Server.Models.ObfuscatedInt32(LegacyShardBalance, (int)((ObfuscationSessionKey >> 17) & 0x7FFFFFFF));
        }

        public void SetGold(long value)
        {
            if (ObfuscationSessionKey == 0L) InitializeObfuscation(PlayerId ^ 0x5F3759DF5F3759DFL);
            CurrentGold = value;
            ObfuscatedGold.Value = value;
        }

        public void AddGold(long delta)
        {
            if (ObfuscationSessionKey == 0L) InitializeObfuscation(PlayerId ^ 0x5F3759DF5F3759DFL);
            CurrentGold += delta;
            ObfuscatedGold.Value = CurrentGold;
        }

        public void SetPremiumCurrency(int value)
        {
            if (ObfuscationSessionKey == 0L) InitializeObfuscation(PlayerId ^ 0x5F3759DF5F3759DFL);
            PremiumCurrency = value;
            ObfuscatedPremiumCurrency.Value = value;
        }

        public void SetLegacyShards(int value)
        {
            if (ObfuscationSessionKey == 0L) InitializeObfuscation(PlayerId ^ 0x5F3759DF5F3759DFL);
            LegacyShardBalance = value;
            ObfuscatedLegacyShards.Value = value;
        }
    }
}
