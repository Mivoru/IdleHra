using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Which tool serves which job, and how good it is.
    ///
    /// The gathering tick read CachedCurrentToolTier, which
    /// VillageManagementEngine set from the FORGE BUILDING LEVEL. So all ten of
    /// GatheringToolEngine's carefully-tiered tool families were driven by a
    /// building, crafting a Void Bark Axe changed nothing at all, and the axe
    /// in your hands sped up your fishing.
    /// </summary>
    public class ToolTierTests
    {
        public ToolTierTests()
        {
            ContentRegistry.Initialize();
        }

        [Theory]
        [InlineData("birch_axe_tool", ContentRegistry.ToolKindAxe, 1)]
        [InlineData("golden_birch_axe_tool", ContentRegistry.ToolKindAxe, 2)]
        [InlineData("willow_pickaxe_tool", ContentRegistry.ToolKindPickaxe, 3)]
        [InlineData("whisper_willow_pickaxe_tool", ContentRegistry.ToolKindPickaxe, 4)]
        [InlineData("acacia_fishing_rod_tool", ContentRegistry.ToolKindRod, 5)]
        [InlineData("ironwood_fishing_rod_tool", ContentRegistry.ToolKindRod, 6)]
        [InlineData("frostpine_axe_tool", ContentRegistry.ToolKindAxe, 7)]
        [InlineData("glacier_pine_axe_tool", ContentRegistry.ToolKindAxe, 8)]
        [InlineData("ebon_pickaxe_tool", ContentRegistry.ToolKindPickaxe, 9)]
        [InlineData("voidbark_fishing_rod_tool", ContentRegistry.ToolKindRod, 10)]
        public void EveryToolResolvesToItsKindAndTier(string baseId, int expectedKind, int expectedTier)
        {
            Assert.Equal(expectedKind, ContentRegistry.GetToolKind(baseId));
            Assert.Equal(expectedTier, ContentRegistry.GetToolTier(baseId));
        }

        [Fact]
        public void WhisperWillowIsNotMistakenForWillow()
        {
            // "willow_" is a substring of "whisper_willow_". A contains-test
            // would rank the tier-4 tool as tier 3 - and quietly, because both
            // are real answers.
            Assert.Equal(3, ContentRegistry.GetToolTier("willow_axe_tool"));
            Assert.Equal(4, ContentRegistry.GetToolTier("whisper_willow_axe_tool"));
        }

        [Fact]
        public void MaterialsAndEquipmentAreNotTools()
        {
            Assert.Equal(-1, ContentRegistry.GetToolKind("birch_log"));
            Assert.Equal(-1, ContentRegistry.GetToolKind("eq_steel_claymore_melee_weapon_slot_base"));
            Assert.Equal(-1, ContentRegistry.GetToolKind("sunlit_perch"));
            Assert.Equal(0, ContentRegistry.GetToolTier("birch_log"));
        }

        [Fact]
        public void EveryAuthoredToolIsRecognised()
        {
            // Thirty craftable tools plus three starters. A tool the resolver
            // does not recognise is a tool that silently does nothing.
            int recognised = 0;
            for (int itemId = 1; itemId <= ContentRegistry.ItemDefinitions.Length; itemId++)
            {
                string baseId = ContentRegistry.GetItemBaseId(itemId);
                if (!baseId.EndsWith("_tool", System.StringComparison.Ordinal)) continue;

                recognised++;
                Assert.True(ContentRegistry.GetToolKind(baseId) >= 0, $"{baseId} has no kind");
            }

            Assert.Equal(33, recognised);
        }

        [Fact]
        public void ATierActuallyMakesGatheringFaster()
        {
            // The point of the whole system: a better tool must cost fewer
            // ticks, and the starter must not be free speed.
            int none = Domain.Shared.GatheringToolEngine.ComputeRequiredTicks(100, 0, 0, 0);
            int birch = Domain.Shared.GatheringToolEngine.ComputeRequiredTicks(100, 0, 1, 0);
            int voidbark = Domain.Shared.GatheringToolEngine.ComputeRequiredTicks(100, 0, 10, 0);

            Assert.True(birch < none, "a birch tool must beat bare hands");
            Assert.True(voidbark < birch, "void bark must beat birch");
        }
    }
}
