using System.Text.Json;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A DATA-ONLY PUSH MESSAGE IS A NOTIFICATION NOBODY EVER SEES.
    ///
    /// Modul: what this engine sent for as long as it existed was an FCM
    /// message carrying `data` and nothing else. FCM delivers that to a RUNNING
    /// app and drops it on the floor of a backgrounded one - so the single
    /// moment the whole feature exists for, a player who is not looking at the
    /// game, arrived as silence. There is nothing to display and nothing to
    /// tap.
    ///
    /// It is invisible from inside the server: the send succeeds, FCM answers
    /// 200, the log says nothing. The only place to catch it is between the
    /// trigger and the wire, which is why BuildFcmMessageJson is a pure
    /// function.
    ///
    /// These assert the SHAPE and the FORWARDING, never particular wording -
    /// the copy is a product decision and should be free to change without a
    /// red test.
    /// </summary>
    public class PushMessageShapeTests
    {
        private readonly ITestOutputHelper _output;
        public PushMessageShapeTests(ITestOutputHelper output) => _output = output;

        /// <summary>Every payload code the server actually schedules today.</summary>
        public static TheoryData<string> ScheduledPayloadCodes() => new()
        {
            // LiveOpsTickEngine, the only scheduler there is.
            "world_boss_window_open",
            "daily_quest_reset",
        };

        private static JsonElement Message(string payloadCode)
        {
            string json = PushNotificationTriggerEngine.BuildFcmMessageJson(
                new PushNotificationTriggerEngine.OutboundPushRequest
                {
                    PlayerId = 4242,
                    DeviceToken = new string('f', 163),
                    PlatformFamily = 1,
                    TriggerType = 2,
                    PayloadCode = payloadCode
                });

            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("message").Clone();
        }

        [Theory]
        [MemberData(nameof(ScheduledPayloadCodes))]
        public void EveryScheduledTriggerCarriesSomethingToDisplay(string payloadCode)
        {
            var message = Message(payloadCode);

            Assert.True(message.TryGetProperty("notification", out var notification),
                $"'{payloadCode}' sends no notification block, so a backgrounded phone shows nothing");

            string title = notification.GetProperty("title").GetString() ?? string.Empty;
            string body = notification.GetProperty("body").GetString() ?? string.Empty;

            _output.WriteLine($"{payloadCode,-24} -> \"{title}\" / \"{body}\"");

            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(body));
        }

        [Theory]
        [MemberData(nameof(ScheduledPayloadCodes))]
        public void EveryScheduledTriggerNamesAScreenForTheTapToOpen(string payloadCode)
        {
            var data = Message(payloadCode).GetProperty("data");

            string screen = data.GetProperty("screen").GetString() ?? string.Empty;
            _output.WriteLine($"{payloadCode,-24} -> screen '{screen}'");

            // Modul: the destination travels on the message rather than being
            // derived client-side, so that this table is not written down twice
            // in two languages. push.ts forwards whatever lands here.
            Assert.False(string.IsNullOrWhiteSpace(screen));

            // The fallback exists for codes nobody has taught the table about;
            // a code that IS scheduled reaching it means somebody added a
            // trigger and never gave it a destination.
            Assert.NotEqual(PushNotificationTriggerEngine.DescribeTrigger("a code no one will ever add").Screen, screen);
        }

        [Fact]
        public void AnUnknownCodeStillOpensSomething()
        {
            // Better a notification that opens the map than one that opens a
            // blank screen - and better than a crash on a bridge.
            var copy = PushNotificationTriggerEngine.DescribeTrigger("something_added_later");

            Assert.False(string.IsNullOrWhiteSpace(copy.Title));
            Assert.False(string.IsNullOrWhiteSpace(copy.Body));
            Assert.False(string.IsNullOrWhiteSpace(copy.Screen));
        }

        [Fact]
        public void TheRoutingDataStillRidesAlongsideTheNotification()
        {
            // The notification block is an addition, not a replacement: the
            // client still reads trigger_type/payload/player_id.
            var data = Message("world_boss_window_open").GetProperty("data");

            Assert.Equal("2", data.GetProperty("trigger_type").GetString());
            Assert.Equal("world_boss_window_open", data.GetProperty("payload").GetString());
            Assert.Equal("4242", data.GetProperty("player_id").GetString());
        }

        [Fact]
        public void ThePhoneIsWokenRatherThanToldWheneverItNextLooks()
        {
            // An idle game's notification is worth nothing an hour late - the
            // boss window it announces may have closed by then.
            var message = Message("world_boss_window_open");

            Assert.Equal("high", message.GetProperty("android").GetProperty("priority").GetString());
        }

        [Fact]
        public void TheTokenIsTheOneTheDeviceHandedOver()
        {
            var message = Message("daily_quest_reset");

            // 163 characters: an FCM registration token, which is what made the
            // 64-byte wire field impossible. See PushTokenBoundsTests.
            Assert.Equal(163, (message.GetProperty("token").GetString() ?? string.Empty).Length);
        }
    }
}
