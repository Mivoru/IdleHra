using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE PLAYER'S BLOW HAD A CEILING OF 2,147,483 HIT POINTS (2026-11-19).
    /// The live swing was an int of milli-HP. Two of the three endgame accounts
    /// on production carried a BestHit of exactly int.MaxValue / 1000 - their
    /// swings had reached it, so more damage did nothing, and the war
    /// multipliers added after it could wrap the int negative and heal the
    /// monster. These hold the blow as a long.
    /// </summary>
    public class DamageCeilingTests
    {
        public DamageCeilingTests()
        {
            ContentRegistry.Initialize();
        }

        [Fact]
        public void WholeHitPoints_ClampsOnlyTheWireFigure()
        {
            Assert.Equal(0, SimulationEngine.WholeHitPoints(-5_000L));
            Assert.Equal(2_147_483, SimulationEngine.WholeHitPoints(int.MaxValue));
            Assert.Equal(5_000_000, SimulationEngine.WholeHitPoints(5_000_000_000L));
            Assert.Equal(int.MaxValue, SimulationEngine.WholeHitPoints(long.MaxValue));
        }

        [Fact]
        public void ABlowPastTheOldIntCeiling_IsRecordedInFull()
        {
            // Strength high enough that one swing lands far past 2.1M hit points.
            var payload = new TickStatePayload
            {
                PlayerId = 985_900L,
                CurrentLevel = 100,
                STR = 200_000_000,
                InventorySpaceRemaining = 1000,
                ActiveActivityId = 91L,
                CurrentMonsterId = 91,
                HighestLocationReached = 5,
                PlayerHp = 100_000,
                RestedHpPending = true,
            };

            for (int i = 0; i < 400; i++)
            {
                SimulationEngine.RunCombatTick(ref payload, 100, 100,
                    new ConcurrentQueue<GuildWarPointEvent>(), new ConcurrentDictionary<long, LiveSessionContext>());
            }

            Assert.True(payload.CachedEffectiveMilliAttack > int.MaxValue,
                $"the fixture should swing past the old ceiling, swung {payload.CachedEffectiveMilliAttack}");
            Assert.True(payload.BestHit > 2_147_483, $"BestHit {payload.BestHit} stopped at the old int ceiling");
        }
    }
}
