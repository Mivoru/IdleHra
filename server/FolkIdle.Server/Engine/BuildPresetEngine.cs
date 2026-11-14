using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>The pure half of build presets: slots, names, the rank encoding, the attribute plan.</summary>
    public static class BuildPresetRules
    {
        public const int MaxSlots = 3;
        public const int MaxNameLength = 24;

        public static bool IsSlot(int slot) => slot >= 1 && slot <= MaxSlots;

        /// <summary>A trimmed name, or "Build N" when it is empty.</summary>
        public static string CleanName(string? name, int slot)
        {
            string trimmed = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (trimmed.Length > MaxNameLength)
            {
                // Modul: never cut between the two halves of a surrogate pair
                // (an emoji): a lone half is not valid UTF-8 and Npgsql refuses it.
                int cut = MaxNameLength;
                if (char.IsHighSurrogate(trimmed[cut - 1])) cut--;
                trimmed = trimmed[..cut].TrimEnd();
            }
            return trimmed.Length == 0 ? $"Build {slot}" : trimmed;
        }

        /// <summary>The War half's ranks above 0 as "id:level,id:level". Hearth and empty slots are left out.</summary>
        public static string EncodeWarRanks(ReadOnlySpan<byte> ranks)
        {
            var sb = new StringBuilder();
            foreach (var n in WarTreeRegistry.All())
            {
                if (n.Id >= ranks.Length || ranks[n.Id] == 0) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(n.Id).Append(':').Append(Math.Min((int)ranks[n.Id], n.MaxLevel));
            }
            return sb.ToString();
        }

        /// <summary>
        /// A full rank buffer from the encoding. A node that no longer exists, or
        /// a level above today's cap, is dropped or capped rather than refused:
        /// a preset saved before a tree change still applies what it can.
        /// </summary>
        public static byte[] DecodeWarRanks(string? encoded)
        {
            var ranks = new byte[WarTreeRegistry.Slots];
            if (string.IsNullOrWhiteSpace(encoded)) return ranks;
            foreach (var part in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length != 2 || !int.TryParse(kv[0], out int id) || !int.TryParse(kv[1], out int level)) continue;
                if (WarTreeRegistry.Get(id) is not { } n || level <= 0) continue;
                ranks[id] = (byte)Math.Min(level, n.MaxLevel);
            }
            return ranks;
        }

        /// <summary>Whether every War node in <paramref name="target"/> is at least where it is in <paramref name="current"/>.</summary>
        public static bool OnlyAdds(ReadOnlySpan<byte> current, ReadOnlySpan<byte> target)
        {
            foreach (var n in WarTreeRegistry.All())
                if (target[n.Id] < current[n.Id]) return false;
            return true;
        }

        /// <summary>Whether the War halves of the two buffers are the same.</summary>
        public static bool SameWar(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            foreach (var n in WarTreeRegistry.All())
                if (a[n.Id] != b[n.Id]) return false;
            return true;
        }
    }

    public sealed class BuildPresetView
    {
        public int Slot { get; set; }
        public string Name { get; set; } = string.Empty;
        public int STR { get; set; }
        public int DEX { get; set; }
        public int CON { get; set; }
        public int LCK { get; set; }
        /// <summary>The full rank buffer (War nodes only), the shape the skill tree screen uses.</summary>
        public int[] Levels { get; set; } = Array.Empty<int>();
        public int PointsSpent { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        /// <summary>The War half already is this preset's.</summary>
        public bool TreeMatches { get; set; }
        /// <summary>Always false since skill tree v3 (switching is free); kept for old clients.</summary>
        public bool NeedsRespec { get; set; }
        /// <summary>The player's whole War budget still buys this build.</summary>
        public bool Affordable { get; set; }
        /// <summary>The build's keystone, or empty.</summary>
        public string Keystone { get; set; } = string.Empty;
        /// <summary>SkillLoadoutEngine.Activity flags this preset fights for.</summary>
        public int BoundActivities { get; set; }
    }

    public sealed class BuildPresetTreeResult
    {
        /// <summary>Null on success, else why nothing (or only the respec) happened.</summary>
        public string? Reason { get; set; }
        public bool Changed { get; set; }
        public bool Respecced { get; set; }
        public long GoldSpent { get; set; }
        public int Spent { get; set; }
    }

    /// <summary>
    /// Saved builds (BuildPreset). Saving stores what the player holds now;
    /// applying the tree goes through SkillTreeEngine, and the attributes are
    /// applied by the client with the ordinary respec and spend commands.
    /// </summary>
    public sealed class BuildPresetEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public BuildPresetEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task<List<BuildPresetView>> ListAsync(long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var rows = await db.BuildPresets.AsNoTracking().Where(p => p.PlayerId == playerId).OrderBy(p => p.Slot).ToListAsync();
            var current = await SkillTreeEngine.LoadRanksAsync(db, playerId);
            int available = await AvailableAsync(db, playerId);
            return rows.Select(r => ToView(r, current, available)).ToList();
        }

        /// <summary>
        /// Saves the given attributes and the player's CURRENT War ranks (read
        /// here, never from the client) under the slot. The attributes come
        /// from the caller because the tick owns them and the database row may
        /// be a few seconds behind; they are only ever a wish, see BuildPreset.
        /// </summary>
        public async Task<BuildPresetView?> SaveAsync(long playerId, int slot, string? name, int str, int dex, int con, int lck)
        {
            if (!BuildPresetRules.IsSlot(slot)) return null;
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var ranks = await SkillTreeEngine.LoadRanksAsync(db, playerId);

            var row = await db.BuildPresets.FirstOrDefaultAsync(p => p.PlayerId == playerId && p.Slot == slot);
            if (row == null)
            {
                row = new BuildPreset { PlayerId = playerId, Slot = slot };
                db.BuildPresets.Add(row);
            }
            row.Name = BuildPresetRules.CleanName(name, slot);
            row.Strength = Math.Max(0, str);
            row.Dexterity = Math.Max(0, dex);
            row.Constitution = Math.Max(0, con);
            row.Luck = Math.Max(0, lck);
            row.WarRanks = BuildPresetRules.EncodeWarRanks(ranks);
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await SkillLoadoutEngine.RefreshCacheAsync(db, playerId);
            return ToView(row, ranks, await AvailableAsync(db, playerId));
        }

        public async Task<bool> DeleteAsync(long playerId, int slot)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            int removed = await db.BuildPresets.Where(p => p.PlayerId == playerId && p.Slot == slot).ExecuteDeleteAsync();
            await SkillLoadoutEngine.RefreshCacheAsync(db, playerId);
            return removed > 0;
        }

        /// <summary>
        /// Makes the War half of the tree the preset's, for free: nothing when
        /// it already is, otherwise SkillTreeEngine.EquipPresetTreeAsync.
        /// </summary>
        /// <remarks>
        /// Modul: SWITCHING IS FREE NOW (owner, 2026-11-14, skill tree v3). This
        /// used to respec - the free one, a grant or gold - whenever the preset
        /// lacked a rank the player held, which made a saved build a one-off.
        /// Builds for farming, bosses and dungeons only mean something if the
        /// player can move between them, so the switch replays the whole War
        /// budget from an empty tree in one transaction. A respec is still what
        /// it takes to make a NEW build. payWithGold is kept for old clients
        /// and ignored.
        /// </remarks>
        public async Task<BuildPresetTreeResult> ApplyTreeAsync(long playerId, int slot, bool payWithGold)
        {
            if (!BuildPresetRules.IsSlot(slot)) return new BuildPresetTreeResult { Reason = "No such preset." };
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var preset = await db.BuildPresets.AsNoTracking().FirstOrDefaultAsync(p => p.PlayerId == playerId && p.Slot == slot);
                if (preset == null) return new BuildPresetTreeResult { Reason = "That preset is empty." };
                var current = await SkillTreeEngine.LoadRanksAsync(db, playerId);
                if (BuildPresetRules.SameWar(current, BuildPresetRules.DecodeWarRanks(preset.WarRanks)))
                    return new BuildPresetTreeResult { Changed = false };
            }

            var result = await new SkillTreeEngine(_serviceProvider, _playerRegistry).EquipPresetTreeAsync(playerId, slot);
            return new BuildPresetTreeResult { Reason = result.Reason, Changed = result.Reason == null, Spent = result.Spent };
        }

        private static async Task<int> AvailableAsync(FolkIdleDbContext db, long playerId)
            => (await db.PlayerRecords.AsNoTracking().Where(p => p.Id == playerId).Select(p => (int?)p.AvailableSkillPoints).FirstOrDefaultAsync()) ?? 0;

        private static BuildPresetView ToView(BuildPreset row, byte[] current, int available)
        {
            var ranks = BuildPresetRules.DecodeWarRanks(row.WarRanks);
            bool matches = BuildPresetRules.SameWar(current, ranks);
            return new BuildPresetView
            {
                Slot = row.Slot,
                Name = row.Name,
                STR = row.Strength,
                DEX = row.Dexterity,
                CON = row.Constitution,
                LCK = row.Luck,
                Levels = Array.ConvertAll(ranks, b => (int)b),
                PointsSpent = WarTreeRegistry.PointsSpent(ranks),
                UpdatedAtUtc = row.UpdatedAtUtc,
                TreeMatches = matches,
                // Switching is free since skill tree v3; kept for old clients.
                NeedsRespec = false,
                Affordable = SkillLoadoutEngine.Affordable(ranks, SkillLoadoutEngine.Budget(available, current)),
                Keystone = WarTreeRegistry.HeldKeystone(ranks)?.Name ?? string.Empty,
                BoundActivities = row.BoundActivities,
            };
        }
    }
}
