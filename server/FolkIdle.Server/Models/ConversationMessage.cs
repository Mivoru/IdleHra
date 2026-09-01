using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: private messages become a CONVERSATION, 2026-09-01.
    //
    // Before this, chat was not persisted anywhere at all. Every channel was
    // Redis Pub/Sub fan-out to whoever happened to be connected, and the client
    // kept the last 200 lines in a plain Svelte store that a page reload wiped.
    // Two consequences, and the second is a defect rather than a missing
    // feature:
    //
    //   - There was no history. Reload the page and the conversation was gone.
    //   - A whisper to an OFFLINE player was silently DROPPED. The dispatch
    //     looked the recipient up in the connected-client map and returned if
    //     they were absent, so the sender saw their own message sent and the
    //     recipient never learned it existed.
    //
    // A row here is the durable record; the WebSocket packet remains the LIVE
    // delivery and is unchanged. That split is deliberate - history is read
    // over REST like the friends list and the mailbox, so none of this touches
    // the wire, where every packet is demultiplexed by exact byte size and the
    // state packet has about one byte of headroom left.
    [Table("conversation_messages")]
    public class ConversationMessage
    {
        [Key]
        public long Id { get; set; }

        // Modul: THE PAIR IS STORED SORTED - Low is always the smaller player
        // id - so one thread has one key regardless of who spoke. Sender and
        // recipient are kept separately below because the direction still
        // matters for display and for unread counting; these two exist purely
        // so a thread can be found with one indexed lookup instead of an OR of
        // two directed comparisons, which is what PlayerRelationship's
        // directed-edge shape would otherwise force on every history read.
        public long LowPlayerId { get; set; }

        public long HighPlayerId { get; set; }

        public long SenderPlayerId { get; set; }

        public long RecipientPlayerId { get; set; }

        // Capped to the same 128 UTF-8 bytes the live packet carries
        // (RequestChatMessagePacket.MessageCapacity). Storing more than the
        // wire can deliver would create history that cannot be replayed.
        [MaxLength(128)]
        public string MessageText { get; set; } = string.Empty;

        public long SentAtEpochMs { get; set; }

        // Null until the RECIPIENT opens the thread. Only ever set on rows the
        // reader did not send - a message cannot be unread by its own author.
        public long? ReadAtEpochMs { get; set; }

        /// <summary>
        /// The sorted pair for a conversation between two players, in the order
        /// this table stores them. Call it rather than sorting by hand at each
        /// site: getting the order wrong writes a second thread for the same
        /// two people, and nothing would report that as an error.
        /// </summary>
        public static (long Low, long High) PairKey(long playerA, long playerB)
            => playerA <= playerB ? (playerA, playerB) : (playerB, playerA);
    }
}
