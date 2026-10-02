using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Domain.Social
{
    /// <summary>
    /// The durable half of world, guild and announcement chat (task 110e). The
    /// WebSocket still delivers every message live; this answers "what was said
    /// before I signed in", which the live path never could.
    /// </summary>
    public static class ChatHistory
    {
        /// <summary>How many of the newest messages a sign-in gets per channel.</summary>
        public const int RecentPerChannel = 50;

        /// <summary>
        /// Rows older than this are deleted. History exists to show a player
        /// signing in what was just said, not to be an archive; the bound also
        /// keeps a table nothing else reads from growing for ever.
        /// </summary>
        public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

        // Prune on the first write after boot and then every Nth write. A
        // cron loop would be one more StartCron for CronWorkerGuardTests to
        // inventory, for a delete that needs no schedule.
        private const int PruneEveryNthWrite = 200;
        private static int _writesSincePrune = PruneEveryNthWrite - 1;

        /// <summary>
        /// Cut <paramref name="text"/> to at most <paramref name="maxBytes"/>
        /// UTF-8 bytes on a character boundary, which is how much the live
        /// packet carries.
        /// </summary>
        public static string TruncateToUtf8Bytes(string text, int maxBytes)
        {
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

            int bytes = 0;
            int i = 0;
            while (i < text.Length)
            {
                int width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
                int next = Encoding.UTF8.GetByteCount(text.AsSpan(i, width));
                if (bytes + next > maxBytes) break;
                bytes += next;
                i += width;
            }

            return text.Substring(0, i);
        }

        /// <summary>
        /// Store one message. Never throws: a message that is delivered but not
        /// recorded is a far smaller failure than chat going down because a
        /// write did (the same stance as ChatEngine's whisper persistence).
        /// </summary>
        public static async Task RecordAsync(
            IServiceProvider serviceProvider,
            byte channelType,
            long guildId,
            long senderPlayerId,
            string messageText,
            long sentAtEpochMs)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await RecordAsync(db, channelType, guildId, senderPlayerId, messageText, sentAtEpochMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chat history write failed for player {senderPlayerId}: {ex.Message}");
            }
        }

        public static async Task RecordAsync(
            FolkIdleDbContext db,
            byte channelType,
            long guildId,
            long senderPlayerId,
            string messageText,
            long sentAtEpochMs)
        {
            db.ChatChannelMessages.Add(new ChatChannelMessage
            {
                ChannelType = channelType,
                // Only the guild channel is scoped. Anything else carrying a
                // guild id would vanish from everyone's history but that guild's.
                GuildId = channelType == ChatEngine.GuildChannelType ? guildId : 0,
                SenderPlayerId = senderPlayerId,
                MessageText = TruncateToUtf8Bytes(messageText, Network.ResponseChatMessagePacket.MessageCapacity),
                SentAtEpochMs = sentAtEpochMs,
            });

            await db.SaveChangesAsync();

            if (Interlocked.Increment(ref _writesSincePrune) >= PruneEveryNthWrite)
            {
                Interlocked.Exchange(ref _writesSincePrune, 0);
                await PruneAsync(db, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }

        /// <summary>Delete everything older than <see cref="Retention"/>.</summary>
        public static Task<int> PruneAsync(FolkIdleDbContext db, long nowEpochMs)
        {
            long cutoff = nowEpochMs - (long)Retention.TotalMilliseconds;
            return db.ChatChannelMessages.Where(m => m.SentAtEpochMs < cutoff).ExecuteDeleteAsync();
        }

        public sealed class Entry
        {
            public long Id { get; set; }
            public byte ChannelType { get; set; }
            public long SenderPlayerId { get; set; }
            public string MessageText { get; set; } = string.Empty;
            public long SentAtEpochMs { get; set; }
        }

        /// <summary>
        /// The newest <see cref="RecentPerChannel"/> messages of each channel
        /// this player may read, oldest first within the result.
        /// </summary>
        /// <remarks>
        /// The filters are the live path's, applied to the reader: guild rows
        /// only for the guild the player is in NOW (from the database, not
        /// the request), and nothing from a player the reader has blocked -
        /// live dispatch skips a recipient who blocked the sender, so history
        /// must not show the reader what the socket would have withheld.
        /// Whispers are not here at all; they have their own endpoints.
        /// </remarks>
        public static async Task<List<Entry>> ReadRecentAsync(FolkIdleDbContext db, long readerPlayerId, int perChannel = RecentPerChannel)
        {
            long guildId = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == readerPlayerId)
                .Select(p => p.GuildId)
                .FirstOrDefaultAsync();

            var blocked = await db.PlayerRelationships.AsNoTracking()
                .Where(r => r.PlayerId == readerPlayerId && r.RelationType == RelationType.Blocked)
                .Select(r => r.TargetPlayerId)
                .ToListAsync();

            var result = new List<Entry>();

            result.AddRange(await NewestAsync(db, ChatEngine.GlobalChannelType, 0, blocked, perChannel));
            result.AddRange(await NewestAsync(db, ChatEngine.AnnouncementChannelType, 0, blocked, perChannel));
            if (guildId > 0)
            {
                result.AddRange(await NewestAsync(db, ChatEngine.GuildChannelType, guildId, blocked, perChannel));
            }

            return result.OrderBy(e => e.SentAtEpochMs).ThenBy(e => e.Id).ToList();
        }

        private static Task<List<Entry>> NewestAsync(FolkIdleDbContext db, byte channelType, long guildId, List<long> blocked, int take)
        {
            return db.ChatChannelMessages.AsNoTracking()
                .Where(m => m.ChannelType == channelType && m.GuildId == guildId && !blocked.Contains(m.SenderPlayerId))
                .OrderByDescending(m => m.SentAtEpochMs)
                .ThenByDescending(m => m.Id)
                .Take(take)
                .Select(m => new Entry
                {
                    Id = m.Id,
                    ChannelType = m.ChannelType,
                    SenderPlayerId = m.SenderPlayerId,
                    MessageText = m.MessageText,
                    SentAtEpochMs = m.SentAtEpochMs,
                })
                .ToListAsync();
        }
    }
}
