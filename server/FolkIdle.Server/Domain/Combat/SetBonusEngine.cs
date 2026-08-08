using System;

namespace FolkIdle.Server.Domain.Combat
{
    // Authoritative equipment-set bonus catalogue and evaluator. Callers pass
    // every currently-equipped slot's set id AND quality, packed into one int
    // by EquippedSetIds.
    //
    // Two axes, deliberately: WHICH tier a set has reached is a piece count
    // (2, 3, 5), and HOW MUCH that tier gives is the average quality of the
    // pieces worn. See TierOf and QualityScaleOf for why it is both.
    //
    // Mixed loadouts stack, which is the point: the best three things you own
    // from two different sets give the 3-piece tier of one and the 2-piece
    // tier of the other.
    public static class SetBonusEngine
    {
        public const int ChimingSteelSetId = 1;
        public const int EternalDreadnoughtSetId = 10;

        // Bounds the fixed-size local scratch buffers below so the scan
        // never allocates regardless of how many slots a caller passes -
        // matches CharacterSlotEngine's MaxCharacterSlots-style contract
        // of a small, known-at-compile-time upper bound. 8 comfortably
        // covers every equip slot named in the GDD (Weapon, Helper, Helm,
        // Chest, Leggings, Gloves, Boots) with headroom to spare.
        public const int MaxTrackedSlots = 8;

        public struct SetBonusResult
        {
            public int FlatAttackPowerBonus;
            public float TotalArmorMultiplierPct;
            public float FireDamageMultiplierPct;

            // Modul: 4-piece mechanics are cached here but not yet consumed
            // by the live combat tick (no thorns-reflect-damage, burn-DoT,
            // or cooldown-reduction application loop exists yet) - matching
            // this codebase's own established "computed here but not yet
            // consumed anywhere" precedent (see StatsCalculator's
            // CritMitigationPct doc comment).
            public bool ThornsReflectionActive;

            // Modul: never set any more. It reduced ACTIVE SKILL cooldowns,
            // and active skills were removed from the game - there is nothing
            // left for it to shorten. Kept only because StatsCalculator
            // mirrors this struct field for field; it is dead and should go
            // when that mirror does.
            public bool CooldownReductionActive;
            public bool BurnApplicationActive;
            // Modul: set effect rework. Was CcImmunityActive, which could never
            // do anything: this game models no player-facing crowd control at
            // all - Vulnerable, Chilled and Burning are applied BY the player
            // TO the monster - so there was nothing to be immune to, and a
            // quarter of the Eternal Dreadnought 4-piece paid out nothing.
            //
            // Replaced with a per-hit damage cap rather than by inventing a CC
            // system for one set bonus. It fits the same tank/mitigation
            // archetype, and it answers the failure mode this game actually
            // has: burst. Region bosses sit at roughly 2.5x the attack power of
            // their region's regular monsters, so a single boss hit is what
            // ends runs, not sustained chip damage.
            public bool DamageCapActive;
        }

        // Zero-allocation occurrence count + threshold evaluation. Two
        // fixed-size stack scratch arrays (distinct set ids seen, their
        // counts) replace what would otherwise be a Dictionary<int,int> -
        // with at most MaxTrackedSlots equipped items the O(n^2) linear
        // scan is trivially cheap and never touches the managed heap.
        // Modul: set bonuses made real. Convenience overload for callers that
        // hold the payload's cached EquippedSetIds and are not on a path where
        // building the span themselves is convenient - notably the skill-cast
        // command handler, which needs the cooldown-reduction flag and lives in
        // an async method where stackalloc is unavailable. The span is
        // allocated on THIS method's frame, so it still never touches the heap.
        public static SetBonusResult Evaluate(in FolkIdle.Server.Engine.EquippedSetIds equippedSetIds)
        {
            Span<int> setIdSpan = stackalloc int[FolkIdle.Server.Engine.EquippedSetIds.SlotCount];
            equippedSetIds.CopyTo(setIdSpan);
            return Evaluate(setIdSpan);
        }

        /// <summary>
        /// A set has THREE TIERS, reached by wearing 2, 3 and 5 matching
        /// pieces - and how strong each tier is depends on the QUALITY of the
        /// pieces worn.
        ///
        /// Modul: the two ideas were previously fought over. The original rule
        /// counted pieces and paid at exactly 2 and exactly 4, so a
        /// Transcendent helmet was worth what a Normal one was and the third
        /// piece was worth nothing. The replacement made everything continuous
        /// in quality, which fixed that and lost something real: a set stopped
        /// having tiers to aim at, and four junk pieces of one set stopped
        /// being a set at all.
        ///
        /// This is both, and they answer different questions:
        ///
        ///   - WHICH tier you have is a piece count. Wearing a set is a
        ///     decision about what you put on, and it should not be undone by
        ///     the rarity you happened to roll. Linen chest and linen leggings
        ///     is two pieces of Linen whether they are Rare or Uncommon.
        ///   - HOW MUCH that tier gives is the average quality of those
        ///     pieces. So upgrading a piece you already wear is worth
        ///     something immediately, and three excellent pieces of one set
        ///     beat three poor ones without needing a fourth.
        ///
        /// Mixing therefore works the way it was asked to: the best three
        /// things you own from two different sets give you the 3-piece tier of
        /// one and the 2-piece tier of the other, each scaled by what those
        /// pieces actually are.
        ///
        /// FOUR IS DELIBERATELY NOT A TIER. Eight slots exist, and a ladder of
        /// 2/3/4/5 would make every step a formality; 2/3/5 leaves the last
        /// step something to reach for.
        /// </summary>
        public const int TierOnePieces = 2;
        public const int TierTwoPieces = 3;
        public const int TierThreePieces = 5;

