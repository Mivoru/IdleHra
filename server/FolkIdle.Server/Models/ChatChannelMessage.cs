using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: world and guild chat are written down, task 110e (2026-10-02).
    //
    // Private messages became durable on 2026-09-01 (ConversationMessage), but
    // the two broadcast channels stayed Redis fan-out to whoever was connected.
    // A player who signed in a minute after a conversation saw "Nothing in this
    // channel yet" on a channel that was busy - the audit's evidence item 7.
    //
    // One row per message, written ONCE on the pod the sender is connected to
    // (ChatEngine.Publish*Async), never in the per-pod Redis subscription
    // handlers - those run on every pod, and writing there would store each
    // message once per pod. The live packet is unchanged; this is read over
    // REST at sign-in (GET /api/v1/chat/recent), like conversations.
    //
    // Whispers are NOT stored here. They already have their own table, with a
    // sorted pair key and read state this one does not need.
    [Table("chat_channel_messages")]
    public class ChatChannelMessage
    {
        [Key]
        public long Id { get; set; }

        // ChatEngine's channel byte: 0 global, 1 guild, 3 announcement.
        public byte ChannelType { get; set; }

        // 0 for every channel except guild. Read from the sender's SERVER-side
        // session, never from the client, exactly like live guild routing.
        public long GuildId { get; set; }

        // 0 = system-authored (an announcement), matching the live packet.
        public long SenderPlayerId { get; set; }

        // Announcements are built server-side and can be longer than the 128
        // bytes a player may send; the live packet truncates them to 128 UTF-8
        // bytes and so does the write, so history replays what was delivered.
        [MaxLength(128)]
        public string MessageText { get; set; } = string.Empty;

        // The SAME timestamp the live packet carried. The client deduplicates a
        // history row against a live arrival on (channel, sender, time, text),
        // so a second clock read here would make every message appear twice.
        public long SentAtEpochMs { get; set; }
    }
}
