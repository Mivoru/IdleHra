using System;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>
    /// The ONLY writer of telemetry EventType 8: "improbable input, accepted,
    /// no penalty" (spec section 6). Value1 is 32, the world boss opcode, per
    /// the Value1 = commandType convention; Value2 is the StrikeSuspicion detail.
    /// </summary>
    /// <remarks>
    /// Modul: ONE CODE, ONE WRITER. Telemetry Value1/Value2 codes have
    /// collided across unrelated checks before, so WorldBossStrikeTelemetryTests
    /// fails if "EventType = 8" appears in any other file. Nothing here feeds
    /// RequestShadowBan, Quarantine_Active or the macro detector - it records
    /// and nothing else.
    /// </remarks>
    public static class WorldBossStrikeTelemetry
    {
        public const byte ImprobableInputEventType = 8;
        public const int WorldBossOpcode = 32;

        public static void Record(long playerId, StrikeSuspicion detail)
        {
            if (detail == StrikeSuspicion.None) return;
            TelemetryStreamer.TryWrite(new TelemetryEvent
            {
                PlayerId = playerId,
                EventType = 8,
                Value1 = WorldBossOpcode,
                Value2 = (int)detail,
                Timestamp = Environment.TickCount64,
            });
        }
    }
}
