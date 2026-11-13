using System;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One ancestor away on an expedition (2026-11-13): who, in which of the
    /// account's expedition slots, to which region, and when they come back.
    /// The row is deleted when its haul is claimed.
    /// </summary>
    /// <remarks>
    /// Modul: THE ROW IS THE LOCK AND THE TICKET. While it exists the character
    /// cannot be fielded (HallOfAncestorsEngine.AssignSlotAsync) and is resting
    /// for breeding (its BreedingCooldownEndEpoch is raised to the return), and
    /// a claim deletes it inside the transaction that stages the chest - so a
    /// haul is paid once, whatever the client sends twice.
    /// </remarks>
    public class Expedition
    {
        public long Id { get; set; }
        public long PlayerId { get; set; }
        public Guid CharacterId { get; set; }
        public int Slot { get; set; }
        public int Region { get; set; }
        public int Hours { get; set; }
        /// <summary>The ancestor's aptitude total when they set out - what the haul is rolled against.</summary>
        public int AptitudeTotal { get; set; }
        public long StartedAtEpoch { get; set; }
        public long ReturnsAtEpoch { get; set; }
        /// <summary>Gold-bought provisions: a better chance of the better chest (ExpeditionRules.ProvisionUpgradePct).</summary>
        public bool Provisioned { get; set; }
    }
}
