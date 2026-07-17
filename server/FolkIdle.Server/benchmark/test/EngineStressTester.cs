using System;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Benchmark
{
    public static class EngineStressTester
    {
        public static void SetupVirtualSessions(SimulationEngine engine)
        {
            var random = new Random(42); // Deterministic seed
            for (long i = 1; i <= 2500; i++)
            {
                var payload = new TickStatePayload
                {
                    PlayerId = i,
                    ActiveActivityId = (byte)random.Next(1, 5),
                    CurrentLevel = random.Next(1, 51),
                    CurrentXp = random.Next(0, 100000),
                    InventorySpaceRemaining = random.Next(0, 21),
                    PlayerHp = 100000,
                    CurrentMonsterId = 1,
                    CurrentMonsterHp = 1000,
                    SpeedMultiplier = 1,
                    AccumulatedTimeBankMs = 0,
                    LastCommandTimestamp = 0
                };
                engine.InjectVirtualPlayer(payload);
            }
        }

        public static void InjectCommandFlood(SimulationEngine engine)
        {
            // Simulate 10% command flood (250 commands per tick)
            // Deterministic flood sequentially inside the loop to avoid cross-thread contention
            var random = new Random((int)Environment.TickCount64);
            for (int i = 0; i < 250; i++)
            {
                long targetPlayerId = random.Next(1, 2501);
                var packet = new ClientCommandPacket
                {
                    Command = random.Next(2) == 0 ? CommandType.ChangeActivity : CommandType.ToggleChronoAcceleration,
                    TargetId = 1
                };
                engine.InjectBenchmarkCommand(targetPlayerId, packet);
            }
        }
    }
}
