using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public static class GuildBonusesCache
    {
        // Modul: previously a hardcoded int[1000] array indexed directly by
        // guildId, silently no-op-ing (UpdateGuildTier) or falling back to
        // the tier-0 multiplier (GetGuildEfficiencyMultiplier) for any
        // guildId >= 1000 with no exception and no log - a guild past the
        // 1000th ever created would have its efficiency bonus silently and
        // permanently stop working. ConcurrentDictionary<long, int>
        // supports an unbounded guildId space with no size ceiling.
        // TryGetValue/the indexer for a value-type TValue (int here) does
        // not box or allocate - reads remain zero-allocation on the hot
        // path exactly like the array lookup it replaces; only the rare
        // UpdateGuildTier write (on a guild Monolith level-up, not a
        // per-tick event) touches the dictionary's internal locking.
        private static readonly ConcurrentDictionary<long, int> _guildTiers = new();

        public static void UpdateGuildTier(long guildId, int tier)
        {
            if (guildId < 0)
            {
                return;
            }

            _guildTiers[guildId] = tier;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double GetGuildEfficiencyMultiplier(long guildId)
        {
            if (guildId < 0)
            {
                return 1.0;
            }

            int tier = _guildTiers.TryGetValue(guildId, out int cachedTier) ? cachedTier : 0;
            return 1.0 + (tier * 0.02); // +2% efficiency per tier
        }
    }
}
