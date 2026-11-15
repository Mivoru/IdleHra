using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The Chronicle pass: the season's track, what is ready, and the claim
    /// (docs/design/CHRONICLE_PASS.md step 3). The rules live in
    /// <see cref="ChronicleRegistry"/>; the XP is the daily missions'.
    /// </summary>
    public static class ChronicleEngine
    {
        public sealed record RewardView(string Kind, int Amount, long Gold, string Label, string? Art, string? Chest);

        public sealed record LevelView(int Level, string State, IReadOnlyList<RewardView> Rewards);

        public sealed class PassView
        {
            public int Season { get; init; }
            public string SeasonName { get; init; } = string.Empty;
            public int Xp { get; init; }
            public int Level { get; init; }
            public int XpIntoLevel { get; init; }
            public int XpPerLevel { get; init; } = ChronicleRegistry.XpPerLevel;
            public int Levels { get; init; } = ChronicleRegistry.Levels;
            public int SecondsLeft { get; init; }
            public List<LevelView> Track { get; init; } = new();
            /// <summary>Bonus chests earned past level 50 and not yet claimed.</summary>
            public int OverflowReady { get; init; }
            public int OverflowClaimed { get; init; }
            /// <summary>XP toward the next bonus chest, once past level 50.</summary>
            public int OverflowProgress { get; init; }
            /// <summary>Levels reached and not claimed, plus ready bonus chests - the bubble's badge.</summary>
            public int Claimable { get; init; }
            public string PetName { get; init; } = string.Empty;
            public string PetBonus { get; init; } = string.Empty;
        }

        public enum ClaimOutcome { Ok, NothingToClaim, PlayerNotFound }

        public sealed record ClaimResult(
            ClaimOutcome Outcome,
            IReadOnlyList<int> LevelsClaimed,
            long Gold = 0,
            int Diamonds = 0,
            int DiamondBalance = 0,
            int Tickets = 0,
            IReadOnlyList<string>? Chests = null,
            IReadOnlyList<string>? Keepsakes = null,
            int OverflowChests = 0);

        public static (int Season, int Start, int End) CurrentSeason(DateTime utcNow)
        {
            int season = ChronicleRegistry.SeasonNumberFor(MissionEngine.DayKeyOf(utcNow));
            return (season, ChronicleRegistry.StartDayOf(season), ChronicleRegistry.EndDayOf(season));
        }

        public static async Task<PassView> ViewAsync(FolkIdleDbContext db, long playerId, DateTime utcNow)
        {
            var (number, start, end) = CurrentSeason(utcNow);
            var season = ChronicleRegistry.Season(number);
            int xp = await MissionEngine.XpBetweenAsync(db, playerId, start, end);
            var row = await db.PlayerChronicleSeasons.AsNoTracking().SingleOrDefaultAsync(c => c.PlayerId == playerId && c.Season == number);
            long hourly = await GoldRewardScale.HourlyIncomeAsync(db, playerId, utcNow);
            return BuildView(season, xp, row?.ClaimedMask ?? 0, row?.OverflowClaimed ?? 0, hourly, utcNow, end);
        }

        internal static PassView BuildView(ChronicleSeason season, int xp, long claimedMask, int overflowClaimed, long hourly, DateTime utcNow, int endDay)
        {
            int level = ChronicleRegistry.LevelFor(xp);
            var track = new List<LevelView>(ChronicleRegistry.Levels);
            int claimable = 0;
            for (int l = 1; l <= ChronicleRegistry.Levels; l++)
            {
                bool claimed = (claimedMask & (1L << l)) != 0;
                string state = claimed ? "Claimed" : l <= level ? "Ready" : "Locked";
                if (state == "Ready") claimable++;
                track.Add(new LevelView(l, state, ChronicleRegistry.RewardsFor(l, season).Select(r => Describe(r, season, hourly)).ToList()));
            }

            int overflowEarned = ChronicleRegistry.OverflowChestsFor(xp);
            int overflowReady = Math.Max(0, overflowEarned - overflowClaimed);
            claimable += overflowReady;
            long nowEpoch = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();

            return new PassView
            {
                Season = season.Number,
                SeasonName = season.Name,
                Xp = xp,
                Level = level,
                XpIntoLevel = level >= ChronicleRegistry.Levels ? ChronicleRegistry.XpPerLevel : xp % ChronicleRegistry.XpPerLevel,
                SecondsLeft = (int)Math.Max(0, (endDay + 1L) * 86400L - nowEpoch),
                Track = track,
                OverflowReady = overflowReady,
                OverflowClaimed = overflowClaimed,
                OverflowProgress = xp > ChronicleRegistry.MaxXpForLevels ? (xp - ChronicleRegistry.MaxXpForLevels) % ChronicleRegistry.OverflowXpPerChest : 0,
                Claimable = claimable,
                PetName = season.HasArt ? season.PetName : string.Empty,
                PetBonus = season.HasArt ? PetRegistry.Describe(new PetBonus(season.PetStat, ChronicleRegistry.PetPct)) : string.Empty,
            };
        }

        private static RewardView Describe(ChronicleReward r, ChronicleSeason s, long hourly) => r.Kind switch
        {
            ChronicleRewardKind.Gold => new("Gold", r.Amount, GoldRewardScale.ForMinutes(hourly, r.Amount), $"{r.Amount} min of gold", null, null),
            ChronicleRewardKind.Diamonds => new("Diamonds", r.Amount, 0, $"{r.Amount} diamonds", null, null),
            ChronicleRewardKind.Tickets => new("Tickets", r.Amount, 0, "A ticket at every dungeon", null, null),
            ChronicleRewardKind.LootChest => new("LootChest", r.Amount, 0, $"{(LootChestTier)r.Amount} loot chest", null, ((LootChestTier)r.Amount).ToString()),
            ChronicleRewardKind.WardrobeChest => new("WardrobeChest", r.Amount, 0, $"{CosmeticRegistry.RarityNames[r.Amount]} wardrobe chest", null, CosmeticRegistry.RarityNames[r.Amount]),
            ChronicleRewardKind.Avatar => new("Avatar", 1, 0, $"Avatar: {s.AvatarName}", s.AvatarArt, null),
            ChronicleRewardKind.Frame => new("Frame", 1, 0, $"Frame: {s.FrameName}", s.FrameId, null),
            ChronicleRewardKind.Pet => new("Pet", 1, 0, $"Pet: {s.PetName}", s.PetArt, null),
            _ => new(r.Kind.ToString(), r.Amount, 0, r.Kind.ToString(), null, null),
        };

        /// <summary>
        /// Claims one reached level, or - with <paramref name="onlyLevel"/> null -
        /// every reached, unclaimed level plus the ready bonus chests, in one
        /// transaction under the PlayerRecords row lock.
        /// </summary>
        /// <remarks>
        /// Gold and diamonds move in the DATABASE: the caller tells the live
        /// session (ReloadState, BillingSync), as the login week does, or the
        /// checkpoint writes the old balances back from the payload.
        /// </remarks>
        public static async Task<ClaimResult> ClaimAsync(FolkIdleDbContext db, long playerId, DateTime utcNow, int? onlyLevel = null)
        {
            var (number, start, end) = CurrentSeason(utcNow);
            var season = ChronicleRegistry.Season(number);

            // Read before the lock: slow-moving, and nothing here writes them.
            long hourly = await GoldRewardScale.HourlyIncomeAsync(db, playerId, utcNow);
            int region = await DailyLoginRewardEngine.ChestRegionAsync(db, playerId);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var player = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();
                if (player == null)
                {
                    await tx.SafeRollbackAsync();
                    return new ClaimResult(ClaimOutcome.PlayerNotFound, Array.Empty<int>());
                }

                // The XP is read UNDER the lock, so two devices cannot both
                // claim against the same total.
                int xp = await MissionEngine.XpBetweenAsync(db, playerId, start, end);
                int level = ChronicleRegistry.LevelFor(xp);

                var row = await db.PlayerChronicleSeasons.SingleOrDefaultAsync(c => c.PlayerId == playerId && c.Season == number);
                if (row == null)
                {
                    row = new PlayerChronicleSeason { PlayerId = playerId, Season = number };
                    db.PlayerChronicleSeasons.Add(row);
                }

                var levels = Enumerable.Range(1, level)
                    .Where(l => (row.ClaimedMask & (1L << l)) == 0)
                    .Where(l => onlyLevel == null || l == onlyLevel)
                    .ToList();
                int overflow = onlyLevel == null ? Math.Max(0, ChronicleRegistry.OverflowChestsFor(xp) - row.OverflowClaimed) : 0;

                if (levels.Count == 0 && overflow == 0)
                {
                    await tx.SafeRollbackAsync();
                    return new ClaimResult(ClaimOutcome.NothingToClaim, Array.Empty<int>(), DiamondBalance: player.PremiumDiamonds);
                }

                var rewards = levels.SelectMany(l => ChronicleRegistry.RewardsFor(l, season)).ToList();
                foreach (int l in levels) row.ClaimedMask |= 1L << l;
                row.OverflowClaimed += overflow;

                int diamonds = rewards.Where(r => r.Kind == ChronicleRewardKind.Diamonds).Sum(r => r.Amount);
                player.PremiumDiamonds += diamonds;

                int tickets = 0;
                int ticketSets = rewards.Where(r => r.Kind == ChronicleRewardKind.Tickets).Sum(r => r.Amount);
                if (ticketSets > 0)
                {
                    var gates = await DungeonEngine.LoadAsync(db, playerId, utcNow);
                    foreach (var gate in gates.Values)
                    {
                        for (int i = 0; i < ticketSets; i++)
                        {
                            if (DungeonEngine.AddBonusTicket(gate)) tickets++;
                        }
                    }
                }

                await db.SaveChangesAsync();

                long gold = rewards.Where(r => r.Kind == ChronicleRewardKind.Gold).Sum(r => GoldRewardScale.ForMinutes(hourly, r.Amount));
                if (gold > 0)
                {
                    await CommodityLedger.AddAsync(db, playerId, "gold", gold);
                    await GoldLedger.RecordIncomeAsync(db, playerId, GoldIncomeSource.Chronicle, gold);
                }

                var chests = new List<string>();
                foreach (var r in rewards.Where(r => r.Kind == ChronicleRewardKind.LootChest))
                {
                    await LootChestEngine.GrantAsync(db, playerId, (LootChestTier)r.Amount, region, LootChestSource.Chronicle, utcNow);
                    chests.Add($"{(LootChestTier)r.Amount} loot chest");
                }
                for (int i = 0; i < overflow; i++)
                {
                    await LootChestEngine.GrantAsync(db, playerId, ChronicleRegistry.OverflowChest, region, LootChestSource.Chronicle, utcNow);
                    chests.Add($"{ChronicleRegistry.OverflowChest} loot chest");
                }
                var wardrobe = rewards.Where(r => r.Kind == ChronicleRewardKind.WardrobeChest).Select(r => r.Amount).ToList();
                if (wardrobe.Count > 0)
                {
                    await CosmeticEngine.InsertChestsAsync(db, playerId, wardrobe, CosmeticSource.Chronicle, utcNow);
                    chests.AddRange(wardrobe.Select(w => $"{CosmeticRegistry.RarityNames[w]} wardrobe chest"));
                }

                var keepsakes = new List<string>();
                if (rewards.Any(r => r.Kind == ChronicleRewardKind.Avatar))
                {
                    await GrantBoundCosmeticAsync(db, playerId, CosmeticKind.Avatar, season.AvatarId, utcNow);
                    keepsakes.Add($"Avatar: {season.AvatarName}");
                }
                if (rewards.Any(r => r.Kind == ChronicleRewardKind.Frame))
                {
                    await GrantBoundCosmeticAsync(db, playerId, CosmeticKind.Frame, season.FrameId, utcNow);
                    keepsakes.Add($"Frame: {season.FrameName}");
                }
                if (rewards.Any(r => r.Kind == ChronicleRewardKind.Pet))
                {
                    // Announced: the "a pet found you" window opens for it.
                    await PetEngine.GrantAsync(db, playerId, season.PetId, utcNow, announce: true);
                    keepsakes.Add($"Pet: {season.PetName}");
                }

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                return new ClaimResult(ClaimOutcome.Ok, levels, gold, diamonds, player.PremiumDiamonds, tickets, chests, keepsakes, overflow);
            }
            catch
            {
                await tx.SafeRollbackAsync();
                throw;
            }
        }

        /// <summary>A bound avatar or frame, unless the account already has it.</summary>
        internal static Task<int> GrantBoundCosmeticAsync(FolkIdleDbContext db, long playerId, CosmeticKind kind, string definitionId, DateTime utcNow)
            => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO cosmetic_items (\"PlayerId\", \"Kind\", \"DefinitionId\", \"Rarity\", \"Source\", \"AcquiredAtUtc\", \"IsListed\") " +
                "SELECT {0}, {1}, {2}, {3}, {4}, {5}, false " +
                "WHERE NOT EXISTS (SELECT 1 FROM cosmetic_items WHERE \"PlayerId\" = {0} AND \"DefinitionId\" = {2})",
                playerId, (byte)kind, definitionId, (byte)CosmeticRegistry.Legendary,
                (byte)CosmeticSource.Chronicle, DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }
}
