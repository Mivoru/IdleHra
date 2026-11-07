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
            if (trimmed.Length > MaxNameLength) trimmed = trimmed[..MaxNameLength].TrimEnd();
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
        /// <summary>Applying needs a respec: the preset lacks a rank the player holds.</summary>
        public bool NeedsRespec { get; set; }
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
            return rows.Select(r => ToView(r, current)).ToList();
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
            return ToView(row, ranks);
        }

        public async Task<bool> DeleteAsync(long playerId, int slot)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            int removed = await db.BuildPresets.Where(p => p.PlayerId == playerId && p.Slot == slot).ExecuteDeleteAsync();
            return removed > 0;
        }

        /// <summary>
        /// Makes the War half of the tree the preset's: nothing when it already
        /// is, a plain commit when the preset only adds ranks, and otherwise a
        /// respec (the free one, a grant, or gold when <paramref name="payWithGold"/>)
        /// followed by a commit.
        /// </summary>
        /// <remarks>
        /// Modul: CHECKED BEFORE THE RESPEC. The respec and the commit are two
        /// transactions, so a preset the player can no longer afford (fewer
        /// points after a rebirth, a node renamed by a tree change) would leave
        /// a respecced, empty tree behind. The commit's own rule is run first
        /// against the points the respec would hand back, and a preset that
        /// would fail is refused with nothing spent.
        /// </remarks>
        public async Task<BuildPresetTreeResult> ApplyTreeAsync(long playerId, int slot, bool payWithGold)
        {
            if (!BuildPresetRules.IsSlot(slot)) return new BuildPresetTreeResult { Reason = "No such preset." };

            byte[] current;
            byte[] target;
            int available;
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var preset = await db.BuildPresets.AsNoTracking().FirstOrDefaultAsync(p => p.PlayerId == playerId && p.Slot == slot);
                if (preset == null) return new BuildPresetTreeResult { Reason = "That preset is empty." };
                var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null) return new BuildPresetTreeResult { Reason = "No such player." };
                current = await SkillTreeEngine.LoadRanksAsync(db, playerId);
                target = BuildPresetRules.DecodeWarRanks(preset.WarRanks);
                available = player.AvailableSkillPoints;
            }

            if (BuildPresetRules.SameWar(current, target)) return new BuildPresetTreeResult { Changed = false };

            // The commit keeps the Hearth half as it is.
            var draft = (byte[])current.Clone();
            foreach (var n in WarTreeRegistry.All()) draft[n.Id] = target[n.Id];

            var engine = new SkillTreeEngine(_serviceProvider, _playerRegistry);
            if (BuildPresetRules.OnlyAdds(current, target))
            {
                var commit = await engine.CommitAsync(playerId, draft);
                return new BuildPresetTreeResult { Reason = commit.Reason, Changed = commit.Reason == null, Spent = commit.Spent };
            }

            var empty = new byte[WarTreeRegistry.Slots];
            int afterRespec = available + WarTreeRegistry.PointsSpent(current);
            string? planned = WarTreeRegistry.PlanReason(empty, target, afterRespec, out _);
            if (planned != null) return new BuildPresetTreeResult { Reason = $"This build no longer fits: {planned}" };

            var respec = await engine.RespecAsync(playerId, payWithGold);
            if (respec.Reason != null) return new BuildPresetTreeResult { Reason = respec.Reason };

            // The commit reads the respecced (empty) War half itself.
            var result = await engine.CommitAsync(playerId, draft);
            return new BuildPresetTreeResult
            {
                Reason = result.Reason == null ? null : $"The respec went through, but the build was not bought: {result.Reason}",
                Changed = true,
                Respecced = true,
                GoldSpent = respec.GoldSpent,
                Spent = result.Spent,
            };
        }

        private static BuildPresetView ToView(BuildPreset row, byte[] current)
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
                NeedsRespec = !matches && !BuildPresetRules.OnlyAdds(current, ranks),
            };
        }
    }
}
