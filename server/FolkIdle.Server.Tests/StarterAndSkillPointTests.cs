using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Three things a player is entitled to and was not getting.
    /// </summary>
    public class StarterAndSkillPointTests
    {
        private readonly ITestOutputHelper _output;

        public StarterAndSkillPointTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        /// <summary>
        /// Reported as "I am level 20 and have no points to spend".
        ///
        /// The game grows levels in THREE places - an ordinary kill, the
        /// warp/bulk catch-up, and the offline projection - and only the warp
        /// path paid the skill point out. Ordinary play, which is the path
        /// almost every level in the game takes, earned nothing.
        /// </summary>
        [Fact]
        public void KillingThingsEarnsOneSkillPointPerLevel()
        {
            var payload = new TickStatePayload
            {
                CurrentLevel = 1,
                CurrentXp = 0,
                SelectedLineageId = 1,
                AvailableSkillPoints = 0,
            };

            // Enough XP in one go to cross several levels at once - the loop has
            // to pay for each of them, not just for the fact that it moved.
            long xpForFive = 0;
            for (int level = 1; level <= 5; level++)
            {
                xpForFive += ProgressionEngine.GetRequiredXpForLevel(level);
            }

            ProgressionEngine.ProcessMonsterDeath(ref payload, (int)xpForFive, 100, 0, 0);

            _output.WriteLine($"level {payload.CurrentLevel}, {payload.AvailableSkillPoints} points");
            Assert.True(payload.CurrentLevel > 1, "the fixture must actually have levelled");
            Assert.Equal(payload.CurrentLevel - 1, payload.AvailableSkillPoints);
        }

        /// <summary>
        /// A new account owns an axe, a pickaxe and a rod.
        ///
        /// Gathering resolves the tool that MATCHES the job, so an account
        /// holding none of them could not usefully do any of the three
        /// professions the game opens on - and the only route to a tool is
        /// crafting one out of the materials those professions produce.
        /// </summary>
        [Fact]
        public void EveryStarterToolExistsInTheCatalogue()
        {
            Assert.Equal(3, StarterEquipmentGrant.StarterToolBaseIds.Length);

            foreach (string baseId in StarterEquipmentGrant.StarterToolBaseIds)
            {
                // ItemDefinition carries no BaseId - the mapping lives the
                // other way round, so this walks ids and asks for each one's
                // base id, which is also the lookup the grant itself relies on.
                bool found = false;
                for (int itemId = 1; itemId <= ContentRegistry.ItemDefinitions.Length; itemId++)
                {
                    if (ContentRegistry.GetItemBaseId(itemId) == baseId) { found = true; break; }
                }
                Assert.True(found, $"{baseId} must exist in items.json");
                _output.WriteLine($"{baseId} -> tier {ContentRegistry.GetToolTier(baseId)}");
            }

            // One per profession: woodcutting, mining, fishing. Two axes and no
            // rod would pass a count check and leave fishing unreachable.
            Assert.Contains("normal_axe_tool", StarterEquipmentGrant.StarterToolBaseIds);
            Assert.Contains("normal_pickaxe_tool", StarterEquipmentGrant.StarterToolBaseIds);
            Assert.Contains("normal_fishing_rod_tool", StarterEquipmentGrant.StarterToolBaseIds);
        }

        /// <summary>
        /// Mastery must not be able to pin gathering to its floor.
        ///
        /// It used to SUBTRACT two ticks per level before any multiplier
        /// applied, so region 1's 30-tick node went negative at mastery 15 and
        /// clamped to the two-tick minimum. That is what put "0.2s / unit
        /// (floor)" on the gathering screen: the first two regions gathered
        /// instantly, and no tool, village building or affix could move a
        /// number already pinned to the bottom.
        /// </summary>
        [Fact]
        public void MasteryAcceleratesGatheringWithoutPinningItToTheFloor()
        {
            const int regionOneThreshold = 30;

            int bare = GatheringToolEngine.ComputeRequiredTicks(regionOneThreshold, 0, 0, 0, 0);
            Assert.Equal(regionOneThreshold, bare);

            int previous = bare;
            for (int mastery = 1; mastery <= 40; mastery++)
            {
                int ticks = GatheringToolEngine.ComputeRequiredTicks(regionOneThreshold, mastery, 0, 0, 0);
                Assert.True(ticks <= previous, $"mastery {mastery} must not be slower than {mastery - 1}");
                previous = ticks;
            }

            // The point of the change: a maxed-out gatherer on the FIRST node in
            // the game is fast, not instant, so the tool curve still has room to
            // matter there.
            int masteredWithGoodTool = GatheringToolEngine.ComputeRequiredTicks(regionOneThreshold, 15, 2, 0, 0);
            _output.WriteLine($"region 1 node at mastery 15 with a tier-2 axe: {masteredWithGoodTool} ticks");
            Assert.True(
                masteredWithGoodTool > GatheringToolEngine.MinRequiredTicks,
                "a mid-mastery player on region 1 should not already be at the floor");
        }
    }
}
