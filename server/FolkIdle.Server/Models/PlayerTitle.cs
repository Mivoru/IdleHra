using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// A title a player has earned. Insert-only and idempotent: the composite
    /// key (PlayerId, TitleSlug) makes a replayed grant a no-op through
    /// ON CONFLICT DO NOTHING.
    ///
    /// Modul: KEYED BY SLUG, NEVER BY POSITION. An index on the wire is a
    /// two-sources-of-truth surface (auto-reroll's affix index drifted ten of
    /// twelve entries), so a title is a string the registry owns, and renaming
    /// one touches TitleRegistry only - never an earned row.
    ///
    /// Created by task 37 phase 1's migration so no later phase needs a second
    /// one; first written in phase 2, when the Deep starts granting titles.
    /// Snake_case table - see CURRENT_IMPLEMENTATION_STATE.md §3.
    /// </summary>
    [Table("player_titles")]
    public class PlayerTitle
    {
        public long PlayerId { get; set; }

        [MaxLength(32)]
        public string TitleSlug { get; set; } = string.Empty;

        public DateTime EarnedAtUtc { get; set; }
    }
}
