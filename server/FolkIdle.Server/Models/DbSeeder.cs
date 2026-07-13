using System;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Models
{
    public static class DbSeeder
    {
        public const long PlayerLowId = 1001;
        public const long PlayerMidId = 1002;
        public const long PlayerHighId = 1003;

        public const long PlayerLowGold = 10000L;
        public const long PlayerMidGold = 1500000L;
        public const long PlayerHighGold = 10000000L;

        private const int HumanRaceId = 1;

        public static async Task SeedAllAsync(FolkIdleDbContext db)
        {
            if (!await db.WorldBossSnapshots.AnyAsync())
            {
                db.WorldBossSnapshots.Add(new WorldBossSnapshot
                {
                    BossInstanceId = WorldBossEngine.ActiveBossInstanceId,
                    MaxHp = 50000000L,
                    CurrentHp = 50000000L,
                    TotalDamageContributed = 0,
                    LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }

            if (!await db.PlayerRecords.AnyAsync())
            {
                AddSeedPlayer(db, PlayerLowId, PlayerLowGold, 5);
                AddSeedPlayer(db, PlayerMidId, PlayerMidGold, 15);
                AddSeedPlayer(db, PlayerHighId, PlayerHighGold, 30);
            }

            await db.SaveChangesAsync();
        }

        private static void AddSeedPlayer(FolkIdleDbContext db, long playerId, long gold, int masteryLevel)
        {
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                CurrentLevel = 1,
                CurrentXp = 0,
                SelectedLineageId = 1,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid()
            });

            db.CommodityRecords.Add(new CommodityRecord
            {
                PlayerId = playerId,
                ItemId = "gold",
                Quantity = gold
            });

            db.PlayerRaceMasteries.Add(new PlayerRaceMastery
            {
                PlayerId = playerId,
                RaceId = HumanRaceId,
                MasteryLevel = masteryLevel,
                CumulativeXp = 0
            });
        }
    }
}
