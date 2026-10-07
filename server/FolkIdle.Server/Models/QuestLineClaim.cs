using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// The quest line (owner, 2026-10-07): one row per (player, step) once
    /// anything durable is known about that step. Composite key (PlayerId,
    /// StepId), set in FolkIdleDbContext. Written ONLY by QuestLineEngine, whose
    /// raw SQL names this table - keep the [Table] name and the SQL in step.
    /// Snake_case table - see CURRENT_IMPLEMENTATION_STATE.md section 3.
    /// </summary>
    /// <remarks>
    /// Modul: ONE TABLE, TWO JOBS, AND BOTH ARE "A FACT THE GAME CANNOT
    /// RE-DERIVE LATER".
    ///
    /// FactAtUtc LATCHES that the act was done. Most steps already have durable
    /// evidence somewhere (a counter, a gold-ledger row, a progress table) and
    /// the engine reads that; but a rebirth wipes the village and the codex, a
    /// cancelled market listing leaves nothing behind, and the world boss
    /// deletes every player's attempt row when it dies. So the engine writes the
    /// latch the first time it SEES a step done, and the two acts with no
    /// evidence at all (listing on the market, striking the world boss) write it
    /// at the moment they happen. A step that was done is never un-done.
    ///
    /// ClaimedAtUtc is the idempotency token for the reward. It is set in the
    /// same transaction that pays, so a double claim pays once; GoldGranted,
    /// Region and MaterialQuantity record WHAT was paid so a dev tool can take
    /// exactly that back and an audit can say what a player was given.
    ///
    /// Deliberately NOT on rebirth's reset list: a claimed reward must not be
    /// claimable again because the player started a new life.
    /// </remarks>
    [Table("quest_line_claims")]
    public class QuestLineClaim
    {
        public long PlayerId { get; set; }

        /// <summary>A QuestLineRegistry step id. Stable strings, never positions.</summary>
        [MaxLength(32)]
        public string StepId { get; set; } = string.Empty;

        /// <summary>When the act was first seen done; null = no latch yet.</summary>
        public DateTime? FactAtUtc { get; set; }

        /// <summary>When the reward was paid; null = not claimed.</summary>
        public DateTime? ClaimedAtUtc { get; set; }

        public long GoldGranted { get; set; }

        public int MaterialQuantity { get; set; }

        /// <summary>The highest unlocked region the reward was scaled to.</summary>
        public int Region { get; set; }
    }
}
