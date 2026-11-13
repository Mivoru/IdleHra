using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>The Bloodline week board (2026-11-13): each account's best child bred since Monday.</summary>
    [Collection("Postgres collection")]
    public class BloodlineWeekBoardTests
    {
        private readonly PostgresTestFixture _fixture;

        public BloodlineWeekBoardTests(PostgresTestFixture fixture) => _fixture = fixture;

        // A week far from the clock every other test writes with, so their
        // children cannot land on this board: Wednesday 2031-06-11, noon UTC.
        private static readonly DateTimeOffset Now = new(2031, 6, 11, 12, 0, 0, TimeSpan.Zero);
        private static readonly long Monday = new DateTimeOffset(2031, 6, 9, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        private static (CharacterRecord, CharacterLineageRegistry) Child(long playerId, long born, int s, int k, int e, int f, bool founder = false)
        {
            var id = Guid.NewGuid();
            return (new CharacterRecord { Id = id, PlayerId = playerId, BornAtEpoch = born, Name = $"c{s}{k}{e}{f}" },
                new CharacterLineageRegistry
                {
                    CharacterId = id,
                    ParentPaternalId = founder ? null : Guid.NewGuid(),
                    AptitudeStrength = s, AptitudeSkill = k, AptitudeEndurance = e, AptitudeFortune = f,
                });
        }

        [Fact]
        public async Task EachAccountsBestChildThisWeek_RanksByTotalThenByWeakest()
        {
            const long a = 960_116_001L, b = 960_116_002L, c = 960_116_003L, d = 960_116_004L;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                foreach (var (id, name) in new[] { (a, "blood_a"), (b, "blood_b"), (c, "blood_c"), (d, "blood_d") })
                    db.PlayerRecords.Add(new PlayerRecord { Id = id, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), Username = name });

                var kids = new[]
                {
                    // a: two children this week; the better (total 80) counts.
                    Child(a, Monday + 100, 20, 20, 20, 20),
                    Child(a, Monday + 200, 10, 10, 10, 10),
                    // b: the same total as a, weaker lowest - ranks below a.
                    Child(b, Monday + 50, 35, 15, 15, 15),
                    // b: a better child born LAST week does not count.
                    Child(b, Monday - 10, 50, 50, 50, 50),
                    // c: only a founder born this week - not a bred child.
                    Child(c, Monday + 10, 49, 49, 49, 49, founder: true),
                    // d: the best total this week.
                    Child(d, Monday + 300, 30, 25, 25, 25),
                };
                foreach (var (ch, lin) in kids)
                {
                    db.CharacterRecords.Add(ch);
                    db.CharacterLineages.Add(lin);
                }
                await db.SaveChangesAsync();
            }

            await using var read = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await BloodlineWeekBoard.TopAsync(read, Now);
            var mine = rows.Where(r => r.PlayerId is a or b or c or d).ToList();

            Assert.Equal(new[] { d, a, b }, mine.Select(r => r.PlayerId));
            Assert.Equal(105, mine[0].AptitudeTotal);
            Assert.Equal(80, mine[1].AptitudeTotal);
            Assert.Equal(20, mine[1].LowestAptitude);
            Assert.Equal(2, mine[1].ChildrenThisWeek);
            Assert.Equal(80, mine[2].AptitudeTotal);
            Assert.Equal(1, mine[2].ChildrenThisWeek);
            Assert.True(mine[0].Rank < mine[1].Rank && mine[1].Rank < mine[2].Rank);
        }
    }
}
