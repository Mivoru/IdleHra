using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: EATING KILLED A BIG CHARACTER (2026-10-11). PlayerHp was int
    /// milli-HP and auto-eat did `PlayerHp += heal` in int. A high-tier fish
    /// heals more than a full bar, so once the bar passed about 1.4 billion
    /// milli-HP the sum wrapped negative and the character died on the bite.
    /// Found calibrating Malakor on the owner's level-106 character. The fix
    /// then clamped the bar into int range - which became the next defect.
    ///
    /// Modul: AN INT BAR CAPPED EVERYONE AT 2,147,483 HP (owner, 2026-11-08).
    /// The owner's 5.6M HP character showed a bar stuck at 2.15M and fought
    /// with it: food, regen, lifesteal and the respawn all healed toward the
    /// clamped value. PlayerHp is long milli-HP now; these tests pin that no
    /// step of the bar's life clamps into int again.
    /// </summary>
    public class HealthOverflowTests
    {
        private const long OwnersBar = 5_600_000L * 1000L; // 5.6M HP in milli-HP

        [Fact]
        public void AHealLargerThanTheRoomLeftCapsAtTheBar_NeverWraps()
        {
            const long bar = 1_885_127_183L;
            const long atThreshold = bar / 2;
            const long tierTenFish = 2_030_000_000L; // 108% of the bar

            Assert.Equal(bar, SimulationEngine.HealClamped(atThreshold, tierTenFish, bar));
        }

        [Fact]
        public void ABarPastIntRangeIsKeptWhole()
        {
            Assert.Equal(OwnersBar, SimulationEngine.Bar(OwnersBar));
            Assert.Equal(1_000L, SimulationEngine.Bar(1_000L));
            Assert.Equal(0L, SimulationEngine.Bar(-5L));
        }

        [Fact]
        public void HealingPastIntRangeReachesTheWholeBar()
        {
            long half = OwnersBar / 2;
            Assert.Equal(OwnersBar, SimulationEngine.HealClamped(half, OwnersBar, OwnersBar));
            Assert.Equal(half + 1_000_000_000L, SimulationEngine.HealClamped(half, 1_000_000_000L, OwnersBar));
        }

        [Fact]
        public void ADeathRespawnsOnTheWholeBar()
        {
            var payload = new TickStatePayload { PlayerHp = -1, CurrentLevel = 100 };
            SimulationEngine.ApplyCombatDeath(ref payload, 93, OwnersBar);
            Assert.Equal(OwnersBar, payload.PlayerHp);
        }

        [Fact]
        public void ANegativeHealHealsNothing()
        {
            Assert.Equal(500L, SimulationEngine.HealClamped(500, -100, 1_000));
        }
    }
}
