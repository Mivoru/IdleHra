using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>
    /// What a lockstep bot can be woken by, read off one player's tick: the
    /// payload before and after, compared (docs/design/LOCKSTEP_SIM.md, phase 1c).
    ///
    /// Modul: ONE PLACE, NOT ONE HOOK PER CAUSE. Deaths, halts and level-ups each
    /// have several writers in the tick (combat, the larder, the automation
    /// rules, the inventory census), and a hook at each would be another list
    /// that silently stops at the eighth entry. Comparing the payload before and
    /// after ProcessTick sees every writer, including ones added later. Costs
    /// four field reads per player per tick; with lockstep off the diff is
    /// handed to a null conductor and dropped.
    /// </summary>
    public readonly struct LockstepEventProbe
    {
        public readonly int Level;
        public readonly long Deaths;
        public readonly byte Halt;
        public readonly long Activity;

        private LockstepEventProbe(int level, long deaths, byte halt, long activity)
        {
            Level = level;
            Deaths = deaths;
            Halt = halt;
            Activity = activity;
        }

        public static LockstepEventProbe Capture(in TickStatePayload p)
            => new(p.CurrentLevel, p.LifetimeDeaths, p.ActivityHaltReason, p.ActiveActivityId);

        public static SimWakeReason Diff(LockstepEventProbe before, in TickStatePayload after)
        {
            var kinds = SimWakeReason.None;
            if (after.CurrentLevel > before.Level) kinds |= SimWakeReason.LevelUp;
            if (after.LifetimeDeaths > before.Deaths) kinds |= SimWakeReason.Death;
            bool newlyHalted = after.ActivityHaltReason != ActivityHaltReason.None && after.ActivityHaltReason != before.Halt;
            bool stopped = before.Activity != 0 && after.ActiveActivityId == 0;
            if (newlyHalted || stopped) kinds |= SimWakeReason.ActivityHalted;
            return kinds;
        }
    }
}
