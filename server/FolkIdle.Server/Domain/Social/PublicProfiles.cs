using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Social
{
    // Modul: WHAT ONE PLAYER MAY SEE OF ANOTHER, built in one place.
    //
    // GET /api/v1/players/profile used to answer with every CharacterRecord on
    // the account, serialised whole - 185 rows on the dev fixture, nearly all
    // of them bred children wearing nothing - plus the raw equipment entities.
    // The client then drew a card per character with a "Level" it could not
    // fill, because level is an account field and CharacterRecord has none.
    // A profile is opened by tapping a name, so its cost is paid on the tap.
    //
    // The shape below is the opposite: the main character (PlayerRecord
    // .PlayerGuid, the same one /player/worn answers for) and at most
    // MaxExtraCharacters others who actually wear something, eleven slots each,
    // and the statistics the server ALREADY tracks for its own reasons. Nothing
    // here is new tracking, and nothing private (gold, diamonds, treasury,
    // depot, email) is on it - those are the fields to keep off when this grows.
    public sealed class ProfilePiece
    {
        public int SlotIndex { get; set; }
        public long InstanceId { get; set; }
        public string BaseItemId { get; set; } = string.Empty;
        public int QualityTier { get; set; }
        public Dictionary<string, int> Affixes { get; set; } = new();
    }

    public sealed class ProfileCharacter
    {
        public string Name { get; set; } = string.Empty;
        public int SlotIndex { get; set; }
        public bool IsMain { get; set; }
        public bool IsFemale { get; set; }
        public int AgePhase { get; set; }
        /// <summary>"Fighting", "Woodcutting", "Mining", "Fishing", "Crafting" or "Idle".</summary>
        public string Activity { get; set; } = "Idle";
        public List<ProfilePiece> Worn { get; set; } = new();
    }

    public sealed class ProfileGuild
    {
        public long GuildId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Tier { get; set; }
        /// <summary>0 member, 1 officer, 2 leader.</summary>
        public int Role { get; set; }
    }

    public sealed class ProfileStats
    {
        public long TotalKills { get; set; }
        public long BossesSlain { get; set; }
        public int RegionsCompleted { get; set; }
        public int AchievementsClaimed { get; set; }
        public long TotalPlayTimeSeconds { get; set; }
        public long TotalItemsCrafted { get; set; }
        public long TotalDeaths { get; set; }
        public int RebirthCount { get; set; }
        public int DelveDeepestFloor { get; set; }
        public int BestHit { get; set; }
        public int BestDropTier { get; set; }
        public string? BestDropBaseId { get; set; }
        /// <summary>Best season finish, 0 for never placed.</summary>
        public int BestSeasonRank { get; set; }
        /// <summary>Book of Deeds chapters completed (popcount of SealsEarnedMask).</summary>
        public int SealsEarned { get; set; }
        public int WoodcuttingMasteryLevel { get; set; }
        public int MiningMasteryLevel { get; set; }
        public int FishingMasteryLevel { get; set; }
    }

    public sealed class PlayerProfileView
    {
        public long PlayerId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string? ActiveTitle { get; set; }
        public int Level { get; set; }
        public long LastLogoutTimestamp { get; set; }
        public bool IsOnline { get; set; }
        public ProfileGuild? Guild { get; set; }
        /// <summary>The main character first, then up to MaxExtraCharacters who wear something.</summary>
        public List<ProfileCharacter> Characters { get; set; } = new();
        /// <summary>How many more characters wear something but were left off.</summary>
        public int MoreEquippedCharacters { get; set; }
        public ProfileStats Stats { get; set; } = new();
    }

    public sealed class GuildViewMember
    {
        public long PlayerId { get; set; }
        public string Username { get; set; } = string.Empty;
        public int Level { get; set; }
        public int Role { get; set; }
        public bool IsOnline { get; set; }
    }

    public sealed class GuildViewBuff
    {
        public string BuffType { get; set; } = string.Empty;
        public int Tier { get; set; }
        public long ExpiresAtEpoch { get; set; }
    }

    public sealed class GuildPublicView
    {
        public long GuildId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Tier { get; set; }
        public int ActiveMembers { get; set; }
        public int MaxMembers { get; set; }
        public int Rating { get; set; }
        /// <summary>Position on the guild board's own order (tier, then rating).</summary>
        public int Rank { get; set; }
        public int TaxRatePct { get; set; }
        /// <summary>0 open, otherwise application-only - GuildRecord.JoinType.</summary>
        public int JoinType { get; set; }
        public int MinApplicationLevel { get; set; }
        public int MiningMonolithLevel { get; set; }
        public int WoodcuttingMonolithLevel { get; set; }
        /// <summary>Sum of the members' weekly ranking points - the guild's week.</summary>
        public long WeeklyPoints { get; set; }
        public bool ViewerIsMember { get; set; }
        public List<GuildViewBuff> ActiveBuffs { get; set; } = new();
        public List<GuildViewMember> Members { get; set; } = new();
    }

    public static class PublicProfiles
    {
        /// <summary>Characters shown besides the main one. A bred household is hundreds.</summary>
        public const int MaxExtraCharacters = 4;

        /// <summary>The five canonical region bosses, as HandlePlayerStatistics counts them.</summary>
        private static readonly int[] CanonicalBossMonsterIds = { 95, 100, 105, 110, 115 };

        public static async Task<PlayerProfileView?> BuildProfileAsync(
            FolkIdleDbContext db, long targetId, Func<long, bool> isOnline)
        {
            var player = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == targetId)
                .Select(p => new
                {
                    p.Id,
                    p.Username,
                    p.ActiveTitleSlug,
                    p.CurrentLevel,
                    p.LastLogoutTimestamp,
                    p.PlayerGuid,
                    p.GuildId,
                    p.TotalPlayTimeSeconds,
                    p.TotalItemsCrafted,
                    p.TotalDeaths,
                    p.RebirthCount,
                    p.DelveDeepestFloor,
                    p.BestHit,
                    p.BestDropTier,
                    p.BestDropBaseId,
                    p.BestSeasonRank,
                    p.SealsEarnedMask,
                    p.WoodcuttingMasteryLevel,
                    p.MiningMasteryLevel,
                    p.FishingMasteryLevel,
                })
                .FirstOrDefaultAsync();
            if (player == null) return null;

            var view = new PlayerProfileView
            {
                PlayerId = player.Id,
                Username = player.Username ?? string.Empty,
                ActiveTitle = FolkIdle.Server.Domain.Progression.TitleRegistry.DisplayNameFor(player.ActiveTitleSlug),
                Level = player.CurrentLevel,
                LastLogoutTimestamp = player.LastLogoutTimestamp,
                IsOnline = isOnline(player.Id),
            };

            // Modul: the GuildMembers ROW, not PlayerRecord.GuildId alone - the
            // row is what carries the role, and the two are written together by
            // GuildManagementEngine. A GuildId with no row reads as no guild.
            if (player.GuildId > 0)
            {
                view.Guild = await db.GuildMembers.AsNoTracking()
                    .Where(m => m.PlayerId == targetId && m.GuildId == player.GuildId)
                    .Join(db.GuildRecords, m => m.GuildId, g => g.Id, (m, g) => new ProfileGuild
                    {
                        GuildId = g.Id,
                        Name = g.Name,
                        Tier = g.CurrentTier,
                        Role = m.Role,
                    })
                    .FirstOrDefaultAsync();
            }

            // One query for every character worth showing: the main one, and
            // anyone wearing at least one piece. Projected, so a 185-row
            // household costs eleven nullable columns, not 185 entities.
            Guid mainGuid = player.PlayerGuid;
            var dressed = await db.CharacterRecords.AsNoTracking()
                .Where(c => c.PlayerId == targetId && (c.Id == mainGuid
                    || c.EquippedWeaponId != null || c.EquippedHelmetId != null || c.EquippedChestId != null
                    || c.EquippedGlovesId != null || c.EquippedLeggingsId != null || c.EquippedBootsId != null
                    || c.EquippedAmuletId != null || c.EquippedRingId != null
                    || c.EquippedAxeId != null || c.EquippedPickaxeId != null || c.EquippedRodId != null))
                .Select(c => new
                {
                    c.Id, c.Name, c.SlotIndex, c.IsFemale, c.AgePhase, c.ActiveActivityId,
                    c.EquippedWeaponId, c.EquippedHelmetId, c.EquippedChestId, c.EquippedGlovesId,
                    c.EquippedLeggingsId, c.EquippedBootsId, c.EquippedAmuletId, c.EquippedRingId,
                    c.EquippedAxeId, c.EquippedPickaxeId, c.EquippedRodId,
                })
                .ToListAsync();

            var ordered = dressed
                .OrderByDescending(c => c.Id == mainGuid)
                .ThenBy(c => c.SlotIndex)
                .ToList();
            var shown = ordered.Take(1 + MaxExtraCharacters).ToList();
            view.MoreEquippedCharacters = Math.Max(0, ordered.Count - shown.Count);

            var slotsByCharacter = new List<(ProfileCharacter Character, Dictionary<long, int> Slots)>(shown.Count);
            var instanceIds = new HashSet<long>();
            foreach (var c in shown)
            {
                // Modul: ALL ELEVEN, 0-7 combat and 8-10 tools. Every list that
                // stopped at the ring has been a bug in this codebase.
                var slots = new Dictionary<long, int>(11);
                void Worn(long? id, int slot)
                {
                    if (id.HasValue) { slots[id.Value] = slot; instanceIds.Add(id.Value); }
                }
                Worn(c.EquippedWeaponId, EquipmentSlotEngine.SlotWeapon);
                Worn(c.EquippedHelmetId, EquipmentSlotEngine.SlotHelmet);
                Worn(c.EquippedChestId, EquipmentSlotEngine.SlotChest);
                Worn(c.EquippedGlovesId, EquipmentSlotEngine.SlotGloves);
                Worn(c.EquippedLeggingsId, EquipmentSlotEngine.SlotLeggings);
                Worn(c.EquippedBootsId, EquipmentSlotEngine.SlotBoots);
                Worn(c.EquippedAmuletId, EquipmentSlotEngine.SlotAmulet);
                Worn(c.EquippedRingId, EquipmentSlotEngine.SlotRing);
                Worn(c.EquippedAxeId, EquipmentSlotEngine.SlotAxe);
                Worn(c.EquippedPickaxeId, EquipmentSlotEngine.SlotPickaxe);
                Worn(c.EquippedRodId, EquipmentSlotEngine.SlotRod);

                var character = new ProfileCharacter
                {
                    Name = c.Name,
                    SlotIndex = c.SlotIndex,
                    IsMain = c.Id == mainGuid,
                    IsFemale = c.IsFemale,
                    AgePhase = c.AgePhase,
                    Activity = ActivityName(c.ActiveActivityId),
                };
                view.Characters.Add(character);
                slotsByCharacter.Add((character, slots));
            }

            if (instanceIds.Count > 0)
            {
                var ids = instanceIds.ToList();
                var pieces = await db.EquipmentInstances.AsNoTracking()
                    .Where(e => e.PlayerId == targetId && ids.Contains(e.Id))
                    .Select(e => new { e.Id, e.BaseItemId, e.QualityTier, e.AffixPayload })
                    .ToDictionaryAsync(e => e.Id);

                foreach (var (character, slots) in slotsByCharacter)
                {
                    foreach (var (instanceId, slot) in slots.OrderBy(kv => kv.Value))
                    {
                        if (!pieces.TryGetValue(instanceId, out var piece)) continue;
                        character.Worn.Add(new ProfilePiece
                        {
                            SlotIndex = slot,
                            InstanceId = piece.Id,
                            BaseItemId = piece.BaseItemId,
                            QualityTier = piece.QualityTier,
                            Affixes = ParseAffixes(piece.AffixPayload),
                        });
                    }
                }
            }

            // Kills come from the codex, as /player/statistics counts them: one
            // row per monster (115 at most), summed here rather than twice in SQL.
            var codex = await db.MonsterCodexEntries.AsNoTracking()
                .Where(e => e.PlayerId == targetId)
                .Select(e => new { e.MonsterId, e.KillCount })
                .ToListAsync();

            view.Stats = new ProfileStats
            {
                TotalKills = codex.Sum(e => (long)e.KillCount),
                BossesSlain = codex.Where(e => CanonicalBossMonsterIds.Contains(e.MonsterId)).Sum(e => (long)e.KillCount),
                RegionsCompleted = await db.PlayerRegionCompletions.AsNoTracking().CountAsync(r => r.PlayerId == targetId),
                AchievementsClaimed = await db.PlayerLifetimeAchievements.AsNoTracking().CountAsync(a => a.PlayerId == targetId && a.IsClaimed),
                TotalPlayTimeSeconds = player.TotalPlayTimeSeconds,
                TotalItemsCrafted = player.TotalItemsCrafted,
                TotalDeaths = player.TotalDeaths,
                RebirthCount = player.RebirthCount,
                DelveDeepestFloor = player.DelveDeepestFloor,
                BestHit = player.BestHit,
                BestDropTier = player.BestDropTier,
                BestDropBaseId = player.BestDropBaseId,
                BestSeasonRank = player.BestSeasonRank,
                SealsEarned = System.Numerics.BitOperations.PopCount((uint)player.SealsEarnedMask),
                WoodcuttingMasteryLevel = player.WoodcuttingMasteryLevel,
                MiningMasteryLevel = player.MiningMasteryLevel,
                FishingMasteryLevel = player.FishingMasteryLevel,
            };

            return view;
        }

        public static async Task<GuildPublicView?> BuildGuildAsync(
            FolkIdleDbContext db, long guildId, long viewerId, Func<long, bool> isOnline)
        {
            var guild = await db.GuildRecords.AsNoTracking()
                .Where(g => g.Id == guildId)
                .Select(g => new
                {
                    g.Id, g.Name, g.CurrentTier, g.ActiveMembers, g.MaxMembers, g.GuildMMR,
                    g.TaxRatePct, g.JoinType, g.MinApplicationLevel,
                    g.MiningMonolithLevel, g.WoodcuttingMonolithLevel,
                })
                .FirstOrDefaultAsync();
            if (guild == null) return null;

            // Modul: the guild board's own order (LeaderboardCronEngine
            // .SyncGuildLeaderboardAsync: tier, then rating), counted in SQL
            // rather than read from Redis, so the view still has a rank when
            // Redis is down - and agrees with the board whenever it is up.
            int ahead = await db.GuildRecords.AsNoTracking()
                .CountAsync(g => g.CurrentTier > guild.CurrentTier
                    || (g.CurrentTier == guild.CurrentTier && g.GuildMMR > guild.GuildMMR));

            var members = await db.GuildMembers.AsNoTracking()
                .Where(m => m.GuildId == guildId)
                .Join(db.PlayerRecords, m => m.PlayerId, p => p.Id, (m, p) => new
                {
                    m.PlayerId,
                    p.Username,
                    p.CurrentLevel,
                    m.Role,
                    m.WeeklyContributionPoints,
                })
                .ToListAsync();

            var now = DateTime.UtcNow;
            var buffs = await db.GuildActiveBuffs.AsNoTracking()
                .Where(b => b.GuildId == guildId && b.ExpiresAt > now)
                .Select(b => new { b.BuffType, b.Tier, b.ExpiresAt })
                .ToListAsync();

            return new GuildPublicView
            {
                GuildId = guild.Id,
                Name = guild.Name,
                Tier = guild.CurrentTier,
                ActiveMembers = guild.ActiveMembers,
                MaxMembers = guild.MaxMembers,
                Rating = guild.GuildMMR,
                Rank = ahead + 1,
                TaxRatePct = guild.TaxRatePct,
                JoinType = guild.JoinType,
                MinApplicationLevel = guild.MinApplicationLevel,
                MiningMonolithLevel = guild.MiningMonolithLevel,
                WoodcuttingMonolithLevel = guild.WoodcuttingMonolithLevel,
                // The guild's week as one number. Each member's own points
                // stay on the member-only depot view (HandleGuildDepot).
                WeeklyPoints = members.Sum(m => m.WeeklyContributionPoints),
                ViewerIsMember = members.Any(m => m.PlayerId == viewerId),
                ActiveBuffs = buffs
                    .Select(b => new GuildViewBuff
                    {
                        BuffType = b.BuffType,
                        Tier = b.Tier,
                        ExpiresAtEpoch = new DateTimeOffset(DateTime.SpecifyKind(b.ExpiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                    })
                    .ToList(),
                Members = members
                    .OrderByDescending(m => m.Role)
                    .ThenByDescending(m => m.CurrentLevel)
                    .ThenBy(m => m.PlayerId)
                    .Select(m => new GuildViewMember
                    {
                        PlayerId = m.PlayerId,
                        Username = m.Username ?? string.Empty,
                        Level = m.CurrentLevel,
                        Role = m.Role,
                        IsOnline = isOnline(m.PlayerId),
                    })
                    .ToList(),
            };
        }

        public static string ActivityName(long activityId)
        {
            if (activityId >= ActivityIdBands.CombatFirst && activityId <= ActivityIdBands.CombatLast) return "Fighting";
            if (activityId >= ActivityIdBands.WoodcuttingBand && activityId < ActivityIdBands.WoodcuttingBand + ActivityIdBands.BandSize) return "Woodcutting";
            if (activityId >= ActivityIdBands.MiningBand && activityId < ActivityIdBands.MiningBand + ActivityIdBands.BandSize) return "Mining";
            if (activityId >= ActivityIdBands.FishingBand && activityId < ActivityIdBands.FishingBand + ActivityIdBands.BandSize) return "Fishing";
            if (activityId >= ActivityIdBands.CraftingBand && activityId < ActivityIdBands.CraftingBand + ActivityIdBands.BandSize) return "Crafting";
            return "Idle";
        }

        // Modul: the same reading the inventory route does - integer magnitudes
        // only, "is_affix_locked" (a bool ForgeSplicingEngine may write into the
        // same object) skipped. A typed Dictionary<string,int> parse would throw
        // on that mixed payload.
        public static Dictionary<string, int> ParseAffixes(string? payload)
        {
            var affixes = new Dictionary<string, int>();
            if (string.IsNullOrWhiteSpace(payload)) return affixes;
            try
            {
                if (JsonNode.Parse(payload) is not JsonObject obj) return affixes;
                foreach (var kvp in obj)
                {
                    if (kvp.Key == "is_affix_locked" || kvp.Value is not JsonValue value) continue;
                    if (value.TryGetValue(out int magnitude)) affixes[kvp.Key] = magnitude;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed payload shows the piece without its affixes rather
                // than failing the whole profile.
            }
            return affixes;
        }
    }
}
