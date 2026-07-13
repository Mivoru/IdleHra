using System;
using System.Runtime.CompilerServices;

namespace FolkIdle.Server.Engine
{
    public static class GuildBonusesCache
    {
        // Avoid garbage collection allocations, memory mapped statically
        private static int[] _guildTiers = new int[1000];

        public static void UpdateGuildTier(long guildId, int tier)
        {
            if (guildId >= 0 && guildId < _guildTiers.Length)
            {
                _guildTiers[guildId] = tier;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double GetGuildEfficiencyMultiplier(long guildId)
        {
            if (guildId < 0 || guildId >= _guildTiers.Length) return 1.0;
            return 1.0 + (_guildTiers[guildId] * 0.02); // +2% efficiency per tier
        }
    }
}
