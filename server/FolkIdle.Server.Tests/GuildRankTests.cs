using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The four guild ranks (owner, 2026-11-03): Chieftain, Elder (at most 2),
    /// Warden (at most 5), Member. These pin the permission table the owner
    /// approved - who may move whom, the caps, kicking only downwards, the
    /// hand-over, and the 14-day rule for a Chieftain who stopped playing.
    /// </summary>
    [Collection("Postgres collection")]
    public class GuildRankTests
    {
        private readonly PostgresTestFixture _fixture;

        public GuildRankTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private GuildManagementEngine Engine() => new(_fixture.RetryingOptions, _fixture.PlayerRegistry);

        private async Task SeedPlayersAsync(long first, int count)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (long id = first; id < first + count; id++)
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = id, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 30,
                    Username = $"rank{id}", LastLoginTimestamp = now,
                });
            }
            await db.SaveChangesAsync();
        }

        private async Task<long> GuildAsync(string name, long first, int count)
        {
            await SeedPlayersAsync(first, count);
            var engine = Engine();
            long guildId = (await engine.CreateGuildAsync(first, name)).GuildId;
            Assert.True(guildId > 0, $"could not found {name}");
            for (long id = first + 1; id < first + count; id++)
            {
                Assert.True(await engine.JoinGuildAsync(id, guildId), $"{id} could not join {name}");
            }
            return guildId;
        }

        private async Task<int> RoleOfAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return (await db.GuildMembers.AsNoTracking().SingleAsync(m => m.PlayerId == playerId)).Role;
        }

        [Fact]
        public async Task TheFounderIsChieftain_AndPromotionsClimbOneRankAtATimeUpToElder()
        {
            const long chief = 970011001L;
            await GuildAsync("RankClimb", chief, 3);
            long member = chief + 1;

            Assert.Equal(GuildManagementEngine.RoleChieftain, await RoleOfAsync(chief));
            Assert.True(await Engine().PromoteMemberAsync(chief, member));
            Assert.Equal(GuildManagementEngine.RoleWarden, await RoleOfAsync(member));
            Assert.True(await Engine().PromoteMemberAsync(chief, member));
            Assert.Equal(GuildManagementEngine.RoleElder, await RoleOfAsync(member));
            // Nobody is promoted to Chieftain - that is the hand-over.
            Assert.False(await Engine().PromoteMemberAsync(chief, member));
            Assert.Equal(GuildManagementEngine.RoleElder, await RoleOfAsync(member));

            Assert.True(await Engine().DemoteMemberAsync(chief, member));
            Assert.Equal(GuildManagementEngine.RoleWarden, await RoleOfAsync(member));
        }

        [Fact]
        public async Task AnElderMovesPeopleOnlyBetweenMemberAndWarden()
        {
            const long chief = 970011101L;
            await GuildAsync("RankElder", chief, 4);
            long elder = chief + 1, member = chief + 2, other = chief + 3;
            Assert.True(await Engine().PromoteMemberAsync(chief, elder));
            Assert.True(await Engine().PromoteMemberAsync(chief, elder));

            Assert.True(await Engine().PromoteMemberAsync(elder, member));
            Assert.Equal(GuildManagementEngine.RoleWarden, await RoleOfAsync(member));
            Assert.False(await Engine().PromoteMemberAsync(elder, member)); // not to Elder
            Assert.Equal(GuildManagementEngine.RoleWarden, await RoleOfAsync(member));
            Assert.True(await Engine().DemoteMemberAsync(elder, member));
            Assert.Equal(GuildManagementEngine.RoleMember, await RoleOfAsync(member));

            // A Warden promotes nobody; nobody moves the Chieftain.
            Assert.True(await Engine().PromoteMemberAsync(elder, other));
            Assert.False(await Engine().PromoteMemberAsync(other, member));
            Assert.False(await Engine().DemoteMemberAsync(elder, chief));
        }

        [Fact]
        public async Task TheCapsHold_TwoEldersAndFiveWardens()
        {
            const long chief = 970011201L;
            await GuildAsync("RankCaps", chief, 9);
            for (long id = chief + 1; id <= chief + 3; id++)
            {
                Assert.True(await Engine().PromoteMemberAsync(chief, id));
            }
            Assert.True(await Engine().PromoteMemberAsync(chief, chief + 1));
            Assert.True(await Engine().PromoteMemberAsync(chief, chief + 2));
            Assert.False(await Engine().PromoteMemberAsync(chief, chief + 3)); // a third Elder
            Assert.Equal(GuildManagementEngine.RoleWarden, await RoleOfAsync(chief + 3));

            // Wardens: chief+3 is one; four more fill the five places.
            for (long id = chief + 4; id <= chief + 7; id++)
            {
                Assert.True(await Engine().PromoteMemberAsync(chief, id));
            }
            Assert.False(await Engine().PromoteMemberAsync(chief, chief + 8)); // a sixth Warden
            Assert.Equal(GuildManagementEngine.RoleMember, await RoleOfAsync(chief + 8));
        }

        [Fact]
        public async Task KicksGoOnlyDownwards_AndAWardenMayKickAMember()
        {
            const long chief = 970011301L;
            long guildId = await GuildAsync("RankKick", chief, 4);
            long elder = chief + 1, warden = chief + 2, member = chief + 3;
            Assert.True(await Engine().PromoteMemberAsync(chief, elder));
            Assert.True(await Engine().PromoteMemberAsync(chief, elder));
            Assert.True(await Engine().PromoteMemberAsync(chief, warden));

            Assert.False(await Engine().KickMemberAsync(warden, elder));
            Assert.False(await Engine().KickMemberAsync(elder, chief));
            Assert.True(await Engine().KickMemberAsync(warden, member));

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await db.GuildMembers.AnyAsync(m => m.PlayerId == member));
            Assert.Equal(3, await db.GuildMembers.CountAsync(m => m.GuildId == guildId));
        }

        [Fact]
        public async Task TheChieftainHandsTheGuildOn_AndStaysAsElder()
        {
            const long chief = 970011401L;
            await GuildAsync("RankTransfer", chief, 3);
            long heir = chief + 1;

            Assert.False(await Engine().TransferLeadershipAsync(heir, chief)); // only the Chieftain
            Assert.True(await Engine().TransferLeadershipAsync(chief, heir));
            Assert.Equal(GuildManagementEngine.RoleChieftain, await RoleOfAsync(heir));
            Assert.Equal(GuildManagementEngine.RoleElder, await RoleOfAsync(chief));
        }

        [Fact]
        public async Task AGuildWithNoChieftainGetsOne_TheHighestRankFirst()
        {
            // Production's one guild, 2026-11-03: Members and one Officer, no
            // Leader row at all.
            const long chief = 970011601L;
            long guildId = await GuildAsync("RankHeadless", chief, 3);
            long warden = chief + 1;
            Assert.True(await Engine().PromoteMemberAsync(chief, warden));
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                (await db.GuildMembers.SingleAsync(m => m.PlayerId == chief)).Role = GuildManagementEngine.RoleMember;
                await db.SaveChangesAsync();
                Assert.True(await GuildManagementEngine.HandOnAbandonedGuildsAsync(db, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) >= 1);
            }

            Assert.Equal(GuildManagementEngine.RoleChieftain, await RoleOfAsync(warden));
            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(1, await verify.GuildMembers.CountAsync(m => m.GuildId == guildId && m.Role == GuildManagementEngine.RoleChieftain));
        }

        [Fact]
        public async Task AChieftainAwayFourteenDaysHandsOnToTheHighestActiveRank()
        {
            const long chief = 970011501L;
            long guildId = await GuildAsync("RankAbandon", chief, 4);
            long warden = chief + 1, elderAway = chief + 2, member = chief + 3;
            Assert.True(await Engine().PromoteMemberAsync(chief, warden));
            Assert.True(await Engine().PromoteMemberAsync(chief, elderAway));
            Assert.True(await Engine().PromoteMemberAsync(chief, elderAway));

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long fifteenDaysAgo = now - 15L * 24 * 3600;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // The Chieftain and the Elder are both gone; the Warden is the
                // highest rank still playing.
                foreach (var p in await db.PlayerRecords.Where(p => p.Id == chief || p.Id == elderAway).ToListAsync())
                {
                    p.LastLoginTimestamp = fifteenDaysAgo;
                }
                await db.SaveChangesAsync();

                Assert.True(await GuildManagementEngine.HandOnAbandonedGuildsAsync(db, now) >= 1);
            }

            Assert.Equal(GuildManagementEngine.RoleChieftain, await RoleOfAsync(warden));
            Assert.Equal(GuildManagementEngine.RoleMember, await RoleOfAsync(chief));
            Assert.Equal(GuildManagementEngine.RoleElder, await RoleOfAsync(elderAway));

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(1, await verify.GuildMembers.CountAsync(m => m.GuildId == guildId && m.Role == GuildManagementEngine.RoleChieftain));
            Assert.True(await verify.ChatChannelMessages.AnyAsync(m => m.GuildId == guildId && m.SenderPlayerId == 0 && m.MessageText.Contains("now leads the guild")));

            // A second pass changes nothing: the new Chieftain is active.
            await GuildManagementEngine.HandOnAbandonedGuildsAsync(verify, now);
            Assert.Equal(GuildManagementEngine.RoleChieftain, await RoleOfAsync(warden));
        }
    }
}
