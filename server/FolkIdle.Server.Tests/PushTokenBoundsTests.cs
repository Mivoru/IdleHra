using System.Text;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A DEVICE TOKEN IS NOT 64 BYTES, AND THE ONLY PATH THAT COULD STORE ONE
    /// INSISTED THAT IT WAS.
    ///
    /// Modul: `ClientCommandPacket.DeviceTokenBytes` is `fixed byte[64]`, and
    /// `RegisterDeviceAsync` refused anything whose length was not exactly that.
    /// The width of a wire field is not the width of a device token:
    ///
    ///   APNS, as the hex string Capacitor hands over : 64 characters
    ///   FCM registration token                       : ~160 characters
    ///
    /// So Android push could never have worked through opcode 33, and iOS push
    /// fitted with nothing to spare. No client had ever sent one, which is why
    /// this went unnoticed for as long as the feature has existed - the same
    /// shape as the purchase receipt, which went to REST for exactly this
    /// reason and says so in its own comment.
    ///
    /// These pin the bounds rather than the format, because the format is
    /// Google's and Apple's to change.
    /// </summary>
    public class PushTokenBoundsTests
    {
        private readonly ITestOutputHelper _output;
        public PushTokenBoundsTests(ITestOutputHelper output) => _output = output;

        private static bool WouldBeStored(string token)
        {
            int bytes = Encoding.UTF8.GetByteCount(token);
            return bytes >= PushNotificationTriggerEngine.MinDeviceTokenBytes
                   && bytes <= PushNotificationTriggerEngine.MaxDeviceTokenBytes;
        }

        [Fact]
        public void AnFcmTokenFits()
        {
            // A representative FCM registration token length. The point is that
            // it is far past 64 - the exact figure moves.
            string fcm = new string('f', 163);

            _output.WriteLine(
                $"FCM token {fcm.Length} bytes against bounds " +
                $"{PushNotificationTriggerEngine.MinDeviceTokenBytes}-{PushNotificationTriggerEngine.MaxDeviceTokenBytes}");

            Assert.True(fcm.Length > 64, "this test is pointless if the sample fits the old field");
            Assert.True(WouldBeStored(fcm));
        }

        [Fact]
        public void AnApnsTokenFits()
        {
            Assert.True(WouldBeStored(new string('a', 64)));
        }

        [Fact]
        public void ObviousRubbishIsRefused()
        {
            // Modul: the ceiling exists to refuse a body that is plainly not a
            // token, NOT to predict a format - Google has lengthened FCM tokens
            // before without warning, so the bound is deliberately generous.
            Assert.False(WouldBeStored(""));
            Assert.False(WouldBeStored("short"));
            Assert.False(WouldBeStored(new string('x', PushNotificationTriggerEngine.MaxDeviceTokenBytes + 1)));
        }

        [Fact]
        public void TheBoundsLeaveRoomForAFormatChange()
        {
            Assert.True(PushNotificationTriggerEngine.MaxDeviceTokenBytes >= 256,
                "a ceiling that only just clears today's FCM token will fail the day it grows");
            Assert.True(PushNotificationTriggerEngine.MinDeviceTokenBytes >= 16,
                "a floor low enough to accept a stray word is not a floor");
        }
    }
}
