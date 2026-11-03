using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// The process-wide lockstep switch: null conductor = the normal paced
    /// engine. Dev only (docs/design/LOCKSTEP_SIM.md).
    /// </summary>
    public static class SimLockstep
    {
        /// <summary>Set once at startup by Program.cs when lockstep is on; null otherwise.</summary>
        public static SimConductor? Conductor { get; set; }

        /// <summary>The gameplay crons the tick runs under lockstep (phase 1b); null otherwise.</summary>
        public static SimCronPump? Crons { get; set; }

        /// <summary>
        /// The tick thread hands a wake to the control socket through this; the
        /// socket handler sets it when the bot process connects. The tick then
        /// waits for the yields (SimConductor.WaitForYields).
        /// </summary>
        public static Action<long, IReadOnlyList<SimWake>>? WakeSink { get; set; }

        /// <summary>
        /// FOLKIDLE_SIM_LOCKSTEP=1, honoured only where FOLKIDLE_TICK_SPEED is:
        /// dev tools on and not Production. Production can never wait for a bot.
        /// </summary>
        public static bool ResolveEnabled(string? flag, bool devToolsEnabled)
            => devToolsEnabled && flag?.Trim() == "1";

        /// <summary>FOLKIDLE_SIM_QUANTUM (ticks, default 10); acceptance compares 1 and 10.</summary>
        public static int ResolveQuantum(string? flag)
            => int.TryParse(flag?.Trim(), out int q) && q >= 1 && q <= 600 ? q : SimConductor.DefaultQuantumTicks;
    }
}
