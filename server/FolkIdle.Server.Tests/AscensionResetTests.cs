using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The Ascension reset (A1 + A2): the SQL the migration runs, run against
    /// seeded progress. Each cleared step becomes one chest of its region at
    /// its band's odds, each affected player gets one message, the counters go
    /// to zero, and a player with no progress gets nothing.
    /// </summary>
    [Collection("Postgres collection")]
    public class AscensionResetTests
    {
        private readonly PostgresTestFixture _fixture;
        private static long _nextPlayerId = 971_700_000L;

        public AscensionResetTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
            ContentRegistry.Initialize();
        }

        [Fact]
        public async Task EachClearedStepBecomesAChest_OneMessage_AndTheLaddersStartAgain()
        {
            long veteran = Interlocked.Increment(ref _nextPlayerId);
            long fresh = Interlocked.Increment(ref _nextPlayerId);
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                foreach (long id in new[] { veteran, fresh })
                    db.PlayerRecords.Add(new PlayerRecord { Id = id, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid() });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO boss_ascension_progress (\"PlayerId\", \"Region\", \"HighestStep\", \"UpdatedAtUtc\") VALUES ({0}, 1, 10, now()), ({0}, 2, 3, now()), ({1}, 1, 0, now())",
                    veteran, fresh);

                // Scoped to these two players' rows: other tests share the database.
                await db.Database.ExecuteSqlRawAsync(AscensionResetSql.PayChests.Replace("WHERE p.\"HighestStep\" > 0", $"WHERE p.\"HighestStep\" > 0 AND p.\"PlayerId\" IN ({veteran}, {fresh})"));
                await db.Database.ExecuteSqlRawAsync(AscensionResetSql.Notify.Replace("WHERE \"HighestStep\" > 0", $"WHERE \"HighestStep\" > 0 AND \"PlayerId\" IN ({veteran}, {fresh})"));
                await db.Database.ExecuteSqlRawAsync(AscensionResetSql.ZeroSteps.Replace("WHERE \"HighestStep\" > 0", $"WHERE \"HighestStep\" > 0 AND \"PlayerId\" IN ({veteran}, {fresh})"));
            }

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var chests = await verify.LootChests.AsNoTracking().Where(c => c.PlayerId == veteran).ToListAsync();
            Assert.Equal(13, chests.Count);
            Assert.Equal(10, chests.Count(c => c.Region == 1));
            Assert.Equal(3, chests.Count(c => c.Region == 2));
            Assert.All(chests, c => Assert.Equal(3, (int)c.Source));
            Assert.Contains(chests, c => c.Region == 1 && c.Tier >= 3);
            Assert.Empty(await verify.LootChests.AsNoTracking().Where(c => c.PlayerId == fresh).ToListAsync());

            var mail = await verify.MailboxInstances.AsNoTracking().Where(m => m.SenderName == "The Ascension").ToListAsync();
            Assert.Single(mail, m => m.PlayerId == veteran);
            Assert.DoesNotContain(mail, m => m.PlayerId == fresh);
            Assert.Contains("13 in all", mail.Single(m => m.PlayerId == veteran).MessageText);

            var steps = await verify.Database.SqlQueryRaw<int>(
                "SELECT \"HighestStep\"::int AS \"Value\" FROM boss_ascension_progress WHERE \"PlayerId\" = {0}", veteran).ToListAsync();
            Assert.All(steps, s => Assert.Equal(0, s));
        }
    
        [Fact]
        public void TheNewLadderAddsEnrageFromStep4_WardFromStep7_AndMasteryAtStep10()
        {
            for (int region = 1; region <= 5; region++)
            {
                Assert.False(BossAscensionRegistry.ModifiersFor(region, 3).Enrage);
                Assert.True(BossAscensionRegistry.ModifiersFor(region, 4).Enrage);
                Assert.False(BossAscensionRegistry.ModifiersFor(region, 6).Ward);
                Assert.True(BossAscensionRegistry.ModifiersFor(region, 7).Ward);
                Assert.Contains(BossAscensionRegistry.DescribeEffects(region, 10), l => l.StartsWith("Clearing it: +2% damage"));
            }

            int packed = BossAscensionRegistry.WithHighestStep(0, 2, 10);
            Assert.Equal(BossAscensionRegistry.MasteryTenthsPct, BossAscensionRegistry.MasteryTenthsPctFor(packed, 2));
            Assert.Equal(0, BossAscensionRegistry.MasteryTenthsPctFor(packed, 3));
            Assert.Equal(0, BossAscensionRegistry.MasteryTenthsPctFor(BossAscensionRegistry.WithHighestStep(0, 2, 9), 2));
        }
}
}
