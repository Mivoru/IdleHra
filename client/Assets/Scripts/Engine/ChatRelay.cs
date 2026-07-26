using System;
using UnityEngine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Engine
{
    // Modul: UI rework. Single fan-out point for the inbound chat stream.
    //
    // WebSocketClient.ChatMessageQueue is a ConcurrentQueue - whoever calls
    // TryDequeue first CONSUMES the packet. That was fine while exactly one
    // UiChatWindow existed and drained it in its own Update, but the chat
    // rework splits chat into three separate windows (World on the map hub,
    // Guild inside the Guild screen, Whispers inside the Friends screen).
    // Three windows racing the same queue would each swallow a share of the
    // messages and each show a random subset. So the queue is drained
    // exactly once, here, and handed to every listener.
    //
    // Lives on the Managers root next to WebSocketClient/VisualSyncProxy, so
    // it keeps running regardless of which screen is currently active - a
    // guild message must still land in the guild log's history while the
    // player is looking at the map, exactly as it would in any real chat
    // client. UiChatWindow therefore subscribes in Awake/OnDestroy, NOT
    // OnEnable/OnDisable.
    public class ChatRelay : MonoBehaviour
    {
        public WebSocketClient NetworkClient;

        // channelType (0 Global / 1 Guild / 2 Whisper), senderPlayerId,
        // conversationPartnerId, timestampEpochMs, decoded message text.
        //
        // conversationPartnerId is meaningful for Whisper only, and is
        // always "the OTHER player in this thread" regardless of direction:
        // the sender for an inbound whisper, the recipient for one this
        // client just sent. That is what lets the whisper window show one
        // friend's thread at a time without having to reason about who
        // wrote which line. 0 for Global/Guild, which have no counterpart.
        public static event Action<byte, long, long, long, string> OnChatMessageReceived;

        // Modul: a locally-composed whisper never comes back from the
        // server - NetworkBroadcastSystem.HandleChatDispatchAsync's
        // DispatchModeWhisper branch sends to the recipient only, unlike
        // Global/Guild which fan out to every connected client including
        // the sender. Rather than special-casing that inside the whisper
        // window, the send path echoes through this same event so both
        // sides of a conversation reach the UI by one identical route.
        public static void PublishLocalEcho(byte channelType, long senderPlayerId, long conversationPartnerId, string messageText)
        {
            long timestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            OnChatMessageReceived?.Invoke(channelType, senderPlayerId, conversationPartnerId, timestampEpochMs, messageText);
        }

        private unsafe void Update()
        {
            if (NetworkClient == null) return;

            while (NetworkClient.ChatMessageQueue.TryDequeue(out ResponseChatMessagePacket packet))
            {
                int length = packet.MessageLength;
                if (length < 0 || length > ResponseChatMessagePacket.MessageCapacity)
                {
                    length = 0;
                }

                string messageText = System.Text.Encoding.UTF8.GetString(packet.MessageText, length);

                // Inbound whisper: the other party in the thread is by
                // definition whoever sent it (the server only ever routes a
                // whisper to its intended recipient).
                long conversationPartnerId = packet.ChannelType == (byte)ChatChannelType.Whisper ? packet.SenderPlayerId : 0L;

                OnChatMessageReceived?.Invoke(packet.ChannelType, packet.SenderPlayerId, conversationPartnerId, packet.TimestampEpochMs, messageText);
            }
        }
    }
}
