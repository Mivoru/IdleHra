using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 110e: world, guild and announcement chat are written down and read
    /// back at sign-in. A player who signed in a minute after a conversation
    /// used to see "Nothing in this channel yet".
    /// </summary>
    /// <remarks>
    /// The database is shared with every other test in the collection and the
    /// world channel is global by definition, so each test writes at its own
    /// FUTURE time window - its rows are then the newest in the channel and the
    /// 50-row cap cannot push them out behind somebody else's.
    /// </remarks>
    [Collection("Postgres collection")]
    public class ChatHistoryTests
    {
        private readonly PostgresTestFixture _fixture;
        private static long _windowCounter;

        public ChatHistoryTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        private static long FreshWindow()
            => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
               + TimeSpan.FromDays(365).Ticks / TimeSpan.TicksPerMillisecond
               + Interlocked.Increment(ref _windowCounter) * 10_000_000L;

        private async Task<long> CreatePlayerAsync(long guildId = 0)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"chat_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                GuildId = guildId,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            return player.Id;
        }

        private async Task RecordAsync(byte channel, long guildId, long sender, string text, long at)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await ChatHistory.RecordAsync(db, channel, guildId, sender, text, at);
        }

        private async Task<System.Collections.Generic.List<ChatHistory.Entry>> ReadAsync(long reader)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await ChatHistory.ReadRecentAsync(db, reader);
        }

        [Fact]
        public async Task World_ReturnsTheNewestFifty_OldestFirst()
        {
            long sender = await CreatePlayerAsync();
            long reader = await CreatePlayerAsync();
            long t0 = FreshWindow();

            for (int i = 0; i < 55; i++)
            {
                await RecordAsync(ChatEngine.GlobalChannelType, 0, sender, $"world line {i}", t0 + i);
            }

            var world = (await ReadAsync(reader)).Where(e => e.ChannelType == ChatEngine.GlobalChannelType).ToList();

            Assert.Equal(ChatHistory.RecentPerChannel, world.Count);
            // The five OLDEST fell off, and what is left reads top to bottom.
            Assert.Equal("world line 5", world.First().MessageText);
            Assert.Equal("world line 54", world.Last().MessageText);
            Assert.True(world.Zip(world.Skip(1)).All(p => p.First.SentAtEpochMs <= p.Second.SentAtEpochMs));
            Assert.All(world, e => Assert.Equal(sender, e.SenderPlayerId));
        }

        [Fact]
        public async Task Guild_IsReadOnlyByMembersOfThatGuild_AsTheDatabaseSaysNow()
        {
            long guildA = 9_100_000 + Interlocked.Increment(ref _windowCounter);
            long guildB = guildA + 500_000;

            long sender = await CreatePlayerAsync(guildA);
            long member = await CreatePlayerAsync(guildA);
            long outsider = await CreatePlayerAsync(guildB);
            long guildless = await CreatePlayerAsync(0);
            long t0 = FreshWindow();

            await RecordAsync(ChatEngine.GuildChannelType, guildA, sender, "guild A only", t0);

            Assert.Contains(await ReadAsync(member), e => e.MessageText == "guild A only" && e.ChannelType == ChatEngine.GuildChannelType);
            Assert.DoesNotContain(await ReadAsync(outsider), e => e.MessageText == "guild A only");
            Assert.DoesNotContain(await ReadAsync(guildless), e => e.MessageText == "guild A only");

            // Leaving the guild takes its history with it - the guild comes
            // from the reader's row, never from the request.
            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                var row = await db.PlayerRecords.SingleAsync(p => p.Id == member);
                row.GuildId = 0;
                await db.SaveChangesAsync();
            }

            Assert.DoesNotContain(await ReadAsync(member), e => e.MessageText == "guild A only");
        }

        [Fact]
        public async Task GuildIdOnANonGuildChannel_IsNotStored()
        {
            long sender = await CreatePlayerAsync(77);
            long reader = await CreatePlayerAsync(0);
            long t0 = FreshWindow();

            // A world message from someone who happens to be in a guild must
            // stay world-visible, not become guild-scoped by accident.
            await RecordAsync(ChatEngine.GlobalChannelType, 77, sender, "world from a guild member", t0);

            Assert.Contains(await ReadAsync(reader), e => e.MessageText == "world from a guild member");
        }

        [Fact]
        public async Task ABlockedSender_IsHiddenFromTheBlocker_AndOnlyFromTheBlocker()
        {
            long noisy = await CreatePlayerAsync();
            long blocker = await CreatePlayerAsync();
            long bystander = await CreatePlayerAsync();
            long t0 = FreshWindow();

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRelationships.Add(new PlayerRelationship { PlayerId = blocker, TargetPlayerId = noisy, RelationType = RelationType.Blocked });
                await db.SaveChangesAsync();
            }

            await RecordAsync(ChatEngine.GlobalChannelType, 0, noisy, "you blocked me", t0);

            Assert.DoesNotContain(await ReadAsync(blocker), e => e.MessageText == "you blocked me");
            Assert.Contains(await ReadAsync(bystander), e => e.MessageText == "you blocked me");
        }

        [Fact]
        public async Task Announcements_AreIncluded()
        {
            long reader = await CreatePlayerAsync();
            long t0 = FreshWindow();

            await RecordAsync(ChatEngine.AnnouncementChannelType, 0, 0, "Someone found a Mythic", t0);

            Assert.Contains(await ReadAsync(reader),
                e => e.ChannelType == ChatEngine.AnnouncementChannelType && e.SenderPlayerId == 0 && e.MessageText == "Someone found a Mythic");
        }

        [Fact]
        public async Task Prune_DeletesOnlyRowsPastRetention()
        {
            long sender = await CreatePlayerAsync();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long retentionMs = (long)ChatHistory.Retention.TotalMilliseconds;
            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);

            await RecordAsync(ChatEngine.GlobalChannelType, 0, sender, $"old {tag}", now - retentionMs - 60_000);
            await RecordAsync(ChatEngine.GlobalChannelType, 0, sender, $"fresh {tag}", now - 60_000);

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                await ChatHistory.PruneAsync(db, now);
            }

            await using var check = await _fixture.DbContextFactory.CreateDbContextAsync();
            Assert.False(await check.ChatChannelMessages.AnyAsync(m => m.MessageText == $"old {tag}"));
            Assert.True(await check.ChatChannelMessages.AnyAsync(m => m.MessageText == $"fresh {tag}"));
        }

        [Fact]
        public async Task PublishingThroughTheEngine_WritesOneRowWithThePacketsTimestamp()
        {
            long guildId = 9_900_000 + Interlocked.Increment(ref _windowCounter);
            long sender = await CreatePlayerAsync(guildId);
            string worldText = $"engine world {Guid.NewGuid():N}".Substring(0, 30);
            string guildText = $"engine guild {Guid.NewGuid():N}".Substring(0, 30);

            // The fixture runs a real Redis, so the live message goes out on the
            // pub/sub channel; listen there for the timestamp it carried.
            var redis = (StackExchange.Redis.IConnectionMultiplexer)_fixture.ServiceProvider.GetService(typeof(StackExchange.Redis.IConnectionMultiplexer))!;
            var published = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscriber = redis.GetSubscriber();
            var channel = StackExchange.Redis.RedisChannel.Literal(ChatEngine.GlobalChatChannel);
            await subscriber.SubscribeAsync(channel, (_, payload) =>
            {
                string[] parts = payload.ToString().Split(':', 3);
                if (parts.Length == 3 && parts[0] == sender.ToString() && parts[2] == worldText)
                {
                    published.TrySetResult(long.Parse(parts[1]));
                }
            });

            var engine = new ChatEngine(_fixture.ServiceProvider);
            Assert.True(await engine.PublishMessageAsync(sender, worldText));
            Assert.True(await engine.PublishGuildMessageAsync(sender, guildId, guildText));
            // A whisper has its own table and must not land in this one.
            long recipient = await CreatePlayerAsync();
            await engine.PublishWhisperMessageAsync(sender, recipient, worldText + " whisper");

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var rows = await db.ChatChannelMessages.Where(m => m.SenderPlayerId == sender).ToListAsync();

            Assert.Equal(2, rows.Count);
            var world = Assert.Single(rows, r => r.ChannelType == ChatEngine.GlobalChannelType);
            Assert.Equal(worldText, world.MessageText);
            Assert.Equal(0, world.GuildId);
            var guild = Assert.Single(rows, r => r.ChannelType == ChatEngine.GuildChannelType);
            Assert.Equal(guildId, guild.GuildId);

            // The live message carried the same instant the row does; the
            // client deduplicates history against live arrivals on it.
            long liveTimestamp = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(world.SentAtEpochMs, liveTimestamp);
            await subscriber.UnsubscribeAsync(channel);
        }

        [Fact]
        public void Truncation_CutsOnACharacterBoundary_AtThePacketsByteLimit()
        {
            string ascii = new string('a', 200);
            Assert.Equal(128, ChatHistory.TruncateToUtf8Bytes(ascii, 128).Length);

            // 'é' is two bytes: 64 of them fill 128 exactly, a 65th must go.
            string twoByte = new string('é', 65);
            string cut = ChatHistory.TruncateToUtf8Bytes(twoByte, 128);
            Assert.Equal(64, cut.Length);

            // A surrogate pair (4 bytes) is never split in half.
            string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 40));
            string emojiCut = ChatHistory.TruncateToUtf8Bytes(emoji, 128);
            Assert.Equal(64, emojiCut.Length);
            Assert.False(char.IsHighSurrogate(emojiCut[^1]));

            Assert.Equal("short", ChatHistory.TruncateToUtf8Bytes("short", 128));
        }
    }
}
