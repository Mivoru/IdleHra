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
    /// long, and when I reroll it spams the chat." Both halves are pure, so
    /// both are tested here without a fixture; the end-to-end form of the
    /// auto-reroll rule is in AutoRerollRunReportTests.
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

            string longestAffix = string.Empty;
            foreach (var definition in AffixRegistry.Definitions)
            {
                if (definition.Id.Length > longestAffix.Length) longestAffix = definition.Id;
            }

            string[] lines =
            {
                AnnouncementText.Drop(LongestName, RarityTier.GetName(RarityTier.Transcendent), longestItem),
                AnnouncementText.Reroll(LongestName, AffixRarity.Legendary, longestAffix),
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

        [Fact]
        public void TheRerollLineNamesTheAffixAndNotItsRawMagnitude()
        {
            Assert.Equal("Mivoru rerolled Legendary crit dmg%",
                AnnouncementText.Reroll("Mivoru", AffixRarity.Legendary, "crit_dmg_pct"));
            Assert.Equal("Mivoru rerolled Legendary hp",
                AnnouncementText.Reroll("Mivoru", AffixRarity.Legendary, "flat_hp"));
        }

        // ---------- the reroll rule ----------
        //
        // Each test uses its own player id, because the claim table is static
        // and xunit runs test classes in parallel.

        [Fact]
        public void AnEpicRerollIsNoLongerWorldNews()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            Assert.False(RerollAnnouncementPolicy.TryClaim(880000001L, AffixRarity.Epic, now));
            Assert.False(RerollAnnouncementPolicy.TryClaim(880000001L, AffixRarity.Rare, now));
            Assert.True(RerollAnnouncementPolicy.TryClaim(880000001L, AffixRarity.Legendary, now));
        }

        [Fact]
        public void OnePlayerIsAnnouncedAtMostOncePerCooldown()
        {
            const long player = 880000002L;
            var start = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

            Assert.True(RerollAnnouncementPolicy.TryClaim(player, AffixRarity.Legendary, start));

            // A burst of Legendaries inside the window is one line, not many.
            Assert.False(RerollAnnouncementPolicy.TryClaim(player, AffixRarity.Legendary, start.AddSeconds(1)));
            Assert.False(RerollAnnouncementPolicy.TryClaim(player, AffixRarity.Legendary,
                start + RerollAnnouncementPolicy.PerPlayerCooldown - TimeSpan.FromSeconds(1)));

            // And the window reopens - the rule coalesces, it does not silence.
            Assert.True(RerollAnnouncementPolicy.TryClaim(player, AffixRarity.Legendary,
                start + RerollAnnouncementPolicy.PerPlayerCooldown));
        }

        [Fact]
        public void OnePlayersCooldownDoesNotSilenceAnother()
        {
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(RerollAnnouncementPolicy.TryClaim(880000003L, AffixRarity.Legendary, now));
            Assert.True(RerollAnnouncementPolicy.TryClaim(880000004L, AffixRarity.Legendary, now));
        }
    }
}
