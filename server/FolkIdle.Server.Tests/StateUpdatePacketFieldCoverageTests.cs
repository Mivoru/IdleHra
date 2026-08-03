using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Every payload field that has a packet field of the same name must
    /// actually be COPIED into the packet.
    ///
    /// StateUpdatePacket is built by one hand-written object initializer in
    /// SimulationEngine. Adding a field to the struct and to TickStatePayload
    /// compiles, ships, decodes and renders - as zero, forever, because
    /// nothing assigns it. There is no compiler warning for a field an object
    /// initializer omits.
    ///
    /// That is not hypothetical. FishingMasteryXp and HerbalismMasteryXp were
    /// added, persisted, put on the wire and displayed, and the server was
    /// verified to be levelling them correctly - while the client showed a
    /// flat zero, because those four lines were missing from the initializer.
    /// The wire carried the field; the field carried nothing.
    ///
    /// This is a source scan rather than a runtime check because the
    /// initializer sits inside the 10Hz tick loop, behind a ref-struct
    /// dictionary lookup, and cannot be invoked in isolation.
    /// </summary>
    public class StateUpdatePacketFieldCoverageTests
    {
        // Packet fields that legitimately have no same-named payload source:
        // they are computed, defaulted, or renamed at the boundary. Each one
        // is assigned in the initializer, just not from an identical name.
        private static readonly HashSet<string> ComputedAtTheBoundary = new()
        {
            "InventoryCapacity",
            "ActiveLanguageState",
            "Slot1_RaceId",
            "Slot2_RaceId",
            "Slot3_RaceId",
        };

        private static string ReadInitializerSource()
        {
            string path = LocateSimulationEngine();
            string source = File.ReadAllText(path);

            int start = source.IndexOf("new StateUpdatePacket", StringComparison.Ordinal);
            Assert.True(start >= 0, "could not find the StateUpdatePacket initializer");

            // The initializer ends at the first line that closes it - the
            // matching "};" at the same nesting. Scanning braces is more
            // robust than a fixed length as the block grows.
            int depth = 0;
            int i = source.IndexOf('{', start);
            int end = -1;
            for (int j = i; j < source.Length; j++)
            {
                if (source[j] == '{') depth++;
                else if (source[j] == '}')
                {
                    depth--;
                    if (depth == 0) { end = j; break; }
                }
            }

            Assert.True(end > i, "could not find the end of the initializer");
            return source.Substring(i, end - i);
        }

        private static string LocateSimulationEngine()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "FolkIdle.Server", "Domain", "Combat", "SimulationEngine.cs");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException("SimulationEngine.cs not found from " + AppContext.BaseDirectory);
        }

        [Fact]
        public void EveryPacketFieldWithAPayloadTwinIsAssigned()
        {
            string initializer = ReadInitializerSource();

            var payloadFields = typeof(TickStatePayload)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);

            var missing = new List<string>();

            foreach (var packetField in typeof(StateUpdatePacket).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string name = packetField.Name;
                if (ComputedAtTheBoundary.Contains(name)) continue;
                if (!payloadFields.Contains(name)) continue;

                // "Name =" with the assignment, not merely the name appearing
                // as part of a longer identifier.
                bool assigned = initializer.Contains($"{name} =", StringComparison.Ordinal);
                if (!assigned) missing.Add(name);
            }

            Assert.True(
                missing.Count == 0,
                "These packet fields exist on TickStatePayload but are never copied into the packet, "
                    + "so they ship as zero to every client: " + string.Join(", ", missing));
        }

        [Fact]
        public void TheFieldsThatCaughtThisAreCovered()
        {
            // Named explicitly so the regression that motivated this suite
            // fails by name rather than as one entry in a list.
            string initializer = ReadInitializerSource();

            foreach (var name in new[]
                     {
                         "FishingMasteryXp", "FishingMasteryLevel",
                         "HerbalismMasteryXp", "HerbalismMasteryLevel",
                     })
            {
                Assert.Contains($"{name} = currentPayload.{name}", initializer, StringComparison.Ordinal);
            }
        }
    }
}
