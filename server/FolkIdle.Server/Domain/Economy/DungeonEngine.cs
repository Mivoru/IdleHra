using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// The three dungeons' rules outside the fight: tickets, which stages are
    /// open, and what a run pays (owner D1-D6, CHESTS_DUNGEONS_DIAMONDS.md 2.6a
    /// and 2.8). The fight itself is DungeonSimulator with the dungeon's
    /// DungeonBoss.
    /// </summary>
    /// <remarks>
    /// Modul: TICKETS TOP UP ON READ, PER DUNGEON. Two a day for each dungeon,
    /// never more than four held, so a missed day is not punished and a missed
    /// week is not rewarded. Topped up whenever the row is read with a newer
    /// UTC day - no job, no clock.
    ///
    /// Modul: A RUN IS ONE TRANSACTION. The ticket is taken with a row lock,
    /// the reward is written in the same transaction, and a run the server
    /// could not finish takes no ticket. The fight is decided by the live
    /// payload snapshot the caller passes, so the client never says who won.
    ///
    /// Modul: XP AND GOLD ARE MINUTES OF THE CHAMPION'S OWN INCOME. A flat
    /// number is worth nothing at the top and everything at the bottom (the
    /// world boss lesson, GoldRewardScale). A won stage pays 20 + 8 x stage
    /// minutes (28 at stage 1, 140 at stage 15) of the champion's best hunting
    /// XP an hour, or of the account's earned gold an hour; a lost run pays a
    /// quarter. The XP goes to the live payload through DungeonXpGrantQueue
    /// (the tick owns XP and the level-up loop); the gold is a ledger write
    /// here and a ReloadState in the caller, the Delve's path.
    /// </remarks>
    public static class DungeonEngine
    {
        public const int TicketsPerDay = 2;
        public const int TicketCap = 4;
        /// <summary>The lobby opens with the region 2 boss (about level 25).</summary>
        public const int OpensAtRegionBoss = 2;
        /// <summary>A lost equipment run pays a Bronze chest one time in four - about a quarter of a win's chest.</summary>
        public const int ConsolationChestPermille = 250;
        /// <summary>A lost XP or gold run pays this share of the win.</summary>
        public const int ConsolationPct = 25;

        public static int UtcDay(DateTime utcNow) => (int)(utcNow.Date - DateTime.UnixEpoch.Date).TotalDays;

        /// <summary>Tops the row's tickets up to today. True when it changed anything.</summary>
        public static bool TopUpTickets(DungeonProgress row, DateTime utcNow)
        {
            int today = UtcDay(utcNow);
            if (row.TicketDay >= today) return false;
            long days = today - (long)row.TicketDay;
            row.Tickets = (int)Math.Min(TicketCap, row.Tickets + days * TicketsPerDay);
            row.TicketDay = today;
            return true;
        }

        /// <summary>Every dungeon's row for the player, creating the missing ones (tracked, not saved).</summary>
        public static async Task<Dictionary<DungeonKind, DungeonProgress>> LoadAsync(FolkIdleDbContext db, long playerId, DateTime utcNow)
        {
            var rows = await db.DungeonProgress.Where(d => d.PlayerId == playerId).ToListAsync();
            var byKind = new Dictionary<DungeonKind, DungeonProgress>();
            foreach (var kind in DungeonBoss.All)
            {
                var row = rows.FirstOrDefault(r => r.Kind == (int)kind);
                if (row == null)
                {
                    row = new DungeonProgress { PlayerId = playerId, Kind = (int)kind };
                    db.DungeonProgress.Add(row);
                }
                TopUpTickets(row, utcNow);
                byKind[kind] = row;
            }
            return byKind;
        }

        public static bool IsOpen(byte defeatedRegionBossMask)
            => BossFirstClearRules.IsDefeated(defeatedRegionBossMask, RaceUnlockRegistry.GetRegionBossMonsterId(OpensAtRegionBoss));

        /// <summary>Why a stage cannot be entered, or null when it can.</summary>
        public static string? StageLockedReason(int stage, byte defeatedRegionBossMask, int bestStage)
        {
            if (stage < 1 || stage > DungeonSimulator.StageCount) return "No such stage.";
            if (!IsOpen(defeatedRegionBossMask))
                return $"The dungeons open when you beat {MonsterName(RaceUnlockRegistry.GetRegionBossMonsterId(OpensAtRegionBoss))}.";
            int bossId = DungeonSimulator.BossMonsterIdFor(stage);
            if (!BossFirstClearRules.IsDefeated(defeatedRegionBossMask, bossId)) return $"Beat {MonsterName(bossId)} first.";
            if (stage > bestStage + 1) return $"Clear stage {stage - 1} first.";
            return null;
        }

        private static string MonsterName(int monsterId)
            => monsterId > 0 && monsterId <= ContentRegistry.Monsters.Length ? ContentRegistry.GetMonsterName(monsterId) : $"monster {monsterId}";

        public static string BandOf(int stage) => stage <= 5 ? "Regions" : stage <= 10 ? "Ascension" : "Beyond";

        /// <summary>The rarity floor of a won stage's piece: Rare, then Epic, then Legendary.</summary>
        public static int RarityFloorFor(int stage) => stage <= 5 ? RarityTier.Rare : stage <= 10 ? RarityTier.Epic : RarityTier.Legendary;

        public static LootChestTier ChestFor(int stage) => stage switch
        {
            <= 5 => LootChestTier.Bronze,
            <= 8 => LootChestTier.Silver,
            <= 13 => LootChestTier.Gold,
            _ => LootChestTier.Ancient,
        };

        public static int FirstClearDiamonds(int stage) => stage <= 5 ? 5 : stage <= 10 ? 10 : 20;

        /// <summary>Minutes of the champion's own income a won XP or gold stage pays.</summary>
        public static int IncomeMinutes(int stage) => 20 + 8 * Math.Clamp(stage, 1, DungeonSimulator.StageCount);

        public static long XpFor(int stage, long hourlyXp, bool won)
        {
            long xp = (long)Math.Min(long.MaxValue / 4, (double)Math.Max(0, hourlyXp) * IncomeMinutes(stage) / 60.0);
            return won ? xp : xp * ConsolationPct / 100;
        }

        public static long GoldFor(int stage, long hourlyGold, bool won)
        {
            long gold = GoldRewardScale.ForMinutes(hourlyGold, IncomeMinutes(stage));
            return won ? gold : gold * ConsolationPct / 100;
        }

        public static string RewardLine(DungeonKind kind, int stage) => kind switch
        {
            DungeonKind.Xp => $"{IncomeMinutes(stage)} minutes of your best hunting XP. First clear: {FirstClearDiamonds(stage)} diamonds.",
            DungeonKind.Gold => $"{IncomeMinutes(stage)} minutes of your gold income. First clear: {FirstClearDiamonds(stage)} diamonds.",
            _ => $"A region {DungeonSimulator.RegionOfStage(stage)} piece, {RarityTier.GetName(RarityFloorFor(stage))} or better, and a {ChestFor(stage)} chest. First clear: {FirstClearDiamonds(stage)} diamonds.",
        };

        public sealed record RunReward(long PieceId, string PieceBaseId, string PieceRarity, long ChestId, string Chest, int Diamonds, long Xp = 0, long Gold = 0);

        public enum RunOutcome { Ok, NoTickets, Locked, PlayerNotFound }

        /// <summary>What the caller knows about the champion's income, for the XP and gold dungeons.</summary>
        public readonly record struct Income(long HourlyXp, long HourlyGold);

        /// <summary>
        /// Takes a ticket, runs the stage for <paramref name="snapshot"/> and
        /// writes the reward, all in one transaction. An XP reward is returned,
        /// not applied: the caller hands it to the tick.
        /// </summary>
        public static async Task<(RunOutcome Outcome, string? Reason, DungeonRunResult? Result, RunReward? Reward, int TicketsLeft)> RunAsync(
            FolkIdleDbContext db, long playerId, DungeonKind kind, int stage, TickStatePayload snapshot, int foodItemId, int foodCount,
            Income income, DateTime utcNow, Random rng, bool freeRun = false)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var player = await db.PlayerRecords
                .FromSqlInterpolated($"SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {playerId} FOR UPDATE")
                .SingleOrDefaultAsync();
            if (player == null) return (RunOutcome.PlayerNotFound, null, null, null, 0);

            var row = (await LoadAsync(db, playerId, utcNow))[kind];
            string? locked = StageLockedReason(stage, snapshot.DefeatedRegionBossMask, row.BestStage);
            if (locked != null)
            {
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (RunOutcome.Locked, locked, null, null, row.Tickets);
            }
            if (row.Tickets <= 0 && !freeRun)
            {
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (RunOutcome.NoTickets, "No tickets left today - two more at midnight UTC.", null, null, 0);
            }

            var result = DungeonSimulator.Run(snapshot, kind, stage, foodItemId, foodCount);
            if (!freeRun) row.Tickets--;

            int diamonds = 0;
            if (result.Won)
            {
                int bit = 1 << stage;
                if ((row.FirstClearMask & bit) == 0)
                {
                    row.FirstClearMask |= bit;
                    diamonds = FirstClearDiamonds(stage);
                    player.PremiumDiamonds += diamonds;
                }
                row.BestStage = Math.Max(row.BestStage, stage);
            }

            RunReward? reward = kind switch
            {
                DungeonKind.Xp => new RunReward(0, string.Empty, string.Empty, 0, string.Empty, diamonds,
                    Xp: XpFor(stage, income.HourlyXp, result.Won)),
                DungeonKind.Gold => await PayGoldAsync(db, playerId, GoldFor(stage, income.HourlyGold, result.Won), diamonds),
                _ => await PayEquipmentAsync(db, playerId, stage, result.Won, diamonds, utcNow, rng),
            };

            if (freeRun) RecordTestGrant(db, player.PlayerGuid, utcNow, reward);

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return (RunOutcome.Ok, null, result, reward, row.Tickets);
        }

        // Modul: ADMIN TEST RUNS (owner, 2026-10-31: "give me unlimited
        // attempts so I can test, then reset it, rewards included"). A free run
        // writes what it paid into AccountAnalyticsLogs under these hashes, so
        // the reset can take back exactly that XP, gold, piece, chest and
        // diamonds. Diamonds are also recoverable from FirstClearMask; the XP
        // and the piece are not recorded anywhere else.
        public const uint TestGrantXp = 0xD06E0001, TestGrantGold = 0xD06E0002, TestGrantPiece = 0xD06E0003,
            TestGrantChest = 0xD06E0004, TestGrantDiamonds = 0xD06E0005;

        private static void RecordTestGrant(FolkIdleDbContext db, Guid account, DateTime utcNow, RunReward? reward)
        {
            if (reward == null) return;
            long at = new DateTimeOffset(utcNow).ToUnixTimeSeconds();
            void Add(uint hash, long value)
            {
                if (value > 0) db.AccountAnalyticsLogs.Add(new AccountAnalyticsLog { AccountId = account, EventTypeHash = hash, TimestampEpoch = at, PayloadMetric = value });
            }
            Add(TestGrantXp, reward.Xp);
            Add(TestGrantGold, reward.Gold);
            Add(TestGrantPiece, reward.PieceId);
            Add(TestGrantChest, reward.ChestId);
            Add(TestGrantDiamonds, reward.Diamonds);
        }

        private static async Task<RunReward> PayGoldAsync(FolkIdleDbContext db, long playerId, long gold, int diamonds)
        {
            await db.SaveChangesAsync();
            if (gold > 0)
            {
                await CommodityLedger.AddAsync(db, playerId, "gold", gold);
                await GoldLedger.RecordIncomeAsync(db, playerId, GoldIncomeSource.Dungeon, gold);
            }
            return new RunReward(0, string.Empty, string.Empty, 0, string.Empty, diamonds, Gold: gold);
        }

        private static async Task<RunReward?> PayEquipmentAsync(FolkIdleDbContext db, long playerId, int stage, bool won, int diamonds, DateTime utcNow, Random rng)
        {
            int region = DungeonSimulator.RegionOfStage(stage);
            if (won)
            {
                var piece = LootChestEngine.AddPiece(db, playerId, region, RarityFloorFor(stage), rng);
                await db.SaveChangesAsync();
                long chestId = await LootChestEngine.GrantAsync(db, playerId, ChestFor(stage), region, LootChestSource.Dungeon, utcNow);
                return new RunReward(piece?.Id ?? 0, piece?.BaseItemId ?? string.Empty,
                    piece == null ? string.Empty : RarityTier.GetName(piece.QualityTier), chestId, ChestFor(stage).ToString(), diamonds);
            }
            if (rng.Next(1000) < ConsolationChestPermille)
            {
                await db.SaveChangesAsync();
                long chestId = await LootChestEngine.GrantAsync(db, playerId, LootChestTier.Bronze, region, LootChestSource.Dungeon, utcNow);
                return new RunReward(0, string.Empty, string.Empty, chestId, LootChestTier.Bronze.ToString(), 0);
            }
            return null;
        }

        /// <summary>The larder's best food and its count, from a payload snapshot.</summary>
        public static (int SlotIndex, int ItemId, int Count) BestFood(in TickStatePayload payload)
        {
            (int slot, int id, int count)[] slots =
            {
                (0, payload.Food1_ItemId, payload.Food1_Count),
                (1, payload.Food2_ItemId, payload.Food2_Count),
                (2, payload.Food3_ItemId, payload.Food3_Count),
            };
            var best = slots.Where(s => s.id > 0 && s.count > 0)
                .OrderByDescending(s => FoodRegistry.GetHealMilliHp(s.id))
                .FirstOrDefault();
            return best.id > 0 ? (best.slot, best.id, best.count) : (-1, 0, 0);
        }

        /// <summary>
        /// The champion's best hunting XP an hour: the highest projected rate
        /// over every monster in an open region that they can damage and
        /// survive with food - what the Combat screen's best pick would earn.
        /// </summary>
        public static long BestHuntingXpPerHour(in TickStatePayload payload, int globalXpMultiplier, int activeGlobalEventId)
        {
            long best = 0;
            int highestRegion = RegionUnlockGate.HighestUnlockedRegion(payload.DefeatedRegionBossMask);
            for (int id = ContentRegistry.FirstCanonicalMonsterId; id <= ContentRegistry.Monsters.Length; id++)
            {
                if (ContentRegistry.GetMonsterRegionTier(id) > highestRegion) continue;
                var e = HuntingProjection.Project(in payload, id, globalXpMultiplier, activeGlobalEventId);
                if (e.CanDamage && e.SurvivesWithFood && e.XpPerHour > best) best = e.XpPerHour;
            }
            return best;
        }
    }
}
