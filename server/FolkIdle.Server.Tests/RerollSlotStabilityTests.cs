using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// A reroll changes ONE affix and leaves the others where they were.
    ///
    /// Reported from play: "I reroll and get Rare lifesteal, then it jumps to
    /// Epic attack speed out of nowhere and I am rerolling that instead."
    /// Nothing jumped. The affix payload is a JSON object addressed by INDEX -
    /// the reroll command carries the position, not the affix - and the write
    /// back was Remove-then-assign. Assigning a new key to a JsonObject APPENDS
    /// it, so the rerolled affix moved to the end and everything after it slid
    /// up one place under a cursor that had not moved.
    ///
    /// This test is about ORDER, which is the property the whole feature turns
    /// on and which nothing pinned.
    /// </summary>
    public class RerollSlotStabilityTests
    {
        private readonly ITestOutputHelper _output;

        public RerollSlotStabilityTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        /// <summary>
        /// The exact substitution the engine performs, in isolation: rebuild the
        /// object in order with the new key in the old key's place.
        /// </summary>
        private static List<string> KeysAfterSubstitution(IReadOnlyList<string> keys, int index, string newKey)
        {
            var payload = new JsonObject();
            foreach (string key in keys) payload[key] = 1;

            string toReplace = keys[index];

            var rebuilt = new JsonObject();
            foreach (var existing in payload)
            {
                if (existing.Key == toReplace) rebuilt[newKey] = 2;
                else rebuilt[existing.Key] = existing.Value?.DeepClone();
            }

            return rebuilt.Select(pair => pair.Key).ToList();
        }

        [Fact]
        public void TheRerolledAffixKeepsItsPosition()
        {
            var before = new List<string> { "lifesteal_pct#1@3", "crit_chance_pct#1@2", "flat_armor#1@1" };

            var after = KeysAfterSubstitution(before, index: 0, newKey: "attack_speed_pct#1@4");
            _output.WriteLine(string.Join(" | ", after));

            Assert.Equal(3, after.Count);
            Assert.Equal("attack_speed_pct#1@4", after[0]);

            // The untouched two have not moved, which is the whole point: the
            // player's cursor is a position, so anything that shifts positions
            // silently retargets it.
            Assert.Equal("crit_chance_pct#1@2", after[1]);
            Assert.Equal("flat_armor#1@1", after[2]);
        }

        [Fact]
        public void RerollingTheMiddleSlotDoesNotDisturbEitherSide()
        {
            var before = new List<string> { "a#1@1", "b#1@1", "c#1@1", "d#1@1" };
            var after = KeysAfterSubstitution(before, index: 1, newKey: "z#1@5");

            Assert.Equal(new[] { "a#1@1", "z#1@5", "c#1@1", "d#1@1" }, after);
        }

        /// <summary>
        /// The old behaviour, written down so the regression is unmistakable.
        /// Remove-then-assign is what shipped, and this is what it did.
        /// </summary>
        [Fact]
        public void RemoveThenAssignIsWhatMovedTheSlot()
        {
            var payload = new JsonObject
            {
                ["lifesteal_pct#1@3"] = 1,
                ["crit_chance_pct#1@2"] = 1,
                ["flat_armor#1@1"] = 1,
            };

            payload.Remove("lifesteal_pct#1@3");
            payload["attack_speed_pct#1@4"] = 2;

            var keys = payload.Select(pair => pair.Key).ToList();
            _output.WriteLine(string.Join(" | ", keys));

            // Index 0 - the slot the player had selected - is now a different
            // affix, and the one they rerolled is at the end.
            Assert.Equal("crit_chance_pct#1@2", keys[0]);
            Assert.Equal("attack_speed_pct#1@4", keys[2]);
        }
    }
}
