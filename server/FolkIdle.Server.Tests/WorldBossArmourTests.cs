using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The world boss stopped being a button.
    ///
    /// It was three presses of Attack, each posting a damage figure the CLIENT
    /// computed about its own character, bounded only by a 100,000,000 clamp on
    /// a shared, server-authoritative health pool. The only decision in it was
    /// having stocked the larder beforehand.
    ///
    /// It is now five armour plates, one of them soft, re-seeded every
    /// encounter. Striking the soft one pays triple; striking any other pays in
    /// full and BREAKS it, permanently, for every player in the world - so the
    /// state of the boss when a player arrives is a message from everyone who
    /// came before them. See docs/world_boss_design.md.
    ///
    /// The two properties worth guarding above all the rest:
    ///
    ///   1. The client sends a CHOICE, not a quantity. There is no longer a
    ///      number it can inflate.
    ///   2. The weak point is never on the wire until somebody has earned it.
    ///      Leak that and the decision is over before it starts.
    /// </summary>
    [Collection("Postgres collection")]
    public class WorldBossArmourTests
    {
        private readonly PostgresTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public WorldBossArmourTests(PostgresTestFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private const long TestPlayerId = 970_000_101L;

        private async Task<WorldBossEngine> FreshEncounterAsync()
        {
            var engine = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            await engine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
            Assert.True(engine.IsEventActive);
            return engine;
        }

        private async Task<(byte Weak, byte BrokenMask, byte Revealed)> ReadArmourAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var snapshot = await db.WorldBossSnapshots.AsNoTracking()
                .SingleAsync(b => b.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
            return (snapshot.WeakPlateIndex, snapshot.BrokenPlateMask, snapshot.WeakPlateRevealed);
        }

        [Fact]
        public async Task TheWeakPointIsNotOnTheWireUntilSomebodyHasFoundIt()
        {
            var engine = await FreshEncounterAsync();
            var armour = await ReadArmourAsync();

            // THE HEADLINE. A client that could read this before earning it
            // would turn a decision into a lookup, and the whole mechanic with
            // it.
            Assert.Equal(WorldBossEngine.WeakPlateHidden, engine.WeakPlate);
            Assert.Equal(0, armour.Revealed);

            byte armoured = (byte)((armour.Weak + 1) % WorldBossEngine.PlateCount);
            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000, armoured);

            // Striking the WRONG plate reveals nothing about the right one.
            Assert.Equal(WorldBossEngine.WeakPlateHidden, engine.WeakPlate);

            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000, armour.Weak);

            // Landing on it does.
            Assert.Equal(armour.Weak, engine.WeakPlate);
        }

        [Fact]
        public async Task TheWeakPlateTakesTripleAndTheOthersTakeFull()
        {
            var engine = await FreshEncounterAsync();
            var armour = await ReadArmourAsync();
            byte armoured = (byte)((armour.Weak + 1) % WorldBossEngine.PlateCount);

            const uint damage = 4_000;

            long before = engine.BossCurrentHp;
            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, damage, armoured);
            long afterArmoured = engine.BossCurrentHp;

            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, damage, armour.Weak);
            long afterWeak = engine.BossCurrentHp;

            long armouredHit = before - afterArmoured;
            long weakHit = afterArmoured - afterWeak;

            _output.WriteLine($"armoured plate: {armouredHit}, weak plate: {weakHit}");

            // A wrong strike is NOT punished - it does full normal damage. That
            // is deliberate: a player who guesses badly loses an upside rather
            // than paying a fine, and so has no reason to wait for somebody else
            // to strip the armour.
            Assert.Equal(damage, armouredHit);
            Assert.Equal((long)(damage * WorldBossEngine.WeakPlateDamageMultiplier), weakHit);
        }

        [Fact]
        public async Task AWrongStrikeBreaksThatPlateForEverybody()
        {
            var engine = await FreshEncounterAsync();
            var armour = await ReadArmourAsync();
            byte armoured = (byte)((armour.Weak + 1) % WorldBossEngine.PlateCount);

            Assert.Equal(0, engine.BrokenPlateMask);

            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000, armoured);

            // Shared state, not per-player: the mask lives on the boss, so the
            // next arrival sees what this player learned.
            Assert.Equal((byte)(1 << armoured), engine.BrokenPlateMask);

            var after = await ReadArmourAsync();
            Assert.Equal((byte)(1 << armoured), after.BrokenMask);
        }

        [Fact]
        public async Task StrikingTheWeakPointDoesNotBreakIt()
        {
            var engine = await FreshEncounterAsync();
            var armour = await ReadArmourAsync();

            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000, armour.Weak);

            // It is revealed, not broken. Breaking it would take the reward away
            // from everyone who arrives after the person who found it - which is
            // the opposite of what a shared board is for.
            Assert.Equal(0, engine.BrokenPlateMask);
            Assert.Equal(armour.Weak, engine.WeakPlate);
        }

        [Fact]
        public async Task APlateIndexOutsideTheRangeDoesNothingAtAll()
        {
            var engine = await FreshEncounterAsync();
            long before = engine.BossCurrentHp;

            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000, 5);
            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000, 200);

            Assert.Equal(before, engine.BossCurrentHp);
            Assert.Equal(0, engine.BrokenPlateMask);

            // And the attempts were not spent either - an invalid command is
            // refused, not charged for.
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var attempt = await db.PlayerWorldBossAttempts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.PlayerId == TestPlayerId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
            Assert.True(attempt == null || attempt.AttemptCount == 0);
        }

        [Fact]
        public async Task ANewEncounterReSeedsTheSecretAndTheArmour()
        {
            // RE-SEEDED PER ENCOUNTER is the difference between a decision and a
            // wiki lookup. If the weak point were a property of the boss rather
            // than of the encounter, this mechanic would have a shelf life of
            // about a day.
            var engine = await FreshEncounterAsync();
            var first = await ReadArmourAsync();

            byte armoured = (byte)((first.Weak + 1) % WorldBossEngine.PlateCount);
            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000, armoured);
            await engine.ExecuteAttackAsync(TestPlayerId, WorldBossEngine.ActiveBossInstanceId, 5000, first.Weak);
            Assert.NotEqual(0, engine.BrokenPlateMask);
            Assert.Equal(first.Weak, engine.WeakPlate);

            await engine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());

            Assert.Equal(0, engine.BrokenPlateMask);
            Assert.Equal(WorldBossEngine.WeakPlateHidden, engine.WeakPlate);
        }

        [Fact]
        public async Task SpentAttemptsSurviveALogout()
        {
            // Modul: NOTHING LOADED THIS UNTIL 2026-09-05.
            //
            // WorldBossAttemptCount was written in exactly one place - the
            // notification raised after an attack resolves - and read straight
            // onto the wire. So a player who spent their attempts, logged out
            // and came back saw three unspent pips. Clicking Attack then hit
            // the cap inside ExecuteAttackAsync, which rolls back IN SILENCE:
            // no damage, no message, nothing the player could ever see. The
            // screen only told the truth after they had wasted a click on it.
            //
            // Found by the exercise script reporting an attempt going
            // "0 -> 2 spent" on a single strike, which is not a thing one
            // strike can do.
            var engine = await FreshEncounterAsync();
            var armour = await ReadArmourAsync();
            byte armoured = (byte)((armour.Weak + 1) % WorldBossEngine.PlateCount);

            await engine.ExecuteAttackAsync(DbSeeder.PlayerLowId, WorldBossEngine.ActiveBossInstanceId, 5000, armoured);
            await engine.ExecuteAttackAsync(DbSeeder.PlayerLowId, WorldBossEngine.ActiveBossInstanceId, 5000, armoured);

            var checkpointManager = new FolkIdle.Server.Domain.Shared.StateCheckpointManager(_fixture.ServiceProvider);
            var reloaded = await checkpointManager.LoadPlayerState(DbSeeder.PlayerLowId);

            _output.WriteLine($"attempts after reload: {reloaded.WorldBossAttemptCount}");
            Assert.Equal(2, reloaded.WorldBossAttemptCount);
        }

        [Fact]
        public async Task ANewWindowGivesEverybodyTheirAttemptsBack()
        {
            var engine = await FreshEncounterAsync();
            var armour = await ReadArmourAsync();
            byte armoured = (byte)((armour.Weak + 1) % WorldBossEngine.PlateCount);

            await engine.ExecuteAttackAsync(DbSeeder.PlayerLowId, WorldBossEngine.ActiveBossInstanceId, 5000, armoured);

            // ActivateEventWindowAsync deletes the attempt rows, so the absence
            // of a row is the honest zero the hydration above reads.
            await engine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());

            var checkpointManager = new FolkIdle.Server.Domain.Shared.StateCheckpointManager(_fixture.ServiceProvider);
            var reloaded = await checkpointManager.LoadPlayerState(DbSeeder.PlayerLowId);

            Assert.Equal(0, reloaded.WorldBossAttemptCount);
        }

        [Fact]
        public async Task TheSeedIsActuallyRandomAcrossEncounters()
        {
            // Not a strong statistical claim - just enough to catch the seed
            // being a constant, which is exactly what an un-migrated column
            // default would look like: every encounter weak on plate 0.
            var engine = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry);
            var seen = new System.Collections.Generic.HashSet<byte>();

            for (int i = 0; i < 40; i++)
            {
                await engine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
                seen.Add((await ReadArmourAsync()).Weak);
                if (seen.Count >= WorldBossEngine.PlateCount) break;
            }

            _output.WriteLine($"weak plates seen across encounters: {string.Join(", ", seen.OrderBy(x => x))}");

            // Forty encounters against five plates: seeing fewer than three
            // distinct values would be a one-in-millions coincidence, or a
            // constant.
            Assert.True(seen.Count >= 3,
                $"only {seen.Count} distinct weak plates in 40 encounters - the seed looks constant");
        }
    }
}