        /// <summary>
        /// What "an ordinary piece" is worth. Rare - tier 4 of 14 - because
        /// that is roughly what a player is wearing while they still care
        /// about assembling a set at all. A set of exactly this rarity pays
        /// its tier at face value.
        /// </summary>
        public const int ReferenceQualityTier = 4;

        /// <summary>
        /// A ceiling on the quality multiplier, so a set of Transcendent gear
        /// is worth twice one of ordinary gear rather than three and a half
        /// times it. Without it the top of the rarity ladder would drown every
        /// other stat on the character.
        /// </summary>
        public const float MaxQualityScale = 2.0f;

        /// <summary>
        /// A floor, so a set of Normal pieces still does something. Wearing
        /// five matching pieces is a real decision even when they are junk,
        /// and paying nothing for it would make the tiers a lie at exactly the
        /// point a new player first notices them.
        /// </summary>
        public const float MinQualityScale = 0.4f;

        /// <summary>0 for "not a set yet", else 1, 2 or 3.</summary>
        public static int TierOf(int pieceCount)
        {
            if (pieceCount >= TierThreePieces) return 3;
            if (pieceCount >= TierTwoPieces) return 2;
            if (pieceCount >= TierOnePieces) return 1;
            return 0;
        }

        /// <summary>
        /// The average quality of the worn pieces, against the reference
        /// rarity - clamped at both ends.
        /// </summary>
        public static float QualityScaleOf(int qualitySum, int pieceCount)
        {
            if (pieceCount <= 0) return 0f;

            float average = qualitySum / (float)pieceCount;
            float scale = average / ReferenceQualityTier;

            if (scale > MaxQualityScale) return MaxQualityScale;
            if (scale < MinQualityScale) return MinQualityScale;
            return scale;
        }

        public static SetBonusResult Evaluate(ReadOnlySpan<int> equippedSetIds)
        {
            var result = new SetBonusResult();

            Span<int> distinctSetIds = stackalloc int[MaxTrackedSlots];
            Span<int> qualitySums = stackalloc int[MaxTrackedSlots];
            Span<int> pieceCounts = stackalloc int[MaxTrackedSlots];
            int distinctCount = 0;

            for (int i = 0; i < equippedSetIds.Length; i++)
            {
                int setId = FolkIdle.Server.Engine.EquippedSetIds.SetIdOf(equippedSetIds[i]);
                if (setId <= 0)
                {
                    continue;
                }

                // A piece with no recorded rarity still counts as the lowest
                // one: it is worn, and treating it as zero would make an old
                // row silently stop contributing.
                int quality = FolkIdle.Server.Engine.EquippedSetIds.QualityOf(equippedSetIds[i]);
                if (quality < 1) quality = 1;

                int foundIndex = -1;
                for (int j = 0; j < distinctCount; j++)
                {
                    if (distinctSetIds[j] == setId)
                    {
                        foundIndex = j;
                        break;
                    }
                }

                if (foundIndex >= 0)
                {
                    qualitySums[foundIndex] += quality;
                    pieceCounts[foundIndex]++;
                }
                else if (distinctCount < MaxTrackedSlots)
                {
                    distinctSetIds[distinctCount] = setId;
                    qualitySums[distinctCount] = quality;
                    pieceCounts[distinctCount] = 1;
                    distinctCount++;
                }
            }

            for (int i = 0; i < distinctCount; i++)
            {
                ApplySetTiers(
                    distinctSetIds[i],
                    TierOf(pieceCounts[i]),
                    QualityScaleOf(qualitySums[i], pieceCounts[i]),
                    ref result);
            }

            return result;
        }

        /// <summary>
        /// The catalogue. Each set names what its three tiers give; the
        /// numbers are multiplied by the quality scale before they land.
        ///
        /// The BOOLEAN effects belong to the top tier alone, because a thorns
        /// reflection cannot be forty percent on.
        /// </summary>
        private static void ApplySetTiers(int setId, int tier, float qualityScale, ref SetBonusResult result)
        {
            if (tier <= 0)
            {
                return;
            }

            switch (setId)
            {
                case ChimingSteelSetId:
                    // Offensive: fire damage at every tier, the burn at the top.
                    result.FireDamageMultiplierPct += tier switch
                    {
                        1 => 8f * qualityScale,
                        2 => 15f * qualityScale,
                        _ => 26f * qualityScale,
                    };
                    if (tier >= 3)
                    {
                        result.BurnApplicationActive = true;
                    }
                    break;

                case EternalDreadnoughtSetId:
                    // Defensive: armour at every tier, the bulwark at the top.
                    result.TotalArmorMultiplierPct += tier switch
                    {
                        1 => 10f * qualityScale,
                        2 => 18f * qualityScale,
                        _ => 32f * qualityScale,
                    };
                    if (tier >= 3)
                    {
                        result.ThornsReflectionActive = true;
                        result.DamageCapActive = true;
                    }
                    break;
            }
        }
    }
}
