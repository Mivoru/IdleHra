using System;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One saved build: the four attributes and the War half of the skill tree,
    /// under a name the player chose. Up to BuildPresetRules.MaxSlots a player.
    /// Written only by NetworkBroadcastSystem.BuildPresets.
    /// </summary>
    /// <remarks>
    /// Modul: A PRESET IS A WISH, NOT A GRANT (owner, 2026-11-07). It stores the
    /// numbers the player wants and nothing is paid for until it is applied;
    /// applying goes through the same respec and commit a player would click
    /// (SkillTreeEngine.RespecAsync / CommitAsync, the attribute respec and
    /// spends on the tick), so a preset can never hold more than the game
    /// allows at the moment it is used. Its own table, so the tick's checkpoint
    /// of PlayerRecords can never write over it: one writer.
    /// </remarks>
    public class BuildPreset
    {
        public long PlayerId { get; set; }

        /// <summary>1 to BuildPresetRules.MaxSlots.</summary>
        public int Slot { get; set; }

        public string Name { get; set; } = string.Empty;

        public int Strength { get; set; }
        public int Dexterity { get; set; }
        public int Constitution { get; set; }
        public int Luck { get; set; }

        /// <summary>The War half's ranks as "id:level,id:level" - only the nodes above 0.</summary>
        public string WarRanks { get; set; } = string.Empty;

        public DateTime UpdatedAtUtc { get; set; }
    }
}
