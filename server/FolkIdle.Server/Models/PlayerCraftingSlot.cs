using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// Modul: A WORKSHOP COMMISSION (task 83). This table existed from the
    /// initial baseline with no writer and no reader - CompletionEpoch was a
    /// column "nothing has ever written" (CraftingEngine). It is the
    /// commission's row now: one per player, SlotIndex 0, deleted on collect
    /// or cancel. See WorkshopCommissionEngine.
    /// </summary>
    public class PlayerCraftingSlot
    {
        public long PlayerId { get; set; }
        public byte SlotIndex { get; set; }

        /// <summary>The commissioned piece's item-definition id (items.json, positional).</summary>
        public int ActiveRecipeId { get; set; }

        /// <summary>Unix seconds at which the piece is ready. Wall clock, so it finishes while the player is away.</summary>
        public long CompletionEpoch { get; set; }

        /// <summary>Unused; kept because dropping a column is not additive. Readiness is CompletionEpoch &lt;= now.</summary>
        public bool IsReady { get; set; }

        /// <summary>The affix the player chose (an AffixRegistry id, never a payload key).</summary>
        [MaxLength(32)]
        public string ChosenAffixId { get; set; } = string.Empty;

        /// <summary>The rarity floor fixed when the commission was placed, so a later Workshop upgrade cannot change a paid order.</summary>
        public int FloorTier { get; set; }

        /// <summary>Unix seconds the commission was placed - for the progress bar, and the refund on cancel.</summary>
        public long StartedEpoch { get; set; }
    }
}
