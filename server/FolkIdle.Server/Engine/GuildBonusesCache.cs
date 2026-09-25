using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The guild buffs the 10 Hz tick reads without touching the database.
    /// </summary>
    /// <remarks>
    /// Modul: THIS CACHE HAD A READER AND NO WRITER.
    ///
    /// Four places read it: the Damage, Exp and Gold buffs in SimulationEngine,
    /// and DropRate in CombatLootEngine. The only method that filled it,
    /// ReloadBuffsIfDirty, was called by nothing. So from the day guild buffs
    /// shipped, every buff a guild paid 50,000 materials for did NOTHING. The
    /// guild screen still showed it as active, because the screen reads the
    /// GuildActiveBuffs table directly. Found 2026-09-25 while unifying the
    /// live and offline gold formulas.
    ///
    /// The cache is filled from two places now: <see cref="LoadAllAsync"/> at
    /// start-up, and <see cref="Apply"/> right after a purchase commits. Each entry carries its own expiry, so a buff lapses at its
    /// ExpiresAt without a timer, the same way the screen's query does.
    /// GuildBuffCacheTests pins all three.
    /// </remarks>
    public static class GuildBonusesCache
    {
        private static readonly ConcurrentDictionary<long, int> _guildTiers = new();

        // GuildId -> (BuffType -> (Tier, ExpiresAt UTC))
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, (int Tier, DateTime ExpiresAt)>> _activeBuffs = new();

        public static void UpdateGuildTier(long guildId, int tier)
        {
            if (guildId < 0) return;
            _guildTiers[guildId] = tier;
        }

        /// <summary>
        /// Records one buff: the committed row, after a purchase or at load.
        /// A purchase can only raise a tier or extend it, never lower it, so
        /// overwriting the entry is always right.
        /// </summary>
        public static void Apply(long guildId, string buffType, int tier, DateTime expiresAtUtc)
        {
            if (guildId <= 0) return;
            var buffs = _activeBuffs.GetOrAdd(guildId, _ => new ConcurrentDictionary<string, (int, DateTime)>());
            buffs[buffType] = (tier, expiresAtUtc);
        }

        public static void Clear(long guildId) => _activeBuffs.TryRemove(guildId, out _);

        /// <summary>Loads every unexpired buff. Called once at start-up; the table holds at most a few rows per guild.</summary>
        public static async Task LoadAllAsync(FolkIdle.Server.Models.FolkIdleDbContext db)
        {
            DateTime now = DateTime.UtcNow;
            var active = await db.GuildActiveBuffs.Where(b => b.ExpiresAt > now).ToListAsync();
            foreach (var b in active) Apply(b.GuildId, b.BuffType, b.Tier, b.ExpiresAt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetBuffTier(long guildId, string buffType)
        {
            if (guildId <= 0) return 0;
            if (_activeBuffs.TryGetValue(guildId, out var buffs)
                && buffs.TryGetValue(buffType, out var buff)
                && buff.ExpiresAt > DateTime.UtcNow)
            {
                return buff.Tier;
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
