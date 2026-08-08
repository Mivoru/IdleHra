using System;

namespace FolkIdle.Server.Domain.Combat
{
    // Authoritative equipment-set bonus catalogue and evaluator. Callers pass
    // every currently-equipped slot's set id AND quality, packed into one int
    // by EquippedSetIds; the evaluator sums quality per distinct set and pays
    // out in proportion.
    //
    // Modul: it used to COUNT PIECES and pay at exactly two and exactly four.
    // See Evaluate below for why that was the wrong axis in a game with
    // fourteen rarity tiers - the short version is that it rewarded exactly
    // one build ("collect four of the same thing") and made a Transcendent
    // helmet worth what a Normal one was.
    //
    // Mixed loadouts still stack, and now do so meaningfully: three good
    // pieces of one set and three of another pay a real share of both.
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
        /// Modul: POTENCY, not a piece count.
        ///
        /// This counted matching pieces and paid out at exactly 2 and exactly
        /// 4. Three pieces were worth the same as two, a Normal helmet was
        /// worth the same to a set as a Transcendent one, and a player holding
        /// three superb pieces of one set and three of another got nothing
        /// from either. The only build the rules rewarded was "collect four of
        /// the same thing", which in a game with fourteen rarity tiers throws
        /// away the axis players actually care about.
        ///
        /// A set's potency is now the SUM OF ITS PIECES' QUALITY, measured
        /// against what a full set of middling gear would come to:
        ///
        ///     potency = sum(qualityTier) / (PiecesForFullSet * ReferenceTier)
        ///
        /// So four mid-rarity pieces come to 1.0, and so do two Transcendent
        /// ones. Quality substitutes for quantity, which is the point: a player
        /// can wear the best three things they own from three different sets
        /// and get a real, if partial, share of each.
        ///
        /// Every bonus below scales with that fraction, and every one of them
        /// is a PERCENTAGE. The old +10 flat attack was most of a starting
        /// character's damage and a rounding error by region 5.
        /// </summary>
        public const int PiecesForFullSet = 4;

        /// <summary>
        /// What "an ordinary piece" is worth. Rare - tier 4 of 14 - because
        /// that is roughly what a player is wearing while they still care
        /// about assembling a set at all.
        ///
        /// Modul: this was 7, the arithmetic middle of the ladder, and the
        /// doc comment above claimed a player reaches full potency by wearing
        /// "four matching pieces of ordinary gear". At 7 they do not: four
        /// Rare pieces would have come to 0.57 and four Normal ones to 0.14,
        /// which is below the floor - a four-piece set would have paid nothing
        /// at all for most of the early game. The number was picked for
        /// symmetry and the sentence was written for players; the sentence was
        /// right.
        /// </summary>
        public const int ReferenceQualityTier = 4;

        /// <summary>
        /// A ceiling on potency, so a full set of Transcendent gear is worth
        /// twice a full set of middling gear rather than four times it.
        /// Without it the top of the rarity ladder would make every other stat
        /// on the character irrelevant.
        /// </summary>
        public const float MaxPotency = 2.0f;

        /// <summary>
        /// A set is two pieces or it is not a set - see ApplySetTiers.
        /// </summary>
        public const int MinimumPiecesForASet = 2;

        /// <summary>
        /// Below this a set is not "worn" at all - one piece of a set is a
        /// coincidence rather than a choice, and paying out for it would mean
        /// every player always has every bonus slightly active.
        /// </summary>
        public const float MinimumPotency = 0.25f;

        public static float PotencyOf(int qualitySum)
        {
            float full = PiecesForFullSet * ReferenceQualityTier;
            float potency = qualitySum / full;
            return potency > MaxPotency ? MaxPotency : potency;
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
                ApplySetTiers(distinctSetIds[i], pieceCounts[i], PotencyOf(qualitySums[i]), ref result);
            }

            return result;
        }

        /// <summary>
        /// Modul: the thresholds are gone. A bonus that snaps on at exactly
        /// four pieces is a cliff a player either clears or does not; scaled by
        /// potency, every upgrade to every piece of a set is worth something
        /// the moment it is worn.
        ///
        /// The BOOLEAN effects still need a line to cross, because a thorns
        /// reflection cannot be 40 percent on - so they arm at full potency,
        /// which a player reaches either by wearing four matching pieces of
        /// ordinary gear or two exceptional ones.
        /// </summary>
        private static void ApplySetTiers(int setId, int pieceCount, float potency, ref SetBonusResult result)
        {
            // Modul: TWO PIECES, whatever their quality.
            //
            // The potency floor alone did not say this: one Rare piece lands
            // exactly on it, so a single lucky drop would have switched a set
            // bonus on. Wearing one piece of something is a coincidence rather
            // than a decision, and a rule meant to reward CHOOSING a set has to
            // require a choice. A test caught it - the assertion was written
            // before the constant was, and the constant was wrong.
            if (pieceCount < MinimumPiecesForASet || potency < MinimumPotency)
            {
                return;
            }

            bool full = potency >= 1.0f;

            switch (setId)
            {
                case ChimingSteelSetId:
                    // Offensive: fire damage, scaled.
                    result.FireDamageMultiplierPct += 18f * potency;
                    if (full)
                    {
                        result.BurnApplicationActive = true;
                    }
                    break;

                case EternalDreadnoughtSetId:
                    // Defensive: armour, scaled.
                    result.TotalArmorMultiplierPct += 25f * potency;
                    if (full)
                    {
                        result.ThornsReflectionActive = true;
                        result.DamageCapActive = true;
                    }
                    break;
            }
        }
    }
}
