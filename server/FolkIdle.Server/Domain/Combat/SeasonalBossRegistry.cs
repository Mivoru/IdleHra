using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <param name="Diamonds">Paid once, on the tier's first clear, by mail.</param>
    /// <param name="Gold">Paid once, on the tier's first clear, by mail.</param>
    /// <param name="Currency">Event currency, once, on the tier's first clear.</param>
    /// <param name="TitleSlug">A TitleRegistry slug mailed on the first clear, or null.</param>
    /// <param name="PetId">A PetRegistry id given on the first clear, or null.</param>
    public sealed record SeasonalBossTier(
        int Tier, int Region, AscensionModifiers Modifiers,
        int Diamonds, long Gold, int Currency, string? TitleSlug, string? PetId);

    /// <summary>
    /// The seasonal boss - The Cailleach for Samhain. Plan:
    /// docs/superpowers/plans/2026-10-09-samhain-event.md.
    ///
    /// Modul: A TIER IS A REGION BOSS AT ITS WALL (owner, 2026-10-09: six tiers
    /// "graded like the bosses of the five regions, the sixth as strong as a
    /// sixth region's"). Tier N is fought as region N's boss at its FIRST-CLEAR
    /// strength - the wall the player had to gear for - even when that boss
    /// has long been beaten, so a tier is a real fight and not a farm.
    ///
    /// Modul: TIER 6 IS MEASURED (SeasonalBossTests prints the table). The
    /// first idea, one region step as +100% health and +50% attack, beat even
    /// the best gear in the game: boss attack is a cliff, not a slope - at +50%
    /// a Transcendent/Legendary level-100 character dies in 14 s. +20% attack
    /// is the point where region 5's wall gear loses (dead in 22 s) and the
    /// best gear still wins; +50% health makes it a long fight (~33 min for
    /// the best gear, against ~26 min for region 5's wall with wall gear).
    ///
    /// Modul: UNLIMITED ATTEMPTS (owner), SO ONLY A FIRST CLEAR PAYS. A repeat
    /// win is an ordinary kill of that boss - it rolls the event currency at
    /// the kill rate like any monster - so the boss is not a farm faster than
    /// the regions. Diamonds 10/15/20/20/25/30 = 120, the owner's floor.
    /// Diamonds, gold and titles arrive by mail (the payload owns the diamond
    /// balance; mail is how an off-tick worker pays one safely).
    /// </summary>
    public static class SeasonalBossRegistry
    {
        public const int MaxTier = 6;
        public const string FrostTitleSlug = "samhain_frost";
        public const string BreakerTitleSlug = "samhain_breaker";

        private static readonly AscensionModifiers Wall = new(0, 0, 0);

        public static readonly IReadOnlyList<SeasonalBossTier> Tiers = new[]
        {
            new SeasonalBossTier(1, 1, Wall, 10, 25_000, 250, null, null),
            new SeasonalBossTier(2, 2, Wall, 15, 75_000, 500, null, null),
            new SeasonalBossTier(3, 3, Wall, 20, 200_000, 750, FrostTitleSlug, null),
            new SeasonalBossTier(4, 4, Wall, 20, 500_000, 1000, null, null),
            new SeasonalBossTier(5, 5, Wall, 25, 1_000_000, 1250, null, null),
            new SeasonalBossTier(6, 5, new AscensionModifiers(AttackPct: 20, BossHpPct: 50, TimeLimitPctOfSwift: 0),
                30, 2_000_000, 1500, BreakerTitleSlug, "pet_mini_vampire"),
        };

        public static SeasonalBossTier? Find(int tier) => tier >= 1 && tier <= MaxTier ? Tiers[tier - 1] : null;

        public static int BossMonsterIdFor(int tier) => Find(tier) is { } t ? RaceUnlockRegistry.GetRegionBossMonsterId(t.Region) : 0;

        public static bool IsCleared(byte mask, int tier) => tier >= 1 && tier <= MaxTier && (mask & (1 << (tier - 1))) != 0;

        public static byte WithCleared(byte mask, int tier) => tier >= 1 && tier <= MaxTier ? (byte)(mask | (1 << (tier - 1))) : mask;

        /// <summary>
        /// The defeated-boss mask the fight reads while a tier is armed: the
        /// tier's boss reads as NOT yet defeated, so health and attack come out
        /// at the first-clear wall. Only the fight reads this - progression,
        /// doors and trophies keep the real mask.
        /// </summary>
        public static byte WallMaskFor(byte realMask, int tier)
        {
            int bossId = BossMonsterIdFor(tier);
            if (bossId <= 0) return realMask;
            byte bit = BossFirstClearRules.MarkDefeated(0, bossId);
            return (byte)(realMask & ~bit);
        }

        /// <summary>
        /// Whether a tier may be started: the event is live, the tier's region
        /// boss has been beaten once (it is reachable), and the tier below it
        /// is cleared.
        /// </summary>
        public static Network.CommandResultCode Validate(in TickStatePayload payload, int tier, int eventId, long nowEpochSeconds)
        {
            var (current, phase) = SeasonalEventRegistry.Current(nowEpochSeconds);
            if (current == null || phase != SeasonalEventPhase.Live || current.Id != eventId)
            {
                return Network.CommandResultCode.EventShopClosed;
            }
            var def = Find(tier);
            if (def == null) return Network.CommandResultCode.GenericValidationFailure;
            if (!BossFirstClearRules.IsDefeated(payload.DefeatedRegionBossMask, RaceUnlockRegistry.GetRegionBossMonsterId(def.Region)))
            {
                return Network.CommandResultCode.AscensionBossNotDefeated;
            }
            if (tier > 1 && !IsCleared(payload.SeasonalBossClearedMask, tier - 1))
            {
                return Network.CommandResultCode.AscensionStepLocked;
            }
            return Network.CommandResultCode.Success;
        }
    }
}
