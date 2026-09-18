using System;
using System.IO;
using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Extends StateUpdatePacketFieldCoverageTests to a second layer: does a
    /// field that must survive a relogin actually reach StateCheckpointManager's
    /// periodic flush, not just the wire?
    ///
    /// This is deliberately an ALLOWLIST, not an exhaustive scan like the wire
    /// test. Most of TickStatePayload is per-tick or per-session state that has
    /// no business at the checkpoint at all (the live fight, gathering progress,
    /// anti-cheat challenge seeds) - an exhaustive "every field must be assigned,
    /// exclude the runtime-only ones" scan would need an exclusion list nearly as
    /// large as the struct itself, and would say nothing useful. What actually
    /// matters, and what CLAUDE.md's "A Redis frame is not a checkpoint" trap is
    /// about, is a short, named list of fields the checkpoint is DOCUMENTED to
    /// own: the four attributes, UnspentAttributePoints, diamonds, skill points,
    /// the larder, potions, daily quests, the chronicle pass. This test pins
    /// that list.
    ///
    /// Also deliberately a text scan for "state.Name" rather than an
    /// assignment-shaped regex like the wire test's "Name =": several of these
    /// fields are renamed at the checkpoint boundary (STR -> BaseStrength,
    /// PremiumCurrency -> PremiumDiamonds) or handled by a delegate method
    /// (UpsertDailyQuestProgressAsync, UpsertChroniclePassAsync) rather than an
    /// inline assignment, so an assignment-shaped scan would false-positive on
    /// exactly the fields this test most needs to check. Presence of the read
    /// is a weaker guarantee than "is assigned to the right column", but it is
    /// the guarantee this test can make mechanically; the rename mapping itself
    /// is recorded in FieldToTargetDescription for a human to audit if this
    /// ever needs strengthening.
    /// </summary>
    public class CheckpointFieldCoverageTests
    {
        /// <summary>
        /// Fields that must be read somewhere in StateCheckpointManager's flush
        /// path (inline, or via a delegate it calls in the same transaction) or
        /// they never survive a relogin. Value is a human-readable note on
        /// where/how, so a reviewer can spot-check the mapping without
        /// re-deriving it.
        /// </summary>
        private static readonly (string Field, string Note)[] MustReachCheckpoint =
        {
            ("STR", "renamed to PlayerRecord.BaseStrength"),
            ("DEX", "renamed to PlayerRecord.BaseDexterity"),
            ("CON", "renamed to PlayerRecord.BaseConstitution"),
            ("LCK", "renamed to PlayerRecord.BaseLuck"),
            ("UnspentAttributePoints", "same name on PlayerRecord"),
            ("PremiumCurrency", "renamed to PlayerRecord.PremiumDiamonds"),
            ("AvailableSkillPoints", "same name on PlayerRecord"),
            ("Food1_ItemId", "the larder, slot 1"),
            ("Food1_Count", "the larder, slot 1"),
            ("Food2_ItemId", "the larder, slot 2"),
            ("Food2_Count", "the larder, slot 2"),
            ("Food3_ItemId", "the larder, slot 3"),
            ("Food3_Count", "the larder, slot 3"),
            ("ActiveOffensivePotionId", "active potion, offensive"),
            ("OffensivePotionDurationMs", "active potion, offensive"),
            ("ActiveDefensivePotionId", "active potion, defensive"),
            ("DefensivePotionDurationMs", "active potion, defensive"),
            ("DailyQuestDateKeyUtc", "daily quests, via QuestEngine.UpsertDailyQuestProgressAsync"),
            ("QuestSlot0Progress", "daily quests, via QuestEngine.UpsertDailyQuestProgressAsync"),
            ("QuestSlot1Progress", "daily quests, via QuestEngine.UpsertDailyQuestProgressAsync"),
            ("QuestSlot2Progress", "daily quests, via QuestEngine.UpsertDailyQuestProgressAsync"),
            ("ActiveChroniclePassLevel", "the chronicle pass, via UpsertChroniclePassAsync"),
        };

        private static string LocateSource(string relativeDir, string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "FolkIdle.Server", relativeDir, fileName);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException($"{fileName} not found from {AppContext.BaseDirectory}");
        }

        [Fact]
        public void EveryMustPersistFieldIsReadByTheCheckpointPath()
        {
            string checkpointSource = File.ReadAllText(LocateSource(Path.Combine("Domain", "Shared"), "StateCheckpointManager.cs"));
            string questEngineSource = File.ReadAllText(LocateSource(Path.Combine("Domain", "Progression"), "QuestEngine.cs"));
            string combined = checkpointSource + questEngineSource;

            var missing = MustReachCheckpoint
                .Where(f => !combined.Contains("state." + f.Field, StringComparison.Ordinal))
                .Select(f => $"{f.Field} ({f.Note})")
                .ToList();

            Assert.True(missing.Count == 0,
                "these fields are documented as reaching the checkpoint but StateCheckpointManager's flush " +
                "path (including QuestEngine's delegate) no longer reads them - a field that stops being " +
                "read here reverts to its pre-checkpoint value on every relogin: " + string.Join(", ", missing));
        }

        /// <summary>
        /// The allowlist may not rot the other way either: a field renamed or
        /// removed from TickStatePayload without updating this list would make
        /// the test above pass for the wrong reason (matching stale text that
        /// happens to still be there) rather than catching the drift.
        /// </summary>
        [Fact]
        public void EveryListedFieldStillExistsOnTickStatePayload()
        {
            var payloadFields = typeof(TickStatePayload)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);

            var gone = MustReachCheckpoint
                .Where(f => !payloadFields.Contains(f.Field))
                .Select(f => f.Field)
                .ToList();

            Assert.True(gone.Count == 0,
                "these are on the must-reach-checkpoint list but no longer exist on TickStatePayload - " +
                "update or remove them: " + string.Join(", ", gone));
        }
    }
}
