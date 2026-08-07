using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The board ranks by level, then by how far a player has actually got,
    /// then by how hard they have worked at it.
    ///
    /// It used to rank on raw XP, which is nearly the same thing as level and
    /// worse at saying it: two players on level 60 were separated by whichever
    /// happened to be further into the bar, which is minutes of play and reads
    /// as noise.
    ///
    /// Three ordered keys have to survive being packed into the single double a
    /// Redis sorted set gives us, and that packing is the thing worth pinning -
    /// a silently-wrong ranking caused by a number nobody bounded is exactly
    /// the kind of bug that survives for years.
    /// </summary>
    public class LeaderboardRankingTests
    {
        private readonly ITestOutputHelper _output;

        public LeaderboardRankingTests(ITestOutputHelper output) => _output = output;

        private static double Score(int level, int hardest, int kills) =>
            LeaderboardCronEngine.CompositeScore(new LeaderboardCronEngine.LeaderboardRow
            {
                PlayerId = 1,
                Level = level,
                HardestMonsterId = hardest,
                KillsOfHardest = kills,
            });

        [Fact]
        public void LevelBeatsEverythingElse()
        {
            // A level 61 who has killed nothing outranks a level 60 who has
            // cleared the last boss a million times.
            Assert.True(Score(61, 0, 0) > Score(60, 115, 999_999));
        }

        [Fact]
        public void OnTheSameLevelTheHarderMonsterWins()
        {
            Assert.True(Score(60, 115, 1) > Score(60, 114, 999_999));
        }

        [Fact]
        public void OnTheSameMonsterTheKillCountBreaksTheTie()
        {
            Assert.True(Score(60, 100, 500) > Score(60, 100, 499));
            Assert.Equal(Score(60, 100, 500), Score(60, 100, 500));
        }

        /// <summary>
        /// A double carries 53 bits of exact integer precision, about 9e15. The
        /// widest score this can produce has to stay inside that, or two
        /// genuinely different players collapse onto one number and the order
        /// between them becomes whatever Redis feels like.
        /// </summary>
        [Fact]
        public void TheWidestPossibleScoreIsStillExact()
        {
            double widest = Score(9_999, 9_999, 999_999);
            _output.WriteLine($"widest score: {widest:F0}");

            Assert.True(widest < 9.007e15, $"{widest:F0} exceeds the exact-integer range of a double");
            Assert.Equal(widest, widest + 0.0);

            // And one step down in the least significant key is still a
            // distinguishable number at that magnitude.
            Assert.True(widest > Score(9_999, 9_999, 999_998));
        }

        [Fact]
        public void OutOfRangeInputsAreClampedRatherThanWrapping()
        {
            // Nothing in the game reaches these, which is exactly why an
            // unbounded pack would go unnoticed until it did.
            Assert.Equal(Score(9_999, 9_999, 999_999), Score(50_000, 5_000_000, 5_000_000));
            Assert.Equal(Score(0, 0, 0), Score(-5, -5, -5));
        }
    }
}
