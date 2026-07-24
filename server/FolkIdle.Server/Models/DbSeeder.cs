using System;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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
                // PlayerLowId is the only seed account with real Email/Username/
                // PasswordHash set, so it's reachable through the actual
                // LoginWithEmailAsync flow (POST /api/v1/auth/login with
                // {email,password}) instead of only being visible to direct DB
                // queries in xUnit tests - lets an MCP-driven Play Mode session
                // log in against a freshly-seeded dev/compose database without
                // first scripting a full registration flow.
                AddSeedPlayer(db, PlayerLowId, PlayerLowGold, 5, "dev@folkidle.local", "dev", "FolkIdleDev123!");
                AddSeedPlayer(db, PlayerMidId, PlayerMidGold, 15);
                AddSeedPlayer(db, PlayerHighId, PlayerHighGold, 30);
            }

            await db.SaveChangesAsync();
        }

        private static void AddSeedPlayer(FolkIdleDbContext db, long playerId, long gold, int masteryLevel,
            string? email = null, string? username = null, string? password = null)
        {
            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                CurrentLevel = 1,
                CurrentXp = 0,
                SelectedLineageId = 1,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Email = email,
                Username = username,
                PasswordHash = password != null ? PasswordHasher.Hash(password) : null
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
