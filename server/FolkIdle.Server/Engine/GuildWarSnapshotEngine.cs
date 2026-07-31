using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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

            // Modul: guild war snapshot cast. RosterPayloadJson is a jsonb
            // column (see the InitialBaseline migration), and Npgsql binds a
            // string parameter as text. Postgres will not implicitly coerce
            // text to jsonb in an INSERT, so every single refresh failed with
            // 42804 "column is of type jsonb but expression is of type text" -
            // visible on every server boot. The catch around the caller
            // swallowed it, so no guild's defensive snapshot had ever been
            // written or updated, and every guild war resolved against whatever
            // stale row happened to exist. The explicit ::jsonb cast is the
            // standard fix and changes nothing else about the statement.
            var upsertQuery = @"
                INSERT INTO ""GuildWarDefensiveSnapshots"" (""GuildId"", ""RosterPayloadJson"")
                VALUES ({0}, CAST({1} AS jsonb))
                ON CONFLICT (""GuildId"")
                DO UPDATE SET ""RosterPayloadJson"" = CAST({1} AS jsonb);
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

            // Modul: per-character equipment. Guild-war strength is measured
            // from the member's main character, which is the one whose gear
            // this snapshot has always meant - it just used to be stored on the
            // player row. A member with no character contributes bare stats.
            EquippedAffixTotals equippedAffixTotals = default;
            // Modul: seven-slot set bonuses. The set ids used to be discarded
            // here, so a member's guild-war strength ignored set bonuses
            // entirely and disagreed with the same character's real combat
            // stats. Now that they travel as one value there is no reason not
            // to pass them through.
            EquippedSetIds equippedSetIds = default;
            if (character != null)
            {
                (equippedAffixTotals, equippedSetIds) = await EquipmentSlotEngine.ComputeEquippedTotalsAsync(db, character);
            }

            CombatStats stats = StatsCalculator.Calculate(
                player.BaseStrength, player.BaseDexterity, player.BaseConstitution, player.BaseLuck,
                player.ActiveOffensivePotionId, player.ActiveDefensivePotionId,
                activeAgePhase, completedAreaFlags, activeRaceId,
                humanMastery, vilaMastery, draugrMastery,
                equippedAffixTotals,
                isEpicMutation, locusSpeed, locusCrit, equippedSetIds);

            int lineageIndex = player.SelectedLineageId;
            if (lineageIndex < 0 || lineageIndex >= ProgressionEngine.Lineages.Length) lineageIndex = 0;
            LineageDefinition lineage = ProgressionEngine.Lineages[lineageIndex];

            long effectiveMilliAttack = StatsCalculator.ComputeEffectiveMilliAttack(in stats, lineage.DamageScalePerLevelPct, player.CurrentLevel);
            stats.FlatMeleeDamage = (int)((effectiveMilliAttack - StatsCalculator.BaseMilliAttack) / 1000L);

            return stats;
        }
    }
}
