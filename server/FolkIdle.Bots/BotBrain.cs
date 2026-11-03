using System.Text.Json;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// A bot that plays the whole game the way a careful player does, through
    /// the same WebSocket commands and REST routes the web client uses.
    ///
    /// The brain is split by system, one partial file each:
    /// - BotBrain.cs: the loop, logins, timers and the command queue;
    /// - BotBrain.Combat.cs: what to fight, when to fish and scout, the larder;
    /// - BotBrain.Build.cs: attributes, skill tree and gear, chosen by the
    ///   server's own projection (/api/v1/dev/projection/whatif);
    /// - BotBrain.Economy.cs: village, crafting (tools, potions), the potion
    ///   shelves, gathering trips, forge, workshop, loot chests, mail, quests,
    ///   achievements;
    /// - BotBrain.Events.cs: world boss, dungeons, the Delve, boss ascension;
    /// - BotBrain.Lineage.cs: extra characters, breeding, inheritance, rebirth.
    ///
    /// Modul: ONE WEBSOCKET COMMAND PER PASS, WITH JITTER. ClientInputThrottler
    /// drops a sixth packet inside 100 ms, ValidateCommand refuses a gameplay
    /// command within 100 ms of the last one, and the macro detector bans on a
    /// coefficient of variation in command timing below 0.05. The loop sends at
    /// most one command per pass and sleeps a uniform 0.4-1.2 s between
    /// passes (CV about 0.29). It was 1.2-3.0 s until 2026-11-01; at x100 that
    /// left a casual bot's 20-game-minute check-in (12 wall seconds) room for
    /// four commands. REST chores are not rate limited and run inside a pass.
    ///
    /// Modul: TIMERS COUNT GAME TIME. Every "every N minutes" below is game
    /// minutes (MilestoneLog.GameHours), so a decision happens as often per
    /// game day at x100 as at x20, and the bot's attention does not change with
    /// the speed the server runs at.
    /// </summary>
    public sealed partial class BotBrain
    {
        private const int CommandSpacingMinMs = 400;
        private const int CommandSpacingMaxMs = 1200;

        private BotClient _client = null!;
        private readonly Func<CancellationToken, Task<BotClient>> _connect;
        private readonly MilestoneLog _log;
        private readonly Random _rng;
        private readonly string _name;
        private readonly Persona _persona;

        private StateUpdatePacket _state;
        private readonly Dictionary<string, double> _nextDue = new();
        private readonly List<QueuedCommand> _queue = new();

        /// <summary>A WebSocket command a chore decided on, sent by the loop in priority order.</summary>
        private sealed record QueuedCommand(ClientCommandPacket Packet, string Note, int Priority, Action? After = null);

        /// <summary>The last snapshot seen, for the summary after the bot has logged out.</summary>
        public StateUpdatePacket? LastState { get; private set; }
        public long StateFrames { get; private set; }

        private readonly IBotTime _time;

        public BotBrain(Func<CancellationToken, Task<BotClient>> connect, MilestoneLog log, int seed, string name, Persona persona, IBotTime? time = null)
        {
            _connect = connect;
            _time = time ?? new WallBotTime(log, 1);
            _log = log;
            _rng = new Random(seed);
            _name = name;
            _persona = persona;
        }

        private double Now => _time.GameHours;

        /// <summary>True when the timer <paramref name="key"/> has run out; it is then rearmed for <paramref name="everyGameMinutes"/>.</summary>
        private bool Due(string key, double everyGameMinutes)
        {
            if (_nextDue.TryGetValue(key, out double due) && Now < due) return false;
            _nextDue[key] = Now + everyGameMinutes / 60.0;
            return true;
        }

        /// <summary>Makes <paramref name="key"/> due on the next pass.</summary>
        private void Expire(string key) => _nextDue.Remove(key);

        private void Enqueue(ClientCommandPacket packet, string note, int priority = 50, Action? after = null)
        {
            // One of each kind waiting at a time: a chore that runs again
            // before its command went out replaces it rather than doubling it.
            _queue.RemoveAll(q => q.Packet.Command == packet.Command && q.Packet.TargetId == packet.TargetId
                && q.Packet.TargetSlotIndex == packet.TargetSlotIndex && q.Packet.TargetBuildingId == packet.TargetBuildingId);
            _queue.Add(new QueuedCommand(packet, note, priority, after));
        }

        private void Event(string text) => _log.Event(_name, text);
        private void Count(string counter) => _log.Count(_name, counter);

        /// <summary>
        /// Modul: A REAL LOGOUT BETWEEN CHECK-INS (2026-11-01). Casual and idle
        /// bots disconnect when their check-in ends and log in again at the
        /// next one, so the server's own offline catch-up and offline cap decide
        /// what the time away earned - the path every real player who closes
        /// the tab takes. This needed the game clock: offline time is measured
        /// from LastLogoutTimestamp, which is GameClock time, so at x20 an
        /// eight-game-hour gap is 24 wall minutes rather than eight hours.
        /// </summary>
        public async Task RunAsync(DateTime stopAtUtc, CancellationToken token)
        {
            _client = await _connect(token);
            if (_state.PlayerId == 0 && _client.TryGetState(out var first)) _time.Bind(first.PlayerId);
            bool connected = true;
            while (!token.IsCancellationRequested && DateTime.UtcNow < stopAtUtc)
            {
                bool checkedIn = _persona.IsOnline(Now, _time.GameUtc, _name);
                if (!checkedIn && connected)
                {
                    if (_client.TryGetState(out var last)) LastState = last;
                    StateFrames += _client.StateFrames;
                    await _client.DisposeAsync();
                    connected = false;
                    Event($"logged out (level {LastState?.CurrentLevel})");
                }
                else if (checkedIn && !connected)
                {
                    // Modul: A CHECK-IN IS SHORT IN WALL TIME (rebirth experiment,
                    // 2026-11-03). At x200 a 20-game-minute check-in is 6 wall
                    // seconds, and this path used to spend ~5 of them waiting: 3 s
                    // before the first connect attempt (a backoff meant for a
                    // dropped socket, not for a login hours after the last
                    // session closed) and a fixed 2 s for the first snapshot. An
                    // idle bot logged in 19 game minutes into its 20, never
                    // restarted the fight, and sat at level 4 for 61 game days.
                    // Connect at once, wait only until the first state frame, and
                    // log how far into the check-in the bot really got.
                    _client = await ReconnectAsync(token, waitBeforeFirstAttempt: false);
                    connected = true;
                    // Fresh eyes: everything is looked at again.
                    _nextDue.Clear();
                    _queue.Clear();
                    _pendingActivity = 0;
                    _needFightAfterLogin = true;
                    var settleUntil = DateTime.UtcNow.AddSeconds(2); // the login snapshot carries the offline summary
                    while (_client.StateFrames == 0 && DateTime.UtcNow < settleUntil) await Task.Delay(50, token);
                    if (_client.TryGetState(out var back))
                    {
                        double lateMinutes = _persona.CheckInEveryGameHours > 0 ? (Now % _persona.CheckInEveryGameHours) * 60.0 : 0;
                        Event($"logged in: offline {back.OfflineElapsedSeconds / 3600.0:0.00} game h (cap {back.OfflineCapSeconds / 3600.0:0.#} h), +{back.OfflineXpEarned} XP, +{back.OfflineGoldEarned} gold, level {back.CurrentLevel}, {lateMinutes:0.#} of {_persona.CheckInGameMinutes:0} check-in game min already gone");
                        Count("logins");
                        if (back.OfflineCapSeconds > 0 && back.OfflineElapsedSeconds >= back.OfflineCapSeconds) Count("offline_caps_hit");
                    }
                }

                if (connected && (!_client.IsOpen || SessionLooksDead()))
                {
                    Event(_client.IsOpen ? "session looks dead (no new state for 10 s, or the server answered NoSession); reconnecting" : "socket lost; reconnecting");
                    Count("reconnects");
                    StateFrames += _client.StateFrames;
                    await _client.DisposeAsync();
                    _client = await ReconnectAsync(token);
                    _queue.Clear();
                    _pendingActivity = 0;
                    _inventoryDirty = _projectionDirty = true;
                }

                if (connected) await TurnAsync(token);
                await Task.Delay(connected ? _rng.Next(CommandSpacingMinMs, CommandSpacingMaxMs) : 1000, token);
            }
            if (connected)
            {
                StateFrames += _client.StateFrames;
                await _client.DisposeAsync();
            }
        }

        /// <summary>One look at the game: refresh, chores, at most one command. Shared by both loops.</summary>
        private async Task TurnAsync(CancellationToken token)
        {
            if (!_client.TryGetState(out var state)) return;
            _state = state;
            LastState = state;
            _log.Observe(_name, state);
            if (Due("timeline", 60)) _log.Timeline(_name, state, DungeonBest, AscensionSteps);
            try
            {
                await RestChoresAsync(token);
                await OneCommandAsync(token);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"[{_name}] REST failed: {ex.Message}");
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[{_name}] unexpected JSON: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Lockstep (docs/design/LOCKSTEP_SIM.md, phase 2)
        // ------------------------------------------------------------------

        /// <summary>What wakes an ONLINE bot early. A logged-out bot sleeps to its next login.</summary>
        private const BotWake OnlineWakeOn = BotWake.Death | BotWake.ActivityHalted | BotWake.LevelUp | BotWake.DailyReset;

        /// <summary>The longest an online bot sleeps with nothing due - a person glances at the screen every few game minutes.</summary>
        private const double MaxOnlineSleepGameMinutes = 10;

        /// <summary>
        /// Modul: THE BOT'S ATTENTION IS GAME TIME, WHATEVER THE SPEED (phase 2).
        /// Under lockstep the server does not move until this bot yields, so a
        /// turn costs no game time and the gap between turns is exactly what the
        /// bot asks for: a human reaction (0.4-1.2 s) while it has a command to
        /// send or an activity to confirm, otherwise its next chore timer, at
        /// most ten game minutes, and earlier if the server sees it die, halt,
        /// level up or cross midnight. A 20-minute casual check-in is therefore
        /// the same check-in at x50 or x5000 - the defect that left idle9 at
        /// level 4 for 61 days at x200 cannot happen.
        /// </summary>
        /// <summary>
        /// The next chore timer still AHEAD of now. Modul: AN OVERDUE TIMER IS NOT
        /// A WAKE (first lockstep runs, 2026-11-03). A chore whose timer ran out
        /// but was skipped this turn (a guard returned before it called Due) keeps
        /// its old time, so "sleep until the earliest timer" meant "wake next
        /// game second" for ever: 65,000-94,000 idle wakes per optimal bot in 30
        /// game hours, and x219 instead of what the ticks allow. An overdue chore
        /// runs at the next natural wake, at most ten game minutes on.
        /// </summary>
        private double NextChoreDue()
        {
            double now = Now, best = double.MaxValue;
            foreach (double due in _nextDue.Values) if (due > now && due < best) best = due;
            return best;
        }

        public async Task RunLockstepAsync(double endGameHours, LockstepBotTime time, CancellationToken token)
        {
            await time.FirstTurn.WaitAsync(token);
            bool connected = false;
            try
            {
                while (!token.IsCancellationRequested && Now < endGameHours)
                {
                    if (!_persona.IsOnline(Now, _time.GameUtc, _name))
                    {
                        if (connected)
                        {
                            if (_client.TryGetState(out var last)) LastState = last;
                            StateFrames += _client.StateFrames;
                            await _client.DisposeAsync();
                            connected = false;
                            Event($"logged out (level {LastState?.CurrentLevel})");
                        }
                        double away = _persona.HoursUntilOnline(Now, _time.GameUtc, _name);
                        await _time.SleepAsync(Math.Min(endGameHours, Now + Math.Max(away, 1.0 / 3600)), BotWake.None, token);
                        continue;
                    }

                    if (!connected)
                    {
                        try
                        {
                            _client = await _connect(token);
                        }
                        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or System.Net.WebSockets.WebSocketException)
                        {
                            Event($"login failed: {ex.Message}");
                            await _time.DelayAsync(TimeSpan.FromSeconds(30), token);
                            continue;
                        }
                        connected = true;
                        _nextDue.Clear();
                        _queue.Clear();
                        _pendingActivity = 0;
                        _needFightAfterLogin = true;
                        _inventoryDirty = _projectionDirty = true;
                        // The login completes on the server's next ticks, and the
                        // conductor holds this bot until its player has had a
                        // snapshot - so a game second or two is normal.
                        for (int i = 0; i < 120 && _client.StateFrames == 0 && _client.IsOpen; i++)
                        {
                            await _time.DelayAsync(TimeSpan.FromSeconds(1), token);
                        }
                        if (!_client.TryGetState(out var back))
                        {
                            Event("logged in but no state arrived within 2 game minutes; reconnecting");
                            Count("reconnects");
                            await _client.DisposeAsync();
                            connected = false;
                            continue;
                        }
                        _time.Bind(back.PlayerId);
                        Event($"logged in: offline {back.OfflineElapsedSeconds / 3600.0:0.00} game h (cap {back.OfflineCapSeconds / 3600.0:0.#} h), +{back.OfflineXpEarned} XP, +{back.OfflineGoldEarned} gold, level {back.CurrentLevel}");
                        Count("logins");
                        if (back.OfflineCapSeconds > 0 && back.OfflineElapsedSeconds >= back.OfflineCapSeconds) Count("offline_caps_hit");
                    }

                    if (!_client.IsOpen || _noSessionSeen)
                    {
                        Event(_client.IsOpen ? "the server answered NoSession; reconnecting" : "socket lost; reconnecting");
                        Count("reconnects");
                        _noSessionSeen = false;
                        StateFrames += _client.StateFrames;
                        await _client.DisposeAsync();
                        connected = false;
                        continue;
                    }

                    long sentBefore = _client.CommandsSent;
                    await TurnAsync(token);
                    bool sent = _client.CommandsSent != sentBefore;
                    bool busy = sent || _queue.Count > 0 || _pendingActivity != 0;
                    Count(sent ? "wake_sent" : _queue.Count > 0 ? "wake_queue" : _pendingActivity != 0 ? "wake_pending" : "wake_idle");

                    double next = busy
                        ? Now + _rng.Next(CommandSpacingMinMs, CommandSpacingMaxMs + 1) / 3_600_000.0
                        : Math.Min(Now + MaxOnlineSleepGameMinutes / 60.0, NextChoreDue());
                    next = Math.Min(next, Now + _persona.HoursLeftInCheckIn(Now));
                    next = Math.Min(next, Now + _persona.HoursUntilAsleep(_time.GameUtc, _name));
                    next = Math.Min(next, endGameHours);
                    var woke = await _time.SleepAsync(next, OnlineWakeOn, token);
                    foreach (BotWake w in Enum.GetValues<BotWake>()) if (w != BotWake.None && (woke & w) != 0) Count("woke_" + w);
                }
            }
            finally
            {
                if (connected)
                {
                    if (_client.TryGetState(out var last)) LastState = last;
                    StateFrames += _client.StateFrames;
                    await _client.DisposeAsync();
                }
                _time.Leave();
            }
        }


        private long _framesSeen;
        private DateTime _framesSeenAt = DateTime.UtcNow;
        private bool _noSessionSeen;

        /// <summary>
        /// Modul: A SOCKET CAN BE OPEN AND THE SESSION GONE (x100 run,
        /// 2026-11-01). The server force-disconnected a bot (a serialization
        /// conflict in a command's write), the bot reconnected at once, and the
        /// old session's teardown then removed the new one - the socket stayed
        /// open, the state froze and every command went nowhere, for 60 game
        /// hours. A session with no new state for 10 wall seconds, or that the
        /// server reports as NoSession, is reconnected from scratch.
        /// </summary>
        private bool SessionLooksDead()
        {
            long frames = _client.StateFrames;
            if (frames != _framesSeen) { _framesSeen = frames; _framesSeenAt = DateTime.UtcNow; }
            bool dead = DateTime.UtcNow - _framesSeenAt > TimeSpan.FromSeconds(10) || _noSessionSeen;
            _noSessionSeen = false;
            return dead;
        }

        /// <summary>A fresh connection after a pause, retried with a growing wait instead of ending the bot.</summary>
        private async Task<BotClient> ReconnectAsync(CancellationToken token, bool waitBeforeFirstAttempt = true)
        {
            for (int attempt = 1; ; attempt++)
            {
                // The wait before the first attempt lets a dropped session's
                // teardown finish (see SessionLooksDead); a login after a logout
                // has nothing to wait for.
                if (attempt > 1 || waitBeforeFirstAttempt) await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)), token);
                try
                {
                    var client = await _connect(token);
                    _framesSeen = 0;
                    _framesSeenAt = DateTime.UtcNow;
                    return client;
                }
                catch (Exception ex) when (ex is TimeoutException or HttpRequestException or System.Net.WebSockets.WebSocketException)
                {
                    Event($"reconnect attempt {attempt} failed: {ex.Message}");
                    if (attempt >= 20) throw;
                }
            }
        }

        // ------------------------------------------------------------------
        // REST chores: what a player does on screens without a WebSocket
        // command, and the decisions that end in a queued command
        // ------------------------------------------------------------------

        /// <summary>
        /// Modul: THE FIGHT FIRST AFTER A LOGIN (x200 run, 2026-11-02). A
        /// 20-game-minute check-in is 6 wall seconds at x200, and the first
        /// pass ran every chore (all timers expire at login) before the loop
        /// could send a command - so an idle bot whose character had died
        /// offline logged out again without restarting the fight, and stood
        /// dead for ten game days. Until the main character has an activity
        /// again after a login, only the refresh runs.
        /// </summary>
        private bool _needFightAfterLogin;

        private async Task RestChoresAsync(CancellationToken token)
        {
            await RefreshAsync(token);
            if (_needFightAfterLogin)
            {
                if (_state.ActiveActivityId != 0 && _state.ActivityHaltReason == 0) _needFightAfterLogin = false;
                else return;
            }

            // Each chore guards itself with a game-time timer, so most passes
            // run none of them. Ordered by how much a delay costs.
            await BuildChoresAsync(token);
            await EconomyChoresAsync(token);
            await EventChoresAsync(token);
            await LineageChoresAsync(token);
        }

        // ------------------------------------------------------------------
        // WebSocket: at most one command per pass, most urgent first
        // ------------------------------------------------------------------

        private async Task OneCommandAsync(CancellationToken token)
        {
            // Right after a login the fight goes before attributes and food: a
            // check-in can be a second of wall time, and a character left dead
            // earns nothing until the next one.
            if (_needFightAfterLogin && TryFightCommand(out var restart))
            {
                await _client.SendCommandAsync(restart, token);
                return;
            }
            if (_state.UnspentAttributePoints > 0 && TryAttributeCommand(out var attribute))
            {
                await _client.SendCommandAsync(attribute, token);
                _projectionDirty = true;
                return;
            }
            // While fishing the catch stays in the chest until the trip is
            // done, or every fish would go straight to the larder and the trip
            // would never end.
            if (_trip == null && TryFoodCommand(out var food))
            {
                await _client.SendCommandAsync(food, token);
                _inventoryDirty = true;
                return;
            }
            if (_queue.Count > 0)
            {
                var next = _queue.OrderBy(q => q.Priority).First();
                _queue.Remove(next);
                Event(next.Note);
                await _client.SendCommandAsync(next.Packet, token);
                next.After?.Invoke();
                return;
            }
            if (TryFightCommand(out var fight))
            {
                await _client.SendCommandAsync(fight, token);
            }
        }
    }
}
