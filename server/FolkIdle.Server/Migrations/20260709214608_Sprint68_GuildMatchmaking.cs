using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class Sprint68_GuildMatchmaking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveDefensivePotionId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ActiveOffensivePotionId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "BankedChronoSeconds",
                table: "PlayerRecords",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "DefensivePotionDurationMs",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "GuildId",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsChronoAccelerating",
                table: "PlayerRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsQuarantined",
                table: "PlayerRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LogicEpochCounter",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "OffensivePotionDurationMs",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PremiumDiamonds",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Quarantine_Active",
                table: "PlayerRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsQuarantined",
                table: "MarketOrderRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsQuarantined",
                table: "MarketEquipmentInstances",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ReceivedTimestamp",
                table: "MailboxInstances",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AgeTicks",
                table: "characters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ActiveMembers",
                table: "GuildRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GuildMMR",
                table: "GuildRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxMembers",
                table: "GuildRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "account_chrono_registry",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    BankedChronoSeconds = table.Column<int>(type: "integer", nullable: false),
                    ActiveSpeedMultiplier = table.Column<double>(type: "double precision", nullable: false),
                    AccelerationTerminationEpoch = table.Column<long>(type: "bigint", nullable: false),
                    LastClockSyncEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_chrono_registry", x => x.AccountId);
                    table.CheckConstraint("CK_account_chrono_registry_ActiveSpeedMultiplier", "\"ActiveSpeedMultiplier\" IN (1.0, 2.0, 4.0)");
                    table.CheckConstraint("CK_account_chrono_registry_BankedChronoSeconds", "\"BankedChronoSeconds\" >= 0 AND \"BankedChronoSeconds\" <= 604800");
                });

            migrationBuilder.CreateTable(
                name: "AccountAnalyticsLogs",
                columns: table => new
                {
                    LogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventTypeHash = table.Column<long>(type: "bigint", nullable: false),
                    TimestampEpoch = table.Column<long>(type: "bigint", nullable: false),
                    PayloadMetric = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAnalyticsLogs", x => x.LogId);
                });

            migrationBuilder.CreateTable(
                name: "AccountSecurityQuotas",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TotalFloodInfractionsCount = table.Column<int>(type: "integer", nullable: false),
                    LastInfractionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    IsPermanentlyBlacklisted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountSecurityQuotas", x => x.AccountId);
                    table.CheckConstraint("CK_AccountSecurityQuotas_TotalFloodInfractionsCount", "\"TotalFloodInfractionsCount\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "EcoTelemetryLedgers",
                columns: table => new
                {
                    LogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false),
                    TotalGoldMinted = table.Column<long>(type: "bigint", nullable: false),
                    TotalGoldConsumed = table.Column<long>(type: "bigint", nullable: false),
                    TotalDiamondsMinted = table.Column<long>(type: "bigint", nullable: false),
                    TotalDiamondsConsumed = table.Column<long>(type: "bigint", nullable: false),
                    CalculatedRatio = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EcoTelemetryLedgers", x => x.LogId);
                });

            migrationBuilder.CreateTable(
                name: "EquipmentAffixMatrices",
                columns: table => new
                {
                    AffixId = table.Column<int>(type: "integer", nullable: false),
                    StatType = table.Column<byte>(type: "smallint", nullable: false),
                    MinBaseValue = table.Column<int>(type: "integer", nullable: false),
                    MaxBaseValue = table.Column<int>(type: "integer", nullable: false),
                    GeometricalScalingFactor = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentAffixMatrices", x => x.AffixId);
                });

            migrationBuilder.CreateTable(
                name: "EventHorizonPremiumLedgers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    PreviousBalance = table.Column<int>(type: "integer", nullable: false),
                    NewBalance = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventHorizonPremiumLedgers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GuildContributionLedgers",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    MaterialId = table.Column<int>(type: "integer", nullable: false),
                    LifetimeContributed = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildContributionLedgers", x => new { x.PlayerId, x.GuildId, x.MaterialId });
                });

            migrationBuilder.CreateTable(
                name: "GuildDefenseRosters",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegionShardId = table.Column<int>(type: "integer", nullable: false),
                    DefensiveStatsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildDefenseRosters", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "GuildLogisticsDepots",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    MaterialId = table.Column<int>(type: "integer", nullable: false),
                    CurrentStock = table.Column<long>(type: "bigint", nullable: false),
                    TargetRequirement = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildLogisticsDepots", x => new { x.GuildId, x.MaterialId });
                });

            migrationBuilder.CreateTable(
                name: "GuildMatchmakingSnapshots",
                columns: table => new
                {
                    MatchUuid = table.Column<Guid>(type: "uuid", nullable: false),
                    AttackerGuildId = table.Column<long>(type: "bigint", nullable: false),
                    DefenderGuildId = table.Column<long>(type: "bigint", nullable: false),
                    GlobalNodeMaxHp = table.Column<long>(type: "bigint", nullable: false),
                    GlobalNodeRemainingHp = table.Column<long>(type: "bigint", nullable: false),
                    TournamentGroupIndex = table.Column<int>(type: "integer", nullable: false),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveMatchMmr = table.Column<int>(type: "integer", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildMatchmakingSnapshots", x => x.MatchUuid);
                });

            migrationBuilder.CreateTable(
                name: "GuildTradeListings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    MarketEquipmentInstanceId = table.Column<long>(type: "bigint", nullable: true),
                    MarketOrderRecordId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildTradeListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildTradeListings_MarketEquipmentInstances_MarketEquipment~",
                        column: x => x.MarketEquipmentInstanceId,
                        principalTable: "MarketEquipmentInstances",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GuildTradeListings_MarketOrderRecords_MarketOrderRecordId",
                        column: x => x.MarketOrderRecordId,
                        principalTable: "MarketOrderRecords",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GuildWarActiveMatches",
                columns: table => new
                {
                    MatchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttackingGuildId = table.Column<long>(type: "bigint", nullable: false),
                    DefendingGuildId = table.Column<long>(type: "bigint", nullable: false),
                    InitialSeed = table.Column<int>(type: "integer", nullable: false),
                    CurrentStateBitmask = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildWarActiveMatches", x => x.MatchId);
                });

            migrationBuilder.CreateTable(
                name: "GuildWarCombatHistory",
                columns: table => new
                {
                    LogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionTick = table.Column<long>(type: "bigint", nullable: false),
                    DamageDelta = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildWarCombatHistory", x => x.LogId);
                });

            migrationBuilder.CreateTable(
                name: "GuildWarDefensiveSnapshots",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RosterPayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildWarDefensiveSnapshots", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "GuildWarMatches",
                columns: table => new
                {
                    MatchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GuildA_Id = table.Column<long>(type: "bigint", nullable: false),
                    GuildB_Id = table.Column<long>(type: "bigint", nullable: false),
                    MatchEpoch = table.Column<int>(type: "integer", nullable: false),
                    CombatVanguardWP_A = table.Column<int>(type: "integer", nullable: false),
                    ProductionLogisticsWP_A = table.Column<int>(type: "integer", nullable: false),
                    GatheringSupplyChainWP_A = table.Column<int>(type: "integer", nullable: false),
                    CombatVanguardWP_B = table.Column<int>(type: "integer", nullable: false),
                    ProductionLogisticsWP_B = table.Column<int>(type: "integer", nullable: false),
                    GatheringSupplyChainWP_B = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildWarMatches", x => x.MatchId);
                });

            migrationBuilder.CreateTable(
                name: "LiveOpsEventRotations",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventType = table.Column<byte>(type: "smallint", nullable: false),
                    ModifierBitmask = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestamp = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveOpsEventRotations", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "MentorshipAcademyAssignments",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MentorshipAcademyAssignments", x => new { x.PlayerId, x.CharacterId });
                });

            migrationBuilder.CreateTable(
                name: "MentorshipContracts",
                columns: table => new
                {
                    ContractId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MentorPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    MenteePlayerId = table.Column<long>(type: "bigint", nullable: false),
                    ExpBonusMultiplier = table.Column<double>(type: "double precision", nullable: false),
                    TimestampEstablished = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MentorshipContracts", x => x.ContractId);
                });

            migrationBuilder.CreateTable(
                name: "monster_codex_entries",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    MonsterId = table.Column<int>(type: "integer", nullable: false),
                    KillCount = table.Column<int>(type: "integer", nullable: false),
                    FirstDrawnRarity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monster_codex_entries", x => new { x.PlayerId, x.MonsterId });
                });

            migrationBuilder.CreateTable(
                name: "player_achievements",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClaimedAchievementFlags = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_achievements", x => x.PlayerId);
                });

            migrationBuilder.CreateTable(
                name: "player_lifetime_achievements",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    AchievementId = table.Column<int>(type: "integer", nullable: false),
                    CurrentProgress = table.Column<long>(type: "bigint", nullable: false),
                    IsClaimed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_lifetime_achievements", x => new { x.PlayerId, x.AchievementId });
                });

            migrationBuilder.CreateTable(
                name: "player_monster_codex",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    MonsterId = table.Column<int>(type: "integer", nullable: false),
                    KillCount = table.Column<long>(type: "bigint", nullable: false),
                    MaxRarityFound = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_monster_codex", x => new { x.PlayerId, x.MonsterId });
                });

            migrationBuilder.CreateTable(
                name: "player_race_masteries",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    RaceId = table.Column<int>(type: "integer", nullable: false),
                    MasteryLevel = table.Column<int>(type: "integer", nullable: false),
                    CumulativeXp = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_race_masteries", x => new { x.PlayerId, x.RaceId });
                });

            migrationBuilder.CreateTable(
                name: "PlayerCraftingSlots",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    SlotIndex = table.Column<byte>(type: "smallint", nullable: false),
                    ActiveRecipeId = table.Column<int>(type: "integer", nullable: false),
                    CompletionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    IsReady = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerCraftingSlots", x => new { x.PlayerId, x.SlotIndex });
                });

            migrationBuilder.CreateTable(
                name: "PlayerDeviceRegistrations",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    DeviceTokenRaw = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                    PlatformFamily = table.Column<byte>(type: "smallint", nullable: false),
                    TimestampRegistered = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerDeviceRegistrations", x => new { x.PlayerId, x.DeviceTokenRaw });
                });

            migrationBuilder.CreateTable(
                name: "PlayerChroniclePasses",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PassLevel = table.Column<int>(type: "integer", nullable: false),
                    AccumulatedXp = table.Column<int>(type: "integer", nullable: false),
                    ClaimedMilestonesBitmask = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerChroniclePasses", x => x.PlayerId);
                });

            migrationBuilder.CreateTable(
                name: "PlayerChronoRegistries",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankedChronoSeconds = table.Column<long>(type: "bigint", nullable: false),
                    LastDisconnectTimestamp = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerChronoRegistries", x => x.PlayerId);
                });

            migrationBuilder.CreateTable(
                name: "PlayerProductionRegistries",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CookingMasteryLevel = table.Column<int>(type: "integer", nullable: false),
                    AlchemyMasteryLevel = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerProductionRegistries", x => x.PlayerId);
                });

            migrationBuilder.CreateTable(
                name: "PlayerSegmentationProfiles",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CohortTag = table.Column<int>(type: "integer", nullable: false),
                    LifetimeValueCents = table.Column<int>(type: "integer", nullable: false),
                    ChurnRiskScore = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSegmentationProfiles", x => x.PlayerId);
                });

            migrationBuilder.CreateTable(
                name: "PrimaryPurchaseLedgers",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PurchaseState = table.Column<byte>(type: "smallint", nullable: false),
                    TimestampProcessed = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrimaryPurchaseLedgers", x => x.TransactionId);
                });

            migrationBuilder.CreateTable(
                name: "SeasonalEraRecords",
                columns: table => new
                {
                    EraId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EndTimestamp = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonalEraRecords", x => x.EraId);
                });

            migrationBuilder.CreateTable(
                name: "SegmentedStorefrontListings",
                columns: table => new
                {
                    ListingId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TargetCohort = table.Column<int>(type: "integer", nullable: false),
                    ProductIdentifier = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DiamondPackageYield = table.Column<int>(type: "integer", nullable: false),
                    PriceInCents = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentedStorefrontListings", x => x.ListingId);
                });

            migrationBuilder.CreateTable(
                name: "VillageInfrastructures",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    BuildingId = table.Column<int>(type: "integer", nullable: false),
                    CurrentLevel = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VillageInfrastructures", x => new { x.PlayerId, x.BuildingId });
                });

            migrationBuilder.CreateTable(
                name: "VillageResidents",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    SlotIndex = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    EfficiencyModifier = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VillageResidents", x => new { x.PlayerId, x.SlotIndex });
                });

            migrationBuilder.CreateTable(
                name: "WorldBossSnapshots",
                columns: table => new
                {
                    BossInstanceId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaxHp = table.Column<long>(type: "bigint", nullable: false),
                    CurrentHp = table.Column<long>(type: "bigint", nullable: false),
                    TotalDamageContributed = table.Column<long>(type: "bigint", nullable: false),
                    LastActiveTimestamp = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldBossSnapshots", x => x.BossInstanceId);
                });

            migrationBuilder.CreateTable(
                name: "PlayerLegacyLedgers",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    EraId = table.Column<int>(type: "integer", nullable: false),
                    LegacyShardBalance = table.Column<int>(type: "integer", nullable: false),
                    CitizenMultiSlotsUnlocked = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerLegacyLedgers", x => new { x.PlayerId, x.EraId });
                    table.ForeignKey(
                        name: "FK_PlayerLegacyLedgers_SeasonalEraRecords_EraId",
                        column: x => x.EraId,
                        principalTable: "SeasonalEraRecords",
                        principalColumn: "EraId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAnalyticsLogs_AccountId_TimestampEpoch",
                table: "AccountAnalyticsLogs",
                columns: new[] { "AccountId", "TimestampEpoch" })
                .Annotation("Npgsql:IndexMethod", "btree");

            migrationBuilder.CreateIndex(
                name: "IX_AccountSecurityQuotas_Blacklist_LastInfraction",
                table: "AccountSecurityQuotas",
                columns: new[] { "IsPermanentlyBlacklisted", "LastInfractionEpoch" });

            migrationBuilder.CreateIndex(
                name: "IX_EventHorizonPremiumLedgers_PlayerId",
                table: "EventHorizonPremiumLedgers",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildDefenseRosters_RegionShardId_GuildId",
                table: "GuildDefenseRosters",
                columns: new[] { "RegionShardId", "GuildId" });

            migrationBuilder.CreateIndex(
                name: "IX_GuildMatchmakingSnapshots_AttackerGuildId_DefenderGuildId",
                table: "GuildMatchmakingSnapshots",
                columns: new[] { "AttackerGuildId", "DefenderGuildId" });

            migrationBuilder.CreateIndex(
                name: "IX_GuildMatchmakingSnapshots_TournamentGroupIndex",
                table: "GuildMatchmakingSnapshots",
                column: "TournamentGroupIndex");

            migrationBuilder.CreateIndex(
                name: "IX_GuildMatchmakingSnapshots_TournamentGroupIndex_ActiveMatchM~",
                table: "GuildMatchmakingSnapshots",
                columns: new[] { "TournamentGroupIndex", "ActiveMatchMmr" });

            migrationBuilder.CreateIndex(
                name: "IX_GuildTradeListings_MarketEquipmentInstanceId",
                table: "GuildTradeListings",
                column: "MarketEquipmentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_GuildTradeListings_MarketOrderRecordId",
                table: "GuildTradeListings",
                column: "MarketOrderRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_MentorshipContracts_MenteePlayerId",
                table: "MentorshipContracts",
                column: "MenteePlayerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_monster_codex_entries_PlayerId",
                table: "monster_codex_entries",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_player_race_masteries_PlayerId",
                table: "player_race_masteries",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDeviceRegistrations_PlayerId",
                table: "PlayerDeviceRegistrations",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerChroniclePasses_PassLevel",
                table: "PlayerChroniclePasses",
                column: "PassLevel");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerLegacyLedgers_EraId",
                table: "PlayerLegacyLedgers",
                column: "EraId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerProductionRegistries_PlayerId_CookingMasteryLevel_Alc~",
                table: "PlayerProductionRegistries",
                columns: new[] { "PlayerId", "CookingMasteryLevel", "AlchemyMasteryLevel" });

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryPurchaseLedgers_PlayerId",
                table: "PrimaryPurchaseLedgers",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_SegmentedStorefrontListings_TargetCohort",
                table: "SegmentedStorefrontListings",
                column: "TargetCohort")
                .Annotation("Npgsql:IndexMethod", "btree");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_chrono_registry");

            migrationBuilder.DropTable(
                name: "AccountAnalyticsLogs");

            migrationBuilder.DropTable(
                name: "AccountSecurityQuotas");

            migrationBuilder.DropTable(
                name: "EcoTelemetryLedgers");

            migrationBuilder.DropTable(
                name: "EquipmentAffixMatrices");

            migrationBuilder.DropTable(
                name: "EventHorizonPremiumLedgers");

            migrationBuilder.DropTable(
                name: "GuildContributionLedgers");

            migrationBuilder.DropTable(
                name: "GuildDefenseRosters");

            migrationBuilder.DropTable(
                name: "GuildLogisticsDepots");

            migrationBuilder.DropTable(
                name: "GuildMatchmakingSnapshots");

            migrationBuilder.DropTable(
                name: "GuildTradeListings");

            migrationBuilder.DropTable(
                name: "GuildWarActiveMatches");

            migrationBuilder.DropTable(
                name: "GuildWarCombatHistory");

            migrationBuilder.DropTable(
                name: "GuildWarDefensiveSnapshots");

            migrationBuilder.DropTable(
                name: "GuildWarMatches");

            migrationBuilder.DropTable(
                name: "LiveOpsEventRotations");

            migrationBuilder.DropTable(
                name: "MentorshipAcademyAssignments");

            migrationBuilder.DropTable(
                name: "MentorshipContracts");

            migrationBuilder.DropTable(
                name: "monster_codex_entries");

            migrationBuilder.DropTable(
                name: "player_achievements");

            migrationBuilder.DropTable(
                name: "player_lifetime_achievements");

            migrationBuilder.DropTable(
                name: "player_monster_codex");

            migrationBuilder.DropTable(
                name: "player_race_masteries");

            migrationBuilder.DropTable(
                name: "PlayerCraftingSlots");

            migrationBuilder.DropTable(
                name: "PlayerDeviceRegistrations");

            migrationBuilder.DropTable(
                name: "PlayerChroniclePasses");

            migrationBuilder.DropTable(
                name: "PlayerChronoRegistries");

            migrationBuilder.DropTable(
                name: "PlayerLegacyLedgers");

            migrationBuilder.DropTable(
                name: "PlayerProductionRegistries");

            migrationBuilder.DropTable(
                name: "PlayerSegmentationProfiles");

            migrationBuilder.DropTable(
                name: "PrimaryPurchaseLedgers");

            migrationBuilder.DropTable(
                name: "SegmentedStorefrontListings");

            migrationBuilder.DropTable(
                name: "VillageInfrastructures");

            migrationBuilder.DropTable(
                name: "VillageResidents");

            migrationBuilder.DropTable(
                name: "WorldBossSnapshots");

            migrationBuilder.DropTable(
                name: "SeasonalEraRecords");

            migrationBuilder.DropColumn(
                name: "ActiveDefensivePotionId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "ActiveOffensivePotionId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BankedChronoSeconds",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "DefensivePotionDurationMs",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "GuildId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "IsChronoAccelerating",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "IsQuarantined",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LogicEpochCounter",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "OffensivePotionDurationMs",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PremiumDiamonds",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "Quarantine_Active",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "IsQuarantined",
                table: "MarketOrderRecords");

            migrationBuilder.DropColumn(
                name: "IsQuarantined",
                table: "MarketEquipmentInstances");

            migrationBuilder.DropColumn(
                name: "ReceivedTimestamp",
                table: "MailboxInstances");

            migrationBuilder.DropColumn(
                name: "AgeTicks",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "ActiveMembers",
                table: "GuildRecords");

            migrationBuilder.DropColumn(
                name: "GuildMMR",
                table: "GuildRecords");

            migrationBuilder.DropColumn(
                name: "MaxMembers",
                table: "GuildRecords");
        }
    }
}
