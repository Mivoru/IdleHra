using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    public sealed class WorldBossBoardRow
    {
        public int Rank { get; set; }
        public long PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public long Damage { get; set; }
        /// <summary>The display name of the title the player wears, or null.</summary>
        public string? Title { get; set; }
        /// <summary>Registered this week: their damage counts against the boss and earns the kill reward.</summary>
        public bool Registered { get; set; }
    }

    /// <summary>One weekly milestone as the asking player sees it.</summary>
    public sealed class WorldBossMilestoneView
    {
        public long Damage { get; set; }
        /// <summary>The gold it pays THIS player, from their own income (GoldRewardScale).</summary>
        public long Gold { get; set; }
        public int Diamonds { get; set; }
        public string? AvatarName { get; set; }
        public string? AvatarArt { get; set; }
        public string? Title { get; set; }
        public string? PetName { get; set; }
        public string? PetArt { get; set; }
        /// <summary>Paid this week.</summary>
        public bool Reached { get; set; }
        /// <summary>Its avatar, title or pet is already the player's, so crossing it again pays only gold and diamonds.</summary>
        public bool CosmeticOwned { get; set; }
    }

    public sealed class WorldBossPlaceView
    {
        public int Place { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? TitleColor { get; set; }
        public int GoldHours { get; set; }
        /// <summary>Who wears it now - last week's holder - or null.</summary>
        public string? Holder { get; set; }
    }

    public sealed class WorldBossBoardView
    {
        /// <summary>Everything every player has dealt this encounter, together.</summary>
        public long TotalDamage { get; set; }
        /// <summary>0 while the health is hidden (registration) or when nobody registered.</summary>
        public long BossMaxHp { get; set; }
        public long BossCurrentHp { get; set; }
        public int Participants { get; set; }
        public IReadOnlyList<WorldBossBoardRow> Top { get; set; } = Array.Empty<WorldBossBoardRow>();
        /// <summary>The asking player's own row, ranked among everyone - or null before their first strike.</summary>
        public WorldBossBoardRow? Me { get; set; }
        /// <summary>The boss's name, from content (WorldBossIdentity) - the screen keeps no copy.</summary>
        public string BossName { get; set; } = WorldBossIdentity.FallbackName;
        /// <summary>The monsters.json id the boss is.</summary>
        public int BossMonsterId { get; set; } = WorldBossIdentity.MonsterId;
        /// <summary>Sprite paths: the boss, and the Black Serpent form it takes when enraged.</summary>
        public string BossArt { get; set; } = WorldBossIdentity.Art;
        public string EnragedArt { get; set; } = WorldBossIdentity.EnragedArt;

        /// <summary>"Registration", "Revealed" or "Closed".</summary>
        public string Phase { get; set; } = "Closed";
        public long RevealEpoch { get; set; }
        public long EventEndEpoch { get; set; }
        public int RegisteredCount { get; set; }
        public bool Defeated { get; set; }
        public long DefeatedAtEpoch { get; set; }
        /// <summary>The level a player must have to register.</summary>
        public int RegistrationLevel { get; set; } = LeaderboardTierRegistry.MinimumRankedLevel;

        public IReadOnlyList<WorldBossMilestoneView> Milestones { get; set; } = Array.Empty<WorldBossMilestoneView>();
        public IReadOnlyList<WorldBossPlaceView> Places { get; set; } = Array.Empty<WorldBossPlaceView>();
        /// <summary>The kill reward's gold for the asking player, and that it brings a Legendary chest.</summary>
        public long KillGold { get; set; }
        public int KillGoldHours { get; set; } = WorldBossRewards.KillGoldHours;
    }

    /// <summary>
    /// The encounter's damage board, and the one ranking the payout uses.
    /// </summary>
    /// <remarks>
    /// Modul: THE BOARD AND THE PAYOUT RANK FROM THE SAME ROWS.
    /// player_world_boss_attempts.TotalInflictedDamage is written in the same
    /// transaction as the damage itself, so it survives a restart - unlike the
    /// in-memory damage map and the Redis hash the payout used to read, which
    /// a restart mid-week emptied. A board that ranked one way and a payout
    /// that paid another would be two sources for one truth.
    ///
    /// The milestones, places and kill reward are read from WorldBossRewards
    /// and priced with the player's own income here, so the screen shows what
    /// it will actually be paid and keeps no table of its own.
    /// </remarks>
    public static class WorldBossBoard
    {
        public const int Size = 50;

        /// <summary>Everyone who dealt damage this encounter, best first; ties go to the lower id.</summary>
        public static async Task<List<(long PlayerId, long Damage)>> RankedAsync(FolkIdleDbContext db)
        {
            var rows = await db.PlayerWorldBossAttempts.AsNoTracking()
                .Where(a => a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId && a.TotalInflictedDamage > 0)
                .OrderByDescending(a => a.TotalInflictedDamage)
                .ThenBy(a => a.PlayerId)
                .Select(a => new { a.PlayerId, a.TotalInflictedDamage })
                .ToListAsync();
            return rows.Select(r => (r.PlayerId, r.TotalInflictedDamage)).ToList();
        }

        public static string PhaseOf(WorldBossSnapshot? snapshot) =>
            snapshot == null || snapshot.EventState != 1 ? "Closed"
            : snapshot.HpRevealed == 1 ? "Revealed"
            : "Registration";

        public static async Task<WorldBossBoardView> ViewAsync(FolkIdleDbContext db, long playerId)
        {
            var ranked = await RankedAsync(db);
            var snapshot = await db.WorldBossSnapshots.AsNoTracking()
                .SingleOrDefaultAsync(s => s.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);
            var registered = (await db.PlayerWorldBossAttempts.AsNoTracking()
                    .Where(a => a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId && a.IsRegistered)
                    .Select(a => a.PlayerId)
                    .ToListAsync())
                .ToHashSet();
            var mine = await db.PlayerWorldBossAttempts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.PlayerId == playerId && a.BossInstanceId == WorldBossEngine.ActiveBossInstanceId);

            var topIds = ranked.Take(Size).Select(r => r.PlayerId).ToList();
            if (playerId > 0 && !topIds.Contains(playerId)) topIds.Add(playerId);
            var people = await db.PlayerRecords.AsNoTracking()
                .Where(p => topIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Username, p.ActiveTitleSlug })
                .ToDictionaryAsync(p => p.Id);

            WorldBossBoardRow Row(int index)
            {
                var (id, damage) = ranked[index];
                people.TryGetValue(id, out var person);
                return new WorldBossBoardRow
                {
                    Rank = index + 1,
                    PlayerId = id,
                    Name = string.IsNullOrWhiteSpace(person?.Username) ? $"Player {id}" : person!.Username!,
                    Damage = damage,
                    Title = TitleRegistry.DisplayNameFor(person?.ActiveTitleSlug),
                    Registered = registered.Contains(id),
                };
            }

            var utcNow = DateTime.UtcNow;
            long hourly = playerId > 0 ? await GoldRewardScale.HourlyIncomeAsync(db, playerId, utcNow) : 0;
            var ownedCosmetics = (await db.CosmeticItems.AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .Select(c => c.DefinitionId)
                    .ToListAsync())
                .ToHashSet();
            var ownedTitles = (await db.PlayerTitles.AsNoTracking()
                    .Where(t => t.PlayerId == playerId)
                    .Select(t => t.TitleSlug)
                    .ToListAsync())
                .ToHashSet();
            var ownedPets = (await db.PlayerPets.AsNoTracking()
                    .Where(p => p.PlayerId == playerId)
                    .Select(p => p.PetId)
                    .ToListAsync())
                .ToHashSet();
            int paidMask = mine?.MilestonesPaid ?? 0;

            var milestones = WorldBossRewards.Milestones.Select(m =>
            {
                var avatar = WorldBossRewards.FindAvatar(m.AvatarId);
                var pet = PetRegistry.Find(m.PetId);
                bool owned = (m.AvatarId != null && ownedCosmetics.Contains(m.AvatarId))
                    || (m.TitleSlug != null && ownedTitles.Contains(m.TitleSlug))
                    || (m.PetId != null && ownedPets.Contains(m.PetId));
                return new WorldBossMilestoneView
                {
                    Damage = m.Damage,
                    Gold = m.GoldMinutes > 0 ? GoldRewardScale.ForMinutes(hourly, m.GoldMinutes) : 0,
                    Diamonds = m.Diamonds,
                    AvatarName = avatar?.Name,
                    AvatarArt = avatar?.Art,
                    Title = TitleRegistry.DisplayNameFor(m.TitleSlug),
                    PetName = pet?.Name,
                    PetArt = pet?.Art,
                    Reached = (paidMask & (1 << m.Index)) != 0,
                    CosmeticOwned = owned,
                };
            }).ToList();

            var holders = await db.PlayerTitles.AsNoTracking()
                .Where(t => WorldBossRewards.PlaceTitleSlugs.Contains(t.TitleSlug))
                .Join(db.PlayerRecords, t => t.PlayerId, p => p.Id, (t, p) => new { t.TitleSlug, p.Username })
                .ToListAsync();
            var places = WorldBossRewards.PlaceTitleSlugs.Select((slug, i) => new WorldBossPlaceView
            {
                Place = i + 1,
                Title = TitleRegistry.DisplayNameFor(slug) ?? slug,
                TitleColor = TitleRegistry.Find(slug)?.Color,
                GoldHours = WorldBossRewards.PlaceGoldHours[i],
                Holder = holders.FirstOrDefault(h => h.TitleSlug == slug)?.Username,
            }).ToList();

            int myIndex = ranked.FindIndex(r => r.PlayerId == playerId);
            return new WorldBossBoardView
            {
                TotalDamage = ranked.Sum(r => r.Damage),
                BossMaxHp = snapshot?.MaxHp ?? 0,
                BossCurrentHp = snapshot?.CurrentHp ?? 0,
                Participants = ranked.Count,
                Top = Enumerable.Range(0, Math.Min(Size, ranked.Count)).Select(Row).ToList(),
                Me = myIndex >= 0 ? Row(myIndex) : null,
                BossName = WorldBossIdentity.Name,
                BossMonsterId = WorldBossIdentity.MonsterId,
                Phase = PhaseOf(snapshot),
                RevealEpoch = snapshot?.RevealEpoch ?? 0,
                EventEndEpoch = snapshot?.EventEndEpoch ?? 0,
                RegisteredCount = registered.Count,
                Defeated = (snapshot?.DefeatedAtEpoch ?? 0) > 0,
                DefeatedAtEpoch = snapshot?.DefeatedAtEpoch ?? 0,
                Milestones = milestones,
                Places = places,
                KillGold = GoldRewardScale.ForMinutes(hourly, WorldBossRewards.KillGoldHours * 60),
            };
        }
    }
}
