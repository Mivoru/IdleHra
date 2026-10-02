using System;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>
    /// Who the world boss IS: one monster in monsters.json, named from content.
    /// </summary>
    /// <remarks>
    /// Modul: THE BOSS HAD NO NAME (task 105). The encounter is an HP pool,
    /// five plates and a calendar, and nothing on the server said what was
    /// being struck - the screen read "World Boss" and nothing else. The
    /// content already knew: the payout has always mailed
    /// `perun_avatar_reward_token`, and monsters.json carries id 30, "Perun's
    /// Celestial Avatar", which no region uses (the canon is ids 91-115). So
    /// the name comes from that row, and the screen reads it off
    /// /api/v1/worldboss/board rather than keeping a copy of its own - one
    /// source, and renaming the boss is a content edit.
    ///
    /// The same boss returns every Monday; a weekly roster would be a product
    /// decision and would live here.
    /// </remarks>
    public static class WorldBossIdentity
    {
        /// <summary>monsters.json "Perun's Celestial Avatar". Its art, when it exists, is this id's.</summary>
        public const int MonsterId = 30;

        /// <summary>Only if the content failed to load - the board must still answer.</summary>
        public const string FallbackName = "Perun's Celestial Avatar";

        public static string Name
        {
            get
            {
                try
                {
                    string name = ContentRegistry.GetMonsterName(MonsterId);
                    return string.IsNullOrWhiteSpace(name) ? FallbackName : name;
                }
                catch (IndexOutOfRangeException)
                {
                    return FallbackName;
                }
            }
        }
    }
}
