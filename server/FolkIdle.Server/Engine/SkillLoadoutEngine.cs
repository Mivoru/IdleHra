using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Build presets bound to activities (skill tree v3 step A, owner
    /// 2026-11-14): a preset can be bound to the world boss or the dungeon,
    /// which then fights with that preset's War ranks while the live tree -
    /// the grind build - stays as it is.
    /// </summary>
    /// <remarks>
    /// Modul: THE PRESET IS THE BUILD; THIS IS ONLY THE BINDING. Saved builds
    /// already existed (BuildPreset, 2026-11-07, three slots), so a binding is
    /// a column on that row rather than a second table of saved builds - two
    /// stores of "my builds" would drift.
    ///
    /// Modul: A BOUND BUILD NEVER TOUCHES THE LIVE TREE. The dungeon fights on
    /// a payload snapshot and the world boss strike is computed on the tick
    /// from the live payload; both read a COPY with the bound preset's ranks
    /// laid over it (Apply). Swapping the real tree in and out around an
    /// activity would race the tick, the checkpoint and the player's screen.
    /// Only the TREE is bound: the preset's attributes are not laid over,
    /// because attributes are respecced on the tick at their own price.
    ///
    /// Modul: THE CACHE IS WHAT THE TICK READS. The world boss strike resolves
    /// on the tick thread, which never waits on the database, so the bound
    /// ranks live here, filled at hydration and on every save, bind or delete.
    /// A bound build is used only while it is still affordable (PlanReason
    /// from an empty tree against the player's whole War budget): a rebirth
    /// shrinks the budget, and an unaffordable build falls back to the live
    /// tree rather than fighting with points the player does not have.
    /// </remarks>
    public static class SkillLoadoutEngine
    {
        [Flags]
        public enum Activity { None = 0, WorldBoss = 1, Dungeon = 2 }

        public static readonly Activity[] Activities = { Activity.WorldBoss, Activity.Dungeon };

        private static readonly ConcurrentDictionary<(long PlayerId, Activity Activity), byte[]> Bound = new();

        /// <summary>The player's whole War budget: points still free plus points in the live tree.</summary>
        public static int Budget(int availablePoints, ReadOnlySpan<byte> liveRanks)
            => availablePoints + WarTreeRegistry.PointsSpent(liveRanks);

        /// <summary>Whether these ranks can be bought from an empty tree with this budget.</summary>
        public static bool Affordable(ReadOnlySpan<byte> ranks, int budget)
        {
            if (ranks.Length < WarTreeRegistry.Slots) return false;
            Span<byte> empty = stackalloc byte[WarTreeRegistry.Slots];
            return WarTreeRegistry.PlanReason(empty, ranks, budget, out _) == null;
        }

        /// <summary>
        /// Lays the preset bound to this activity over the payload's War ranks,
        /// if there is one and the player can afford it. Returns whether it did.
        /// Call it on a COPY of the live payload.
        /// </summary>
        public static bool Apply(ref TickStatePayload payload, Activity activity)
        {
            if (!Bound.TryGetValue((payload.PlayerId, activity), out var ranks)) return false;
            Span<byte> live = stackalloc byte[WarTreeRegistry.Slots];
            for (int i = 0; i < live.Length; i++) live[i] = payload.SkillRanks[i];
            if (BuildPresetRules.SameWar(live, ranks)) return false;
            if (!Affordable(ranks, Budget(payload.AvailableSkillPoints, live))) return false;
            foreach (var n in WarTreeRegistry.All()) payload.SkillRanks[n.Id] = ranks[n.Id];
            return true;
        }

        /// <summary>Reloads this player's bound presets into the cache the tick reads.</summary>
        public static async Task RefreshCacheAsync(FolkIdleDbContext db, long playerId)
        {
            var rows = await db.BuildPresets.AsNoTracking()
                .Where(p => p.PlayerId == playerId && p.BoundActivities != 0)
                .ToListAsync();
            foreach (var activity in Activities)
            {
                var row = rows.FirstOrDefault(r => (r.BoundActivities & (int)activity) != 0);
                if (row != null) Bound[(playerId, activity)] = BuildPresetRules.DecodeWarRanks(row.WarRanks);
                else Bound.TryRemove((playerId, activity), out _);
            }
        }

        /// <summary>
        /// Binds (or unbinds) a preset to an activity. One preset per activity,
        /// so binding takes the activity off every other slot. Null on success.
        /// </summary>
        public static async Task<string?> BindAsync(FolkIdleDbContext db, long playerId, int slot, Activity activity, bool on)
        {
            if (!Activities.Contains(activity)) return "No such activity.";
            var rows = await db.BuildPresets.Where(p => p.PlayerId == playerId).ToListAsync();
            var row = rows.FirstOrDefault(r => r.Slot == slot);
            if (row == null) return "Save a build in that slot first.";
            foreach (var r in rows) r.BoundActivities &= ~(int)activity;
            if (on) row.BoundActivities |= (int)activity;
            await db.SaveChangesAsync();
            await RefreshCacheAsync(db, playerId);
            return null;
        }

        /// <summary>The activity a client names, or None.</summary>
        public static Activity Parse(string? name) => name?.ToLowerInvariant() switch
        {
            "worldboss" or "world_boss" => Activity.WorldBoss,
            "dungeon" => Activity.Dungeon,
            _ => Activity.None,
        };
    }
}
