using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>Why a bot is being woken. A bitmask: one wake can carry several.</summary>
    [Flags]
    public enum SimWakeReason
    {
        None = 0,
        Planned = 1,
        Joined = 2,
        Death = 4,
        ActivityHalted = 8,
        LevelUp = 16,
        // A game-day boundary passed: dungeon tickets, today's world boss
        // strike, the login bonus. (A dungeon telegraph needs no event: the bot
        // reads it from the REST reply and yields for its own reaction.)
        DailyReset = 32,
    }

    /// <summary>One bot due at a wake: who, and why.</summary>
    public readonly record struct SimWake(string Bot, SimWakeReason Reasons);

    /// <summary>
    /// The lockstep simulation's clock owner (docs/design/LOCKSTEP_SIM.md).
    ///
    /// Modul: THE SERVER WAITS FOR THE BOTS, NOT THE OTHER WAY ROUND
    /// (2026-11-03). With FOLKIDLE_TICK_SPEED the engine ran N ticks per wall
    /// 100 ms and a bot answered in wall time, so how much a bot could do in a
    /// game minute depended on the speed: at x200 a 20-game-minute check-in was
    /// 6 wall seconds and an idle bot sat at level 4 for 61 game days. Under
    /// lockstep the engine runs tick T only after every bot due at or before T
    /// has yielded, and between wakes it runs flat out. A bot's attention is
    /// then whatever it asks for in GAME ticks, at any speed.
    ///
    /// Pure bookkeeping over tick numbers, with no clock and no socket, so the
    /// wake rules are testable on their own (SimConductorTests). The engine
    /// calls <see cref="PrepareTick"/>/<see cref="CompleteTick"/>; the control
    /// socket calls <see cref="Join"/>, <see cref="Yield"/> and friends.
    /// Thread-safe: the tick thread and the socket handler share it.
    /// </summary>
    public sealed class SimConductor
    {
        // Modul: ONE TICK, NOT ONE GAME SECOND (20-bot smoke, 2026-11-03). A
        // 10-tick quantum rounded every 4-12-tick reaction up to the same
        // boundary, so twenty bots' commands landed on one tick and fought each
        // other's Serializable transactions (40001 -> kicks) - a crowd no real
        // population makes. A 1-tick quantum keeps the reactions spread.
        public const int DefaultQuantumTicks = 1;
        public const int DefaultReactionMinTicks = 4;
        public const int DefaultReactionMaxTicks = 12;

        private sealed class Participant
        {
            public required string Bot;
            public long PlayerId;
            public long NextWake;
            public SimWakeReason WakeOn;
            public SimWakeReason Pending;
            public int ReactionMin = DefaultReactionMinTicks;
            public int ReactionMax = DefaultReactionMaxTicks;
            public bool Awake;
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, Participant> _bots = new(StringComparer.Ordinal);
        private readonly Dictionary<long, Participant> _byPlayer = new();
        private readonly Random _rng;
        private int _awakeCount;
        private long _tick;

        public SimConductor(int quantumTicks = DefaultQuantumTicks, int seed = 1, long startTick = 0)
        {
            if (quantumTicks < 1) throw new ArgumentOutOfRangeException(nameof(quantumTicks));
            QuantumTicks = quantumTicks;
            _rng = new Random(seed);
            _tick = startTick;
        }

        /// <summary>Wakes land on multiples of this, so bots due in the same game second think together.</summary>
        public int QuantumTicks { get; }

        /// <summary>The last tick the engine COMPLETED. Game time is this x TickIntervalMs past the anchor.</summary>
        public long Tick { get { lock (_gate) return _tick; } }

        public int ParticipantCount { get { lock (_gate) return _bots.Count; } }

        public int AwakeCount { get { lock (_gate) return _awakeCount; } }

        /// <summary>Ticks skipped because nobody was online and nobody was due.</summary>
        public long TicksJumped { get; private set; }

        /// <summary>The earliest planned wake, or null with no participants.</summary>
        public long? NextDue
        {
            get
            {
                lock (_gate) return NextDueLocked();
            }
        }

        public long QuantizeUp(long tick)
        {
            long q = QuantumTicks;
            return tick <= 0 ? 0 : (tick + q - 1) / q * q;
        }

        /// <summary>A bot enters. It is due at once (the next tick boundary), reason Joined.</summary>
        public void Join(string bot, int reactionMinTicks = DefaultReactionMinTicks, int reactionMaxTicks = DefaultReactionMaxTicks)
        {
            lock (_gate)
            {
                if (_bots.ContainsKey(bot)) return;
                var p = new Participant
                {
                    Bot = bot,
                    NextWake = _tick + 1,
                    Pending = SimWakeReason.Joined,
                };
                SetReaction(p, reactionMinTicks, reactionMaxTicks);
                _bots[bot] = p;
            }
        }

        /// <summary>Ties a bot to the player it signed in as, so that player's events can wake it.</summary>
        public void Bind(string bot, long playerId)
        {
            lock (_gate)
            {
                if (!_bots.TryGetValue(bot, out var p)) return;
                if (p.PlayerId != 0) _byPlayer.Remove(p.PlayerId);
                p.PlayerId = playerId;
                if (playerId > 0) _byPlayer[playerId] = p;
            }
        }

        /// <summary>A bot leaves for good (run over, socket gone). An awake bot stops being waited for.</summary>
        public void Leave(string bot)
        {
            lock (_gate)
            {
                if (!_bots.Remove(bot, out var p)) return;
                if (p.PlayerId != 0) _byPlayer.Remove(p.PlayerId);
                if (p.Awake) { p.Awake = false; _awakeCount--; Monitor.PulseAll(_gate); }
            }
        }

        public void LeaveAll()
        {
            lock (_gate)
            {
                foreach (var bot in _bots.Keys.ToList()) Leave(bot);
            }
        }

        /// <summary>
        /// A bot is done thinking. It sleeps until <paramref name="nextWakeTick"/>
        /// (never before the next tick boundary), or until an event in
        /// <paramref name="wakeOn"/> plus its reaction, whichever is first.
        /// </summary>
        public void Yield(string bot, long nextWakeTick, SimWakeReason wakeOn, int? reactionMinTicks = null, int? reactionMaxTicks = null)
        {
            lock (_gate)
            {
                if (!_bots.TryGetValue(bot, out var p)) return;
                if (reactionMinTicks.HasValue || reactionMaxTicks.HasValue)
                {
                    SetReaction(p, reactionMinTicks ?? p.ReactionMin, reactionMaxTicks ?? p.ReactionMax);
                }
                p.WakeOn = wakeOn;
                p.Pending = SimWakeReason.None;
                p.NextWake = Math.Max(QuantizeUp(nextWakeTick), _tick + 1);
                if (p.Awake)
                {
                    p.Awake = false;
                    _awakeCount--;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>
        /// Modul: THE CONDUCTOR ADDS THE REACTION, NOT THE BOT (owner review,
        /// 2026-11-03). A bot woken AT the event tick would call REST at that very
        /// tick, with no human delay at all. Woken at event + reaction, it sees
        /// the state at the moment it reacts - including having died in between -
        /// and needs no queue of delayed commands. Ignored unless the bot asked
        /// for this kind of event; an earlier planned wake is kept.
        /// </summary>
        public void Notify(long playerId, SimWakeReason kind, long eventTick)
        {
            lock (_gate)
            {
                if (!_byPlayer.TryGetValue(playerId, out var p)) return;
                if ((p.WakeOn & kind) == 0) return;
                int reaction = p.ReactionMin + _rng.Next(p.ReactionMax - p.ReactionMin + 1);
                long at = Math.Max(QuantizeUp(eventTick + reaction), _tick + 1);
                if (p.Awake)
                {
                    // Thinking already: it will see the state itself.
                    return;
                }
                if (at < p.NextWake) p.NextWake = at;
                if (at <= p.NextWake) p.Pending |= kind;
            }
        }

        /// <summary>A world-wide event (the day turned): every bot that asked for it, bound or not.</summary>
        public void NotifyEveryone(SimWakeReason kind, long eventTick)
        {
            lock (_gate)
            {
                foreach (var p in _bots.Values)
                {
                    if (p.Awake || (p.WakeOn & kind) == 0) continue;
                    int reaction = p.ReactionMin + _rng.Next(p.ReactionMax - p.ReactionMin + 1);
                    long at = Math.Max(QuantizeUp(eventTick + reaction), _tick + 1);
                    if (at < p.NextWake) p.NextWake = at;
                    if (at <= p.NextWake) p.Pending |= kind;
                }
            }
        }

        /// <summary>Notify for each reason set in <paramref name="kinds"/>; a no-op for None.</summary>
        public void NotifyAll(long playerId, SimWakeReason kinds, long eventTick)
        {
            if (kinds == SimWakeReason.None) return;
            foreach (SimWakeReason kind in Enum.GetValues<SimWakeReason>())
            {
                if (kind != SimWakeReason.None && (kinds & kind) != 0) Notify(playerId, kind, eventTick);
            }
        }

        /// <summary>
        /// Before the engine runs its next tick. Returns the tick to run - later
        /// than <see cref="Tick"/> + 1 only when nobody is online and nobody is
        /// due (the clock jump) - and the bots to wake before it runs. With no
        /// participants at all the result is null: hold the world still until
        /// bots arrive, so a run never starts hours into game time.
        ///
        /// Modul: A WAKE NEEDS A SNAPSHOT FROM THE TICK BEFORE IT. The engine
        /// broadcasts in lockstep only when a wake is coming, and an event in
        /// the last tick can make a bot due before that broadcast was planned.
        /// <paramref name="lacksFreshSnapshot"/> says which online players did
        /// not get one; their bots are left for one more tick and
        /// <c>NeedsBroadcast</c> asks the engine to send it during this one -
        /// a 100 ms delay, against a bot acting on a stale picture.
        /// </summary>
        public (long TickToRun, IReadOnlyList<SimWake> Wakes, bool NeedsBroadcast)? PrepareTick(int onlineSessions, Func<long, bool>? lacksFreshSnapshot = null)
        {
            lock (_gate)
            {
                if (_bots.Count == 0 || _awakeCount > 0) return null;
                long next = _tick + 1;
                long? due = NextDueLocked();
                if (onlineSessions == 0 && due.HasValue && due.Value > next)
                {
                    // Modul: NOBODY ONLINE, NOBODY DUE: JUMP. A logged-out player
                    // is not ticked; the login's offline catch-up pays for the
                    // time away, which is the path every real player who closes
                    // the tab takes - no second copy of game logic.
                    TicksJumped += due.Value - next;
                    _tick = due.Value - 1;
                    next = due.Value;
                }

                var wakes = new List<SimWake>();
                bool needsBroadcast = false;
                foreach (var p in _bots.Values)
                {
                    if (p.NextWake > next) continue;
                    if (p.PlayerId != 0 && lacksFreshSnapshot != null && lacksFreshSnapshot(p.PlayerId))
                    {
                        p.NextWake = next + 1;
                        needsBroadcast = true;
                        continue;
                    }
                    var reasons = p.Pending == SimWakeReason.None ? SimWakeReason.Planned : p.Pending;
                    p.Awake = true;
                    _awakeCount++;
                    wakes.Add(new SimWake(p.Bot, reasons));
                }
                wakes.Sort((a, b) => string.CompareOrdinal(a.Bot, b.Bot));
                return (next, wakes, needsBroadcast);
            }
        }

        /// <summary>Blocks until every woken bot has yielded or left, or the timeout runs out.</summary>
        public bool WaitForYields(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (_gate)
            {
                while (_awakeCount > 0)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(_gate, left);
                }
                return true;
            }
        }

        /// <summary>The engine finished <paramref name="tick"/>.</summary>
        public void CompleteTick(long tick)
        {
            lock (_gate)
            {
                if (tick > _tick) _tick = tick;
            }
        }

        /// <summary>True when some bot is due by <paramref name="tick"/> - the engine then broadcasts a fresh snapshot in the tick before.</summary>
        public bool AnyDueBy(long tick)
        {
            lock (_gate)
            {
                foreach (var p in _bots.Values) if (p.NextWake <= tick) return true;
                return false;
            }
        }

        private long? NextDueLocked()
        {
            long? min = null;
            foreach (var p in _bots.Values)
            {
                if (min == null || p.NextWake < min) min = p.NextWake;
            }
            return min;
        }

        private static void SetReaction(Participant p, int min, int max)
        {
            p.ReactionMin = Math.Max(1, min);
            p.ReactionMax = Math.Max(p.ReactionMin, max);
        }
    }
}
