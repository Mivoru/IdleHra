using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 94: leaving a guild. LeaveGuildAsync existed with no caller, so
    /// these pin what the new route promises in the UI - a member leaves
    /// quietly, a leader hands over to the successor the preview NAMED, and
    /// the last member closes the guild.
    /// </summary>
    [Collection("Postgres collection")]
    public class GuildLeaveTests
    {
        private readonly PostgresTestFixture _fixture;

        public GuildLeaveTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private GuildManagementEngine Engine() => new(_fixture.RetryingOptions, _fixture.PlayerRegistry);

        private async Task SeedPlayersAsync(params long[] ids)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            foreach (long id in ids)
            {
                db.PlayerRecords.Add(new PlayerRecord { Id = id, PlayerGuid = Guid.NewGuid(), AuthenticatorToken = Guid.NewGuid(), CurrentLevel = 30 });
            }
            await db.SaveChangesAsync();
        }

        private async Task<long> GuildWithAsync(string name, long leaderId, params long[] memberIds)
        {
            var engine = Engine();
            long guildId = (await engine.CreateGuildAsync(leaderId, name)).GuildId;
            Assert.True(guildId > 0, $"could not found {name}");
            foreach (long member in memberIds)
            {
                Assert.True(await engine.JoinGuildAsync(member, guildId), $"{member} could not join {name}");
            }
            return guildId;
        }

        [Fact]
        public async Task A_MemberLeaves_GuildStaysAndLeaderIsUnchanged()
        {
            const long leader = 970009401L, member = 970009402L, other = 970009403L;
            await SeedPlayersAsync(leader, member, other);
            long guildId = await GuildWithAsync("LeaveTestA", leader, member, other);

            var preview = await Engine().PreviewLeaveAsync(member);
            Assert.True(preview.InGuild);
            Assert.False(preview.IsLeader);
            Assert.False(preview.ClosesGuild);
            Assert.Equal(0L, preview.SuccessorPlayerId);

            var outcome = await Engine().LeaveAsync(member);
            Assert.True(outcome.Left);
            Assert.False(outcome.ClosedGuild);
            Assert.Equal(0L, outcome.SuccessorPlayerId);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var guild = await db.GuildRecords.AsNoTracking().SingleAsync(g => g.Id == guildId);
            Assert.Equal(2, guild.ActiveMembers);
            Assert.False(await db.GuildMembers.AnyAsync(m => m.PlayerId == member));
            Assert.Equal(0L, (await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == member)).GuildId);
            Assert.Equal(GuildManagementEngine.RoleLeader, (await db.GuildMembers.AsNoTracking().SingleAsync(m => m.PlayerId == leader)).Role);

            // Leaving twice is a refusal, not a second success.
            Assert.False((await Engine().LeaveAsync(member)).Left);
            Assert.False((await Engine().PreviewLeaveAsync(member)).InGuild);
        }

        [Fact]
        public async Task B_LeaderLeaves_TheSuccessorThePreviewNamedLeads()
        {
            const long leader = 970009411L, low = 970009412L, high = 970009413L;
            await SeedPlayersAsync(leader, low, high);
            long guildId = await GuildWithAsync("LeaveTestB", leader, low, high);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                // The higher PlayerId has the higher contribution, so a
                // "lowest id" or "first joined" rule would pick wrongly.
                (await db.GuildMembers.SingleAsync(m => m.PlayerId == low)).ContributionPoints = 10;
                (await db.GuildMembers.SingleAsync(m => m.PlayerId == high)).ContributionPoints = 500;
                await db.SaveChangesAsync();
            }

            var preview = await Engine().PreviewLeaveAsync(leader);
            Assert.True(preview.IsLeader);
            Assert.False(preview.ClosesGuild);
            Assert.Equal(high, preview.SuccessorPlayerId);

            var outcome = await Engine().LeaveAsync(leader);
            Assert.True(outcome.Left);
            Assert.Equal(preview.SuccessorPlayerId, outcome.SuccessorPlayerId);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.Equal(GuildManagementEngine.RoleLeader, (await verify.GuildMembers.AsNoTracking().SingleAsync(m => m.PlayerId == high)).Role);
            Assert.Equal(GuildManagementEngine.RoleMember, (await verify.GuildMembers.AsNoTracking().SingleAsync(m => m.PlayerId == low)).Role);
            Assert.Equal(2, (await verify.GuildRecords.AsNoTracking().SingleAsync(g => g.Id == guildId)).ActiveMembers);
        }

        [Fact]
        public async Task C_LastMemberLeaves_TheGuildClosesWithItsApplications()
        {
            const long leader = 970009421L, applicant = 970009422L;
            await SeedPlayersAsync(leader, applicant);
            long guildId = await GuildWithAsync("LeaveTestC", leader);

            var engine = Engine();
            Assert.True(await engine.SetGuildAccessPolicyAsync(leader, GuildManagementEngine.JoinTypeApplicationRequired, 20));
            Assert.False(await engine.JoinGuildAsync(applicant, guildId)); // files an application

            var preview = await engine.PreviewLeaveAsync(leader);
            Assert.True(preview.IsLeader);
            Assert.True(preview.ClosesGuild);
            Assert.Equal(0L, preview.SuccessorPlayerId);

            var outcome = await engine.LeaveAsync(leader);
            Assert.True(outcome.Left);
            Assert.True(outcome.ClosedGuild);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await db.GuildRecords.AnyAsync(g => g.Id == guildId));
            Assert.False(await db.GuildApplications.AnyAsync(a => a.GuildId == guildId));
            Assert.Equal(0L, (await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == leader)).GuildId);

            // The name is free again: founding it anew is how a sole member
            // gets back what they closed.
            Assert.True((await engine.CreateGuildAsync(leader, "LeaveTestC")).GuildId > 0);
        }

        // Modul: the old code deleted the guild when the ActiveMembers COUNTER
        // reached 0. A counter that had drifted low deleted a guild with people
        // still in it. The rows decide now, and the counter is resynced.
        [Fact]
        public async Task D_DriftedCounter_DoesNotCloseAGuildWithMembersLeft()
        {
            const long leader = 970009431L, member = 970009432L;
            await SeedPlayersAsync(leader, member);
            long guildId = await GuildWithAsync("LeaveTestD", leader, member);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                (await db.GuildRecords.SingleAsync(g => g.Id == guildId)).ActiveMembers = 1;
                await db.SaveChangesAsync();
            }

            var outcome = await Engine().LeaveAsync(member);
            Assert.True(outcome.Left);
            Assert.False(outcome.ClosedGuild);

            await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
            var guild = await verify.GuildRecords.AsNoTracking().SingleOrDefaultAsync(g => g.Id == guildId);
            Assert.NotNull(guild);
            Assert.Equal(1, guild!.ActiveMembers);
        }

        [Fact]
        public async Task E_GuildNameIsCappedAt32()
        {
            const long founder = 970009441L;
            await SeedPlayersAsync(founder);
            var engine = Engine();

            var tooLong = await engine.CreateGuildAsync(founder, new string('x', GuildManagementEngine.MaxGuildNameLength + 1));
            Assert.Equal(GuildManagementEngine.GuildCreateRefusal.NameInvalid, tooLong.Refusal);

            var exact = await engine.CreateGuildAsync(founder, "L" + new string('y', GuildManagementEngine.MaxGuildNameLength - 1));
            Assert.True(exact.GuildId > 0);
        }

        // The route itself: an engine nobody calls is the defect this task
        // exists for, so the HTTP path is pinned as well as the engine.
        [Fact]
        public async Task F_LeaveRoute_AnswersWithTheOutcomeAndRefusesASecondLeave()
        {
            const long leader = 970009451L, member = 970009452L;
            await SeedPlayersAsync(leader, member);
            await GuildWithAsync("LeaveTestF", leader, member);

            Guid accountId;
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                accountId = (await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == leader)).PlayerGuid;
            }

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var network = new NetworkBroadcastSystem(_fixture.ServiceProvider, AuthenticationDefaults.LocalDevelopmentFallback, $"http://localhost:{port}/");
            network.RegisterPlayerSessionRegistry(_fixture.PlayerRegistry);
            GlobalEngineState.IsColdBootRecoveryComplete = true;
            network.Start();

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2ETestHarness.MintTestJwt(accountId));

                var previewResponse = await http.GetAsync($"http://localhost:{port}/api/v1/guilds/leave-preview");
                Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
                var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync()).RootElement;
                Assert.True(preview.GetProperty("IsLeader").GetBoolean());
                Assert.Equal(member, preview.GetProperty("SuccessorPlayerId").GetInt64());

                var first = await http.PostAsync($"http://localhost:{port}/api/v1/guilds/leave", new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
                Assert.True(body.GetProperty("Left").GetBoolean());
                Assert.Equal(member, body.GetProperty("SuccessorPlayerId").GetInt64());

                var second = await http.PostAsync($"http://localhost:{port}/api/v1/guilds/leave", new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
                var refusal = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
                Assert.False(string.IsNullOrEmpty(refusal.GetProperty("Reason").GetString()));
            }
            finally
            {
                GlobalEngineState.IsColdBootRecoveryComplete = false;
                network.Stop();
            }
        }
    }
}
