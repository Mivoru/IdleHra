using System;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public enum GuildCombatTurnResult
    {
        Applied = 0,
        InvalidRequest = 1,
        NotFound = 2
    }

    public readonly struct GuildCombatParticipantRegisters
    {
        public readonly int Attack;
        public readonly int Defense;
        public readonly int CriticalThreshold;

        public GuildCombatParticipantRegisters(int attack, int defense, int criticalThreshold)
        {
            Attack = attack;
            Defense = defense;
            CriticalThreshold = criticalThreshold;
        }
    }

    public struct GuildCombatRoundRegisters
    {
        public GuildCombatParticipantRegisters Attacker;
        public GuildCombatParticipantRegisters Defender;
        public int DamageDelta;
        public uint TurnCounter;
    }

    public class GuildCombatSimulationEngine
    {
        private const uint TurnCounterMask = 0x0000FFFFU;
        private const uint AttackerMomentumMask = 0x00010000U;
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public GuildCombatSimulationEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task<GuildCombatTurnResult> ExecuteCombatTurnAsync(long playerId, long guildId, ClientCommandPacket packet)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                long matchId = packet.MatchId;
                var match = await db.GuildWarActiveMatches
                    .FromSqlRaw("SELECT * FROM \"GuildWarActiveMatches\" WHERE \"MatchId\" = {0} ORDER BY \"MatchId\" FOR UPDATE", matchId)
                    .SingleOrDefaultAsync();

                if (match == null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 27, Value2 = 1, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return GuildCombatTurnResult.NotFound;
                }

                if (!ClientCommandValidator.ValidateCombatTurnRequest(playerId, guildId, ref packet, match))
                {
                    await transaction.RollbackAsync();
                    return GuildCombatTurnResult.InvalidRequest;
                }

                // Modul: both guilds' aggregate CombatStats, read from the same
                // GuildWarDefensiveSnapshots table GuildWarEngine's weekly
                // matchmaking combat phase already uses (a generic, GuildId-
                // keyed snapshot, not specific to either guild-combat system -
                // see GuildWarSnapshotEngine for how it is built from real
                // member stats). Malformed/missing snapshot data for either
                // side defaults to a zeroed CombatStats rather than failing
                // the turn.
                CombatStats attackerStats = await LoadGuildCombatStatsAsync(db, match.AttackingGuildId);
                CombatStats defenderStats = await LoadGuildCombatStatsAsync(db, match.DefendingGuildId);

                uint serverTurn = ExtractTurnCounter(match.CurrentStateBitmask);
                int delta = CalculateDamageDelta(in match, serverTurn, in attackerStats, in defenderStats);
                uint nextTurn = (serverTurn + 1U) & TurnCounterMask;
                uint momentum = delta >= 0 ? AttackerMomentumMask : 0U;
                match.CurrentStateBitmask = momentum | nextTurn;

                db.GuildWarCombatHistory.Add(new GuildWarCombatHistory
                {
                    MatchId = match.MatchId,
                    ExecutionTick = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    DamageDelta = delta
                });

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.GuildCombatSimulationUpdateQueue.Enqueue(new GuildCombatSimulationUpdateNotification
                {
                    MatchId = match.MatchId,
                    AttackingGuildId = match.AttackingGuildId,
                    DefendingGuildId = match.DefendingGuildId,
                    TurnCounter = (int)nextTurn,
                    DamageDelta = delta
                });

                return GuildCombatTurnResult.Applied;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Guild combat turn failed: {ex.Message}");
                return GuildCombatTurnResult.InvalidRequest;
            }
        }

        public static uint ExtractTurnCounter(uint stateBitmask)
        {
            return stateBitmask & TurnCounterMask;
        }

        // Modul: previously synthesized Attack/Defense/CriticalThreshold from
        // guildId % constant (160 + guildId % 37, etc.) - a fixed placeholder
        // completely disconnected from either guild's real combat power, so a
        // freshly-formed guild of naked level-1 characters hit exactly as
        // hard as one of fully-geared level-50 veterans. Now derived directly
        // from the real, StatsCalculator-built aggregate CombatStats loaded
        // via LoadGuildCombatStatsAsync - FlatMeleeDamage/FlatPhysicalArmor
        // are already on the same rough 0-200+ magnitude the old synthetic
        // constants used, so this does not blow out DamageDelta's existing
        // scale (unlike StatsCalculator.ComputeEffectiveMilliAttack's
        // milli-unit output, which is for the PvE/GuildWarEngine formula, not
        // this turn-based register system).
        private static int CalculateDamageDelta(in GuildWarActiveMatch match, uint serverTurn, in CombatStats attackerStats, in CombatStats defenderStats)
        {
            int attackerCritThreshold = (int)Math.Clamp(attackerStats.CritChancePct, 0f, 100f);

            Span<GuildCombatRoundRegisters> registers = stackalloc GuildCombatRoundRegisters[1];
            registers[0] = new GuildCombatRoundRegisters
            {
                Attacker = new GuildCombatParticipantRegisters(attackerStats.FlatMeleeDamage, defenderStats.FlatPhysicalArmor, attackerCritThreshold),
                Defender = new GuildCombatParticipantRegisters(defenderStats.FlatMeleeDamage, attackerStats.FlatPhysicalArmor, 0),
                TurnCounter = serverTurn
            };

            uint randomState = unchecked((uint)match.InitialSeed);
            if (randomState == 0U) randomState = 0x6D2B79F5U;
            AdvanceSeed(ref randomState, serverTurn);
            ExecuteDeterministicRound(ref registers[0], ref randomState, defenderStats.CritMitigationPct);
            return registers[0].DamageDelta;
        }

        // Modul: mirrors GuildWarEngine.ResolveCombatPhaseAsync's snapshot
        // read exactly (same table, same deserialize-with-fallback shape) -
        // malformed/missing snapshot data defaults to a zeroed CombatStats
        // rather than failing the turn, matching the previous defender-only
        // fallback behavior this replaces.
        private static async Task<CombatStats> LoadGuildCombatStatsAsync(FolkIdleDbContext db, long guildId)
        {
            var snapshot = await db.GuildWarDefensiveSnapshots
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.GuildId == guildId);

            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RosterPayloadJson))
            {
                return default;
            }

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<CombatStats>(snapshot.RosterPayloadJson);
            }
            catch (Exception jsonEx)
            {
                Console.WriteLine($"Guild combat turn: failed to parse defensive snapshot for guild {guildId}: {jsonEx.Message}");
                return default;
            }
        }

        // Modul: offensive crit roll unchanged (deterministic seeded XORshift,
        // not Random.Shared, so both parties can reproduce/verify the turn
        // outcome). The flat 2x crit multiplier is now reduced by the
        // defending guild's Vodnik CritMitigationPct, floored at 1.0 so
        // mitigation can never make a crit deal less than a normal hit -
        // matching the same mitigation formula used against monster crits.
        private static void ExecuteDeterministicRound(ref GuildCombatRoundRegisters registers, ref uint randomState, float defenderCritMitigationPct)
        {
            uint attackRoll = NextXorshift32(ref randomState);
            uint defenseRoll = NextXorshift32(ref randomState);
            uint criticalRoll = NextXorshift32(ref randomState);

            int attack = registers.Attacker.Attack + (int)(attackRoll % 47U);
            int defense = registers.Defender.Defense + (int)(defenseRoll % 31U);
            int damage = attack - defense;
            if (damage < 1) damage = 1;

            if ((criticalRoll % 100U) < registers.Attacker.CriticalThreshold)
            {
                float mitigatedCritMult = Math.Max(1.0f, 2.0f - (defenderCritMitigationPct / 100f));
                damage = (int)(damage * mitigatedCritMult);
            }

            registers.DamageDelta = damage;
        }

        private static void AdvanceSeed(ref uint state, uint turnCounter)
        {
            for (uint i = 0U; i <= turnCounter; i++)
            {
                NextXorshift32(ref state);
            }
        }

        private static uint NextXorshift32(ref uint state)
        {
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }
    }
}
