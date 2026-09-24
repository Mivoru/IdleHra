using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Guild War Phase 0: the population lock.
    ///
    /// Modul: the live war code could mint 100 diamonds per member per win and
    /// its pairing could be forced by two alt guilds. The owner's answer
    /// (2026-09-23/24) is that wars stay LOCKED until 50 qualifying players and
    /// 4 guilds of 3, the unlock is one-way, and every war path - both pairers,
    /// settlement, payouts and opcodes 23/27/49/50 - goes through one gate.
    /// These tests pin each of those clauses, against a real database for the
    /// counting and against the real handlers for the opcodes.
    ///
    /// Own database, because the whole point is COUNTING players and guilds:
    /// in the shared one, whatever other tests happened to seed would decide
    /// the answer.
    /// </summary>
    [Collection("Postgres collection")]
    public class GuildWarUnlockTests : IAsyncLifetime
    {
        private readonly PostgresTestFixture _fixture;
        private string _databaseName = string.Empty;
        private string _connectionString = string.Empty;
        private DbContextOptions<FolkIdleDbContext> _options = null!;
        private ServiceProvider _services = null!;
        private long _nextPlayerId = 950_000_000L;

        public GuildWarUnlockTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        public async Task InitializeAsync()
        {
            _databaseName = $"guildwarunlock_{Guid.NewGuid():N}";

            var builder = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString);
            await using (var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            builder.Database = _databaseName;
            _connectionString = builder.ConnectionString;
            _options = new DbContextOptionsBuilder<FolkIdleDbContext>().UseNpgsql(_connectionString).Options;

            await using (var db = new FolkIdleDbContext(_options))
            {
                await db.Database.MigrateAsync();
            }

            // The two pairers resolve their context the way Program.cs wires
            // them: GuildWarEngine through a scoped FolkIdleDbContext,
            // GuildMatchmakingEngine through the factory.
            var collection = new ServiceCollection();
            collection.AddDbContextFactory<FolkIdleDbContext>(o => o.UseNpgsql(_connectionString));
            collection.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());
            _services = collection.BuildServiceProvider();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var admin = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }

        private FolkIdleDbContext NewContext() => new(_options);

        /// <summary>A guild with the given member levels. Returns its id.</summary>
        private async Task<long> SeedGuildAsync(params int[] memberLevels)
        {
            await using var db = NewContext();
            var guild = new GuildRecord { Name = "War" + Guid.NewGuid().ToString("N")[..10], GuildMMR = 1000, ActiveMembers = memberLevels.Length };
            db.GuildRecords.Add(guild);
            await db.SaveChangesAsync();

            foreach (int level in memberLevels)
            {
                long id = ++_nextPlayerId;
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = id,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    GuildId = guild.Id,
                    CurrentLevel = level,
                });
                db.GuildMembers.Add(new GuildMember { GuildId = guild.Id, PlayerId = id });
            }
            await db.SaveChangesAsync();
            return guild.Id;
        }

        /// <summary>Guildless players at a level.</summary>
        private async Task SeedLonersAsync(int count, int level)
        {
            await using var db = NewContext();
            for (int i = 0; i < count; i++)
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = ++_nextPlayerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    CurrentLevel = level,
                });
            }
            await db.SaveChangesAsync();
        }

        /// <summary>Exactly the floor: 4 guilds of 3 qualifying members, topped up to 50 players.</summary>
        private async Task SeedExactlyTheFloorAsync()
        {
            int lvl = LeaderboardTierRegistry.MinimumRankedLevel;
            for (int g = 0; g < GuildWarUnlock.RequiredGuilds; g++)
            {
                await SeedGuildAsync(Enumerable.Repeat(lvl, GuildWarUnlock.RequiredMembersPerGuild).ToArray());
            }
            await using var db = NewContext();
            int have = await QualifyingPopulation.CountPlayersAsync(db);
            await SeedLonersAsync(GuildWarUnlock.RequiredQualifyingPlayers - have, lvl);
        }

        [Fact]
        public void TheFloorIsTheOwnersNumbers_AndBothHalvesAreRequired()
        {
            Assert.Equal(50, GuildWarUnlock.RequiredQualifyingPlayers);
            Assert.Equal(4, GuildWarUnlock.RequiredGuilds);
            Assert.Equal(3, GuildWarUnlock.RequiredMembersPerGuild);

            Assert.False(GuildWarUnlock.MeetsFloor(49, 4));
            Assert.False(GuildWarUnlock.MeetsFloor(500, 3));
            Assert.True(GuildWarUnlock.MeetsFloor(50, 4));

            // One-way: a recorded unlock wins over any count.
            Assert.True(GuildWarUnlock.IsUnlockedBy(alreadyRecorded: true, 0, 0));
            Assert.False(GuildWarUnlock.IsUnlockedBy(alreadyRecorded: false, 49, 4));
        }

        [Fact]
        public async Task TheGuildCriterion_CountsOnlyGuildsWithThreeQualifyingMembers()
        {
            int lvl = LeaderboardTierRegistry.MinimumRankedLevel;

            await SeedGuildAsync(lvl, lvl, lvl);                 // counts: three at the bar
            await SeedGuildAsync(lvl + 30, lvl, lvl, 1, 1);      // counts: three qualifying, two alts beside them
            await SeedGuildAsync(lvl, lvl, 1, 1, 1, 1, 1);       // does not: seven members, only two qualify
            await SeedGuildAsync(lvl - 1, lvl - 1, lvl - 1);     // does not: one level short, all three
            await SeedLonersAsync(5, lvl);                       // players, but in no guild

            await using var db = NewContext();
            Assert.Equal(2, await QualifyingPopulation.CountGuildsAsync(db, GuildWarUnlock.RequiredMembersPerGuild));

            // 3 + 3 + 2 + 0 in guilds, 5 loners.
            Assert.Equal(13, await QualifyingPopulation.CountPlayersAsync(db));
        }

        [Fact]
        public async Task PairingCannotRunBelowTheFloor_AndDoesOnceItIsCrossed()
        {
            int lvl = LeaderboardTierRegistry.MinimumRankedLevel;

            // Four healthy guilds - the guild half is met - but only 12 players.
            for (int g = 0; g < 4; g++)
            {
                await SeedGuildAsync(lvl, lvl, lvl);
            }

            var warEngine = new GuildWarEngine(_services);
            var matchmaking = new GuildMatchmakingEngine(_services);

            Assert.False(await warEngine.RunMatchmakingPassAsync(CancellationToken.None));
            await matchmaking.ExecutePairingCycleAsync(CancellationToken.None);

            await using (var db = NewContext())
            {
                Assert.Equal(0, await db.GuildWarMatches.CountAsync());
                Assert.Equal(0, await db.GuildMatchmakingSnapshots.CountAsync());
                Assert.Equal(0, await db.FeatureUnlocks.CountAsync());
            }
            Assert.False(warEngine.Unlock.IsUnlocked);
            Assert.Equal(12, warEngine.Unlock.Current.QualifyingPlayers);
            Assert.Equal(4, warEngine.Unlock.Current.QualifyingGuilds);

            // The control: the same engines on the same guilds DO pair once
            // the player half is met - so it was the gate that stopped them,
            // not something else about this seed.
            await SeedLonersAsync(GuildWarUnlock.RequiredQualifyingPlayers - 12, lvl);

            Assert.True(await warEngine.RunMatchmakingPassAsync(CancellationToken.None));
            await matchmaking.ExecutePairingCycleAsync(CancellationToken.None);

            await using (var db = NewContext())
            {
                Assert.Equal(2, await db.GuildWarMatches.CountAsync(m => m.IsActive));
                Assert.True(await db.GuildMatchmakingSnapshots.CountAsync() > 0);
            }
            Assert.True(warEngine.Unlock.IsUnlocked);
        }

        [Fact]
        public async Task TheUnlockIsOneWay_APopulationDipDoesNotRelock()
        {
            await SeedExactlyTheFloorAsync();

            await using (var db = NewContext())
            {
                var first = await GuildWarUnlock.EvaluateAsync(db);
                Assert.True(first.Unlocked);

                var row = await db.FeatureUnlocks.AsNoTracking().SingleAsync();
                Assert.Equal(GuildWarUnlock.FeatureKey, row.FeatureKey);
                Assert.Equal(50, row.QualifyingPlayersAtUnlock);
                Assert.Equal(4, row.QualifyingGuildsAtUnlock);
            }

            // Everybody drops out of the ranked population.
            await using (var db = NewContext())
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE \"PlayerRecords\" SET \"CurrentLevel\" = 1");
            }

            // A brand-new gate (a restart) evaluated from the database alone.
            var restarted = new GuildWarUnlock();
            var after = await restarted.RefreshAsync(_services);
            Assert.True(after.Unlocked, "The unlock re-locked when the population dipped - it must be one-way.");
            Assert.Equal(0, after.QualifyingPlayers);

            // Evaluating again does not write a second row.
            await using (var db = NewContext())
            {
                await GuildWarUnlock.EvaluateAsync(db);
                Assert.Equal(1, await db.FeatureUnlocks.CountAsync());
            }

            // And the in-memory cache cannot be pushed back to locked either.
            restarted.Publish(new GuildWarUnlockStatus(false, 0, 0));
            Assert.True(restarted.IsUnlocked);
        }

        // ---- the opcodes ----------------------------------------------------

        private sealed class Probe
        {
            public int ShardAttackCalls;
            public int RegisterDefenseCalls;
            public int Terminations;
            public int Removals;
        }

        private static void Dispatch(
            CommandType command,
            GuildWarEngine warEngine,
            PlayerSessionRegistry registry,
            Probe probe)
        {
            var payload = new TickStatePayload
            {
                PlayerId = 424242L,
                GuildId = 7L,
                ActiveGuildWarId = 3L,
            };
            var cmd = new ClientCommandPacket
            {
                Command = command,
                SecondaryId = 1,
                TertiaryId = 10,
                TargetMatchUuid = Guid.NewGuid(),
                ClientPredictedDamage = 1000,
                MatchId = 5,
            };

            var ctx = new CommandCoordinatorContext
            {
                RoutingPlayerId = payload.PlayerId,
                PlayerRegistry = registry,
                GuildWarEngine = warEngine,
                SafeDispatch = (_, _, work) => work().GetAwaiter().GetResult(),
                TerminateSessionForSecurity = _ => probe.Terminations++,
                RemoveActivePlayer = _ => probe.Removals++,
                RegisterGuildDefense = _ => { probe.RegisterDefenseCalls++; return Task.CompletedTask; },
                SubmitShardAttack = (_, _, _, _, _) =>
                {
                    probe.ShardAttackCalls++;
                    return Task.FromResult((new SyncMatchStateResponseBuffer(0, 0), 0));
                },
            };

            switch (command)
            {
                case CommandType.ContributeToWarSupply:
                    GuildWarTickCoordinator.HandleContributeToWarSupply(ref payload, ref cmd, in ctx);
                    break;
                case CommandType.ExecuteCombatTurn:
                    GuildWarTickCoordinator.HandleExecuteCombatTurn(ref payload, ref cmd, in ctx);
                    break;
                case CommandType.RegisterGuildDefense:
                    GuildWarTickCoordinator.HandleRegisterGuildDefense(ref payload, ref cmd, in ctx);
                    break;
                case CommandType.SubmitShardAttack:
                    GuildWarTickCoordinator.HandleSubmitShardAttack(ref payload, ref cmd, in ctx);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }
        }

        private static List<byte> DrainResults(PlayerSessionRegistry registry)
        {
            var codes = new List<byte>();
            while (registry.CommandResultQueue.TryDequeue(out var n)) codes.Add(n.ResultCode);
            return codes;
        }

        [Theory]
        [InlineData(CommandType.ContributeToWarSupply)]
        [InlineData(CommandType.ExecuteCombatTurn)]
        [InlineData(CommandType.RegisterGuildDefense)]
        [InlineData(CommandType.SubmitShardAttack)]
        public void WhileLocked_EveryWarOpcodeAnswersGuildWarsLocked_AndNeitherActsNorDisconnects(CommandType command)
        {
            var warEngine = new GuildWarEngine(null!);
            var registry = new PlayerSessionRegistry();
            var probe = new Probe();

            Dispatch(command, warEngine, registry, probe);

            Assert.Equal(new List<byte> { (byte)CommandResultCode.GuildWarsLocked }, DrainResults(registry));
            Assert.Equal(0, probe.ShardAttackCalls);
            Assert.Equal(0, probe.RegisterDefenseCalls);
            Assert.Equal(0, probe.Terminations);
            Assert.Equal(0, probe.Removals);
            Assert.True(warEngine.SupplyChainQueue.IsEmpty, "A locked war supply command still queued a material burn.");
        }

        [Fact]
        public void Opcode50_DoesNotReachTheTournamentMeshWhileLocked_AndDoesOnceUnlocked()
        {
            var warEngine = new GuildWarEngine(null!);
            var registry = new PlayerSessionRegistry();
            var probe = new Probe();

            Dispatch(CommandType.SubmitShardAttack, warEngine, registry, probe);
            Assert.Equal(0, probe.ShardAttackCalls);

            // The control: the same packet with the gate open reaches the
            // delegate that SimulationEngine binds to GlobalTournamentMeshService,
            // so the refusal above was the lock and not the validator.
            warEngine.Unlock.Publish(new GuildWarUnlockStatus(true, 50, 4));
            Dispatch(CommandType.SubmitShardAttack, warEngine, registry, probe);
            Assert.Equal(1, probe.ShardAttackCalls);
            // Exactly one refusal - the locked attempt - and none for the open one.
            Assert.Equal(new List<byte> { (byte)CommandResultCode.GuildWarsLocked }, DrainResults(registry));
        }
    }
}
