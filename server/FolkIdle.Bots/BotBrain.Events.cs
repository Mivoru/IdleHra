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

            if (mode.Equals("wheel", StringComparison.OrdinalIgnoreCase))
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
                    var (status, body) = await _client.PostAsync("/api/v1/dungeon/run", new { Kind = kind, Stage = best, Sweep = true }, token);
                    string outcome = body is { } b ? b.Str("Outcome") : status.ToString();
                    Event($"dungeon {d.Str("Name")}: swept stage {best} ({outcome})");
                    if (outcome == "Ok") Count("dungeon_sweeps");
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
                    await Task.Delay(wait, token);
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
                if (v.Long("EntryFeeForNextRun") <= 0 || v.Long("EntryFeeForNextRun") * 5 > spare) return;
                if (v.Int("WeeklyDiamondCeiling") > 0 && v.Int("DiamondsEarnedThisWeek") >= v.Int("WeeklyDiamondCeiling")) return;
                var (_, started) = await _client.PostAsync("/api/v1/delve/start", new { }, token);
                if (started is not { } st || st.Str("Result") != "Ok") return;
                Event($"delve: started (fee {v.Long("EntryFeeForNextRun")})");
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
                else { _ascensionFailed[pending.Region] = (pending.Step, _state.CurrentLevel); Event($"ascension: region {pending.Region} step {pending.Step} failed"); }
                _ascensionPending = null;
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

                _ascensionPending = (region, step);
                _busyUntil = Now + Math.Max(10, limit + 30) / 3600.0;
                Enqueue(new ClientCommandPacket { Command = CommandType.StartBossAscension, TargetId = region, SecondaryId = step },
                    $"ascension: region {region} step {step} ({stepView.Str("Summary")}; boss dies in {estimate.Num("SecondsPerKill"):0.0} s, limit {limit} s)", 15);
                Expire("ascension");
                return;
            }
        }
    }
}
