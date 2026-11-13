using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>BreedingMatchmaker - "who should my hero marry?" (2026-11-13).</summary>
    public class BreedingMatchmakerTests
    {
        [Fact]
        public void AStrongerPartnerScoresHigher()
        {
            int[] hero = { 20, 20, 20, 20 };
            var weak = BreedingMatchmaker.Score(hero, new[] { 5, 5, 5, 5 }, 35, false, 2);
            var strong = BreedingMatchmaker.Score(hero, new[] { 20, 20, 20, 20 }, 35, false, 2);
            Assert.True(strong.Score > weak.Score, $"{strong.Score} <= {weak.Score}");
        }

        [Fact]
        public void ARelativeScoresBelowTheSameNumbersUnrelated()
        {
            int[] hero = { 20, 20, 20, 20 };
            int[] partner = { 20, 20, 20, 20 };
            var related = BreedingMatchmaker.Score(hero, partner, 35, true, 3);
            var stranger = BreedingMatchmaker.Score(hero, partner, 35, false, 3);
            Assert.True(stranger.Score > related.Score);
        }

        [Fact]
        public void TheSelectionTicksWhereItGainsMost_AndNeverMoreThanTheGroundsAllows()
        {
            // Strength is where the weighted coin would most likely lose the
            // hero's 30 to the partner's 4; that is where a tick buys most.
            int[] hero = { 30, 10, 10, 10 };
            int[] partner = { 4, 10, 10, 10 };
            var match = BreedingMatchmaker.Score(hero, partner, 35, false, 1);
            Assert.Equal(0b0001, match.SelectionMask);

            var none = BreedingMatchmaker.Score(hero, partner, 35, false, 0);
            Assert.Equal(0, none.SelectionMask);
            Assert.True(match.Score > none.Score);

            var three = BreedingMatchmaker.Score(hero, partner, 35, false, 3);
            Assert.True(System.Numerics.BitOperations.PopCount((uint)three.SelectionMask) <= 3);
        }

        [Fact]
        public void TheFloorMatchesThePreviewBands()
        {
            int[] hero = { 20, 18, 12, 9 };
            int[] partner = { 15, 22, 12, 30 };
            var match = BreedingMatchmaker.Score(hero, partner, 35, false, 2);
            int floor = 0;
            for (int i = 0; i < 4; i++)
            {
                bool selected = (match.SelectionMask & (1 << i)) != 0;
                if (selected) { BreedingAptitudes.PreviewSelected(hero[i], partner[i], out int min, out _, 35); floor += min; }
                else { BreedingAptitudes.PreviewOne(hero[i], partner[i], out int min, out _, 35); floor += min; }
            }
            Assert.Equal(floor, match.ChildFloorTotal);
        }
    }
}
