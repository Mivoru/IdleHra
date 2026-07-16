namespace FolkIdle.Server.Models
{
    public class PlayerRecord
    {
        public long Id { get; set; }
        public int CurrentLevel { get; set; }
        public long CurrentXp { get; set; }
        public int SelectedLineageId { get; set; }
        public System.Guid PlayerGuid { get; set; }
        public System.Guid AuthenticatorToken { get; set; }

        // Modul: device-ID login identity, resolved by AuthenticationEngine.
        // LoginOrProvisionAsync at /api/v1/auth/login. Null for any row
        // created before this existed (DbSeeder rows, pre-JWT test fixtures).
        // Nothing reads this outside the login flow itself - PlayerGuid
        // remains the AccountId used everywhere else (AccountSecurityQuotas,
        // JWT claims, purchase/refund webhooks).
        public string? DeviceId { get; set; }

        // Modul: OAuth account binding. ProviderType 0 means "not linked to
        // any external provider" - ExternalProviderId is null in that case,
        // matching DeviceId's existing convention for this same unique-
        // index-with-many-NULLs shape (Postgres treats NULL as distinct from
        // every other NULL, so unlinked rows never collide with each other).
        // Linking is irreversible by design (see AuthenticationEngine.
        // LinkOAuthAccountAsync) - once set, these two fields are never
        // cleared back to their unlinked state.
        public int ProviderType { get; set; }
        public string? ExternalProviderId { get; set; }

        public long LastLogoutTimestamp { get; set; }

        // Modul: daily login reward tracking (DailyLoginRewardEngine).
        // LastLoginTimestamp is the epoch second of the last login that was
        // actually credited a reward - compared against the current UTC day
        // boundary, not the exact previous login attempt, so multiple
        // logins on the same UTC day never grant twice. LoginStreakDays
        // counts consecutive credited days and resets to 1 (not 0) the
        // moment a day is skipped, since a fresh streak still counts today.
        public long LastLoginTimestamp { get; set; }
        public int LoginStreakDays { get; set; }
        public int AccumulatedTimeBankSeconds { get; set; }
        public long GuildId { get; set; }
        public int ActiveOffensivePotionId { get; set; }
        public int OffensivePotionDurationMs { get; set; }
        public int ActiveDefensivePotionId { get; set; }
        public int DefensivePotionDurationMs { get; set; }
        public int PremiumDiamonds { get; set; }
        public bool Quarantine_Active { get; set; }
        public bool IsQuarantined { get; set; }
        public long LogicEpochCounter { get; set; }
        public double BankedChronoSeconds { get; set; }
        public bool IsChronoAccelerating { get; set; }

        // Modul 16/21: persistent character attribute base values. Never
        // modified directly except by level-up growth (RaceAttributeGrowth) -
        // equipment/potion/age bonuses are applied on top of these at read time
        // in StatsCalculator, never folded back into the persisted base.
        public int BaseStrength { get; set; }
        public int BaseDexterity { get; set; }
        public int BaseConstitution { get; set; }
        public int BaseLuck { get; set; }

        // Modul 16/21: currently equipped gear. Null/0 means the slot is empty.
        // References EquipmentInstances.Id (informally called a "Guid" elsewhere
        // in this codebase, e.g. AffixRerollEngine.ExecuteRerollAsync, but it is
        // actually a long).
        public long? EquippedWeaponId { get; set; }
        public long? EquippedArmorId { get; set; }

        // Modul 13.4.3: set by MentorshipEngine when a mentee's contract is
        // terminated before its maturation threshold - while this is in the
        // future, character XP generation is reduced (see StatsCalculator
        // consumers of TickStatePayload.XpPenaltyExpiresEpoch). 0 means no
        // active penalty.
        public long XpPenaltyExpiresEpoch { get; set; }

        // Active Skill Tree: earned on level-up (see SimulationEngine.
        // ApplyBulkExperience), spent unlocking a skill (see
        // ActiveSkillEngine and the PlayerSkillUnlocks table, which records
        // WHICH skills were unlocked - this column only tracks the
        // remaining, unspent balance).
        public int AvailableSkillPoints { get; set; }

        // Modul: Prestige (Legacy Shard) permanent perk tree - purchased
        // through LegacyStoreEngine.PurchaseLegacyPerkAsync, distinct from
        // CitizenMultiSlotsUnlocked (a per-era ledger field). Bitmask: each
        // perk gets an 8-bit rank slot (0-255, though
        // LegacyPerkResolver.MaxPerkRank caps purchasable rank well below
        // that), packed as XpMultiplier in bits 0-7, GoldDropRate in bits
        // 8-15, CombatSpeedMultiplier in bits 16-23 - see LegacyPerkResolver
        // for the exact bit layout and per-rank bonus values. Survives
        // across eras (unlike PlayerLegacyLedger.CitizenMultiSlotsUnlocked,
        // which is per-EraId) since these are permanent account-wide power,
        // not a seasonal unlock.
        public long LegacyPerks { get; set; }

        // Modul: Logistics achievement family's stackable claim reward
        // (Phase: Full-Stack Production Polish, Part 2.3) - cumulative flat
        // percent reduction applied to gathering-node tick thresholds,
        // granted alongside PremiumDiamonds each time a Logistics tier is
        // crossed (see AchievementMilestones.LogisticsStatBonusRewards and
        // StateCheckpointManager.EvaluateAndAwardTierAsync). Never resets -
        // once earned, always active, exactly like PremiumDiamonds rewards
        // from the same tier crossing.
        public int LogisticsGatheringSpeedBonusPct { get; set; }
    }
}
