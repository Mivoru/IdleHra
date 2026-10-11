using FolkIdle.Server.Domain.Combat;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: EATING KILLED A BIG CHARACTER (2026-10-11). PlayerHp is int
    /// milli-HP and auto-eat did `PlayerHp += heal` in int. A high-tier fish
    /// heals more than a full bar, so once the bar passed about 1.4 billion
    /// milli-HP the sum wrapped negative and the character died on the bite.
    /// Found calibrating Malakor on the owner's level-106 character, which sat
    /// just under that line: +20 health inheritance measured as a FIFTH of the
    /// headroom instead of more. Every heal now adds in long and caps at the
    /// bar, and the bar itself is clamped into int range rather than truncated.
    /// </summary>
    public class HealthOverflowTests
    {
        [Fact]
        public void AHealLargerThanTheRoomLeftCapsAtTheBar_NeverWraps()
        {
            const int bar = 1_885_127_183;      // the owner's bar with +20 health inheritance
            const int atThreshold = bar / 2;    // auto-eat fires at half
            const long tierTenFish = 2_030_000_000L; // 108% of the bar, clamped to int by FoodRegistry

            int after = SimulationEngine.HealClamped(atThreshold, tierTenFish, bar);

            Assert.Equal(bar, after);
            // What the old int sum did:
            Assert.True(unchecked(atThreshold + (int)tierTenFish) < 0);
        }

        [Fact]
        public void ABarPastIntRangeIsClampedNotTruncated()
        {
            Assert.Equal(int.MaxValue, SimulationEngine.BarAsInt(3_000_000_000L));
            Assert.Equal(1_000, SimulationEngine.BarAsInt(1_000L));
            Assert.Equal(0, SimulationEngine.BarAsInt(-5L));
        }

        [Fact]
        public void ANegativeHealHealsNothing()
        {
            Assert.Equal(500, SimulationEngine.HealClamped(500, -100, 1_000));
        }
    }
}
