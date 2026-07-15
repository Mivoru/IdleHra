using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Models
{
    public class FolkIdleDbContext : DbContext
    {
        public DbSet<CommodityRecord> CommodityRecords { get; set; }
        public DbSet<MarketEquipmentInstance> MarketEquipmentInstances { get; set; }
        public DbSet<EquipmentInstance> EquipmentInstances { get; set; }
        public DbSet<BankEquipmentInstance> BankEquipmentInstances { get; set; }
        public DbSet<MailboxInstance> MailboxInstances { get; set; }
        public DbSet<MarketOrderRecord> MarketOrderRecords { get; set; }
        public DbSet<HistoricalMarketArchive> HistoricalMarketArchives { get; set; }
        public DbSet<GuildRecord> GuildRecords { get; set; }
        public DbSet<GuildDepotBalance> GuildDepotBalances { get; set; }
        public DbSet<GuildMaterialSinkLedger> GuildMaterialSinkLedgers { get; set; }
        public DbSet<GuildLogisticsDepot> GuildLogisticsDepots { get; set; }
        public DbSet<GuildContributionLedger> GuildContributionLedgers { get; set; }
        public DbSet<GuildRaidState> GuildRaidStates { get; set; }
        public DbSet<GuildMember> GuildMembers { get; set; }
        public DbSet<PlayerRecord> PlayerRecords { get; set; }
        public DbSet<CharacterRecord> CharacterRecords { get; set; }
        public DbSet<CharacterLineageRegistry> CharacterLineages { get; set; }
        public DbSet<VillageInfrastructure> VillageInfrastructures { get; set; }
        public DbSet<VillageResident> VillageResidents { get; set; }
        public DbSet<MentorshipAcademyAssignment> MentorshipAcademyAssignments { get; set; }
        public DbSet<MentorshipContract> MentorshipContracts { get; set; }
        public DbSet<MonsterCodexEntry> MonsterCodexEntries { get; set; }
        public DbSet<PlayerRegionCompletion> PlayerRegionCompletions { get; set; }
        public DbSet<PlayerRaceMastery> PlayerRaceMasteries { get; set; }
        public DbSet<PlayerAchievement> PlayerAchievements { get; set; }
        public DbSet<GuildWarMatch> GuildWarMatches { get; set; }
        public DbSet<GuildWarActiveMatch> GuildWarActiveMatches { get; set; }
        public DbSet<GuildWarCombatHistory> GuildWarCombatHistory { get; set; }
        public DbSet<GuildWarDefensiveSnapshot> GuildWarDefensiveSnapshots { get; set; }
        public DbSet<GuildTradeListing> GuildTradeListings { get; set; }
        public DbSet<PrimaryPurchaseLedger> PrimaryPurchaseLedgers { get; set; }
        public DbSet<EventHorizonPremiumLedger> EventHorizonPremiumLedgers { get; set; }
        public DbSet<EcoTelemetryLedger> EcoTelemetryLedgers { get; set; }
        public DbSet<SeasonalEraRecord> SeasonalEraRecords { get; set; }
        public DbSet<PlayerLegacyLedger> PlayerLegacyLedgers { get; set; }
        public DbSet<PlayerSegmentationProfile> PlayerSegmentationProfiles { get; set; }
        public DbSet<SegmentedStorefrontListing> SegmentedStorefrontListings { get; set; }
        public DbSet<WorldBossSnapshot> WorldBossSnapshots { get; set; }
        public DbSet<PlayerWorldBossAttempt> PlayerWorldBossAttempts { get; set; }
        public DbSet<LiveOpsEventRotation> LiveOpsEventRotations { get; set; }
        public DbSet<PlayerDeviceRegistration> PlayerDeviceRegistrations { get; set; }
        public DbSet<PlayerChronoRegistry> PlayerChronoRegistries { get; set; }
        public DbSet<EquipmentAffixMatrix> EquipmentAffixMatrices { get; set; }
        public DbSet<PlayerCraftingSlot> PlayerCraftingSlots { get; set; }
        public DbSet<PlayerLifetimeAchievement> PlayerLifetimeAchievements { get; set; }
        public DbSet<PlayerProductionRegistry> PlayerProductionRegistries { get; set; }
        public DbSet<PlayerChroniclePass> PlayerChroniclePasses { get; set; }
        public DbSet<AccountChronoRegistry> AccountChronoRegistries { get; set; }
        public DbSet<GuildMatchmakingSnapshot> GuildMatchmakingSnapshots { get; set; }
        public DbSet<GuildDefenseRoster> GuildDefenseRosters { get; set; }
        public DbSet<AccountAnalyticsLog> AccountAnalyticsLogs { get; set; }
        public DbSet<AccountSecurityQuota> AccountSecurityQuotas { get; set; }
        public DbSet<PlayerSkillUnlock> PlayerSkillUnlocks { get; set; }

        public FolkIdleDbContext(DbContextOptions<FolkIdleDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            
            modelBuilder.Entity<GuildMatchmakingSnapshot>()
                .HasIndex(g => g.TournamentGroupIndex);

            // Modul: unique login identity for AuthenticationEngine.
            // LoginOrProvisionAsync. Postgres unique indexes treat NULL as
            // distinct from every other NULL, so rows without a DeviceId
            // (every row created before this existed) never collide with
            // each other.
            modelBuilder.Entity<PlayerRecord>()
                .HasIndex(p => p.DeviceId)
                .IsUnique();

            modelBuilder.Entity<GuildWarDefensiveSnapshot>()
                .Property(g => g.RosterPayloadJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<PrimaryPurchaseLedger>()
                .HasIndex(p => p.PlayerId);

            modelBuilder.Entity<EventHorizonPremiumLedger>()
                .HasIndex(p => p.PlayerId);

            modelBuilder.Entity<PlayerProductionRegistry>()
                .HasKey(p => p.PlayerId);

            modelBuilder.Entity<PlayerProductionRegistry>()
                .HasIndex(p => new { p.PlayerId, p.CookingMasteryLevel, p.AlchemyMasteryLevel });

            modelBuilder.Entity<PlayerChroniclePass>()
                .HasKey(p => p.PlayerId);
            modelBuilder.Entity<PlayerChroniclePass>()
                .HasIndex(p => p.PassLevel);
            modelBuilder.Entity<PlayerChroniclePass>()
                .Property(p => p.ClaimedMilestonesBitmask)
                .HasConversion<long>()
                .HasColumnType("bigint");

            modelBuilder.Entity<AccountChronoRegistry>()
                .ToTable("account_chrono_registry", table =>
                {
                    table.HasCheckConstraint("CK_account_chrono_registry_BankedChronoSeconds", "\"BankedChronoSeconds\" >= 0 AND \"BankedChronoSeconds\" <= 604800");
                    table.HasCheckConstraint("CK_account_chrono_registry_ActiveSpeedMultiplier", "\"ActiveSpeedMultiplier\" IN (1.0, 2.0, 4.0)");
                });
            modelBuilder.Entity<AccountChronoRegistry>()
                .HasKey(c => c.AccountId);

            modelBuilder.Entity<GuildMatchmakingSnapshot>()
                .HasKey(m => m.MatchUuid);
            modelBuilder.Entity<GuildMatchmakingSnapshot>()
                .HasIndex(m => new { m.TournamentGroupIndex, m.ActiveMatchMmr });
            modelBuilder.Entity<GuildMatchmakingSnapshot>()
                .HasIndex(m => new { m.AttackerGuildId, m.DefenderGuildId });

            modelBuilder.Entity<GuildDefenseRoster>()
                .HasKey(r => r.GuildId);
            modelBuilder.Entity<GuildDefenseRoster>()
                .HasIndex(r => new { r.RegionShardId, r.GuildId });
            modelBuilder.Entity<GuildDefenseRoster>()
                .Property(r => r.DefensiveStatsJson)
                .HasColumnType("jsonb");

            modelBuilder.Entity<AccountAnalyticsLog>()
                .HasKey(l => l.LogId);
            modelBuilder.Entity<AccountAnalyticsLog>()
                .Property(l => l.EventTypeHash)
                .HasConversion<long>()
                .HasColumnType("bigint");
            modelBuilder.Entity<AccountAnalyticsLog>()
                .HasIndex(l => new { l.AccountId, l.TimestampEpoch })
                .HasDatabaseName("IX_AccountAnalyticsLogs_AccountId_TimestampEpoch")
                .HasMethod("btree");

            modelBuilder.Entity<AccountSecurityQuota>()
                .ToTable("AccountSecurityQuotas", table =>
                {
                    table.HasCheckConstraint("CK_AccountSecurityQuotas_TotalFloodInfractionsCount", "\"TotalFloodInfractionsCount\" >= 0");
                });
            modelBuilder.Entity<AccountSecurityQuota>()
                .HasKey(q => q.AccountId);
            modelBuilder.Entity<AccountSecurityQuota>()
                .HasIndex(q => new { q.IsPermanentlyBlacklisted, q.LastInfractionEpoch })
                .HasDatabaseName("IX_AccountSecurityQuotas_Blacklist_LastInfraction");

            modelBuilder.Entity<AccountSecurityQuota>()
                .HasIndex(q => new { q.AccountId, q.IsPermanentlyBlacklisted })
                .HasDatabaseName("IX_AccountSecurityQuotas_SecurityIndex");

            modelBuilder.Entity<MentorshipAcademyAssignment>()
                .HasKey(m => new { m.PlayerId, m.CharacterId });

            modelBuilder.Entity<VillageInfrastructure>()
                .HasKey(v => new { v.PlayerId, v.BuildingId });

            modelBuilder.Entity<VillageResident>()
                .HasKey(v => new { v.PlayerId, v.SlotIndex });

            modelBuilder.Entity<MentorshipContract>()
                .HasIndex(m => m.MenteePlayerId)
                .IsUnique();
            
            modelBuilder.Entity<GuildDepotBalance>()
                .HasKey(gdb => new { gdb.GuildId, gdb.ItemDefinitionId });

            modelBuilder.Entity<GuildWarActiveMatch>()
                .Property(m => m.CurrentStateBitmask)
                .HasConversion<long>()
                .HasColumnType("bigint");

            modelBuilder.Entity<GuildLogisticsDepot>()
                .HasKey(g => new { g.GuildId, g.MaterialId });

            modelBuilder.Entity<GuildContributionLedger>()
                .HasKey(g => new { g.PlayerId, g.GuildId, g.MaterialId });

            modelBuilder.Entity<GuildMember>()
                .HasIndex(m => m.GuildId);

            modelBuilder.Entity<MarketEquipmentInstance>()
                .Property(e => e.AffixPayload)
                .HasColumnType("jsonb");

            modelBuilder.Entity<EquipmentInstance>()
                .Property(e => e.AffixPayload)
                .HasColumnType("jsonb");

            modelBuilder.Entity<BankEquipmentInstance>()
                .Property(e => e.AffixPayload)
                .HasColumnType("jsonb");

            modelBuilder.Entity<MonsterCodexEntry>()
                .HasKey(m => new { m.PlayerId, m.MonsterId });
            modelBuilder.Entity<MonsterCodexEntry>()
                .HasIndex(m => m.PlayerId);

            modelBuilder.Entity<PlayerRaceMastery>()
                .HasKey(m => new { m.PlayerId, m.RaceId });
            modelBuilder.Entity<PlayerRaceMastery>()
                .HasIndex(m => m.PlayerId);

            modelBuilder.Entity<PlayerRegionCompletion>()
                .HasKey(r => new { r.PlayerId, r.RegionId });
            modelBuilder.Entity<PlayerRegionCompletion>()
                .HasIndex(r => r.PlayerId);

            modelBuilder.Entity<PlayerLegacyLedger>()
                .HasKey(l => new { l.PlayerId, l.EraId });
            modelBuilder.Entity<PlayerLegacyLedger>()
                .HasOne(l => l.Era)
                .WithMany()
                .HasForeignKey(l => l.EraId);

            modelBuilder.Entity<PlayerSegmentationProfile>()
                .HasKey(p => p.PlayerId);

            modelBuilder.Entity<SegmentedStorefrontListing>()
                .HasIndex(l => l.TargetCohort)
                .HasDatabaseName("IX_SegmentedStorefrontListings_TargetCohort")
                .HasMethod("btree");

            modelBuilder.Entity<WorldBossSnapshot>()
                .HasKey(w => w.BossInstanceId);

            modelBuilder.Entity<PlayerWorldBossAttempt>()
                .HasKey(a => new { a.PlayerId, a.BossInstanceId });

            modelBuilder.Entity<LiveOpsEventRotation>()
                .HasKey(l => l.EventId);
            modelBuilder.Entity<LiveOpsEventRotation>()
                .Property(l => l.ModifierBitmask)
                .HasConversion<long>()
                .HasColumnType("bigint");

            modelBuilder.Entity<PlayerDeviceRegistration>()
                .HasKey(d => new { d.PlayerId, d.DeviceTokenRaw });
            modelBuilder.Entity<PlayerDeviceRegistration>()
                .Property(d => d.DeviceTokenRaw)
                .HasMaxLength(64);
            modelBuilder.Entity<PlayerDeviceRegistration>()
                .HasIndex(d => d.PlayerId);

            modelBuilder.Entity<PlayerChronoRegistry>()
                .HasKey(p => p.PlayerId);

            modelBuilder.Entity<PlayerCraftingSlot>()
                .HasKey(p => new { p.PlayerId, p.SlotIndex });

            modelBuilder.Entity<PlayerLifetimeAchievement>()
                .HasKey(p => new { p.PlayerId, p.AchievementId });

            modelBuilder.Entity<PlayerSkillUnlock>()
                .HasKey(s => new { s.PlayerId, s.SkillId });
            modelBuilder.Entity<PlayerSkillUnlock>()
                .HasIndex(s => s.PlayerId);

        }
    }
}
