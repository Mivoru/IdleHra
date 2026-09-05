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

        /// <summary>
        /// Fields that reach the client and are DELIBERATELY not loaded at
        /// login, with the reason.
        ///
        /// Everything here is per-session state whose honest value after a
        /// relogin is zero. Anything NOT here has to be loaded, or the client
        /// is shown a zero that is simply wrong until something happens to
        /// refresh it - see EveryWireFieldIsEitherLoadedAtLoginOrDeliberatelyNot
        /// for the defect that produced this list.
        /// </summary>
        private static readonly Dictionary<string, string> RuntimeOnlyByDesign = new()
        {
            // The live fight. A relogin starts a new one.
            ["CurrentMonsterId"] = "combat state, re-established by the tick",
            ["CurrentMonsterHp"] = "combat state, re-established by the tick",
            ["LastHitWasCrit"] = "describes the last swing of this session",
            ["GatheringProgressTicks"] = "progress within the current node",
            ["VillagePopulation"] = "recomputed by the tick from the roster",
            ["ActiveChildMaturationMs"] = "recomputed by the tick",
            ["ActiveChallengeSeed"] = "anti-cheat challenge, per session by design",
            ["NetworkDiagnosticsToken"] = "per-connection diagnostic",

            // The victory and death cards, and the offline summary: shown once
            // and dismissed. The *Tick bytes are EDGES, never values.
            ["LastVictoryTick"] = "edge byte for a card shown once",
            ["LastVictoryMonsterId"] = "victory card, shown once and dismissed",
            ["LastVictoryDurationSeconds"] = "victory card, shown once and dismissed",
            ["LastVictoryGold"] = "victory card, shown once and dismissed",
            ["LastVictoryXp"] = "victory card, shown once and dismissed",
            ["LastDeathTick"] = "edge byte for a card shown once",
            ["LastDeathMonsterId"] = "death card, shown once and dismissed",
            ["OfflineSummaryTick"] = "edge byte, set by OfflineSimulationEngine at login",
            ["OfflineElapsedSeconds"] = "offline summary, computed at login",
            ["OfflineGoldEarned"] = "offline summary, computed at login",
            ["OfflineXpEarned"] = "offline summary, computed at login",
            ["OfflineMaterialDropsGranted"] = "offline summary, computed at login",
            ["OfflineSlot1Xp"] = "offline summary, computed at login",
            ["OfflineSlot1Gold"] = "offline summary, computed at login",
            ["OfflineSlot1Drops"] = "offline summary, computed at login",
            ["OfflineSlot2Xp"] = "offline summary, computed at login",
            ["OfflineSlot2Gold"] = "offline summary, computed at login",
            ["OfflineSlot2Drops"] = "offline summary, computed at login",
            ["OfflineSlot3Xp"] = "offline summary, computed at login",
            ["OfflineSlot3Gold"] = "offline summary, computed at login",
            ["OfflineSlot3Drops"] = "offline summary, computed at login",

            // Guild Wars is hidden rather than removed - see CLAUDE.md on the
            // four svelte-check errors in GuildOps.svelte.
            ["CachedWarMultiplier"] = "Guild Wars, on the roadmap",
            ["GuildCombatVanguardPoints"] = "Guild Wars, on the roadmap",
            ["GuildGatheringSupplyChainPoints"] = "Guild Wars, on the roadmap",
            ["GuildProductionLogisticsPoints"] = "Guild Wars, on the roadmap",
            ["EnemyCombatVanguardPoints"] = "Guild Wars, on the roadmap",
            ["EnemyGatheringSupplyChainPoints"] = "Guild Wars, on the roadmap",
            ["EnemyProductionLogisticsPoints"] = "Guild Wars, on the roadmap",

            // Modul: THESE THREE ARE NOT RUNTIME STATE - THEY ARE INERT.
            //
            // EquipmentInstance.IsAffixLocked is read in ten places and set to
            // true in none, so the affix lock cannot be engaged by any path.
            // Listed here rather than fixed because building the lock is a
            // product decision - see docs/audit_2026_09_05.md finding A. If it
            // is ever built, these move OUT of this list and the hydration is
            // the first thing to write.
            ["EquippedWeaponAffixLocked"] = "the affix lock is inert - audit finding A",
            ["EquippedArmorAffixLocked"] = "the affix lock is inert - audit finding A",
            ["EquippedLeggingsAffixLocked"] = "the affix lock is inert - audit finding A",

            // The broadcast reads these from WorldBossEngine directly; the
            // payload copies are dead. Audit finding F.
            ["WorldBossMaxHp"] = "packet is filled from the engine, not the payload",
            ["WorldBossCurrentHp"] = "packet is filled from the engine, not the payload",
        };

        private static string LocateSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(new[] { dir.FullName, "FolkIdle.Server" }.Concat(parts).ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException(string.Join("/", parts) + " not found from " + AppContext.BaseDirectory);
        }

        /// <summary>
        /// A FIELD THE CLIENT IS SHOWN MUST COME FROM SOMEWHERE AFTER A
        /// RELOGIN.
        ///
        /// WorldBossAttemptCount was written in exactly one place - the
        /// notification raised after an attack resolves - and read straight
        /// onto the wire. Nothing loaded it. So a player who spent their three
        /// attempts, logged out and came back saw three unspent pips; clicking
        /// Attack hit the cap inside ExecuteAttackAsync, which rolls back with
        /// no damage, no message and no telemetry they would ever see. The
        /// screen only told the truth after they had wasted a click on it.
        ///
        /// It was found by an exercise run reporting an attempt going
        /// "0 -> 2 spent" on a single strike, which is not a thing one strike
        /// can do. That is a very lucky way to find a defect, and this test is
        /// the unlucky-proof version.
        ///
        /// The whitelist above is the point: adding a wire field now forces a
        /// decision about where its value comes from on the next login, rather
        /// than letting the answer default to "nowhere".
        /// </summary>
        [Fact]
        public void EveryWireFieldIsEitherLoadedAtLoginOrDeliberatelyNot()
        {
            string hydration = File.ReadAllText(LocateSource("Domain", "Shared", "StateCheckpointManager.cs"));

            var payloadFields = typeof(TickStatePayload)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);

            var packetFields = typeof(StateUpdatePacket)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name)
                .Where(payloadFields.Contains)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.True(packetFields.Count > 100,
                $"only {packetFields.Count} shared fields found - the reflection is wrong, not the code");

            var unloaded = new List<string>();
            foreach (string name in packetFields)
            {
                if (RuntimeOnlyByDesign.ContainsKey(name)) continue;

                // An assignment anywhere in the loader: the payload initialiser,
                // or a later fix-up.
                bool assigned = System.Text.RegularExpressions.Regex.IsMatch(
                    hydration, @"\b" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*=[^=]");
                if (!assigned) unloaded.Add(name);
            }

            Assert.True(unloaded.Count == 0,
                "these fields reach the client but nothing loads them at login, so they read as zero " +
                "for the whole session after a relogin. Load them, or add them to RuntimeOnlyByDesign " +
                "WITH A REASON: " + string.Join(", ", unloaded));
        }

        /// <summary>
        /// The whitelist may not rot. A field that stops travelling, or gets
        /// hydrated after all, must leave the list - otherwise it becomes a
        /// permanent excuse nobody re-reads.
        /// </summary>
        [Fact]
        public void TheRuntimeOnlyListHasNoStaleEntries()
        {
            var payloadFields = typeof(TickStatePayload)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);
            var packetFields = typeof(StateUpdatePacket)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);

            var gone = RuntimeOnlyByDesign.Keys
                .Where(n => !payloadFields.Contains(n) || !packetFields.Contains(n))
                .ToList();

            Assert.True(gone.Count == 0,
                "these are on the runtime-only list but no longer travel to the client - remove them: "
                + string.Join(", ", gone));
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
