using System.Collections.Generic;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE AUTO-REROLL LOOP DECIDED ON UNCOMMITTED STATE.
    ///
    /// Reported from play: "when I try to do like 50 rerolls I think it
    /// sometimes skips the legendary". It could, and here is how. The loop read
    /// two INSTANCE FIELDS on AffixRerollEngine - LastRerollResultRarity and
    /// LastRerollResultAffixId - which were assigned BEFORE SaveChangesAsync and
    /// never cleared on a rollback. A Serializable conflict (the reroll locks
    /// the item row and the gold row inside a Serializable transaction while the
    /// tick is writing PlayerRecords) rolls the attempt back but leaves those
    /// fields holding the roll that did not happen. The run could therefore stop
    /// on a Legendary the player never received, which looks exactly like the
    /// Legendary being skipped.
    ///
    /// Worse, Program.cs constructs ONE AffixRerollEngine for the whole server
    /// and SimulationEngine dispatches reroll requests as concurrent tasks, so
    /// the fields are shared across players - the file's own comment claiming
    /// "one engine instance handles one request at a time" was wrong.
    ///
    /// The loop is extracted here so the decision is testable without a Postgres
    /// fixture, and so the only thing it can see is what an attempt REPORTS
    /// having committed. Every test below drives the real loop with scripted
    /// attempt outcomes.
    /// </summary>
    public class AutoRerollRunnerTests
    {
        private readonly ITestOutputHelper _output;

        public AutoRerollRunnerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Scripts a run: each entry is what that attempt reports back.
        /// Records how many times the loop actually asked for an attempt, which
        /// is what the player is charged for.
        /// </summary>
        private sealed class ScriptedAttempts
        {
            private readonly IReadOnlyList<RerollAttemptOutcome> _script;
            public int Requested { get; private set; }

            public ScriptedAttempts(params RerollAttemptOutcome[] script) => _script = script;

            public Task<RerollAttemptOutcome> NextAsync(int attemptIndex)
            {
                Requested++;
                // Past the end of the script the item keeps rolling junk, which
                // is what a real run does - the script only pins the
                // interesting attempts.
                var outcome = attemptIndex < _script.Count
                    ? _script[attemptIndex]
                    : RerollAttemptOutcome.Committed(AffixRarity.Common, "flat_armor", goldSpent: 1000L);
                return Task.FromResult(outcome);
            }
        }

        private static AutoRerollStopCondition StopAt(AffixRarity rarity, string? affixId = null)
            => new AutoRerollStopCondition(rarity, affixId);

        [Fact]
        public async Task StopsOnTheFirstCommittedRollThatSatisfiesBothHalves()
        {
            var attempts = new ScriptedAttempts(
                RerollAttemptOutcome.Committed(AffixRarity.Legendary, "crit_chance_pct", 1000L),
                RerollAttemptOutcome.Committed(AffixRarity.Epic, "flat_hp", 1000L),
                RerollAttemptOutcome.Committed(AffixRarity.Legendary, "flat_hp", 1000L));

            var result = await AutoRerollRunner.RunAsync(
                StopAt(AffixRarity.Legendary, "flat_hp"), 50, attempts.NextAsync);

            Assert.Equal(AutoRerollStopReason.ConditionMet, result.Reason);
            Assert.Equal(3, result.AttemptsCommitted);
            Assert.Equal(3, attempts.Requested);
            Assert.Equal(AffixRarity.Legendary, result.FinalRarity);
            Assert.Equal("flat_hp", result.FinalAffixId);
        }

        /// <summary>
        /// THE DEFECT. An attempt that rolled the target and then rolled back
        /// has given the player nothing, so it cannot end the run as a success.
        /// </summary>
        [Fact]
        public async Task DoesNotStopOnARollbackThatWouldHaveSatisfiedTheCondition()
        {
            var attempts = new ScriptedAttempts(
                RerollAttemptOutcome.Rejected(failureCode: 7));

            var result = await AutoRerollRunner.RunAsync(
                StopAt(AffixRarity.Legendary, "flat_hp"), 50, attempts.NextAsync);

            Assert.NotEqual(AutoRerollStopReason.ConditionMet, result.Reason);
            Assert.Equal(AutoRerollStopReason.BudgetExhausted, result.Reason);
            Assert.Equal(0, result.AttemptsCommitted);
            Assert.Equal(0L, result.GoldSpent);
        }

        /// <summary>
        /// "I would only be paying for 5 rerolls, because I only did 5/50."
        /// The charge is already per attempt performed - this pins it, and pins
        /// that the run REPORTS the figure so the player can see it.
        /// </summary>
        [Fact]
        public async Task ChargesAndReportsOnlyTheAttemptsItPerformed()
        {
            var attempts = new ScriptedAttempts(
                RerollAttemptOutcome.Committed(AffixRarity.Common, "flat_hp", 5000L),
                RerollAttemptOutcome.Committed(AffixRarity.Rare, "flat_hp", 5000L),
                RerollAttemptOutcome.Committed(AffixRarity.Uncommon, "flat_hp", 5000L),
                RerollAttemptOutcome.Committed(AffixRarity.Epic, "flat_hp", 5000L),
                RerollAttemptOutcome.Committed(AffixRarity.Legendary, "flat_hp", 5000L));

            var result = await AutoRerollRunner.RunAsync(
                StopAt(AffixRarity.Legendary, "flat_hp"), 50, attempts.NextAsync);

            Assert.Equal(AutoRerollStopReason.ConditionMet, result.Reason);
            Assert.Equal(5, result.AttemptsCommitted);
            Assert.Equal(25_000L, result.GoldSpent);
            _output.WriteLine($"stopped after {result.AttemptsCommitted} of 50 for {result.GoldSpent}g");
        }

        /// <summary>
        /// "If I have set legendary + flat hp, I need it to only stop at
        /// legendary hp stat, not anything else." A Legendary of the wrong stat
        /// is not the condition, however tempting it looks.
        /// </summary>
        [Fact]
        public async Task ALegendaryOfTheWrongStatDoesNotSatisfyAStatConstraint()
        {
            var attempts = new ScriptedAttempts(
                RerollAttemptOutcome.Committed(AffixRarity.Legendary, "crit_chance_pct", 1000L),
                RerollAttemptOutcome.Committed(AffixRarity.Legendary, "lifesteal_pct", 1000L),
                RerollAttemptOutcome.Committed(AffixRarity.Legendary, "flat_armor", 1000L));

            var result = await AutoRerollRunner.RunAsync(
                StopAt(AffixRarity.Legendary, "flat_hp"), 3, attempts.NextAsync);

            Assert.Equal(AutoRerollStopReason.AttemptLimitReached, result.Reason);
            Assert.Equal(3, result.AttemptsCommitted);
        }

        /// <summary>
        /// "If I have set it to stop on epic or higher + crit chance I need it to
        /// stop at epic/legendary crit chance affix." The rarity is a FLOOR, so
        /// a Legendary satisfies an Epic request.
        /// </summary>
        [Fact]
        public async Task ARarityFloorIsSatisfiedByAnythingAboveIt()
        {
            var attempts = new ScriptedAttempts(
                RerollAttemptOutcome.Committed(AffixRarity.Rare, "crit_chance_pct", 1000L),
                RerollAttemptOutcome.Committed(AffixRarity.Legendary, "crit_chance_pct", 1000L));

            var result = await AutoRerollRunner.RunAsync(
                StopAt(AffixRarity.Epic, "crit_chance_pct"), 50, attempts.NextAsync);

            Assert.Equal(AutoRerollStopReason.ConditionMet, result.Reason);
            Assert.Equal(2, result.AttemptsCommitted);
            Assert.Equal(AffixRarity.Legendary, result.FinalRarity);
        }

        [Fact]
        public async Task StopsAtTheExactRarityWhenThatIsWhatLands()
        {
            var attempts = new ScriptedAttempts(
                RerollAttemptOutcome.Committed(AffixRarity.Epic, "crit_chance_pct", 1000L));

            var result = await AutoRerollRunner.RunAsync(
                StopAt(AffixRarity.Epic, "crit_chance_pct"), 50, attempts.NextAsync);

            Assert.Equal(AutoRerollStopReason.ConditionMet, result.Reason);
            Assert.Equal(1, result.AttemptsCommitted);
        }

        /// <summary>
        /// The client's attempt count is a request, never a bound - a run of
        /// Serializable transactions is a self-inflicted denial of service.
        /// </summary>
        [Fact]
        public async Task NeverRunsMoreAttemptsThanThePlannerAllows()
        {
            var attempts = new ScriptedAttempts();

            var result = await AutoRerollRunner.RunAsync(
                StopAt(AffixRarity.Legendary, "flat_hp"), 10_000, attempts.NextAsync);

            Assert.Equal(AutoRerollStopReason.AttemptLimitReached, result.Reason);
            Assert.Equal(AutoRerollPlanner.MaxAttemptsPerRequest, attempts.Requested);
        }

        /// <summary>
        /// A rollback halfway through keeps what was already paid for and stops
        /// rather than hammering the same failing transaction to the limit.
        /// </summary>
        [Fact]
        public async Task ARollbackMidRunEndsTheRunAndKeepsWhatWasAlreadySpent()
        {
            var attempts = new ScriptedAttempts(
                RerollAttemptOutcome.Committed(AffixRarity.Common, "flat_hp", 2000L),
                RerollAttemptOutcome.Committed(AffixRarity.Rare, "flat_hp", 2000L),
                RerollAttemptOutcome.Rejected(failureCode: 7));

            var result = await AutoRerollRunner.RunAsync(
                StopAt(AffixRarity.Legendary, "flat_hp"), 50, attempts.NextAsync);

            Assert.Equal(AutoRerollStopReason.BudgetExhausted, result.Reason);
            Assert.Equal(2, result.AttemptsCommitted);
            Assert.Equal(4000L, result.GoldSpent);
            Assert.Equal(3, attempts.Requested);
        }
    }
}
