using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolkIdle.Bots
{
    /// <summary>What can cut a lockstep bot's sleep short. Mirrors the server's SimWakeReason.</summary>
    [Flags]
    public enum BotWake
    {
        None = 0,
        Planned = 1,
        Joined = 2,
        Death = 4,
        ActivityHalted = 8,
        LevelUp = 16,
        DailyReset = 32,
    }

    /// <summary>
    /// A bot's clock. Paced runs (FOLKIDLE_TICK_SPEED) read wall time x speed
    /// and wait with Task.Delay, as the bots always did. Lockstep runs
    /// (docs/design/LOCKSTEP_SIM.md, phase 2) read the conductor's tick, and
    /// every wait is a YIELD: the bot tells the server when to wake it, and
    /// the world does not move on until it has said so.
    /// </summary>
    public interface IBotTime
    {
        bool IsLockstep { get; }

        /// <summary>Game hours since the run started.</summary>
        double GameHours { get; }

        /// <summary>The server's game clock (UTC) - what the day boundary and the nightly sleep are read from.</summary>
        DateTime GameUtc { get; }

        /// <summary>A short wait inside a turn (a swipe's reaction, a login settling). Game time under lockstep.</summary>
        Task DelayAsync(TimeSpan gameTime, CancellationToken token);

        /// <summary>The end of a turn: sleep until <paramref name="untilGameHours"/> or an event in <paramref name="wakeOn"/>.</summary>
        Task<BotWake> SleepAsync(double untilGameHours, BotWake wakeOn, CancellationToken token);

        /// <summary>The player this bot signed in as, so the server can wake it on that player's events.</summary>
        void Bind(long playerId);

        /// <summary>The bot is done for the run.</summary>
        void Leave();
    }

    /// <summary>The paced clock: wall time x the multiplier, as before lockstep.</summary>
    public sealed class WallBotTime : IBotTime
    {
        private readonly MilestoneLog _log;
        private readonly DateTime _startUtc = DateTime.UtcNow;
        private readonly int _speed;

        public WallBotTime(MilestoneLog log, int speed)
        {
            _log = log;
            _speed = Math.Max(1, speed);
        }

        public bool IsLockstep => false;
        public double GameHours => _log.GameHours;
        public DateTime GameUtc => _startUtc + TimeSpan.FromHours(GameHours);
        public Task DelayAsync(TimeSpan gameTime, CancellationToken token) => Task.Delay(gameTime, token);
        public Task<BotWake> SleepAsync(double untilGameHours, BotWake wakeOn, CancellationToken token) => Task.FromResult(BotWake.Planned);
        public void Bind(long playerId) { }
        public void Leave() { }
    }
}
