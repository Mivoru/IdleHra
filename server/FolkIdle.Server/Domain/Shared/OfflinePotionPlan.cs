using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// Which potions were running during an absence, and when (owner H2: a
    /// potion works offline exactly as online).
    /// </summary>
    /// <remarks>
    /// Modul: THE ABSENCE IS SPLIT AT EVERY EXPIRY. The load turns each slot's
    /// stored expiry into "remaining from NOW", so before this the projection
    /// read whatever was left: a potion still running at sign-in counted for
    /// the whole absence, one that ran out while away for none of it. Each
    /// slot here contributes up to two boundaries - where the potion that was
    /// drunk before logout ran out, and where auto-drink emptied the shelf -
    /// so three slots make at most seven stretches, each projected with
    /// exactly the potions that were running in it. Pure: the caller swaps the
    /// ids in and runs its projections per stretch.
    /// </remarks>
    public static class OfflinePotionPlan
    {
        public readonly record struct Stretch(long Seconds, int RewardsId, int CombatId, int GatheringId);

        private readonly record struct SlotPlan(int LoadedId, long LoadedRemaining, int ShelfId, long CoveredUntil);

        private static SlotPlan PlanSlot(in TickStatePayload payload, int slot, long logoutEpoch, long windowSeconds)
        {
            (int loadedId, long expires) = slot switch
            {
                PotionSlot.Rewards => (payload.OffensivePotionLoadedId, payload.OffensivePotionExpiresEpoch),
                PotionSlot.Combat => (payload.DefensivePotionLoadedId, payload.DefensivePotionExpiresEpoch),
                _ => (payload.GatheringPotionLoadedId, payload.GatheringPotionExpiresEpoch),
            };
            long remaining = loadedId > 0 ? Math.Max(0L, expires - logoutEpoch) : 0L;

            var copy = payload;
            int shelfId = ConsumableEngine.ShelfItem(ref copy, slot);
            int shelfCount = ConsumableEngine.ShelfCount(ref copy, slot);
            long covered = remaining;
            if (ConsumableEngine.AutoDrinks(in payload, slot) && shelfCount > 0
                && PotionRegistry.TryGet(shelfId, out var potion) && potion.Slot == slot)
            {
                covered += (long)shelfCount * (potion.DurationMs / 1000L);
            }
            else
            {
                shelfId = 0;
            }
            return new SlotPlan(loadedId, Math.Min(remaining, windowSeconds), shelfId, Math.Min(covered, windowSeconds));
        }

        /// <summary>The absence as stretches, in order, summing to <paramref name="windowSeconds"/>.</summary>
        public static List<Stretch> Plan(in TickStatePayload payload, long logoutEpoch, long windowSeconds)
        {
            var slots = new SlotPlan[PotionSlot.Count];
            var cuts = new SortedSet<long> { 0L, windowSeconds };
            for (int s = 0; s < PotionSlot.Count; s++)
            {
                slots[s] = PlanSlot(in payload, s, logoutEpoch, windowSeconds);
                cuts.Add(slots[s].LoadedRemaining);
                cuts.Add(slots[s].CoveredUntil);
            }

            var stretches = new List<Stretch>();
            long previous = -1;
            foreach (long cut in cuts)
            {
                if (previous >= 0 && cut > previous)
                {
                    stretches.Add(new Stretch(cut - previous,
                        IdAt(slots[PotionSlot.Rewards], previous),
                        IdAt(slots[PotionSlot.Combat], previous),
                        IdAt(slots[PotionSlot.Gathering], previous)));
                }
                previous = cut;
            }
            return stretches;
        }

        private static int IdAt(SlotPlan slot, long t)
            => t < slot.LoadedRemaining ? slot.LoadedId : t < slot.CoveredUntil ? slot.ShelfId : 0;

        /// <summary>Puts a stretch's potions in the three active slots for its projection.</summary>
        public static void Apply(ref TickStatePayload payload, in Stretch stretch)
        {
            payload.ActiveOffensivePotionId = stretch.RewardsId;
            payload.ActiveDefensivePotionId = stretch.CombatId;
            payload.ActiveGatheringPotionId = stretch.GatheringId;
        }

        /// <summary>
        /// The slots as they stand NOW, after <paramref name="realSeconds"/> of
        /// real time (the whole absence, cap or not - a potion's clock does not
        /// stop at the offline cap): what auto-drink took from each shelf, and
        /// what is still running. A slot the load already found running keeps
        /// its own countdown.
        /// </summary>
        public static void Settle(ref TickStatePayload payload, long logoutEpoch, long realSeconds,
            int loadedRewardsId, int loadedCombatId, int loadedGatheringId,
            int nowRewardsMs, int nowCombatMs, int nowGatheringMs)
        {
            int[] loadedIds = { loadedRewardsId, loadedCombatId, loadedGatheringId };
            int[] nowMs = { nowRewardsMs, nowCombatMs, nowGatheringMs };
            for (int slot = 0; slot < PotionSlot.Count; slot++)
            {
                if (nowMs[slot] > 0)
                {
                    // Still running at sign-in: nothing was drunk while away.
                    ConsumableEngine.ActiveId(ref payload, slot) = loadedIds[slot];
                    ConsumableEngine.Duration(ref payload, slot) = nowMs[slot];
                    continue;
                }

                ConsumableEngine.ActiveId(ref payload, slot) = 0;
                ConsumableEngine.Duration(ref payload, slot) = 0;
                if (!ConsumableEngine.AutoDrinks(in payload, slot)) continue;

                long expires = slot switch
                {
                    PotionSlot.Rewards => payload.OffensivePotionExpiresEpoch,
                    PotionSlot.Combat => payload.DefensivePotionExpiresEpoch,
                    _ => payload.GatheringPotionExpiresEpoch,
                };
                long remaining = loadedIds[slot] > 0 ? Math.Max(0L, expires - logoutEpoch) : 0L;
                ref int shelfId = ref ConsumableEngine.ShelfItem(ref payload, slot);
                ref int shelfCount = ref ConsumableEngine.ShelfCount(ref payload, slot);
                if (shelfCount <= 0 || !PotionRegistry.TryGet(shelfId, out var potion) || potion.Slot != slot) continue;

                long each = potion.DurationMs / 1000L;
                long needed = realSeconds - remaining;
                if (needed <= 0 || each <= 0) continue;
                long drunk = Math.Min(shelfCount, (needed + each - 1) / each);
                long coveredUntil = remaining + drunk * each;
                shelfCount -= (int)drunk;
                if (coveredUntil > realSeconds)
                {
                    ConsumableEngine.ActiveId(ref payload, slot) = potion.ItemId;
                    ConsumableEngine.Duration(ref payload, slot) = (int)Math.Min(int.MaxValue, (coveredUntil - realSeconds) * 1000L);
                }
                if (shelfCount == 0) shelfId = 0;
            }
            payload.IsDirty = true;
        }
    }
}
