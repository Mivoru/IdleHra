using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FolkIdle.Server.Engine
{
    public static class GuildBonusesCache
    {
        private static readonly ConcurrentDictionary<long, int> _guildTiers = new();
        
        // Cache active buffs: GuildId -> (BuffType -> Tier)
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, int>> _activeBuffs = new();
        private static readonly ConcurrentDictionary<long, DateTime> _buffsLastFetched = new();

        public static void UpdateGuildTier(long guildId, int tier)
        {
            if (guildId < 0) return;
            _guildTiers[guildId] = tier;
        }

        public static void MarkGuildDirty(long guildId)
        {
            _buffsLastFetched.TryRemove(guildId, out _);
        }

        // Must be called from an engine with a DB context if dirty
        public static void ReloadBuffsIfDirty(long guildId, FolkIdle.Server.Models.FolkIdleDbContext db)
        {
            if (guildId <= 0) return;

            // Cache for 60 seconds or until marked dirty
            if (_buffsLastFetched.TryGetValue(guildId, out var lastFetched))
            {
                if ((DateTime.UtcNow - lastFetched).TotalSeconds < 60) return;
            }

            var active = db.GuildActiveBuffs
                .Where(b => b.GuildId == guildId && b.ExpiresAt > DateTime.UtcNow)
                .ToList();

            var buffDict = new ConcurrentDictionary<string, int>();
            foreach (var b in active)
            {
                buffDict[b.BuffType] = b.Tier;
            }

            _activeBuffs[guildId] = buffDict;
            _buffsLastFetched[guildId] = DateTime.UtcNow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetBuffTier(long guildId, string buffType)
        {
            if (guildId <= 0) return 0;
            if (_activeBuffs.TryGetValue(guildId, out var buffs))
            {
                if (buffs.TryGetValue(buffType, out int tier))
                {
                    return tier;
                }
            }
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double GetGuildEfficiencyMultiplier(long guildId)
        {
            if (guildId < 0) return 1.0;
            int tier = _guildTiers.TryGetValue(guildId, out int cachedTier) ? cachedTier : 0;
            return 1.0 + (tier * 0.02);
        }
    }
}
