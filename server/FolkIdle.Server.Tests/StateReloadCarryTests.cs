using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE RELOAD WIPED THE ANSWER TO THE COMMAND THAT CAUSED IT.
    ///
    /// Every engine that changes the database out-of-band - reroll, fusion, the
    /// market, the village, crafting - suspends the player, does its work, and
    /// finishes by enqueuing ReloadState so the live payload picks the change
    /// up. ReloadState loads a FRESH payload with StateCheckpointManager
    /// .LoadPlayerState and the drain replaces the live one with it wholesale.
    ///
    /// The command result ring (CommandResultSlot0-3) and its tick counter are
    /// runtime-only: they exist on the payload and in no table. So the sequence
    /// was
    ///
    ///   1. the engine enqueues its result, the tick writes it into the ring;
    ///   2. the player is suspended, so no broadcast carries the ring;
    ///   3. the reload lands and replaces the payload - ring zeroed, counter 0;
    ///   4. broadcasts resume, carrying nothing.
    ///
    /// Measured in a browser on the dev fixture: a single reroll, a
    /// fifty-attempt auto-reroll run, and a deliberately impossible stop
    /// condition all produced NO toast whatsoever. The affix changed and the
    /// gold was deducted, so the command worked; the only thing missing was
    /// every word the server said about it.
    ///
    /// That is this codebase's signature defect - the output side was never
    /// wired - sitting underneath the complaint that a reroll pops too many
    /// notifications. Both are true: too many while the run was in flight on
    /// some timings, none at all on these.
    ///
    /// The counter also has to carry, not just the slots. The client's
    /// CommandResultFeed keeps a high-water mark of ticks it has shown and
    /// filters anything at or below it, so a counter that restarts at zero makes
    /// every later result in the session invisible even once the slots survive.
    /// </summary>
    public class StateReloadCarryTests
    {
        private static TickStatePayload LivePayloadWithAResult()
        {
            return new TickStatePayload
            {
                PlayerId = 4711L,
                CommandResultTickCounter = 9,
                CommandResultSlot0 = new CommandResultEntry { ResultCode = 23, ResultTick = 9 },
                CommandResultSlot1 = new CommandResultEntry { ResultCode = 5, ResultTick = 8 },
                CommandResultRingWriteIndex = 1,
                CurrentGold = 1_000L
            };
        }

        [Fact]
        public void AReloadKeepsTheResultOfTheCommandThatTriggeredIt()
        {
            var live = LivePayloadWithAResult();

            // What LoadPlayerState returns: everything persisted, nothing
            // runtime-only.
            var reloaded = new TickStatePayload { PlayerId = 4711L, CurrentGold = 900L };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(23, reloaded.CommandResultSlot0.ResultCode);
            Assert.Equal(9u, reloaded.CommandResultSlot0.ResultTick);
            Assert.Equal(5, reloaded.CommandResultSlot1.ResultCode);
            Assert.Equal(1, reloaded.CommandResultRingWriteIndex);
        }

        [Fact]
        public void TheResultTickCounterCarriesToo()
        {
            var live = LivePayloadWithAResult();
            var reloaded = new TickStatePayload { PlayerId = 4711L };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            // A counter that restarts at 0 puts every later result below the
            // client's watermark, which is indistinguishable from the server
            // going quiet.
            Assert.Equal(9u, reloaded.CommandResultTickCounter);
        }

        /// <summary>
        /// The whole point of a reload is that the database is now the truth
        /// about everything it stores. Carrying the ring must not drag a stale
        /// balance back with it.
        /// </summary>
        [Fact]
        public void TheReloadedValuesStillWinForEverythingTheDatabaseHolds()
        {
            var live = LivePayloadWithAResult();
            var reloaded = new TickStatePayload { PlayerId = 4711L, CurrentGold = 900L };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(900L, reloaded.CurrentGold);
        }
    }
}
