using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A caught fish is food, and every predicate that asks agrees.
    ///
    /// Cooking is not in the design list, so the fish a player pulls out of the
    /// water IS the meal. That was implemented in GetHealMilliHp and NOT in
    /// IsFood - which is worse than implementing neither, because the larder
    /// asks IsFood: stocking a Sunlit Perch was refused with "server rejected
    /// that" while the eating code was perfectly willing to consume one.
    /// </summary>
    public class RawFishFoodTests
    {
        // ContentRegistry reads its tables from GameData on demand - the base
        // id array is empty until Initialize runs, and every lookup then
        // indexes past the end of nothing.
        public RawFishFoodTests()
        {
            ContentRegistry.Initialize();
        }

        [Fact]
        public void EveryFishAFishingNodeDropsIsFood()
        {
            Assert.NotEmpty(ContentRegistry.RawFishItemIds);

            foreach (int fishId in ContentRegistry.RawFishItemIds)
            {
                Assert.True(FoodRegistry.IsFood(fishId),
                    $"{ContentRegistry.GetItemBaseId(fishId)} drops from a fishing node but is not food");
                Assert.True(FoodRegistry.GetHealMilliHp(fishId) > 0,
                    $"{ContentRegistry.GetItemBaseId(fishId)} is food but heals nothing");
            }
        }

        [Fact]
        public void ThereAreExactlyTenFishAndTheyAreTheCanonTen()
        {
            // The client carries this list literally (content.ts RAW_FISH_BASE_IDS)
            // because it is never sent a loot table. Pinning the set here is what
            // stops the two drifting apart in silence.
            var expected = new[]
            {
                "sunlit_perch", "shimmering_trout",
                "moss_bass", "ancient_eel",
                "lava_carp", "hellfire_salmon",
                "frost_cod", "glacier_halibut",
                "void_ray", "spectral_lanternfish",
            };

            var actual = new System.Collections.Generic.List<string>();
            foreach (int fishId in ContentRegistry.RawFishItemIds)
            {
                actual.Add(ContentRegistry.GetItemBaseId(fishId));
            }

            Assert.Equal(expected.Length, actual.Count);
            foreach (string name in expected)
            {
                Assert.Contains(name, actual);
            }
        }

        [Fact]
        public void ARockIsNotFood()
        {
            // The guard that matters: IsFood must not have become "anything".
            Assert.False(FoodRegistry.IsFood(0));
            Assert.False(FoodRegistry.IsFood(-1));
            Assert.False(FoodRegistry.IsFood(int.MaxValue));
        }
    }
}
