using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class WorldBossSnapshot
    {
        [Key]
        public long BossInstanceId { get; set; }
        public long MaxHp { get; set; }
        public long CurrentHp { get; set; }
        public long TotalDamageContributed { get; set; }
        public long LastActiveTimestamp { get; set; }

        // 0 = Inactive (no event window open), 1 = Active (event window open, attacks allowed),
        // 2 = Concluded (window closed, either defeated or failed, dormant until next window).
        public byte EventState { get; set; }
        public long EventEndEpoch { get; set; }

        // Modul: the boss wears armour, and the players strip it together.
        //
        // Five plates. One of them - WeakPlateIndex - takes triple damage, and
        // which one is re-seeded on every encounter so the answer cannot live
        // on a wiki. A strike on any OTHER plate does full normal damage and
        // breaks that plate permanently for everyone, so the state of the boss
        // when a player arrives is a message from everyone who came before.
        //
        // See docs/world_boss_design.md. The short version of why five and not
        // three: with three plates against three attempts a blind player cannot
        // fail to find the weak point, so knowing where it is would be worth
        // 1.2x and nobody would look. At five it is worth 1.67x.
        //
        // Bit i of the mask is plate i.
        public byte BrokenPlateMask { get; set; }

        // NEVER SENT TO A CLIENT until WeakPlateRevealed is 1. The whole
        // mechanic is that this is learned by striking, so leaking it ends the
        // decision before it starts.
        public byte WeakPlateIndex { get; set; }

        // Set the first time anyone lands on the weak point. From then on every
        // client can see it and every strike on it pays the multiplier - the
        // finder included, because paying a discovery bonus to one player in a
        // global game with one shared boss rewards a timezone rather than a
        // decision.
        public byte WeakPlateRevealed { get; set; }
    }
}
