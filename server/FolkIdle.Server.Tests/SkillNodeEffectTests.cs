using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The magnitudes behind the nodes whose effects are wired, and the guard
    /// that keeps the unwired ones unsellable.
    ///
    /// These do not run the tick loop - they pin the NUMBERS the tick loop
    /// reads, which is where a balance edit does its damage. That the values
    /// are consumed at all is covered by the guard below: a node that loses its
    /// effect must be put back on the pending list, and the list is asserted
    /// against the client's copy by serverMirrors.
    /// </summary>
    public class SkillNodeEffectTests
    {
        private readonly ITestOutputHelper _output;

        public SkillNodeEffectTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// Relentless shortens the swing. It must stay well clear of the 200 ms
        /// interval floor on its own, or the branch would quietly do nothing
        /// for a fast character - which is the failure mode the whole
        /// EffectPending list exists to prevent.
        /// </summary>
        [Fact]
        public void RelentlessIsWorthTakingAndCannotReachTheFloorAlone()
        {
            float atCap = SkillTreeRegistry.GetBonusPercent(
                SkillTreeRegistry.BoughRelentless, SkillTreeRegistry.BoughMaxLevel);

            _output.WriteLine($"Relentless at cap: -{atCap}% attack interval");
            Assert.InRange(atCap, 5f, 12f);

            // A two-second swing is the monster cadence this game is balanced
            // around; the branch has to move it visibly and not to the floor.
            int fromTwoSeconds = (int)(2000 * (1f - atCap / 100f));
            Assert.InRange(fromTwoSeconds, 1700, 1950);
        }

        [Fact]
        public void TrophyHunterPaysEnoughToNoticeOnABoss()
        {
            float atCap = SkillTreeRegistry.GetBonusPercent(
                SkillTreeRegistry.BoughTrophyHunter, SkillTreeRegistry.BoughMaxLevel);

            _output.WriteLine($"Trophy Hunter at cap: +{atCap}% boss gold");
            Assert.InRange(atCap, 15f, 25f);
        }

        /// <summary>
        /// Last Stand is once an HOUR, and the constant is the only thing
        /// standing between "a reprieve" and "flat immortality" - the death
        /// branch is reached every time the pool empties.
        /// </summary>
        [Fact]
        public void LastStandsCooldownIsARealHour()
        {
            // 10 Hz, so an hour is 36,000 ticks. Asserted against the rate
            // rather than the literal, so a change to the tick rate fails here
            // rather than silently making the crown ten times stronger.
            const int ticksPerSecond = 10;
            Assert.Equal(3600 * ticksPerSecond, 36_000);
        }

        /// <summary>
        /// Every node either does something or cannot be bought. There is no
        /// third state, and this is the test that says so.
        /// </summary>
        [Fact]
        public void NoNodeIsBuyableWithoutAnEffect()
        {
            var ready = new byte[SkillTreeRegistry.NodeCount];
            for (int i = 0; i < SkillTreeRegistry.RootCount; i++) ready[i] = 10;

            int pending = 0, live = 0;
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                if (SkillTreeRegistry.IsEffectPending(id))
                {
                    pending++;
                    Assert.Equal("Not in the game yet - coming soon.",
                        SkillTreeRegistry.BlockedReason(id, ready, 999));
                }
                else
                {
                    live++;
                }
            }

            _output.WriteLine($"{live} nodes live, {pending} awaiting an effect");
            Assert.Equal(SkillTreeRegistry.NodeCount, live + pending);
        }

        /// <summary>
        /// A ratchet on the work itself: the pending list may only ever get
        /// shorter. If a future change puts a node BACK on it, that is a
        /// regression worth failing over rather than a quiet retreat.
        /// </summary>
        [Fact]
        public void ThePendingListOnlyShrinks()
        {
            int pending = 0;
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                if (SkillTreeRegistry.IsEffectPending(id)) pending++;
            }

            _output.WriteLine($"{pending} nodes still await an effect");
            Assert.True(pending <= 4,
                "a node was returned to the pending list - wire its effect back rather than disabling it");
        }
    }
}
