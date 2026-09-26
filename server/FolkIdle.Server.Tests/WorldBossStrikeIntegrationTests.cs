using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 36 Phase 2, the engine's half: a scored order becomes damage inside
    /// the Serializable transaction, with a per-attempt weak plate, one break,
    /// no reveal, the last plate spared, and armour that regrows at midnight
    /// (spec 3.2, 3.3.1, 5.7). Against a real Postgres.
    /// </summary>
    [Collection("Postgres collection")]
    public class WorldBossStrikeIntegrationTests
    {
        private readonly PostgresTestFixture _fixture;

        public WorldBossStrikeIntegrationTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private const long A = 10_000;
        private const long P1 = 970_036_001L;
        private const long P2 = 970_036_002L;
        private const long P3 = 970_036_003L;

        private async Task<WorldBossEngine> FreshWheelEncounterAsync(BossMinigameMode mode = BossMinigameMode.Wheel)
        {
            var engine = new WorldBossEngine(_fixture.ServiceProvider, _fixture.PlayerRegistry, mode);
            await engine.ActivateEventWindowAsync(DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
            Assert.True(engine.IsEventActive);
            return engine;
        }

        private async Task<WorldBossSnapshot> SnapshotAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.WorldBossSnapshots.AsNoTracking().SingleAsync(b => b.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
        }

        private async Task SetArmourAsync(byte mask, long? dayKey = null)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"WorldBossSnapshots\" SET \"BrokenPlateMask\" = {0}, \"ArmourDayKey\" = {1} WHERE \"BossInstanceId\" = {2}",
                (int)mask, dayKey ?? WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), (long)WorldBossEngine.ActiveBossInstanceId);
        }

        private async Task<PlayerWorldBossAttempt?> AttemptAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerWorldBossAttempts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.PlayerId == playerId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
        }

        private static WorldBossStrikeOrder Order(long player, int? weak, double m, params SpearLanding[] landings) => new()
        {
            PlayerId = player,
            Landings = landings,
            WeakPlate = weak,
            Multiplier = m,
            LandedAs = WorldBossStrikeResult.Landed,
        };

        private static SpearLanding Seam(int seq, int plate) => new(seq, plate, SpearClass.Seam, false);
        private static SpearLanding Hit(int seq, int plate) => new(seq, plate, SpearClass.Plate, false);

        [Fact]
        public async Task AWheelStrikeDealsATimesThePlayedMultiplier()
        {
            var engine = await FreshWheelEncounterAsync();
            long hp = engine.BossCurrentHp;

            // Two seams on this attempt's weak plate and M at the cap: 2.0 x 3.0.
            var outcome = await engine.ExecuteStrikeAsync(Order(P1, weak: 2, m: 2.0, Seam(0, 2), Seam(1, 2)), A);

            Assert.Equal(WorldBossStrikeResult.Landed, outcome.Result);
            Assert.Equal(6.0, outcome.Played, 6);
            Assert.Equal(A * 6, outcome.Damage);
            Assert.Equal(hp - A * 6, (await SnapshotAsync()).CurrentHp);
            Assert.Equal(1, (await AttemptAsync(P1))!.AttemptCount);
            Assert.All(outcome.Landings!, l => Assert.True(l.WeakHit));
        }

        [Fact]
        public async Task AWeakCharactersMultipliersCountOnTopOfTheFloor()
        {
            // A x G of 150 is below the 1,000 floor. The floor lifts the BASE
            // hit, and the skill and weak-plate multipliers still apply to it -
            // before this, every strike by such a character dealt exactly 1,000.
            var engine = await FreshWheelEncounterAsync();
            var blind = await engine.ExecuteStrikeAsync(Order(P1, weak: 0, m: 1.0, Hit(0, 1)), 150);
            var skilled = await engine.ExecuteStrikeAsync(Order(P2, weak: 3, m: 2.0, Seam(0, 3)), 150);

            Assert.Equal(WorldBossEngine.MinStrikeDamage, blind.Damage);
            Assert.Equal(WorldBossEngine.MinStrikeDamage * 6, skilled.Damage);
        }

        [Fact]
        public async Task APoorRunThatFoundTheWeakPlatePaysAtLeastAnAutoStrikeOnIt()
        {
            var engine = await FreshWheelEncounterAsync();
            var outcome = await engine.ExecuteStrikeAsync(Order(P1, weak: 4, m: 1.0, Hit(0, 4), Hit(1, 0)), A);
            // M x P = 1.0 x 2.0, but the floor is the 3.0 of auto-striking plate 4.
            Assert.Equal(3.0, outcome.Played, 6);
            Assert.Equal(A * 3, outcome.Damage);
        }

        [Fact]
        public async Task OneAttemptBreaksOnePlateAndAWeakHitRevealsNothing()
        {
            var engine = await FreshWheelEncounterAsync();
            var outcome = await engine.ExecuteStrikeAsync(Order(P1, weak: 1, m: 1.0, Seam(0, 1), Hit(1, 3), Hit(2, 0)), A);

            Assert.Equal(3, outcome.BrokePlate);
            var snapshot = await SnapshotAsync();
            Assert.Equal(1 << 3, snapshot.BrokenPlateMask);
            Assert.Equal(0, snapshot.WeakPlateRevealed);
            Assert.Equal(WorldBossEngine.WeakPlateHidden, engine.WeakPlate);
        }

        [Fact]
        public async Task TheLastPlateStandingNeverBreaks()
        {
            var engine = await FreshWheelEncounterAsync();
            await SetArmourAsync(0b01111);
            // A stale challenge: it thinks plate 0 (already broken) is weak, and
            // its spear lands on plate 4 - the only plate standing.
            var outcome = await engine.ExecuteStrikeAsync(Order(P1, weak: 0, m: 1.0, Hit(0, 4)), A);

            Assert.Equal(WorldBossStrikeResult.Landed, outcome.Result);
            Assert.Equal(-1, outcome.BrokePlate);
            Assert.Equal(0b01111, (await SnapshotAsync()).BrokenPlateMask);
        }

        [Fact]
        public async Task AnAutoStrikeDrawsItsWeakPlateUnderTheLockFromTheUnbrokenPlates()
        {
            var engine = await FreshWheelEncounterAsync();
            await SetArmourAsync(0b10111); // only plate 3 standing
            var outcome = await engine.ExecuteStrikeAsync(Order(P1, weak: null, m: 1.0, Hit(0, 3)), A);

            Assert.True(Assert.Single(outcome.Landings!).WeakHit);
            Assert.Equal(3.0, outcome.Played, 6);
            Assert.Equal(-1, outcome.BrokePlate);
        }

        [Fact]
        public async Task ASecondStrikeTheSameDayIsRefusedAndSpendsNothing()
        {
            var engine = await FreshWheelEncounterAsync();
            Assert.Equal(WorldBossStrikeResult.Landed, (await engine.ExecuteStrikeAsync(Order(P1, 0, 1.0), A)).Result);
            long hp = engine.BossCurrentHp;

            var second = await engine.ExecuteStrikeAsync(Order(P1, 0, 1.0, Hit(0, 2)), A);

            Assert.Equal(WorldBossStrikeResult.NoAttemptsLeft, second.Result);
            Assert.Equal(hp, (await SnapshotAsync()).CurrentHp);
            Assert.Equal(1, (await AttemptAsync(P1))!.AttemptCount);
            Assert.Equal(0, (await SnapshotAsync()).BrokenPlateMask);
        }

        [Fact]
        public async Task AChallengeFromAnEncounterThatRolledOverSpendsNothing()
        {
            var engine = await FreshWheelEncounterAsync();
            var order = Order(P1, 0, 2.0, Seam(0, 0));
            var stale = new WorldBossStrikeOrder
            {
                PlayerId = order.PlayerId, Landings = order.Landings, WeakPlate = order.WeakPlate,
                Multiplier = order.Multiplier, LandedAs = order.LandedAs,
                EncounterEndEpoch = engine.EventEndEpoch - 7 * 86_400,
            };

            Assert.Equal(WorldBossStrikeResult.NotActive, (await engine.ExecuteStrikeAsync(stale, A)).Result);
            Assert.Null(await AttemptAsync(P1));
        }

        [Fact]
        public async Task TheArmourRegrowsWhenTheDayMovesOn_AndOnlyTheArmour()
        {
            var engine = await FreshWheelEncounterAsync();
            long today = WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await SetArmourAsync(0b00111, dayKey: today - 1);
            var before = await SnapshotAsync();

            // LiveOps' once-a-minute rescale holds the row lock too: it regrows the board.
            await engine.ScaleActiveBossAsync(Array.Empty<long>());

            var after = await SnapshotAsync();
            Assert.Equal(0, after.BrokenPlateMask);
            Assert.Equal(today, after.ArmourDayKey);
            Assert.Equal(before.CurrentHp, after.CurrentHp);
            Assert.Equal(before.EventEndEpoch, after.EventEndEpoch);
            Assert.Equal(before.TotalDamageContributed, after.TotalDamageContributed);
            Assert.Equal(0, engine.BrokenPlateMask);

            // And a strike on a stale day regrows it before it breaks anything.
            await SetArmourAsync(0b01011, dayKey: today - 1);
            var outcome = await engine.ExecuteStrikeAsync(Order(P2, weak: 4, m: 1.0, Hit(0, 0)), A);
            Assert.Equal(0, outcome.BrokePlate);
            Assert.Equal(1, (await SnapshotAsync()).BrokenPlateMask);
        }

        [Fact]
        public async Task OutsideWheelModeTheArmourDoesNotRegrow_AndTheWheelIsRefused()
        {
            var engine = await FreshWheelEncounterAsync(BossMinigameMode.Practice);
            long today = WorldBossCalendar.DayKey(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await SetArmourAsync(0b00101, dayKey: today - 3);

            await engine.ScaleActiveBossAsync(Array.Empty<long>());
            Assert.Equal(0b00101, (await SnapshotAsync()).BrokenPlateMask);

            Assert.Equal(WorldBossStrikeResult.Failed, (await engine.ExecuteStrikeAsync(Order(P3, 0, 1.0), A)).Result);
            Assert.Null(await AttemptAsync(P3));
        }
    }
}
