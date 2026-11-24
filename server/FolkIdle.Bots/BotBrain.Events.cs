using System.Text.Json;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// The world boss, the three dungeons, the Delve and the boss Ascension
    /// ladder - the parts of the game a player visits on purpose, a few times a
    /// day.
    ///
    /// Two of them are judged against WALL-time windows (the dungeon's swipes,
    /// the shield wheel), so the bot answers them like a person at a screen:
    /// it waits a human reaction time before it swipes, and it takes the
    /// world boss's Auto strike (the floor multiplier) rather than playing the
    /// wheel, which is what a player who does not practise does.
    /// </summary>
    public sealed partial class BotBrain
    {
        /// <summary>Until this game hour a fight of its own (Ascension) owns the main character.</summary>
        private double _busyUntil;

        private async Task EventChoresAsync(CancellationToken token)
        {
            if (Due("worldboss", 60)) await WorldBossAsync(token);
            if (Due("dungeon", 60)) await DungeonsAsync(token);
            if (Due("delve", 360)) await DelveAsync(token);
            if (Due("ascension", 180)) await AscensionAsync(token);
        }

        // ------------------------------------------------------------------
        // World boss
        // ------------------------------------------------------------------

        /// <summary>
        /// Today's strike, as soon as it is there. With the shield wheel on
        /// (production's FOLKIDLE_BOSS_MINIGAME=wheel) it is the REST Auto strike
        /// on the first unbroken plate; with it off, opcode 32 on that plate.
        /// A strike during the registration phase is what registers the
        /// account (WorldBossLedger), so the bot does not wait for the reveal.
        /// </summary>
        private async Task WorldBossAsync(CancellationToken token)
        {
            var challenge = await _client.GetAsync("/api/v1/worldboss/challenge", token);
            string mode = challenge is { } c ? c.Str("Mode") : "off";
            int plate = Enumerable.Range(0, 5).FirstOrDefault(i => (_state.WorldBossBrokenPlateMask & (1 << i)) == 0);

            // Modul: the duel replaced the wheel on 2026-11-14 (#418) and takes
            // the same REST Auto strike (WorldBossStrikeService.AutoOpen covers
            // both). Matching "wheel" alone sent the bot to opcode 32, which a
            // minigame mode refuses, so no bot struck the boss after the switch.
            if (mode.Equals("wheel", StringComparison.OrdinalIgnoreCase) || mode.Equals("duel", StringComparison.OrdinalIgnoreCase))
            {
                var (status, body) = await _client.PostAsync("/api/v1/worldboss/strike", new { Mode = "Auto", Plate = plate }, token);
                string result = body is { } b ? b.Str("Result") : status.ToString();
                if (result == "Landed")
                {
                    long damage = body!.Value.Long("Damage");
                    Event($"world boss: auto strike on plate {plate} landed for {damage}");
                    Count("world_boss_strikes");
                }
                else if (result is not ("NoAttemptsLeft" or "NotActive" or "AlreadyDefeated" or "TooLateInWindow"))
                {
                    Event($"world boss strike: {result}");
                }
                return;
            }

            // Opcode 32: the strike the old screen sent. 1 = Active (BossEventState).
            if (_state.WorldBossEventState == 1 && _state.WorldBossAttemptCount < 1)
            {
                Enqueue(new ClientCommandPacket { Command = CommandType.AttackWorldBoss, TargetedBossId = 1, TargetedPlateIndex = (byte)plate },
                    $"world boss: strike plate {plate} (opcode 32)", 25, () => Count("world_boss_strikes"));
            }
        }

        // ------------------------------------------------------------------
        // Dungeons
        // ------------------------------------------------------------------

        private readonly Dictionary<int, int> _dungeonLostAtLevel = new();

        private static readonly string[] PerkPreference =
        {
            "fury", "keen", "brutal", "leech", "vigour", "iron", "bastion", "swift", "evade", "wind", "feast", "quickstep",
            "glass", "reckless", "bulwark", "bloodlust",
        };

        /// <summary>
        /// Every ticket gets spent. Each dungeon pushes its next stage; after a
        /// lost push it sweeps its best stage (paid as won, no fight) until the
        /// character has gained two levels, then pushes again.
        /// </summary>
        private async Task DungeonsAsync(CancellationToken token)
        {
            var view = await _client.GetAsync("/api/v1/dungeon", token);
            if (view is not { } v || !v.Bool("Open")) return;
            int[] party = v.Arr("Roster").Select(r => r.Int("Slot")).Where(s => s is 1 or 2).ToArray();
            NoteProgress(dungeon: v.Arr("Dungeons").Select(d => d.Int("BestStage")).DefaultIfEmpty(0).Max());

            foreach (var d in v.Arr("Dungeons"))
            {
                int kind = d.Int("Kind");
                int tickets = d.Int("Tickets");
                if (tickets <= 0) continue;
                int best = d.Int("BestStage");
                var stages = d.Arr("Stages").ToList();
                int push = best + 1;
                bool pushOpen = stages.Any(s => s.Int("Stage") == push && s.Str("LockedReason").Length == 0);
                bool waiting = _dungeonLostAtLevel.TryGetValue(kind, out int lostAt) && _state.CurrentLevel < lostAt + 2;

                if (best > 0 && (waiting || !pushOpen))
                {
                    // Modul: one sweep spends every ticket held (#475), the way a
                    // player presses "sweep all"; before, the bot sent no Count and
                    // spent one ticket per chore pass, so tickets piled up.
                    var (status, body) = await _client.PostAsync("/api/v1/dungeon/run", new { Kind = kind, Stage = best, Sweep = true, Count = tickets }, token);
                    string outcome = body is { } b ? b.Str("Outcome") : status.ToString();
                    int runs = body is { } rb ? Math.Max(1, rb.Obj("Reward").Int("Runs", 1)) : 1;
                    Event($"dungeon {d.Str("Name")}: swept stage {best} x{runs} ({outcome})");
                    if (outcome == "Ok") for (int i = 0; i < runs; i++) Count("dungeon_sweeps");
                    _inventoryDirty = true;
                    return; // one run per chore pass
                }
                if (!pushOpen) continue;

                bool won = await PlayDungeonAsync(kind, push, party, d.Str("Name"), token);
                if (!won) _dungeonLostAtLevel[kind] = _state.CurrentLevel;
                else _dungeonLostAtLevel.Remove(kind);
                _inventoryDirty = true;
                Expire("dungeon"); // more tickets may be waiting
                return;
            }
        }

        /// <summary>
        /// One live run: telegraph, a human-paced answer, a bonus pick, until the
        /// result. The bot reads the telegraph correctly; it misses one in
        /// <see cref="DungeonMissOneIn"/> the way a hand on a phone does.
        /// </summary>
        private const int DungeonMissOneIn = 12;

        private async Task<bool> PlayDungeonAsync(int kind, int stage, int[] party, string name, CancellationToken token)
        {
            var (status, seg) = await _client.PostAsync("/api/v1/dungeon/run", new { Kind = kind, Stage = stage, Party = party }, token);
            if (status is < 200 or >= 300 || seg is not { } s || s.Str("Outcome") != "Ok")
            {
                Event($"dungeon {name} stage {stage}: {(seg is { } x ? x.Str("Outcome") + " " + x.Str("Reason") : status.ToString())}");
                return false;
            }
            string runId = s.Str("RunId");
            int dodges = 0, misses = 0;
            var picks = new List<string>();
            for (int guard = 0; guard < 300 && !token.IsCancellationRequested; guard++)
            {
                if (s.Bool("Finished"))
                {
                    bool won = s.Obj("Result").Bool("Won");
                    var reward = s.Obj("Reward");
                    Event($"dungeon {name} stage {stage}: {(won ? "WON" : "lost")} (dodged {dodges}, missed {misses}, bonuses {string.Join("/", picks)}; reward {reward.Long("Xp")} XP, {reward.Long("Gold")} gold, {reward.Int("Diamonds")} diamonds)");
                    Count(won ? "dungeon_wins" : "dungeon_losses");
                    return won;
                }

                JsonElement? next;
                var offer = s.Arr("Offer").ToList();
                if (offer.Count > 0)
                {
                    string perk = PerkPreference.FirstOrDefault(p => offer.Any(o => o.Str("Id") == p)) ?? offer[0].Str("Id");
                    picks.Add(perk);
                    (_, next) = await _client.PostAsync("/api/v1/dungeon/act", new { RunId = runId, Action = "pick", Perk = perk }, token);
                }
                else if (s.Bool("Paused"))
                {
                    var (_, tele) = await _client.PostAsync("/api/v1/dungeon/act", new { RunId = runId, Action = "telegraph" }, token);
                    if (tele is not { } t || t.Str("Outcome") != "Ok") return false;
                    var dirs = t.Arr("Dirs").Select(e => e.GetString() ?? "").ToList();
                    int window = t.Int("WindowMs");
                    // A person reads, then swipes: about 350 ms plus 120-200 ms a swipe,
                    // never under the server's 90 ms floor a swipe, never past the window.
                    int wait = Math.Min(Math.Max(dirs.Count * 95, 350 + dirs.Count * _rng.Next(120, 200)), Math.Max(dirs.Count * 95, window - 150));
                    await _time.DelayAsync(TimeSpan.FromMilliseconds(wait), token);
                    bool miss = _rng.Next(DungeonMissOneIn) == 0;
                    if (miss && dirs.Count > 0) dirs[0] = dirs[0] == "up" ? "down" : "up";
                    if (miss) misses++; else dodges++;
                    (_, next) = await _client.PostAsync("/api/v1/dungeon/act", new { RunId = runId, Action = "answer", Dirs = dirs }, token);
                }
                else
                {
                    return false; // neither paused nor finished: the server lost the run
                }
                if (next is not { } n || n.Str("Outcome") != "Ok") return false;
                s = n;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // The Delve
        // ------------------------------------------------------------------

        /// <summary>
        /// Once in a while, when the entry fee is under a fifth of the gold
        /// above the reserve and the week's diamond ceiling is not reached: a
        /// run, always through the door with the best shown odds (a hidden door
        /// counts as even), banked as soon as no door offers 65% or better once
        /// something is banked, and banked at the landing rather than
        /// descending into the Deep.
        /// </summary>
        private async Task DelveAsync(CancellationToken token)
        {
            var view = await _client.GetAsync("/api/v1/delve", token);
            if (view is not { } v) return;
            if (!v.Bool("Active"))
            {
                long spare = _state.Gold - GoldReserve;
                long fee = v.Long("EntryFeeForNextRun");
                // Modul: a fee of 0 is the day's FREE run (2026-11-20,
                // FreeRunToday), not a missing field - the old "<= 0 means
                // skip" made the bot the one player who never took it.
                bool free = v.Bool("FreeRunToday") || fee <= 0;
                if (!free && fee * 5 > spare) return;
                if (v.Int("WeeklyDiamondCeiling") > 0 && v.Int("DiamondsEarnedThisWeek") >= v.Int("WeeklyDiamondCeiling")) return;
                var (_, started) = await _client.PostAsync("/api/v1/delve/start", new { }, token);
                if (started is not { } st || st.Str("Result") != "Ok") return;
                Event(free ? "delve: started (free run)" : $"delve: started (fee {fee})");
                Count("delve_runs");
                v = st.Obj("View");
            }

            for (int guard = 0; guard < 40 && v.Bool("Active") && !token.IsCancellationRequested; guard++)
            {
                var odds = v.Arr("DoorOdds").Select(e => e.GetDouble()).Select(o => o < 0 ? 0.5 : o).ToList();
                double bestOdds = odds.DefaultIfEmpty(0).Max();
                bool banked = v.Int("DiamondsIfBankedNow") > 0;
                if (v.Bool("AtLanding") || odds.Count == 0 || (banked && bestOdds < 0.65))
                {
                    var (_, bank) = await _client.PostAsync("/api/v1/delve/bank", new { }, token);
                    if (bank is { } b) Event($"delve: banked at floor {v.Int("CurrentFloor")} ({b.Str("Result")}, {b.Int("DiamondsGranted")} diamonds, {b.Long("GoldReturned")} gold)");
                    if (bank is { } b2 && b2.Int("DiamondsGranted") > 0) for (int i = 0; i < b2.Int("DiamondsGranted"); i++) Count("delve_diamonds");
                    return;
                }
                int door = odds.IndexOf(bestOdds);
                var (_, step) = await _client.PostAsync("/api/v1/delve/door", new { Door = door }, token);
                if (step is not { } r) return;
                string result = r.Str("Result");
                if (result == "RunLost")
                {
                    Event($"delve: lost at floor {v.Int("CurrentFloor")} (door odds {bestOdds:0.00})");
                    return;
                }
                v = r.Obj("View");
            }
        }

        // ------------------------------------------------------------------
        // Boss Ascension
        // ------------------------------------------------------------------

        private readonly Dictionary<int, (int Step, int Level)> _ascensionFailed = new();
        private (int Region, int Step)? _ascensionPending;
        private bool _ascensionArmedSeen;
        private uint _ascensionSentAfterTick;
        private bool _ascensionResultLogged;
        private bool _ascensionOver;
        private bool _ascensionRechecked;

        /// <summary>
        /// Modul: WATCH THE ATTEMPT, DO NOT GUESS IT (2026-11-22). Run D logged
        /// region 1 step 1 "failed" over and over with the boss dying in a few
        /// seconds and no time limit, and the log could not say why: the bot
        /// held the character for 30 seconds and read the ladder three hours
        /// later. Every turn this now reads the wire - the command's result
        /// code, and AscensionStep, which is the armed step and drops to 0
        /// when the attempt ends - so it logs the refusal or the activity the
        /// attempt ended on, and releases the hold the moment it is over.
        /// </summary>
        private void WatchAscension()
        {
            if (_ascensionPending is not { } pending) return;

            if (!_ascensionResultLogged)
            {
                foreach (var (code, tick) in new[]
                {
                    (_state.CommandResult0_Code, _state.CommandResult0_Tick), (_state.CommandResult1_Code, _state.CommandResult1_Tick),
                    (_state.CommandResult2_Code, _state.CommandResult2_Tick), (_state.CommandResult3_Code, _state.CommandResult3_Tick),
                })
                {
                    var result = (CommandResultCode)code;
                    if (tick <= _ascensionSentAfterTick) continue;
                    if (result is CommandResultCode.AscensionBossNotDefeated or CommandResultCode.AscensionStepLocked or CommandResultCode.NodeOccupied)
                    {
                        Event($"ascension: region {pending.Region} step {pending.Step} refused ({result})");
                        _ascensionResultLogged = true;
                        _busyUntil = Now;
                    }
                    else if (result == CommandResultCode.AscensionTooSlow)
                    {
                        Event($"ascension: region {pending.Region} step {pending.Step} kill too slow, still armed");
                        _ascensionResultLogged = true;
                    }
                }
            }

            if (_state.AscensionStep == pending.Step) { _ascensionArmedSeen = true; return; }
            if (_ascensionArmedSeen && !_ascensionOver && _state.AscensionStep == 0)
            {
                // Over: cleared, died, or the activity moved. The ladder read
                // says which of the first; the rest is on the wire now.
                if (_state.ActiveActivityId != _ascensionBossId || _state.ActivityHaltReason != 0)
                    Event($"ascension: region {pending.Region} step {pending.Step} ended off the boss (activity {_state.ActiveActivityId}, halt {_state.ActivityHaltReason})");
                _ascensionOver = true;
                _busyUntil = Now;
                Expire("ascension"); // read the ladder on the next turn
            }
        }

        private long _ascensionBossId;
        private byte _ascensionStartDeathTick;

        /// <summary>
        /// The next step of the lowest beaten boss's ladder, when the plain boss
        /// is a fight that holds without food and dies inside a third of the
        /// step's time limit (the steps make it harder). A failed step waits
        /// three levels.
        /// </summary>
        private async Task AscensionAsync(CancellationToken token)
        {
            if (_busyUntil > Now) return;
            var view = await _client.GetAsync("/api/v1/boss-ascension", token);
            if (view is not { } v) return;

            NoteProgress(ascension: v.Arr("Bosses").Sum(b => b.Int("HighestStep")));
            if (_ascensionPending is { } pending)
            {
                var row = v.Arr("Bosses").FirstOrDefault(b => b.Int("Region") == pending.Region);
                if (row.Int("HighestStep") >= pending.Step) { Event($"ascension: region {pending.Region} step {pending.Step} CLEARED"); Count("ascension_steps"); }
                else if (_state.LastDeathTick == _ascensionStartDeathTick && !_ascensionRechecked)
                {
                    // Modul: THE LADDER LAGS THE KILL (run G, 2026-11-24). The
                    // step drops off the wire the tick the boss dies, but the
                    // ladder is read from the database, which hears of the clear
                    // a flush later. Half of run G's "failed, no death" steps were
                    // followed straight away by the NEXT step - they had cleared.
                    // Without a death, read the ladder once more a few game
                    // minutes on before calling it a failure.
                    _ascensionRechecked = true;
                    _nextDue["ascension"] = Now + 5 / 60.0;
                    return;
                }
                else
                {
                    _ascensionFailed[pending.Region] = (pending.Step, _state.CurrentLevel);
                    // Modul: SAY HOW IT FAILED (run E, 2026-11-24). #482's watch
                    // logged a reason for 2 of 63 failures; the rest ended between
                    // two frames. The death tick and what the wire shows now name
                    // the likely end: a death to the boss, or a disarm never seen.
                    string died = _state.LastDeathTick != _ascensionStartDeathTick
                        ? $"died to {MonsterName((long)_state.LastDeathMonsterId)}" : "no death";
                    Event($"ascension: region {pending.Region} step {pending.Step} failed ({died}; armed seen {_ascensionArmedSeen}, wire step {_state.AscensionStep}, activity {_state.ActiveActivityId}, halt {_state.ActivityHaltReason}, hp {_state.PlayerHp})");
                }
                _ascensionPending = null;
                _ascensionArmedSeen = false;
                _ascensionOver = false;
            }

            foreach (var boss in v.Arr("Bosses").Where(b => b.Bool("BossDefeated")).OrderBy(b => b.Int("Region")))
            {
                int region = boss.Int("Region");
                int step = boss.Int("NextStep");
                if (step <= boss.Int("HighestStep")) continue; // ladder complete
                if (_ascensionFailed.TryGetValue(region, out var failed) && failed.Step == step && _state.CurrentLevel < failed.Level + 3) continue;
                var stepView = boss.Arr("Steps").FirstOrDefault(s => s.Int("Step") == step);
                if (!stepView.Bool("Startable")) continue;
                int limit = stepView.Int("TimeLimitSeconds");
                var estimate = _projection is { } p ? p.Arr("Monsters").FirstOrDefault(e => e.Int("MonsterId") == boss.Int("BossMonsterId")) : default;
                if (estimate.ValueKind != JsonValueKind.Object || !estimate.Bool("SurvivesWithoutFood")) continue;
                if (limit > 0 && estimate.Num("SecondsPerKill") * 3 > limit) continue;
                // Modul: NOT ON THE WAY OUT (run F, 2026-11-24). A logout ends
                // the attempt, and 15 of run F's 18 failures were casual bots
                // starting a step in the last minutes of a check-in and logging
                // out on it. Start only when the hold fits before the bot leaves.
                double holdSeconds = Math.Max(limit + 30, estimate.Num("SecondsPerKill") * 10 + 60);
                double hoursLeft = Math.Min(_persona.HoursLeftInCheckIn(Now), _persona.HoursUntilAsleep(_time.GameUtc, _name));
                if (hoursLeft * 3600 < Math.Min(holdSeconds, 1800)) return;

                _ascensionPending = (region, step);
                _ascensionBossId = boss.Int("BossMonsterId");
                _ascensionArmedSeen = false;
                _ascensionResultLogged = false;
                _ascensionOver = false;
                _ascensionRechecked = false;
                _ascensionStartDeathTick = _state.LastDeathTick;
                _ascensionSentAfterTick = Math.Max(Math.Max(_state.CommandResult0_Tick, _state.CommandResult1_Tick), Math.Max(_state.CommandResult2_Tick, _state.CommandResult3_Tick));
                // The hold: a step with no limit used to get 30 seconds, after
                // which the fight loop could walk the character off the boss.
                // Ten estimated kills plus a minute, at most 30 game minutes;
                // WatchAscension releases it as soon as the attempt ends.
                _busyUntil = Now + Math.Min(holdSeconds, 1800) / 3600.0;
                Enqueue(new ClientCommandPacket { Command = CommandType.StartBossAscension, TargetId = region, SecondaryId = step },
                    $"ascension: region {region} step {step} ({stepView.Str("Summary")}; boss dies in {estimate.Num("SecondsPerKill"):0.0} s, limit {limit} s)", 15);
                Expire("ascension");
                return;
            }
        }
    }
}
