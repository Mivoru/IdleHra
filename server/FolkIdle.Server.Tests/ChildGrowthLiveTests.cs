using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A fielded child grows on the live payload the same way the SQL settle
    /// grows it on the row (2026-11-13): from its birth, working or not.
    /// </summary>
    public class ChildGrowthLiveTests
    {
        private const long Born = 1_800_000_000L;

        [Fact]
        public void AChildStartsTheSessionAtItsAgeSinceBirth()
        {
            // Twenty minutes old, never worked: a third of the childhood.
            long ticks = ChildGrowth.AgeTicksOnLoad(0, AgePhaseCurve.Child, Born, Born + 1200);
            Assert.Equal(AgePhaseCurve.ChildEndTicks / 3, ticks);
            Assert.Equal(AgePhaseCurve.Child, AgePhaseCurve.PhaseFor(ticks));
        }

        [Fact]
        public void WorkAlreadyDoneIsNeverTakenBack()
        {
            long worked = AgePhaseCurve.ChildEndTicks - 10;
            Assert.Equal(worked, ChildGrowth.AgeTicksOnLoad(worked, AgePhaseCurve.Child, Born, Born + 60));
        }

        [Fact]
        public void TheSinceBirthAgeStopsAtTheEndOfChildhood()
        {
            long ticks = ChildGrowth.AgeTicksOnLoad(0, AgePhaseCurve.Child, Born, Born + 10 * 3600);
            Assert.Equal(AgePhaseCurve.ChildEndTicks, ticks);
        }

        [Fact]
        public void AdultsAndChildrenOfUnknownBirthAreUnchanged()
        {
            Assert.Equal(500_000L, ChildGrowth.AgeTicksOnLoad(500_000L, AgePhaseCurve.Adult, Born, Born + 99_999));
            Assert.Equal(7L, ChildGrowth.AgeTicksOnLoad(7L, AgePhaseCurve.Child, 0, Born));
        }

        [Fact]
        public void AChildAgesEveryTick_AnAdultOnlyWhileWorking()
        {
            Assert.True(ChildGrowth.AgesThisTick(AgePhaseCurve.Child, slotIsWorking: false));
            Assert.True(ChildGrowth.AgesThisTick(AgePhaseCurve.Adult, slotIsWorking: true));
            Assert.False(ChildGrowth.AgesThisTick(AgePhaseCurve.Adult, slotIsWorking: false));
        }

        [Fact]
        public void TheLiveTickFinishesTheHourWhenTheSettleWould()
        {
            // Loaded at 50 minutes, then 10 ticks a second for the last 10.
            long loaded = ChildGrowth.AgeTicksOnLoad(0, AgePhaseCurve.Child, Born, Born + 3000);
            long afterTenMinutes = loaded + 600 * 10;
            Assert.Equal(AgePhaseCurve.Adult, AgePhaseCurve.PhaseFor(afterTenMinutes));
            Assert.True(ChildGrowth.IsGrown(Born, Born + 3600));
        }
    }
}
