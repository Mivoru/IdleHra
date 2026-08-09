using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// What fills the chrono bank, and whether it can be switched off.
    ///
    /// Both halves were broken and neither showed up as an error anywhere. The
    /// bank's only source was a consumable that is not in the catalogue, so it
    /// was zero for every player who has ever played and both of its buttons
    /// sat permanently disabled - while the screen told players it "fills on
    /// its own". And acceleration could be started from the Boosts screen but
    /// not stopped from it, because a request to stop was treated as tampering.
    /// </summary>
    public class ChronoGrantTests
    {
        private readonly ITestOutputHelper _output;

        public ChronoGrantTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void EveryGrantIsWorthRealPlayTime()
        {
            // The drain is (multiplier - 1) seconds of bank per real second, so
            // at 2x an hour of bank is an hour of doubled play. A grant that
            // bought a few minutes would not be worth the button.
            Assert.True(ChronoGrantRules.LoginStreakDaySevenSeconds >= 1800.0);
            Assert.True(ChronoGrantRules.SealSeconds >= 3600.0);
            Assert.True(ChronoGrantRules.FirstBossClearSeconds >= 3600.0);
        }

        /// <summary>
        /// A whole season's worth must not overflow the cap on its own - if it
        /// did, the last rewards of a season would silently pay nothing, which
        /// is the same shape of bug as an unreachable achievement threshold.
        /// </summary>
        [Fact]
        public void AWholeSeasonOfRewardsFitsUnderTheCap()
        {
            // Thirteen weeks of streaks, all five Seals, all five bosses.
            double total = ChronoGrantRules.MaximumSeasonalGrant(loginStreakSevens: 13, seals: 5, bosses: 5);
            double hours = total / 3600.0;

            _output.WriteLine($"a full season pays {hours:0.0} hours of banked time");

            Assert.True(total < ChronoBufferEngine.MaxBankedChronoSeconds,
                $"a season pays {hours:0.0}h against a {ChronoBufferEngine.MaxBankedChronoSeconds / 3600}h cap");
        }

        [Fact]
        public void TheCapHoldsAndNothingNegativeIsAdded()
        {
            Assert.Equal(
                ChronoBufferEngine.MaxBankedChronoSeconds,
                ChronoGrantRules.AddCapped(ChronoBufferEngine.MaxBankedChronoSeconds - 10.0, 999_999.0));

            Assert.Equal(100.0, ChronoGrantRules.AddCapped(100.0, 0.0));
            Assert.Equal(100.0, ChronoGrantRules.AddCapped(100.0, -50.0));
            Assert.Equal(3700.0, ChronoGrantRules.AddCapped(100.0, 3600.0));
        }

        // --- the off switch ----------------------------------------------------

        private static TickStatePayload PayloadWithBank(double bankedSeconds)
        {
            var payload = default(TickStatePayload);
            payload.PlayerId = 1L;
            payload.BankedChronoSeconds = bankedSeconds;
            return payload;
        }

        private static ClientCommandPacket BoostPacket(double multiplier)
        {
            return new ClientCommandPacket
            {
                Command = CommandType.ActivateChronoBoost,
                RequestedSpeedMultiplier = multiplier,
                LogicEpochCounter = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }

        /// <summary>
        /// STOPPING MUST NOT BE A DISCONNECT. A false from this validator is
        /// TerminateSessionForSecurity, so before this fix a client asking to
        /// turn its own boost off was kicked as an attacker - which is why the
        /// only working off-switch lived on a different screen entirely.
        /// </summary>
        [Fact]
        public void AskingToStopIsAllowed()
        {
            var payload = PayloadWithBank(3600.0);
            var packet = BoostPacket(1.0);

            Assert.True(ClientCommandValidator.ValidateChronoManipulation(ref payload, ref packet, 3600U));
        }

        /// <summary>
        /// And it is allowed with an EMPTY bank, which is precisely when a
        /// player most needs the stop to land - the bank running dry is the
        /// common way a boost ends.
        /// </summary>
        [Fact]
        public void StoppingWorksEvenWithNothingBanked()
        {
            var payload = PayloadWithBank(0.0);
            var packet = BoostPacket(1.0);

            Assert.True(ClientCommandValidator.ValidateChronoManipulation(ref payload, ref packet, 0U));
        }

        [Fact]
        public void StartingStillNeedsBankedTimeAndALegalMultiplier()
        {
            var empty = PayloadWithBank(0.0);
            var start = BoostPacket(2.0);
            Assert.False(ClientCommandValidator.ValidateChronoManipulation(ref empty, ref start, 0U));

            var funded = PayloadWithBank(3600.0);
            var legal = BoostPacket(2.0);
            Assert.True(ClientCommandValidator.ValidateChronoManipulation(ref funded, ref legal, 3600U));

            // 3x has never existed and must still be refused.
            var illegal = BoostPacket(3.0);
            Assert.False(ClientCommandValidator.ValidateChronoManipulation(ref funded, ref illegal, 3600U));
        }
    }
}
