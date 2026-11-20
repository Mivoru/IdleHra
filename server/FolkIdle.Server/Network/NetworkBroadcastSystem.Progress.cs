using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Network
{
    // Modul: moved out of NetworkBroadcastSystem.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md). Routing stays in RouteCoreAsync.
    public partial class NetworkBroadcastSystem
    {
        // Modul: Production Release Hardening, Part 2. ClaimedAchievementFlags/
        // TotalAchievementsClaimedCount/ClaimedMilestonesBitmask were
        // removed from StateUpdatePacket - this is their new home. Distinct
        // from the pre-existing /api/v1/achievements/snapshot (which lists
        // the tiered Treasury/Forging/Logistics achievement family's
        // per-achievement progress) - this endpoint covers the separate
        // bitflag-based legacy achievement system plus the Chronicle Pass
        // milestone claim bitmask, matching this task's explicitly named
        // route.
        private async Task HandleAchievementsState(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                int claimedAchievementFlags = await db.PlayerAchievements
                    .AsNoTracking()
                    .Where(a => a.PlayerId == playerId)
                    .Select(a => a.ClaimedAchievementFlags)
                    .SingleOrDefaultAsync();

                int totalAchievementsClaimedCount = await db.PlayerLifetimeAchievements
                    .AsNoTracking()
                    .CountAsync(a => a.PlayerId == playerId && a.IsClaimed);

                ulong claimedMilestonesBitmask = await db.PlayerChroniclePasses
                    .AsNoTracking()
                    .Where(p => p.PlayerId == playerId)
                    .Select(p => p.ClaimedMilestonesBitmask)
                    .SingleOrDefaultAsync();

                await transaction.CommitAsync();

                var response = new AchievementsStateResponse
                {
                    ClaimedAchievementFlags = claimedAchievementFlags,
                    TotalAchievementsClaimedCount = totalAchievementsClaimedCount,
                    ClaimedMilestonesBitmask = claimedMilestonesBitmask
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Achievements state error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 23: authorized snapshot of the player's real Monster Codex
        // progress. MonsterCodexEntries is already populated by CodexEngine's
        // kill-event cron (SimulationEngine enqueues a KillEvent on every monster
        // death; CodexEngine batches and upserts it off the 10 Hz hot path). Level
        // is read directly off the persisted column rather than recomputed here,
        // so this endpoint can never drift from CodexEngine.CalculateLevelFromKillCount
        // (Level = KillCount / 10, uncapped) if that formula ever changes.
        // Modul 23 fix: previously ran raw SQL against "MonsterCodexEntries"
        // (PascalCase, quoted), but the table is mapped via
        // [Table("monster_codex_entries")] (lowercase, unlike every other
        // table in this codebase - see FolkIdleDbContextModelSnapshot's
        // ToTable("monster_codex_entries")), so the quoted identifier never
        // matched the real table and Postgres would reject it outright.
        // Switched to plain LINQ, matching HandleMasterySnapshot's established
        // fix for this exact lowercase-table situation - EF Core resolves the
        // mapping correctly on its own, sidestepping manual identifier
        // quoting entirely.
        private async Task HandleCodexSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var entries = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var response = new System.Collections.Generic.List<CodexSnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new CodexSnapshotEntryResponse
                    {
                        MonsterId = entry.MonsterId,
                        Level = entry.Level,
                        Kills = entry.KillCount,
                        NextLevelKills = (entry.Level + 1) * 10L
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Codex snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// What a player's drops are rolling at, from the figures the loot
        /// worker last rolled with and the engine's own expectation - see
        /// CombatLootEngine.TryGetLastOdds and RarityTier.ExpectedFinalShares.
        /// Task 26's odds line in the Wiki.
        /// </summary>
        internal static LootOddsResponse BuildLootOdds(bool known, CombatLootEngine.LootOddsSnapshot odds)
        {
            var response = new LootOddsResponse
            {
                Known = known,
                EquipmentDropChance = CombatLootEngine.EquipmentDropChance,
            };
            if (!known) return response;

            double[] shares = RarityTier.ExpectedFinalShares(
                odds.LootLuckPct, odds.RarityElevationPct,
                odds.HasGoldenFleece ? 1.0 / SimulationEngine.GoldenFleeceKillInterval : 0.0,
                SimulationEngine.GoldenFleeceBonusTiers);

            double legendaryPlus = 0, ancientPlus = 0;
            for (int tier = RarityTier.Legendary; tier <= RarityTier.Transcendent; tier++)
            {
                legendaryPlus += shares[tier];
                if (tier >= RarityTier.Ancient) ancientPlus += shares[tier];
            }

            response.LootLuckPct = odds.LootLuckPct;
            response.RarityElevationPct = odds.RarityElevationPct;
            response.HasGoldenFleece = odds.HasGoldenFleece;
            response.LegendaryPlusPerDrop = legendaryPlus;
            response.AncientPlusPerDrop = ancientPlus;
            response.TierShares = shares;
            return response;
        }

        private async Task HandleLootOdds(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                bool known = CombatLootEngine.TryGetLastOdds(playerId, out var odds);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, BuildLootOdds(known, odds));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Loot odds error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 13.4.3: region-completion progress for the Codex regions UI. A
        // region is the five canonical monsters of one location and completes
        // once each regular has 1000 kills and the boss 100 (see
        // RegionCompletionRules) - so CurrentKills here is the MINIMUM kill count across the
        // region's monsters (the true bottleneck to completion), not a sum.
        // IsCompleted comes from PlayerRegionCompletions (the durable ledger
        // CodexEngine writes to and never re-grants) rather than being
        // re-derived from kill counts here, so it can never flip back to
        // false if kill counts are read at a slightly different instant than
        // the completion check ran. LootLuckBonusPct mirrors
        // StatsCalculator's "+1.0% Loot Luck per completed area" exactly.
        private async Task HandleCodexRegionsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var codexEntries = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var completedRegionIds = await db.PlayerRegionCompletions
                    .AsNoTracking()
                    .Where(r => r.PlayerId == playerId)
                    .Select(r => r.RegionId)
                    .ToListAsync();
                var completedRegionSet = new System.Collections.Generic.HashSet<int>(completedRegionIds);

                var killsByMonsterId = new System.Collections.Generic.Dictionary<int, int>(codexEntries.Count);
                for (int i = 0; i < codexEntries.Count; i++)
                {
                    killsByMonsterId[codexEntries[i].MonsterId] = codexEntries[i].KillCount;
                }

                // Modul: task 26 (H5) - the FIVE canonical regions and the rule
                // in RegionCompletionRules. This used to walk RegionTier 1-10,
                // legacy monsters included, and report ten bars of which none
                // could ever fill. CurrentKills is still the weakest REGULAR
                // (the bottleneck), and the boss has its own, smaller target.
                // IsCompleted also accepts a region the rule says is done but the
                // ledger has not recorded yet (CodexEngine writes it on the next
                // kill), so the screen agrees with the luck login already grants.
                System.Func<int, int> killsFor = id => killsByMonsterId.TryGetValue(id, out int k) ? k : 0;
                var response = new System.Collections.Generic.List<RegionProgressResponse>(ContentRegistry.LocationCount);
                for (int region = 1; region <= ContentRegistry.LocationCount; region++)
                {
                    int minRegular = int.MaxValue;
                    int bossKills = 0;
                    int first = RegionCompletionRules.FirstMonsterOf(region);
                    for (int monsterId = first; monsterId < first + ContentRegistry.MonstersPerRegion; monsterId++)
                    {
                        int kills = killsFor(monsterId);
                        if (ContentRegistry.IsRegionalBoss(monsterId)) bossKills = kills;
                        else if (kills < minRegular) minRegular = kills;
                    }
                    if (minRegular == int.MaxValue) minRegular = 0;

                    bool isCompleted = completedRegionSet.Contains(region)
                        || RegionCompletionRules.IsComplete(region, killsFor);
                    response.Add(new RegionProgressResponse
                    {
                        RegionId = region,
                        CurrentKills = Math.Min(minRegular, RegionCompletionRules.RegularKillsRequired),
                        RequiredKills = RegionCompletionRules.RegularKillsRequired,
                        BossKills = Math.Min(bossKills, RegionCompletionRules.BossKillsRequired),
                        RequiredBossKills = RegionCompletionRules.BossKillsRequired,
                        IsCompleted = isCompleted,
                        LootLuckBonusPct = isCompleted ? 1 : 0
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Codex regions snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// A character row as BreedingGateRules wants to see it. The endpoints
        /// and the engine build the same struct from the same columns, so the
        /// preview and the command cannot disagree about who may pair.
        /// </summary>
        private static Engine.BreedingGateRules.Parent ToGateParent(Models.CharacterRecord character, long geneticVector) => new()
        {
            AgePhase = character.AgePhase,
            IsFemale = character.IsFemale,
            RaceId = new GeneticVector(geneticVector).LocusRace.Dominant,
            IsLockedInEscrow = character.IsLockedInEscrow,
            IsBreedingActive = character.IsBreedingActive,
            BreedingCooldownEndEpoch = character.BreedingCooldownEndEpoch,
        };

        /// <summary>
        /// The Book of Deeds: five chapters, every deed with a live x / y, and
        /// the Seals.
        ///
        /// AWARDS ON READ. There is no claim command and deliberately is not
        /// one - a claim button is a thing to forget, and the question "how am
        /// I doing" is the same question as "have I finished a chapter". A
        /// chapter that completes while the player is offline is theirs the
        /// next time they look.
        ///
        /// A CHAPTER OPENS WHEN THE ONE BEFORE IT COMPLETES, so the payload
        /// says which are open rather than letting the client guess from the
        /// Seal mask - "open" and "sealed" differ for exactly one chapter at a
        /// time and that is the one the player is working on.
        /// </summary>
        private async Task HandleDeedsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var progress = await Engine.DeedProgressSource.LoadAsync(db, playerId);

                var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                int newlyAwarded = await Engine.SealEngine.AwardCompletedChaptersAsync(db, player, progress);

                // Task 57: the Lifetime chapter. Monster Slayer is paid here, on
                // read, like a Seal; the other three are paid by the checkpoint.
                var (lifetimePaid, diamondBalance) = await Engine.LifetimeAchievementBank.BankMonsterSlayerAsync(db, playerId, progress.TotalKills);
                if (lifetimePaid > 0)
                {
                    // The live payload owns PremiumCurrency and the next
                    // checkpoint assigns it back - hand it the new balance, the
                    // same way AchievementEngine's sweep does.
                    _playerSessionRegistry?.BillingSyncQueue.Enqueue(new BillingSyncNotification
                    {
                        PlayerId = playerId,
                        PremiumDiamondsBalance = diamondBalance
                    });
                }
                var lifetime = await Engine.LifetimeAchievementBank.ReadAsync(db, playerId, progress.TotalKills);

                var chapters = Engine.DeedRegistry.Chapters;

                // A chapter is DONE once sealed or complete - see
                // DeedRegistry.IsOpen for why sealed counts. The open rule
                // itself (task 75: II-IV together, V at two of them) lives
                // there, not here.
                int doneMask = 0;
                foreach (var chapter in chapters)
                {
                    if (Engine.DeedRegistry.HasSeal(player.SealsEarnedMask, chapter.Index)
                        || Engine.DeedRegistry.IsComplete(chapter, progress))
                    {
                        doneMask |= 1 << (chapter.Index - 1);
                    }
                }

                var payload = new
                {
                    SealsEarnedMask = player.SealsEarnedMask,
                    SealCount = Engine.DeedRegistry.SealCount(player.SealsEarnedMask),
                    SkillPointsFromSeals = Engine.DeedRegistry.SkillPointsFrom(player.SealsEarnedMask),
                    SkillPointsPerSeal = Engine.DeedRegistry.SkillPointsPerSeal,
                    // A bitmask of chapters sealed by THIS request, so the
                    // client can celebrate the moment rather than noticing a
                    // number changed.
                    NewlySealedMask = newlyAwarded,
                    Lifetime = lifetime,
                    LifetimeDiamondsPaidNow = lifetimePaid,
                    // Achievements PR 1: the goals nearest done, across the
                    // open chapters and the lifetime tiers (DeedCloseness).
                    Closest = Engine.DeedCloseness.Closest(progress, doneMask, lifetime),
                    // Hidden deeds say only their category until they are done.
                    Hidden = Engine.DeedRegistry.Hidden.Select(h =>
                    {
                        bool done = h.Progress(progress) >= h.Target;
                        return new
                        {
                            h.Id,
                            h.Category,
                            Title = done ? h.Title : "???",
                            Body = done ? h.Body : string.Empty,
                            Done = done,
                        };
                    }).ToList(),
                    Chapters = chapters.Select(chapter =>
                    {
                        bool isOpen = Engine.DeedRegistry.IsOpen(chapter.Index, doneMask);
                        bool isComplete = Engine.DeedRegistry.IsComplete(chapter, progress);

                        return new
                        {
                            chapter.Index,
                            chapter.Title,
                            chapter.Reward,
                            IsOpen = isOpen,
                            // What a closed chapter waits for, so the client
                            // does not restate the rule (it said "the chapter
                            // above", which stopped being true with task 75).
                            OpensWhen = isOpen ? string.Empty : Engine.DeedRegistry.OpensWhen(chapter.Index),
                            IsComplete = isComplete,
                            HasSeal = Engine.DeedRegistry.HasSeal(player.SealsEarnedMask, chapter.Index),
                            Deeds = chapter.Deeds.Select(deed => new
                            {
                                deed.Id,
                                deed.Title,
                                deed.Body,
                                deed.Screen,
                                deed.Target,
                                Current = Math.Min(deed.Progress(progress), deed.Target),
                                Done = deed.Progress(progress) >= deed.Target,
                            }).ToList(),
                        };
                    }).ToList(),
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleDeedsSnapshot failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private async Task HandleCollection(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await Engine.CollectionLog.BuildAsync(db, playerId, GameClock.UtcNow);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleCollection failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private async Task HandleInsights(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                var view = await Engine.StatSampler.BuildInsightsAsync(db, playerId, GameClock.UtcNow);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, view);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleInsights failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private Task HandleMonsterLoot(HttpListenerContext context)
        {
            try
            {
                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!int.TryParse(query["monsterId"], out int monsterId) || monsterId <= 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return Task.CompletedTask;
                }

                var rows = new System.Collections.Generic.List<MonsterLootEntryResponse>();

                ReadOnlySpan<MonsterDefinition> monsters = ContentRegistry.Monsters;
                if (monsterId > monsters.Length)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return Task.CompletedTask;
                }

                int lootTableId = monsters[monsterId - 1].LootTableId;
                ReadOnlySpan<LootTableEntry> table = ContentRegistry.GetLootTable(lootTableId);

                // Total weight first: an entry's odds are its share of the
                // table, and quoting raw weights would be unreadable.
                long totalWeight = 0;
                for (int i = 0; i < table.Length; i++)
                {
                    if (table[i].Weight > 0) totalWeight += table[i].Weight;
                }

                for (int i = 0; i < table.Length; i++)
                {
                    if (table[i].Weight <= 0 || totalWeight <= 0) continue;

                    double share = table[i].Weight / (double)totalWeight;

                    rows.Add(new MonsterLootEntryResponse
                    {
                        ItemId = table[i].ItemId,
                        BaseItemId = ContentRegistry.GetItemBaseId(table[i].ItemId),
                        ChancePct = CombatLootEngine.MaterialDropChance * share * 100.0,
                        // Legacy entries carry 0 here, which would render as
                        // "0-1" and read as "might drop nothing" when the entry
                        // already succeeded its roll. One is the real floor.
                        MinQuantity = table[i].MinQuantity > 0 ? table[i].MinQuantity : 1,

                        // MaxQuantity <= 0 is the documented legacy "one unit
                        // per successful roll" shape - see LootTableEntry.
                        MaxQuantity = table[i].MaxQuantity > 0 ? table[i].MaxQuantity : 1,
                        IsEquipment = false
                    });
                }

                // Equipment does not come from the weighted table at all - it
                // rolls on its own chance against this monster's drop table, so
                // it has to be reported separately or the screen would claim a
                // monster drops no gear.
                //
                // Modul: THE REAL ITEMS, not four "any_melee_weapon" placeholder
                // rows. Those were honest when every monster in a region shared
                // one pool and the answer genuinely was "some weapon" - now each
                // monster has its own list, and the list is the whole point of
                // the change, so the screen names what falls. It is also the only
                // way a player can tell that the thing they are missing comes
                // from the boar and not the mouse.
                ReadOnlySpan<int> equipment = EquipmentDropTable.GetDrops(monsterId);
                if (equipment.Length > 0)
                {
                    // Uniform within the table, so each entry's odds are the
                    // equipment roll divided by the table size. A boss rolls a
                    // second, guaranteed time, so its per-item chance is that
                    // much higher - quote what actually happens rather than the
                    // ordinary-monster number.
                    double rolls = CombatLootEngine.EquipmentDropChance;
                    if (ContentRegistry.IsRegionalBoss(monsterId)) rolls += 1.0;

                    double perItem = rolls / equipment.Length * 100.0;

                    for (int i = 0; i < equipment.Length; i++)
                    {
                        rows.Add(new MonsterLootEntryResponse
                        {
                            ItemId = equipment[i],
                            BaseItemId = ContentRegistry.GetItemBaseId(equipment[i]),
                            ChancePct = perItem,
                            MinQuantity = 1,
                            MaxQuantity = 1,
                            IsEquipment = true
                        });
                    }
                }

                context.Response.ContentType = "application/json";
                return JsonSerializer.SerializeAsync(context.Response.OutputStream, rows)
                    .ContinueWith(_ => context.Response.Close());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Monster loot lookup error: {ex}");
                context.Response.StatusCode = 500;
                context.Response.Close();
                return Task.CompletedTask;
            }
        }

        // Modul 13: authorized snapshot of the player's real lifetime achievement
        // progress (PlayerLifetimeAchievements, including but not limited to the
        // three auto-awarded tiered achievements from AchievementMilestones).
        private async Task HandleAchievementsSnapshot(HttpListenerContext context)
        {
            try
            {
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");

                var entries = await db.PlayerLifetimeAchievements
                    .AsNoTracking()
                    .Where(a => a.PlayerId == playerId)
                    .ToListAsync();

                await transaction.CommitAsync();

                var response = new System.Collections.Generic.List<AchievementSnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new AchievementSnapshotEntryResponse
                    {
                        AchievementId = entry.AchievementId,
                        Title = AchievementMilestones.TitleFor(entry.AchievementId),
                        Description = AchievementMilestones.DescriptionFor(entry.AchievementId),
                        CurrentProgress = entry.CurrentProgress,
                        CompletedTier = entry.CompletedTier,
                        NextTierTarget = AchievementMilestones.GetNextTierTarget(entry.AchievementId, entry.CompletedTier),
                        NextTierReward = AchievementMilestones.GetNextTierReward(entry.AchievementId, entry.CompletedTier),
                        // Task 57: every achievement pays itself now. A tier the
                        // checkpoint already paid reads as claimed, so an old
                        // client does not offer a button that used to pay it twice.
                        IsClaimed = entry.IsClaimed || entry.CompletedTier > 0
                    });
                }

                // Modul: Achievement claim button. AchievementEngine.ProcessClaimsQueueAsync
                // only ever creates the monster-kill achievement's row the first time a
                // claim is attempted - so without this, the claim button would have
                // nothing to attach to until the player had already (impossibly, with no
                // button) claimed once. Synthesize the unclaimed row here so it is always
                // visible and claimable from a fresh account.
                if (!response.Exists(r => r.AchievementId == AchievementMilestones.MonsterKillAchievementId))
                {
                    response.Add(new AchievementSnapshotEntryResponse
                    {
                        AchievementId = AchievementMilestones.MonsterKillAchievementId,
                        Title = AchievementMilestones.TitleFor(AchievementMilestones.MonsterKillAchievementId),
                        Description = AchievementMilestones.DescriptionFor(AchievementMilestones.MonsterKillAchievementId),
                        CurrentProgress = 0,
                        CompletedTier = 0,
                        NextTierTarget = AchievementMilestones.GetNextTierTarget(AchievementMilestones.MonsterKillAchievementId, 0),
                        NextTierReward = AchievementMilestones.GetNextTierReward(AchievementMilestones.MonsterKillAchievementId, 0),
                        IsClaimed = false
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Achievements snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
    }
}
