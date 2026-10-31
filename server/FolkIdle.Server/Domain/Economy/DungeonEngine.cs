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
    /// The equipment dungeon's rules outside the fight: tickets, which stages
    /// are open, and what a run pays (owner D1-D6, CHESTS_DUNGEONS_DIAMONDS.md
    /// 2.6a). The fight itself is DungeonSimulator.
    /// </summary>
    /// <remarks>
    /// Modul: TICKETS TOP UP ON READ. Two a day, never more than four held, so
    /// a missed day is not punished and a missed week is not rewarded. Topped
    /// up whenever the row is read with a newer UTC day - no job, no clock.
    ///
    /// Modul: A RUN IS ONE TRANSACTION. The ticket is taken with a row lock,
    /// the reward is written in the same transaction, and a run the server
    /// could not finish takes no ticket. The fight is decided by the live
    /// payload snapshot the caller passes, so the client never says who won.
    /// </remarks>
    public static class DungeonEngine
    {
        public const int TicketsPerDay = 2;
        public const int TicketCap = 4;
        /// <summary>The lobby opens with the region 2 boss (about level 25).</summary>
        public const int OpensAtRegionBoss = 2;
        /// <summary>A lost run pays a Bronze chest one time in four - about a quarter of a win's chest.</summary>
        public const int ConsolationChestPermille = 250;

        public static int UtcDay(DateTime utcNow) => (int)(utcNow.Date - DateTime.UnixEpoch.Date).TotalDays;

        /// <summary>Tops the row's tickets up to today. True when it changed anything.</summary>
        public static bool TopUpTickets(PlayerRecord player, DateTime utcNow)
        {
            int today = UtcDay(utcNow);
            if (player.DungeonTicketDay >= today) return false;
            long days = today - (long)player.DungeonTicketDay;
            player.DungeonTickets = (int)Math.Min(TicketCap, player.DungeonTickets + days * TicketsPerDay);
            player.DungeonTicketDay = today;
            return true;
        }

        public static bool IsOpen(byte defeatedRegionBossMask)
            => BossFirstClearRules.IsDefeated(defeatedRegionBossMask, RaceUnlockRegistry.GetRegionBossMonsterId(OpensAtRegionBoss));

        /// <summary>Why a stage cannot be entered, or null when it can.</summary>
        public static string? StageLockedReason(int stage, byte defeatedRegionBossMask, int bestStage)
        {
            if (stage < 1 || stage > DungeonSimulator.StageCount) return "No such stage.";
            if (!IsOpen(defeatedRegionBossMask))
                return $"The dungeon opens when you beat {MonsterName(RaceUnlockRegistry.GetRegionBossMonsterId(OpensAtRegionBoss))}.";
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

        public static string RewardLine(int stage)
            => $"A region {DungeonSimulator.RegionOfStage(stage)} piece, {RarityTier.GetName(RarityFloorFor(stage))} or better, and a {ChestFor(stage)} chest. First clear: {FirstClearDiamonds(stage)} diamonds.";

        public sealed record RunReward(long PieceId, string PieceBaseId, string PieceRarity, long ChestId, string Chest, int Diamonds);

        public enum RunOutcome { Ok, NoTickets, Locked, PlayerNotFound }

        /// <summary>
        /// Takes a ticket, runs the stage for <paramref name="snapshot"/> and
        /// writes the reward, all in one transaction.
        /// </summary>
        public static async Task<(RunOutcome Outcome, string? Reason, DungeonRunResult? Result, RunReward? Reward, int TicketsLeft)> RunAsync(
            FolkIdleDbContext db, long playerId, int stage, TickStatePayload snapshot, int foodItemId, int foodCount, DateTime utcNow, Random rng)
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var player = await db.PlayerRecords
                .FromSqlInterpolated($"SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {playerId} FOR UPDATE")
                .SingleOrDefaultAsync();
            if (player == null) return (RunOutcome.PlayerNotFound, null, null, null, 0);

            TopUpTickets(player, utcNow);
            string? locked = StageLockedReason(stage, snapshot.DefeatedRegionBossMask, player.DungeonBestStage);
            if (locked != null)
            {
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (RunOutcome.Locked, locked, null, null, player.DungeonTickets);
            }
            if (player.DungeonTickets <= 0)
            {
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (RunOutcome.NoTickets, "No tickets left today - two more at midnight UTC.", null, null, 0);
            }

            var result = DungeonSimulator.Run(snapshot, stage, foodItemId, foodCount);
            player.DungeonTickets--;

            RunReward? reward = null;
            if (result.Won)
            {
                var piece = LootChestEngine.AddPiece(db, playerId, DungeonSimulator.RegionOfStage(stage), RarityFloorFor(stage), rng);
                int diamonds = 0;
                int bit = 1 << stage;
                if ((player.DungeonFirstClearMask & bit) == 0)
                {
                    player.DungeonFirstClearMask |= bit;
                    diamonds = FirstClearDiamonds(stage);
                    player.PremiumDiamonds += diamonds;
                }
                player.DungeonBestStage = Math.Max(player.DungeonBestStage, stage);
                await db.SaveChangesAsync();
                long chestId = await LootChestEngine.GrantAsync(db, playerId, ChestFor(stage), DungeonSimulator.RegionOfStage(stage), LootChestSource.Dungeon, utcNow);
                reward = new RunReward(piece?.Id ?? 0, piece?.BaseItemId ?? string.Empty,
                    piece == null ? string.Empty : RarityTier.GetName(piece.QualityTier), chestId, ChestFor(stage).ToString(), diamonds);
            }
            else if (rng.Next(1000) < ConsolationChestPermille)
            {
                await db.SaveChangesAsync();
                long chestId = await LootChestEngine.GrantAsync(db, playerId, LootChestTier.Bronze, DungeonSimulator.RegionOfStage(stage), LootChestSource.Dungeon, utcNow);
                reward = new RunReward(0, string.Empty, string.Empty, chestId, LootChestTier.Bronze.ToString(), 0);
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return (RunOutcome.Ok, null, result, reward, player.DungeonTickets);
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
    }
}
