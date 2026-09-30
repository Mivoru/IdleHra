using System;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Economy;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// Task 85, "Orders": up to three automation rules per character, unlocked
    /// at level 20, 40 and 60. See docs/superpowers/specs/2026-09-30-automation-rules.md.
    /// </summary>
    /// <remarks>
    /// Modul: ONE FUNCTION PER EVENT, AND EVERY PATH CALLS IT. The live tick and
    /// the offline projection both meet a death (ApplyCombatDeath, already
    /// shared) and a dry larder (the live auto-eat branch; the offline fight's
    /// StopWhenStarved), and both build drop requests through
    /// CombatLootDropRequest.Build. Each of those calls into THIS class, so a
    /// rule cannot act while watched and be forgotten while away - "three
    /// paths grow a level" is the lesson this is shaped around.
    ///
    /// Everything here is pure field arithmetic on the payload's active
    /// register - no allocation, no database - because two of the three
    /// callers are the 10 Hz tick.
    /// </remarks>
    public static class AutomationRules
    {
        public const byte None = 0;
        /// <summary>When the larder runs dry, fish the chosen spot instead of fighting on unhealed.</summary>
        public const byte FishWhenLarderDry = 1;
        /// <summary>After a death, respawn one monster down the ladder instead of going idle.</summary>
        public const byte StepDownOnDeath = 2;
        /// <summary>Fuse the stacks new drops land in, up to tier N.</summary>
        public const byte AutoFuseToTier = 3;

        public const int SlotCount = 3;

        /// <summary>The account level each slot opens at (owner decision, 2026-09-30).</summary>
        public static readonly int[] UnlockLevels = { 20, 40, 60 };

        private const int BitsPerSlot = 16;
        private const int TypeBits = 4;
        private const long SlotMask = 0xFFFFL;
        private const int TypeMask = 0xF;
        private const int MaxParam = 0xFFF;

        public readonly struct Rule
        {
            public byte Type { get; init; }
            public int Param { get; init; }
        }

        // Modul: PACKED, because the rules ride the register swap. Three
        // 16-bit slots in one long: type in the low four bits, the parameter in
        // the high twelve. One field to swap in SwapRegisterWith and one column
        // on characters, rather than six of each to keep in step.
        public static long Pack(ReadOnlySpan<Rule> rules)
        {
            long packed = 0;
            for (int i = 0; i < SlotCount && i < rules.Length; i++)
            {
                long slot = (rules[i].Type & TypeMask) | ((long)(rules[i].Param & MaxParam) << TypeBits);
                packed |= slot << (i * BitsPerSlot);
            }
            return packed;
        }

        public static Rule Unpack(long packed, int slotIndex)
        {
            long slot = (packed >> (slotIndex * BitsPerSlot)) & SlotMask;
            return new Rule { Type = (byte)(slot & TypeMask), Param = (int)(slot >> TypeBits) };
        }

        public static bool IsSlotUnlocked(int slotIndex, int level) =>
            slotIndex >= 0 && slotIndex < SlotCount && level >= UnlockLevels[slotIndex];

        /// <summary>
        /// The parameter of the rule of <paramref name="type"/>, if a slot the
        /// level has opened holds it.
        /// </summary>
        /// <remarks>
        /// Modul: THE LEVEL GATE IS READ WHEN A RULE ACTS, not only when it is
        /// set. A rebirth drops the level to 1 and keeps the rules; checking
        /// only at the setter would leave a level-1 character with three live
        /// rules it has not earned again.
        /// </remarks>
        public static bool TryGetActive(long packed, int level, byte type, out int param)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                Rule rule = Unpack(packed, i);
                if (rule.Type == type && IsSlotUnlocked(i, level))
                {
                    param = rule.Param;
                    return true;
                }
            }
            param = 0;
            return false;
        }

        /// <summary>The fishing node a rule parameter names (its offset in the fishing band).</summary>
        public static long FishingNodeFor(int param) => ActivityIdBands.FishingBand + param;

        /// <summary>
        /// Why a set of rules is refused, or null when it is valid. The REST
        /// setter's whole validation - the client is trusted with none of it.
        /// </summary>
        public static string? Validate(ReadOnlySpan<Rule> rules, int level)
        {
            if (rules.Length != SlotCount) return "WrongSlotCount";
            Span<bool> seen = stackalloc bool[TypeMask + 1];
            for (int i = 0; i < SlotCount; i++)
            {
                Rule rule = rules[i];
                if (rule.Type == None)
                {
                    if (rule.Param != 0) return "ParamWithoutRule";
                    continue;
                }
                if (rule.Type > AutoFuseToTier) return "UnknownRule";
                if (!IsSlotUnlocked(i, level)) return "SlotLocked";
                if (seen[rule.Type]) return "DuplicateRule";
                seen[rule.Type] = true;

                switch (rule.Type)
                {
                    case FishWhenLarderDry:
                        if (rule.Param <= 0 || rule.Param >= ActivityIdBands.BandSize
                            || !ContentRegistry.TryGetGatheringNode(FishingNodeFor(rule.Param), out var node)
                            || node.ProfessionType != 2)
                        {
                            return "NotAFishingSpot";
                        }
                        break;
                    case StepDownOnDeath:
                        if (rule.Param != 0) return "ParamWithoutRule";
                        break;
                    case AutoFuseToTier:
                        if (rule.Param < 2 || rule.Param > ForgeSplicingEngine.MaxQualityTier) return "TierOutOfRange";
                        break;
                }
            }
            return null;
        }

        /// <summary>
        /// The next monster down the canonical ladder, or 0 when there is none.
        /// </summary>
        /// <remarks>
        /// Modul: "ONE MONSTER EASIER" IS THE PREVIOUS REGULAR, NOT id - 1. The
        /// ladder (MonsterLadderTests) climbs through the twenty regulars;
        /// the five bosses sit at the end of each region and are harder than the
        /// first regular of the next. So a regular steps to the regular before
        /// it (skipping the boss at a region border), and a boss steps to its
        /// own region's strongest regular. Legacy ids 1-90 are not on the ladder.
        /// </remarks>
        public static int EasierMonster(int monsterId)
        {
            if (monsterId <= ContentRegistry.FirstCanonicalMonsterId || monsterId > ContentRegistry.LastCanonicalMonsterId)
            {
                return 0;
            }

            int step = monsterId - 1;
            if (ContentRegistry.IsRegionalBoss(step)) step--;
            return step >= ContentRegistry.FirstCanonicalMonsterId ? step : 0;
        }

        /// <summary>
        /// Whether another of the player's characters already works
        /// <paramref name="activityId"/>. The active register holds the
        /// character being decided for, so the other two are the parked ones -
        /// whichever slots they are.
        /// </summary>
        private static bool TakenByAnotherSlot(in TickStatePayload payload, long activityId)
        {
            return payload.Slot2Activity.ActiveActivityId == activityId
                || payload.Slot3Activity.ActiveActivityId == activityId;
        }

        /// <summary>
        /// Rule 2, called by ApplyCombatDeath after the death is recorded - so
        /// the live tick and the offline projection both reach it. Returns
        /// whether the character was sent on.
        /// </summary>
        public static bool TryStepDownAfterDeath(ref TickStatePayload payload, int deathMonsterId)
        {
            if (!TryGetActive(payload.AutomationRules, payload.CurrentLevel, StepDownOnDeath, out _))
            {
                return false;
            }

            int easier = EasierMonster(deathMonsterId);
            if (easier <= 0 || TakenByAnotherSlot(in payload, easier))
            {
                return false;
            }

            // The deploy command's own mutation (monster, swing clock and
            // progress reset), then the note that says a rule did it.
            SimulationEngine.ApplyActivityChangeToPayload(ref payload, easier);
            payload.ActivityHaltReason = Network.ActivityHaltReason.AutomationSteppedDown;
            return true;
        }

        /// <summary>
        /// The fishing spot rule 1 would send this character to right now, or
        /// false when it cannot act (no rule, a slot the level has not opened,
        /// a spot not reached yet, or one another character already works).
        /// </summary>
        public static bool CanGoFishing(in TickStatePayload payload, out long nodeId)
        {
            nodeId = 0;
            if (!TryGetActive(payload.AutomationRules, payload.CurrentLevel, FishWhenLarderDry, out int param))
            {
                return false;
            }

            long node = FishingNodeFor(param);
            if (!ContentRegistry.TryGetGatheringNode(node, out _)
                || ContentRegistry.GetNodeLocation(node) > payload.HighestLocationReached
                || TakenByAnotherSlot(in payload, node))
            {
                return false;
            }

            nodeId = node;
            return true;
        }

        /// <summary>
        /// Rule 1. Called when the larder has run dry in a fight: by the live
        /// combat tick on the tick after auto-eat raised OutOfFood, and by the
        /// offline projection at the tick its fight starved. Returns whether
        /// the character was sent fishing.
        /// </summary>
        public static bool TryGoFishing(ref TickStatePayload payload)
        {
            if (!CanGoFishing(in payload, out long node))
            {
                return false;
            }

            SimulationEngine.ApplyActivityChangeToPayload(ref payload, node);
            payload.ActivityHaltReason = Network.ActivityHaltReason.AutomationFishing;
            return true;
        }

        /// <summary>
        /// Rule 3's tier for the character in the active register, or 0 when it
        /// has no such rule the level has opened. Read by
        /// CombatLootDropRequest.Build, which both the live kill and the offline
        /// window use.
        /// </summary>
        public static int AutoFuseTierFor(in TickStatePayload payload)
        {
            return TryGetActive(payload.AutomationRules, payload.CurrentLevel, AutoFuseToTier, out int tier) ? tier : 0;
        }
    }

    /// <summary>
    /// Task 85: hands a rules change made over REST to the live register.
    /// </summary>
    internal static class AutomationRulesTickCoordinator
    {
        internal static void Drain(
            PlayerSessionRegistry registry,
            System.Collections.Generic.Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.AutomationRulesQueue.TryDequeue(out var change))
            {
                ref var payload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, change.PlayerId);
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref payload))
                {
                    // Offline: the row is already written and the next login reads it.
                    continue;
                }

                Apply(ref payload, in change);
            }
        }

        /// <summary>
        /// Writes the rules onto whichever register holds the character. The
        /// tick is between slot passes when this runs, so slot 1 is in the
        /// active register and slots 2 and 3 are parked.
        /// </summary>
        internal static void Apply(ref TickStatePayload payload, in AutomationRulesNotification change)
        {
            if (change.CharacterId == Guid.Empty) return;
            if (payload.Slot1_CharacterId == change.CharacterId) payload.AutomationRules = change.PackedRules;
            else if (payload.Slot2_CharacterId == change.CharacterId) payload.Slot2Activity.AutomationRules = change.PackedRules;
            else if (payload.Slot3_CharacterId == change.CharacterId) payload.Slot3Activity.AutomationRules = change.PackedRules;
            else return;
            payload.IsDirty = true;
        }
    }
}
