using System;
using System.Data;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

                uint serverTurn = ExtractTurnCounter(match.CurrentStateBitmask);
                int delta = CalculateDamageDelta(in match, serverTurn);
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

        private static int CalculateDamageDelta(in GuildWarActiveMatch match, uint serverTurn)
        {
            Span<GuildCombatRoundRegisters> registers = stackalloc GuildCombatRoundRegisters[1];
            registers[0] = new GuildCombatRoundRegisters
            {
                Attacker = new GuildCombatParticipantRegisters(160 + (int)(match.AttackingGuildId % 37L), 40 + (int)(match.AttackingGuildId % 13L), 12),
                Defender = new GuildCombatParticipantRegisters(140 + (int)(match.DefendingGuildId % 31L), 55 + (int)(match.DefendingGuildId % 17L), 10),
                TurnCounter = serverTurn
            };

            uint randomState = unchecked((uint)match.InitialSeed);
            if (randomState == 0U) randomState = 0x6D2B79F5U;
            AdvanceSeed(ref randomState, serverTurn);
            ExecuteDeterministicRound(ref registers[0], ref randomState);
            return registers[0].DamageDelta;
        }

        private static void ExecuteDeterministicRound(ref GuildCombatRoundRegisters registers, ref uint randomState)
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
                damage *= 2;
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
