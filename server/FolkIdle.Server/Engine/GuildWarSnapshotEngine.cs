using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    // Modul: periodically aggregates each guild's real member CombatStats
    // (via StatsCalculator, from live persisted attributes/gear/lineage) into
    // GuildWarDefensiveSnapshots - the table GuildWarEngine.ResolveCombatPhaseAsync
    // and GuildCombatSimulationEngine both read CombatStats from, but that
    // nothing previously ever wrote, leaving guild war combat permanently
    // dead (ResolveCombatPhaseAsync's null-snapshot guard always tripped).
    // Aggregation is the sum of the top TopContributorCount members ranked by
    // CurrentLevel (the same power proxy leaderboards already use elsewhere
    // in this codebase) - guild strength scales with its best warriors, not
    // its whole roster, so a large guild full of low-level alts cannot
    // inflate its snapshot by headcount alone.
    public class GuildWarSnapshotEngine
    {
        private const int TopContributorCount = 20;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

        private readonly IServiceProvider _serviceProvider;
        private CancellationTokenSource _cts = new();

        public GuildWarSnapshotEngine(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAllGuildSnapshotsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Guild war snapshot refresh failed: {ex.Message}");
                }

                try
                {
                    await Task.Delay(RefreshInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public async Task RefreshAllGuildSnapshotsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var guildIds = await db.PlayerRecords
                .AsNoTracking()
                .Where(p => p.GuildId > 0)
                .Select(p => p.GuildId)
                .Distinct()
                .ToListAsync(cancellationToken);

            for (int i = 0; i < guildIds.Count; i++)
            {
                await RefreshGuildSnapshotAsync(db, guildIds[i], cancellationToken);
            }
        }

        public static async Task RefreshGuildSnapshotAsync(FolkIdleDbContext db, long guildId, CancellationToken cancellationToken)
        {
            var topMembers = await db.PlayerRecords
                .AsNoTracking()
                .Where(p => p.GuildId == guildId)
                .OrderByDescending(p => p.CurrentLevel)
                .Take(TopContributorCount)
                .ToListAsync(cancellationToken);

            if (topMembers.Count == 0)
            {
                return;
            }

            var aggregate = new CombatStats();

            for (int i = 0; i < topMembers.Count; i++)
            {
                CombatStats memberStats = await BuildMemberCombatStatsAsync(db, topMembers[i], cancellationToken);
                aggregate.FlatMeleeDamage += memberStats.FlatMeleeDamage;
                aggregate.FlatRangedDamage += memberStats.FlatRangedDamage;
                aggregate.FlatArmorPenetration += memberStats.FlatArmorPenetration;
                aggregate.FlatPhysicalArmor += memberStats.FlatPhysicalArmor;
                aggregate.MaxHp += memberStats.MaxHp;
                aggregate.AttackSpeedPct += memberStats.AttackSpeedPct;
                aggregate.CritChancePct += memberStats.CritChancePct;
                aggregate.CritMitigationPct += memberStats.CritMitigationPct;
                aggregate.DodgeChancePct += memberStats.DodgeChancePct;
                aggregate.LifestealPct += memberStats.LifestealPct;
            }

            // Average, not sum, for percentage-scale stats - summing crit
            // chance across 20 members would push it past 100% trivially and
            // make the deterministic crit roll in
            // GuildCombatSimulationEngine/GuildWarEngine meaningless. Flat
            // damage/armor/HP stay summed - those genuinely represent
            // combined roster striking/soaking power.
            aggregate.AttackSpeedPct /= topMembers.Count;
            aggregate.CritChancePct /= topMembers.Count;
            aggregate.CritMitigationPct /= topMembers.Count;
            aggregate.DodgeChancePct /= topMembers.Count;
            aggregate.LifestealPct /= topMembers.Count;

            string payload = JsonSerializer.Serialize(aggregate);

            var upsertQuery = @"
                INSERT INTO ""GuildWarDefensiveSnapshots"" (""GuildId"", ""RosterPayloadJson"")
                VALUES ({0}, {1})
                ON CONFLICT (""GuildId"")
                DO UPDATE SET ""RosterPayloadJson"" = {1};
            ";
            await db.Database.ExecuteSqlRawAsync(upsertQuery, guildId, payload);
        }

        // Modul: mirrors StateCheckpointManager.LoadPlayerState's Slot1
        // resolution (race/age/genetics/mastery/equipment) closely enough for
        // a guild-strength approximation - mentor bonuses, chrono state, and
        // other non-combat fields are irrelevant here and intentionally
        // skipped. The member's own level-scaled attack contribution (via
        // StatsCalculator.ComputeEffectiveMilliAttack) is folded back into
        // FlatMeleeDamage before returning, so GuildWarEngine/
        // GuildCombatSimulationEngine never need a separate per-guild level
        // to re-apply level scaling on top of the aggregate.
        private static async Task<CombatStats> BuildMemberCombatStatsAsync(FolkIdleDbContext db, PlayerRecord player, CancellationToken cancellationToken)
        {
            var character = await db.CharacterRecords
                .AsNoTracking()
                .Include(c => c.Lineage)
                .Where(c => c.PlayerId == player.Id)
                .OrderBy(c => c.Id)
                .FirstOrDefaultAsync(cancellationToken);

            int activeAgePhase = 1;
            int activeRaceId = 0;
            bool isEpicMutation = false;
            int locusSpeed = 0;
            int locusCrit = 0;

            if (character != null)
            {
                activeAgePhase = character.AgePhase;
                if (character.Lineage != null)
                {
                    activeRaceId = (int)(character.Lineage.GeneticVector & 0xFF);
                    isEpicMutation = character.Lineage.IsEpicMutation;
                    var geneVec = new GeneticVector(character.Lineage.GeneticVector);
                    locusSpeed = geneVec.LocusSpeed.Dominant;
                    locusCrit = geneVec.LocusCrit.Dominant;
                }
            }

            var masteries = await db.PlayerRaceMasteries
                .AsNoTracking()
                .Where(m => m.PlayerId == player.Id)
                .ToListAsync(cancellationToken);

            int humanMastery = 0, vilaMastery = 0, draugrMastery = 0;
            for (int i = 0; i < masteries.Count; i++)
            {
                if (masteries[i].RaceId == RaceIds.Human) humanMastery = masteries[i].MasteryLevel;
                else if (masteries[i].RaceId == RaceIds.Vila) vilaMastery = masteries[i].MasteryLevel;
                else if (masteries[i].RaceId == RaceIds.Draugr) draugrMastery = masteries[i].MasteryLevel;
            }

            var completedRegionIds = await db.PlayerRegionCompletions
                .AsNoTracking()
                .Where(r => r.PlayerId == player.Id)
                .Select(r => r.RegionId)
                .ToListAsync(cancellationToken);

            int completedAreaFlags = 0;
            for (int i = 0; i < completedRegionIds.Count; i++)
            {
                completedAreaFlags |= 1 << completedRegionIds[i];
            }

            (int equippedFlatAttack, int equippedFlatDefense, int equippedCritBonus, int equippedLuckBonus) =
                await EquipmentSlotEngine.ComputeEquippedTotalsAsync(db, player.EquippedWeaponId, player.EquippedArmorId);

            CombatStats stats = StatsCalculator.Calculate(
                player.BaseStrength, player.BaseDexterity, player.BaseConstitution, player.BaseLuck,
                player.ActiveOffensivePotionId, player.ActiveDefensivePotionId,
                activeAgePhase, completedAreaFlags, activeRaceId,
                humanMastery, vilaMastery, draugrMastery,
                equippedFlatAttack, equippedFlatDefense, equippedCritBonus, equippedLuckBonus,
                isEpicMutation, locusSpeed, locusCrit);

            int lineageIndex = player.SelectedLineageId;
            if (lineageIndex < 0 || lineageIndex >= ProgressionEngine.Lineages.Length) lineageIndex = 0;
            LineageDefinition lineage = ProgressionEngine.Lineages[lineageIndex];

            long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in stats, lineage.DamageScalePerLevelPct, player.CurrentLevel);
            stats.FlatMeleeDamage = (int)((effectiveMilliAttack - StatsCalculator.BaseMilliAttack) / 1000L);

            return stats;
        }
    }
}
