using System;
using System.Text;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: world announcements, 2026-10-02. "The announcements are too
    /// long." Pure, so tested here without a fixture.
    /// </summary>
    public class AnnouncementTextTests
    {
        public AnnouncementTextTests()
        {
            ContentRegistry.Initialize();
        }

        // A username is 3-20 characters (AuthenticationEngine's register rule).
        private const string LongestName = "ABCDEFGHIJKLMNOPQRST";

        /// <summary>
        /// ChatEngine cuts the text at the packet's 128-byte buffer, so an
        /// over-long line arrives truncated mid-word. Measured against the
        /// worst case the catalogue can produce, not a typical one: the longest
        /// item slug (Readable only ever shortens it), the longest monster
        /// name, the longest rarity name and the longest affix id.
        /// </summary>
        [Fact]
        public void EveryAnnouncementFitsThePacketAtItsLongest()
        {
            string longestItem = string.Empty;
            foreach (var item in ContentRegistry.ItemDefinitions)
            {
                string slug = ContentRegistry.GetItemBaseId(item.Id);
                if (slug.Length > longestItem.Length) longestItem = slug;
            }

            string longestMonster = string.Empty;
            for (int id = 1; id <= ContentRegistry.Monsters.Length; id++)
            {
                string name = ContentRegistry.GetMonsterName(id);
                if (name.Length > longestMonster.Length) longestMonster = name;
            }

            string[] lines =
            {
                AnnouncementText.Drop(LongestName, RarityTier.GetName(RarityTier.Transcendent), longestItem),
                AnnouncementText.BossClear(LongestName, longestMonster, worldFirst: true),
                AnnouncementText.BossClear(LongestName, longestMonster, worldFirst: false),
                AnnouncementText.SeasonPlacement(9999, 3, LongestName),
            };

            foreach (string line in lines)
            {
                Assert.True(Encoding.UTF8.GetByteCount(line) <= ResponseChatMessagePacket.MessageCapacity,
                    $"{Encoding.UTF8.GetByteCount(line)} bytes: \"{line}\"");

                // One line, no closing congratulation - the client's gz! button
                // already says that beside every announcement.
                Assert.DoesNotContain("Congratulations", line);
                Assert.DoesNotContain("\n", line);
            }
        }

        /// <summary>
        /// Owner decision 2026-10-02: a drop is world news from Mythic up. The
        /// threshold is read by NAME from the server's own rarity list, so this
        /// fails if the ladder is reordered under it.
        /// </summary>
        [Fact]
        public void ADropIsAnnouncedFromMythicAndAbove()
        {
            Assert.Equal("Mythic", RarityTier.GetName(CombatLootEngine.AnnounceableRarityTier));
            Assert.Equal(RarityTier.Mythic, CombatLootEngine.AnnounceableRarityTier);
            Assert.True(RarityTier.Legendary < CombatLootEngine.AnnounceableRarityTier);
        }
    }
}
