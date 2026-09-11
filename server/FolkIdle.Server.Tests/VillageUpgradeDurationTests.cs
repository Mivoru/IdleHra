using System;
using FolkIdle.Server.Domain.Progression;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// How long a village upgrade takes, and the shape that number has to keep.
    ///
    /// Modul: written because the old one could not grow AT ALL, and nothing
    /// noticed for as long as the feature has existed.
    ///
    /// The duration was `max(30, cost / 10)` fed from
    /// CalculateProductionUpgradeCost, whose curve is `100 * 1.5^(level % 5)`.
    /// The modulo is right for the PRICE - tier materials change every five
    /// levels, so the cost restarts inside each tier - and meaningless for
    /// TIME. Levels 0-2 floored to 30 seconds, level 3 was 33, level 4 was 50,
    /// and level 5 dropped back to 30 because the cost did. A twelfth-level
    /// upgrade finished as fast as the first.
    ///
    /// Reported by a player as "it's like always around 30-40s, that's
    /// ridiculous". It was, and the arithmetic said so the whole time.
    ///
    /// So this file asserts the SHAPE - it never descends, it actually reaches
    /// a meaningful length, and it is bounded - rather than pinning the exact
    /// seconds, which is a balance number somebody should be free to retune.
    /// That is the same rule MonsterLadderTests works under.
    /// </summary>
    public class VillageUpgradeDurationTests
    {
        private readonly ITestOutputHelper _output;

        public VillageUpgradeDurationTests(ITestOutputHelper output) => _output = output;

        /// <summary>The ceiling a maxed Town Hall allows.</summary>
        private const int MaxBuildingLevel = 12;

        private static string Pretty(long seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m{t.Seconds:00}";
            return $"{seconds}s";
        }

        [Fact]
        public void The_curve_is_printed_and_never_descends()
        {
            long previous = 0;
            _output.WriteLine("level  duration");

            for (int level = 0; level <= MaxBuildingLevel; level++)
            {
                long seconds = VillageManagementEngine.CalculateUpgradeDurationSeconds(level);
                _output.WriteLine($"{level,5}  {Pretty(seconds),8}  ({seconds}s)");

                Assert.True(
                    seconds >= previous,
                    $"level {level} takes {seconds}s, less than level {level - 1}'s {previous}s - the ladder descended");
                previous = seconds;
            }
        }

        [Fact]
        public void It_does_not_reset_every_five_levels()
        {
            // Modul: THE EXACT DEFECT, pinned. CalculateProductionUpgradeCost
            // resets on `level % 5`, and the duration used to inherit that. If
            // anybody ever wires the cost back into the duration, these three
            // pairs go equal again and this fails.
            for (int level = 0; level + 5 <= MaxBuildingLevel; level++)
            {
                long here = VillageManagementEngine.CalculateUpgradeDurationSeconds(level);
                long fiveLater = VillageManagementEngine.CalculateUpgradeDurationSeconds(level + 5);

                Assert.True(
                    fiveLater > here,
                    $"level {level + 5} takes {fiveLater}s and level {level} takes {here}s - the duration is following a curve that resets every five levels");
            }
        }

        [Fact]
        public void An_early_upgrade_is_a_coffee_and_a_late_one_is_not()
        {
            long first = VillageManagementEngine.CalculateUpgradeDurationSeconds(0);
            long last = VillageManagementEngine.CalculateUpgradeDurationSeconds(MaxBuildingLevel);

            // The first one must not be a wall in front of a new player.
            Assert.InRange(first, 30, 120);

            // The last one must be worth leaving the game for, which is the
            // whole point of a build timer in an idle game. Anything under a
            // few minutes is the bug this file exists for.
            Assert.True(last >= 60 * 60, $"the top upgrade takes {Pretty(last)} - not long enough to matter");

            _output.WriteLine($"level 0: {Pretty(first)}   level {MaxBuildingLevel}: {Pretty(last)}");
        }

        [Fact]
        public void It_is_bounded_even_if_the_level_ceiling_rises()
        {
            // A geometric curve with no cap becomes the economy - this project
            // has unwound two of those already. The ceiling is a decision, not
            // an accident of the exponent.
            long farPastTheCeiling = VillageManagementEngine.CalculateUpgradeDurationSeconds(60);
            Assert.True(
                farPastTheCeiling <= 24L * 60L * 60L,
                $"level 60 would take {Pretty(farPastTheCeiling)} - the curve is unbounded");

            _output.WriteLine($"level 60 clamps to {Pretty(farPastTheCeiling)}");
        }

        [Fact]
        public void A_negative_level_is_treated_as_the_first_one()
        {
            Assert.Equal(
                VillageManagementEngine.CalculateUpgradeDurationSeconds(0),
                VillageManagementEngine.CalculateUpgradeDurationSeconds(-3));
        }
    }
}
