using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Who counts as a regional boss, pinned.
    ///
    /// This was `monsterId % 6 == 0` in two places, copied between them with a
    /// comment saying it kept the meaning consistent everywhere. It did - at
    /// the wrong monsters, and it drove three separate rewards:
    ///
    ///   - a guaranteed armour drop,
    ///   - 500 Guild War Combat Vanguard points instead of 10,
    ///   - a guaranteed TEN PREMIUM DIAMONDS.
    ///
    /// None of the five real bosses is divisible by six, so no boss ever paid
    /// any of them. Four ordinary monsters did. Thorny Vine is 96, and at a
    /// measured 36 kills a minute that is around twenty thousand free diamonds
    /// an hour - which is also why an equipment "drop rate" of 1.63% was
    /// producing sixty pieces from sixty-one kills.
    ///
    /// These tests are cheap and the thing they guard is a live-currency
    /// exploit, so they assert the exact set rather than a property.
    /// </summary>
    public class RegionalBossIdentityTests
    {
        [Theory]
        [InlineData(95)]
        [InlineData(100)]
        [InlineData(105)]
        [InlineData(110)]
        [InlineData(115)]
        public void TheFifthMonsterOfEachRegionIsTheBoss(int monsterId)
        {
            Assert.True(ContentRegistry.IsRegionalBoss(monsterId), $"monster {monsterId} should be a boss");
        }

        [Theory]
        [InlineData(96)]
        [InlineData(102)]
        [InlineData(108)]
        [InlineData(114)]
        public void TheMonstersTheOldHeuristicRewardedAreNotBosses(int monsterId)
        {
            // Every one of these is divisible by six and none is a boss. They
            // are the exact ids that were paying out.
            Assert.Equal(0, monsterId % 6);
            Assert.False(ContentRegistry.IsRegionalBoss(monsterId), $"monster {monsterId} must not be a boss");
        }

        [Fact]
        public void ExactlyFiveOfTheTwentyFiveCanonicalMonstersAreBosses()
        {
            int bosses = 0;
            for (int id = ContentRegistry.FirstCanonicalMonsterId; id <= ContentRegistry.LastCanonicalMonsterId; id++)
            {
                if (ContentRegistry.IsRegionalBoss(id)) bosses++;
            }

            Assert.Equal(5, bosses);
        }

        [Fact]
        public void LegacyMonstersAreNeverBosses()
        {
            // Ids 1-90 predate the five regions and belong to none of them.
            // The old rule made every sixth one a boss, which is fifteen more
            // sources of guaranteed diamonds.
            for (int id = 1; id < ContentRegistry.FirstCanonicalMonsterId; id++)
            {
                Assert.False(ContentRegistry.IsRegionalBoss(id), $"legacy monster {id} must not be a boss");
            }
        }

        [Fact]
        public void OutOfRangeIdsAreRefusedRatherThanComputed()
        {
            // A zero or negative id reaching a modulo would produce an answer
            // rather than a refusal, which is how the old version treated 0 as
            // a boss.
            Assert.False(ContentRegistry.IsRegionalBoss(0));
            Assert.False(ContentRegistry.IsRegionalBoss(-6));
            Assert.False(ContentRegistry.IsRegionalBoss(ContentRegistry.LastCanonicalMonsterId + 1));
        }
    }
}
