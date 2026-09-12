using System;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The reward for putting a region boss down for the first time: one
    /// Transcendent piece of that boss's own gear, once, ever.
    ///
    /// Asked for alongside the wall - "it would be cool if the first clear would
    /// drop one of the things (equipment) in transcendent tier, but only on first
    /// clear" - and it is the right shape for it. QualityTier 14 is five affixes
    /// and the top of RarityTier.PowerMultiplier, and it is otherwise effectively
    /// unobtainable: fusion stops at the Forge's ceiling of 12, and the drop roll
    /// for tier 14 carries a weight of 0.0001 against a total of 196.7. Five
    /// trophies an account is therefore the game's only dependable source of its
    /// best item frame, which is the reward a wall should pay.
    ///
    /// The FRAME, not a finished item: the affixes roll normally, so the trophy
    /// can come out with Common magnitudes and the reroll system is how a player
    /// finishes it. A guaranteed perfect item would make the Forge and the reroll
    /// economy pointless at exactly the moment a player reaches them.
    ///
    /// Modul: WHERE THE ONCE-EVER COMES FROM. Not from this class - it is pure.
    /// CodexEngine grants it inside the same transaction that moves the boss's
    /// codex KillCount from zero, which is the same durable first-kill transition
    /// the race unlocks already hang off. The payload's DefeatedRegionBossMask is
    /// NOT that signal: it is a cache a reconnecting session can re-present, and
    /// granting from it would hand out a trophy per relogin.
    /// </summary>
    public static class BossFirstClearTrophy
    {
        public const int QualityTier = RarityTier.Transcendent;

        /// <summary>
        /// Which item a boss's trophy is, for a given roll.
        ///
        /// Drawn from the boss's OWN drop table, which is what makes it
        /// region-correct without a second derivation of "what does region N
        /// drop" - and that table already excludes tools and the two catalogue
        /// BaseIds that name slots this game does not have. False for anything
        /// that is not a region boss: an ordinary monster has no first clear.
        /// </summary>
        public static bool TryChooseItemId(int monsterId, int roll, out int itemId)
        {
            itemId = 0;

            if (BossFirstClearRules.RegionOfBoss(monsterId) == 0)
            {
                return false;
            }

            ReadOnlySpan<int> drops = EquipmentDropTable.GetDrops(monsterId);
            if (drops.Length == 0)
            {
                return false;
            }

            // Non-negative modulo, so a caller passing a raw Random.Next() or a
            // counter cannot index out of the table.
            int index = (int)((uint)roll % (uint)drops.Length);
            itemId = drops[index];
            return true;
        }

        /// <summary>
        /// The affix payload a trophy carries: rolled by the same path a dropped
        /// item uses, at the trophy's tier and the boss's region.
        /// </summary>
        public static string BuildAffixPayload(int itemId, int regionTier)
        {
            string baseItemId = ContentRegistry.GetItemBaseId(itemId);

            var affixes = new System.Collections.Generic.Dictionary<string, int>(
                RarityTier.GetAffixCount(QualityTier));

            AffixRegistry.RollAffixes(baseItemId, regionTier, QualityTier, RarityTier.GetAffixCount(QualityTier), affixes);

            return System.Text.Json.JsonSerializer.Serialize(affixes);
        }
    }
}
