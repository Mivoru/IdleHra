using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Network
{
    public partial class NetworkBroadcastSystem
    {
        // The three dungeons (CHESTS_DUNGEONS_DIAMONDS.md 2.6a, 2.8). REST, like
        // the Delve and the loot chests: a run is one request that the server
        // decides and writes; nothing is added to StateUpdatePacket.
        // Modul: OPEN TO EVERYONE since 2026-10-31 (owner: "when the dungeon
        // is finished, give it to everyone"). It was admin-only while it was
        // rebuilt; the region 2 boss is the only gate now, and the admin test
        // runs (no ticket) were removed with the gate.

        // Modul: DEV ONLY - region bosses counted as beaten for the dungeons,
        // in memory and never written. The dev fixture has not beaten the
        // region 2 boss, so the lobby is shut for it; exercise.mjs opens it
        // here, runs its stages and clears it, leaving the codex untouched.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _devDungeonBossMask = new();

        private byte DungeonBossMask(long playerId, byte live)
            => DevToolsEnabled() && _devDungeonBossMask.TryGetValue(playerId, out byte extra) ? (byte)(live | extra) : live;

        private async Task<bool> TryHandleDungeonAsync(HttpListenerContext context, string requestPath)
        {
            if (!requestPath.StartsWith("/api/v1/dungeon", StringComparison.Ordinal)) return false;
            string method = context.Request.HttpMethod;
            if (requestPath == "/api/v1/dungeon" && method == "GET") { await HandleDungeonView(context); return true; }
            if (requestPath == "/api/v1/dungeon/run" && method == "POST") { await HandleDungeonRun(context); return true; }
            if (requestPath == "/api/v1/dungeon/act" && method == "POST") { await HandleDungeonAct(context); return true; }
            return false;
        }

        /// <summary>A copy of the live payload with the main character in the active register, or null offline.</summary>
        private async Task<PayloadSnapshot?> MainCharacterSnapshotAsync(long playerId)
        {
            if (_playerSessionRegistry == null) return null;
            var order = new PayloadSnapshotOrder { PlayerId = playerId };
            _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
            var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
            var snapshot = finished == order.Completion.Task ? order.Completion.Task.Result : null;
            if (snapshot == null) return null;
            var payload = snapshot.Value.Payload;
            SimulationEngine.SwapSlotIntoActiveRegister(ref payload, 0);
            // The build bound to the dungeon fights it (SkillLoadoutEngine); the copy, never the live tree.
            SkillLoadoutEngine.Apply(ref payload, SkillLoadoutEngine.Activity.Dungeon);
            return snapshot.Value with { Payload = payload };
        }

        private async Task HandleDungeonView(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null) { context.Response.StatusCode = 404; return; }
                var stale = DungeonLiveRuns.Current(playerId);
                if (stale != null && GameClock.ReactionUtcNow - stale.TouchedUtc > DungeonLiveRuns.AbandonAfter) await SettleAbandonedAsync(stale);

                var rows = await DungeonEngine.LoadAsync(db, playerId, GameClock.UtcNow);
                await db.SaveChangesAsync();

                var snapshot = await MainCharacterSnapshotAsync(playerId);
                byte mask = DungeonBossMask(playerId, snapshot?.Payload.DefeatedRegionBossMask ?? 0);
                bool open = DungeonEngine.IsOpen(mask);
                await WriteJsonAsync(context, new
                {
                    Open = open,
                    LockedReason = open ? string.Empty : DungeonEngine.StageLockedReason(1, mask, 0) ?? string.Empty,
                    Online = snapshot != null,
                    // The roster's champions, for the party picker: who stands in
                    // each slot and from which stage a dungeon takes them.
                    Roster = snapshot == null ? Array.Empty<object>() : Enumerable.Range(0, 3)
                        .Where(slot => SlotHoldsCharacter(snapshot.Value.Payload, slot))
                        .Select(slot =>
                        {
                            var power = DungeonRunner.ChampionPower(snapshot.Value.Payload, slot);
                            return (object)new { Slot = slot, UnlockStage = DungeonEngine.PartyUnlockStage(slot), power.Dps, power.MaxHp, power.Worn };
                        }).ToArray(),
                    DungeonEngine.TicketCap,
                    DungeonEngine.TicketsPerDay,
                    DungeonSimulator.Waves,
                    DungeonSimulator.MealCap,
                    Dungeons = DungeonBoss.All.Select(kind =>
                    {
                        var boss = DungeonBoss.For(kind);
                        var row = rows[kind];
                        return new
                        {
                            Kind = (int)kind,
                            Key = kind.ToString(),
                            Name = boss.DungeonName,
                            Boss = boss.BossName,
                            Tickets = row.Tickets,
                            BestStage = row.BestStage,
                            PartySlots = DungeonEngine.PartySlotsOpen(row.BestStage),
                            // Modul: THE NEXT STAGE, FORECAST (2026-11-13). Two of the
                            // three endgame accounts had sat on the same best stage for
                            // days with tickets piling up, and nothing said whether the
                            // next one was a near miss or a wall. One run of the same
                            // simulator the sweep and calibration use - expected values,
                            // NO dodges and no blessings, so it is the floor of what a
                            // live run does - with the whole open party and the larder.
                            Forecast = snapshot == null || row.BestStage >= DungeonSimulator.StageCount
                                || DungeonEngine.StageLockedReason(row.BestStage + 1, mask, row.BestStage) != null
                                ? null : ForecastFor(snapshot.Value.Payload, kind, row.BestStage + 1, DungeonEngine.PartySlotsOpen(row.BestStage)),
                            Stages = Enumerable.Range(1, DungeonSimulator.StageCount).Select(stage => new
                            {
                                Stage = stage,
                                Band = DungeonEngine.BandOf(stage),
                                LockedReason = DungeonEngine.StageLockedReason(stage, mask, row.BestStage) ?? string.Empty,
                                Cleared = stage <= row.BestStage,
                                FirstClearPaid = (row.FirstClearMask & (1 << stage)) != 0,
                                Reward = DungeonEngine.RewardLine(kind, stage),
                                Abilities = boss.AbilitiesAt(stage),
                            }).ToArray(),
                        };
                    }).ToArray(),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dungeon view error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// "Your road": the gate whose next stage comes closest - a win first,
        /// then the boss left lowest, then the most waves - as one line.
        /// </summary>
        private async Task<string?> DungeonRoadHintAsync(FolkIdleDbContext db, long playerId)
        {
            var snapshot = await MainCharacterSnapshotAsync(playerId);
            if (snapshot == null) return null;
            var payload = snapshot.Value.Payload;
            byte mask = DungeonBossMask(playerId, payload.DefeatedRegionBossMask);
            if (!DungeonEngine.IsOpen(mask)) return null;
            var stored = await db.DungeonProgress.AsNoTracking().Where(d => d.PlayerId == playerId).ToListAsync();

            (int Score, string Line)? best = null;
            foreach (var kind in DungeonBoss.All)
            {
                int bestStage = stored.FirstOrDefault(r => r.Kind == (int)kind)?.BestStage ?? 0;
                int next = bestStage + 1;
                if (next > DungeonSimulator.StageCount || DungeonEngine.StageLockedReason(next, mask, bestStage) != null) continue;
                var party = Enumerable.Range(0, Math.Min(3, DungeonEngine.PartySlotsOpen(bestStage))).Where(s => SlotHoldsCharacter(in payload, s)).ToList();
                if (party.Count == 0) party.Add(0);
                var food = DungeonEngine.BestFood(in payload);
                var runner = new DungeonRunner(payload, kind, next, food.ItemId, food.Count, party, challenges: false);
                runner.Advance();
                var r = runner.Result!;
                var boss = DungeonBoss.For(kind);
                int score = r.Won ? 1000 : r.ReachedBoss ? 500 + (100 - r.BossHpLeftPct) : r.WavesCleared * 10;
                string line = r.Won ? $"{boss.DungeonName} stage {next} is yours to take"
                    : r.ReachedBoss ? $"{boss.DungeonName} stage {next}: {boss.BossName} would keep {r.BossHpLeftPct}% without dodges"
                    : $"{boss.DungeonName} stage {next}: you would fall in wave {r.WavesCleared + 1} without dodges";
                if (best == null || score > best.Value.Score) best = (score, line);
            }
            return best?.Line;
        }

        private static object ForecastFor(TickStatePayload payload, DungeonKind kind, int stage, int partySlotsOpen)
        {
            var party = Enumerable.Range(0, Math.Min(3, partySlotsOpen)).Where(slot => SlotHoldsCharacter(in payload, slot)).ToList();
            if (party.Count == 0) party.Add(0);
            var food = DungeonEngine.BestFood(in payload);
            var runner = new DungeonRunner(payload, kind, stage, food.ItemId, food.Count, party, challenges: false);
            runner.Advance();
            var r = runner.Result!;
            return new { Stage = stage, r.Won, r.WavesCleared, r.ReachedBoss, r.BossHpLeftPct, Party = party.Count };
        }

        // POST /api/v1/dungeon/run {Kind, Stage, Party?, Sweep?}
        // A live run (slice 3): takes the ticket, simulates to the first
        // telegraph and answers with that segment and a RunId; /dungeon/act
        // carries it on. Sweep pays a beaten stage as won without the fight.
        private async Task HandleDungeonRun(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                int stage = 0;
                bool sweep = false;
                DungeonKind kind = DungeonKind.Equipment;
                var party = new System.Collections.Generic.List<int> { 0 };
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (parsed.RootElement.TryGetProperty("Stage", out var s) && s.TryGetInt32(out int sv)) stage = sv;
                    if (parsed.RootElement.TryGetProperty("Sweep", out var sw) && sw.ValueKind == JsonValueKind.True) sweep = true;
                    if (parsed.RootElement.TryGetProperty("Kind", out var k) && k.TryGetInt32(out int kv))
                    {
                        if (!Enum.IsDefined(typeof(DungeonKind), kv)) { context.Response.StatusCode = 400; return; }
                        kind = (DungeonKind)kv;
                    }
                    if (parsed.RootElement.TryGetProperty("Party", out var pa) && pa.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in pa.EnumerateArray())
                            if (e.TryGetInt32(out int slot) && slot is 1 or 2 && !party.Contains(slot)) party.Add(slot);
                    }
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                // A run still open is played out with its telegraphs missed and paid first.
                var previous = DungeonLiveRuns.Current(playerId);
                if (previous != null) await SettleAbandonedAsync(previous);

                // The fight is the live character's: no session, no run.
                var snapshot = await MainCharacterSnapshotAsync(playerId);
                if (snapshot == null)
                {
                    context.Response.StatusCode = 409;
                    await WriteJsonAsync(context, new { Outcome = "NoSession", Reason = "Your character is not online - reconnect and try again." });
                    return;
                }

                var payload = snapshot.Value.Payload;
                payload.DefeatedRegionBossMask = DungeonBossMask(playerId, payload.DefeatedRegionBossMask);
                var food = DungeonEngine.BestFood(payload);
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                // Experience 10 and Gold 5 inheritance milestones: the gates pay
                // minutes of a bigger hourly income.
                var (gateXp, gateGold) = InheritanceMilestones.DungeonIncome(in payload,
                    kind == DungeonKind.Xp ? DungeonEngine.BestHuntingXpPerHour(in payload, snapshot.Value.GlobalXpMultiplier, snapshot.Value.ActiveGlobalEventId) : 0,
                    kind == DungeonKind.Gold ? await GoldRewardScale.HourlyIncomeAsync(db, playerId, GameClock.UtcNow) : 0);
                var income = new DungeonEngine.Income(gateXp, gateGold);

                if (sweep)
                {
                    var (sOutcome, sReason, sResult, sReward, sTickets) = await DungeonEngine.RunAsync(
                        db, playerId, kind, stage, payload, food.ItemId, food.Count, income, GameClock.UtcNow, Random.Shared, sweep: true);
                    if (sResult != null) await AfterDungeonRunAsync(db, playerId, sResult, sReward, food.SlotIndex, food.ItemId);
                    await WriteJsonAsync(context, new { Outcome = sOutcome.ToString(), Reason = sReason ?? string.Empty, Finished = true, Result = sResult, Reward = sReward, Tickets = sTickets });
                    return;
                }

                // The party: champions whose slot holds a character and whose
                // place this dungeon's best stage has opened.
                var rows = await DungeonEngine.LoadAsync(db, playerId, GameClock.UtcNow);
                int open = DungeonEngine.PartySlotsOpen(rows[kind].BestStage);
                party = party.Where(slot => slot < open && SlotHoldsCharacter(in payload, slot)).ToList();
                db.ChangeTracker.Clear();

                var (outcome, reason, tickets) = await DungeonEngine.StartLiveAsync(db, playerId, kind, stage, payload.DefeatedRegionBossMask, GameClock.UtcNow);
                if (outcome != DungeonEngine.RunOutcome.Ok)
                {
                    await WriteJsonAsync(context, new { Outcome = outcome.ToString(), Reason = reason ?? string.Empty, Tickets = tickets });
                    return;
                }

                var run = new DungeonLiveRun
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    Kind = kind,
                    Stage = stage,
                    Runner = new DungeonRunner(payload, kind, stage, food.ItemId, food.Count, party, challenges: true),
                    Income = income,
                    FoodSlot = food.SlotIndex,
                    FoodItemId = food.ItemId,
                };
                DungeonLiveRuns.Put(run);
                run.Runner.Advance();
                await WriteJsonAsync(context, await SegmentAsync(db, run, tickets));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dungeon run error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private static bool SlotHoldsCharacter(in TickStatePayload payload, int slot) => slot switch
        {
            0 => true,
            1 => payload.Slot2_CharacterId != Guid.Empty,
            2 => payload.Slot3_CharacterId != Guid.Empty,
            _ => false,
        };

        // POST /api/v1/dungeon/act {RunId, Action, Dirs?, Perk?}
        // "telegraph": show the swipes (the window starts now, on the server);
        // "answer": judge the swipes - a dodge offers two bonuses, a miss lands a
        // heavy blow and a penalty and the fight goes on; "pick": take a bonus.
        private async Task HandleDungeonAct(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0) { context.Response.StatusCode = 401; return; }

                Guid runId = Guid.Empty;
                string action = string.Empty, perk = string.Empty;
                var dirs = new System.Collections.Generic.List<string>();
                try
                {
                    string body = await ReadBodyAsync(context);
                    using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    var root = parsed.RootElement;
                    if (root.TryGetProperty("RunId", out var r) && r.ValueKind == JsonValueKind.String) Guid.TryParse(r.GetString(), out runId);
                    if (root.TryGetProperty("Action", out var a) && a.ValueKind == JsonValueKind.String) action = a.GetString() ?? string.Empty;
                    if (root.TryGetProperty("Perk", out var pk) && pk.ValueKind == JsonValueKind.String) perk = pk.GetString() ?? string.Empty;
                    if (root.TryGetProperty("Dirs", out var d) && d.ValueKind == JsonValueKind.Array)
                        foreach (var e in d.EnumerateArray().Take(8))
                            if (e.ValueKind == JsonValueKind.String) dirs.Add(e.GetString() ?? string.Empty);
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var run = DungeonLiveRuns.Get(playerId, runId);
                if (run == null)
                {
                    await WriteJsonAsync(context, new { Outcome = "NoRun", Reason = "That run is over." });
                    return;
                }

                object? reply = null;
                lock (run.Gate)
                {
                    run.TouchedUtc = GameClock.ReactionUtcNow;
                    var runner = run.Runner;
                    bool advance = false;
                    switch (action)
                    {
                        case "telegraph" when runner.Paused && run.Offer == null:
                            if (run.Telegraph == null)
                            {
                                run.Telegraph = DungeonPerks.Telegraph(run.Stage, runner.PausedOnBoss, run.Rng);
                                run.WindowMs = DungeonPerks.WindowMs(run.Stage, runner.PausedOnBoss, runner.Mods.QuickstepPct);
                                run.IssuedUtc = GameClock.ReactionUtcNow;
                            }
                            double used = (GameClock.ReactionUtcNow - run.IssuedUtc).TotalMilliseconds;
                            reply = new { Outcome = "Ok", Dirs = run.Telegraph, WindowMs = Math.Max(0, run.WindowMs - (int)used) };
                            break;
                        case "answer" when runner.Paused && run.Telegraph != null && run.Offer == null:
                            double elapsed = (GameClock.ReactionUtcNow - run.IssuedUtc).TotalMilliseconds;
                            bool clean = DungeonPerks.Judge(run.Telegraph, dirs, run.WindowMs, elapsed);
                            run.Telegraph = null;
                            if (clean)
                            {
                                runner.Dodge();
                                run.Offer = DungeonPerks.Offer(runner.Mods, run.Rng);
                                if (run.Offer.Count == 0) { run.Offer = null; runner.Resume(); advance = true; }
                            }
                            else
                            {
                                runner.Miss(run.Rng);
                                advance = true;
                            }
                            break;
                        case "pick" when run.Offer != null:
                            if (!run.Offer.Any(o => o.Id == perk) || !runner.Choose(perk))
                            {
                                reply = new { Outcome = "Refused", Reason = "That bonus is not on offer." };
                                break;
                            }
                            run.Offer = null;
                            advance = true;
                            break;
                        default:
                            reply = new { Outcome = "Refused", Reason = "Not now." };
                            break;
                    }
                    if (advance) runner.Advance();
                }

                if (reply != null)
                {
                    await WriteJsonAsync(context, reply);
                    return;
                }
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                await WriteJsonAsync(context, await SegmentAsync(db, run, null));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Dungeon act error: {ex}");
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// What the client plays next: the events since the last answer, and
        /// either the pause (a telegraph, or bonuses to choose from) or the end
        /// with its reward, paid here exactly once.
        /// </summary>
        private async Task<object> SegmentAsync(FolkIdleDbContext db, DungeonLiveRun run, int? tickets)
        {
            var runner = run.Runner;
            IReadOnlyList<DungeonEvent> events;
            bool pay;
            lock (run.Gate)
            {
                events = runner.TakeNewEvents();
                pay = runner.Result != null && !run.Paid;
                if (pay) run.Paid = true;
            }
            DungeonEngine.RunReward? reward = null;
            int? left = tickets;
            if (pay)
            {
                DungeonLiveRuns.Remove(run);
                var (paid, t) = await DungeonEngine.FinishLiveAsync(db, run.PlayerId, run.Kind, run.Stage, runner.Result!.Won, run.Income, GameClock.UtcNow, Random.Shared);
                reward = paid;
                left = t;
                await AfterDungeonRunAsync(db, run.PlayerId, runner.Result, reward, run.FoodSlot, run.FoodItemId);
            }
            var m = runner.Mods;
            return new
            {
                Outcome = "Ok",
                Reason = string.Empty,
                RunId = run.Id,
                Events = events,
                Paused = runner.Paused,
                Offer = run.Offer,
                Finished = runner.Result != null,
                Result = runner.Result,
                Reward = reward,
                Tickets = left,
                PartySize = runner.PartySize,
                WaveMonsterIds = DungeonSimulator.WaveMonsterIdsFor(run.Stage),
                Mods = new { m.DamagePct, m.CritChancePct, m.CritDamagePct, m.MaxHpPct, m.ArmorPct, m.SpeedPct, m.LeechTenths, m.ExtraMeals, m.FeastPct, m.EvadePct, m.QuickstepPct, m.NoMeals },
                Schools = Enum.GetValues<DungeonSchool>().Where(x => x != DungeonSchool.None).Select(x => new { School = x.ToString(), Cards = m.CountOf(x) }).Where(x => x.Cards > 0).ToArray(),
                Sets = DungeonPerks.SetBonuses.Where(x => m.Sets.Contains(x.Id)).ToArray(),
            };
        }

        /// <summary>A run left alone: every telegraph missed, played out and paid.</summary>
        private async Task SettleAbandonedAsync(DungeonLiveRun run)
        {
            lock (run.Gate)
            {
                run.Offer = null;
                run.Telegraph = null;
                run.Runner.FinishAbandoned(run.Rng);
            }
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await SegmentAsync(db, run, null);
            DungeonLiveRuns.Remove(run);
        }

        /// <summary>The run's side effects on the live character: meals, XP, gold and diamonds.</summary>
        private async Task AfterDungeonRunAsync(FolkIdleDbContext db, long playerId, DungeonRunResult result, DungeonEngine.RunReward? reward, int foodSlot, int foodItemId)
        {
            // Meals eaten come out of the live larder, as a delta the tick applies.
            if (result.MealsEaten > 0 && foodSlot >= 0)
            {
                _playerSessionRegistry?.LarderSlotUpdateQueue.Enqueue(new LarderSlotUpdateNotification
                {
                    PlayerId = playerId,
                    SlotIndex = foodSlot,
                    ItemId = foodItemId,
                    Added = -result.MealsEaten,
                });
            }
            if (reward != null && reward.Xp > 0)
            {
                _playerSessionRegistry?.DungeonXpGrantQueue.Enqueue(new DungeonXpGrantNotification { PlayerId = playerId, Xp = reward.Xp });
            }
            if (reward != null && reward.Gold > 0)
            {
                // Modul: the gold moved in the database, off the tick - the
                // Delve's and the quest line's answer (see HandleQuestLineClaim).
                CommandQueue.Enqueue(new PlayerCommand
                {
                    PlayerId = playerId,
                    Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                });
            }
            if (reward != null && reward.Diamonds > 0)
            {
                int balance = await db.PlayerRecords.Where(p => p.Id == playerId).Select(p => p.PremiumDiamonds).FirstAsync();
                _playerSessionRegistry?.BillingSyncQueue.Enqueue(new BillingSyncNotification { PlayerId = playerId, PremiumDiamondsBalance = balance });
            }
        }

        // POST /api/v1/dev/dungeon/unlock {Regions} - dev only: count the bosses
        // of regions 1..Regions as beaten for the caller's dungeons (0 clears).
        private async Task HandleDevDungeonUnlock(HttpListenerContext context, long playerId)
        {
            int regions = 0;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Regions", out var r) && r.TryGetInt32(out int rv)) regions = rv;
            }
            catch (JsonException) { }
            byte mask = 0;
            for (int region = 1; region <= Math.Clamp(regions, 0, 5); region++)
                mask = BossFirstClearRules.MarkDefeated(mask, RaceUnlockRegistry.GetRegionBossMonsterId(region));
            if (mask == 0) _devDungeonBossMask.TryRemove(playerId, out _);
            else _devDungeonBossMask[playerId] = mask;
            await WriteJsonAsync(context, new { Regions = Math.Clamp(regions, 0, 5) });
        }

        // POST /api/v1/dev/dungeon/tickets {Kind, Tickets} - dev only: set the
        // caller's tickets in one dungeon (every dungeon when Kind is absent),
        // so exercise.mjs can run a stage and put them back.
        private async Task HandleDevDungeonTickets(HttpListenerContext context, long playerId)
        {
            int tickets = DungeonEngine.TicketCap;
            int kind = -1;
            try
            {
                string body = await ReadBodyAsync(context);
                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (parsed.RootElement.TryGetProperty("Tickets", out var t) && t.TryGetInt32(out int tv)) tickets = tv;
                if (parsed.RootElement.TryGetProperty("Kind", out var k) && k.TryGetInt32(out int kv)) kind = kv;
            }
            catch (JsonException) { }
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var rows = await DungeonEngine.LoadAsync(db, playerId, GameClock.UtcNow);
            foreach (var (k, row) in rows)
            {
                if (kind < 0 || (int)k == kind) row.Tickets = Math.Clamp(tickets, 0, DungeonEngine.TicketCap);
            }
            await db.SaveChangesAsync();
            await WriteJsonAsync(context, new { Tickets = rows.ToDictionary(r => r.Key.ToString(), r => r.Value.Tickets) });
        }
    }
}
