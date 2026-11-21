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
        // Modul 13.4.3: the player's own bred/breedable character roster, for
        // the Breeding Lab's parent-selection slots. BreedingEngine.
        // BreedingGateRules' own eligibility rules (AgePhase >= 1, not
        // already IsBreedingActive, not IsLockedInEscrow) are
        // intentionally NOT filtered out here - the client shows every owned
        // character and lets the preview/execute round trip surface exactly
        // why an ineligible pairing was rejected, rather than this endpoint
        // silently hiding characters and leaving a player unable to tell an
        // "under cooldown" character apart from one that was never bred.
        /// <summary>
        /// The village's current gene pool, and how much room is left in it.
        ///
        /// Returns the CAP alongside the people, because "Village 11/14" is the
        /// number that makes the keep-or-turn-away decision legible, and
        /// deriving it client-side would mean mirroring
        /// VillagerArrivalRules.PopulationCapFor into TypeScript for one label.
        /// </summary>
        private async Task HandleVillageQuote(HttpListenerContext context)
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

                // Matured upgrades are applied by the next command or login;
                // the quote reads rows as they stand, which can trail a
                // finished timer by one level for a moment. The screen asks
                // again when the infrastructure notification lands.
                var levels = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .Select(v => new { v.BuildingId, v.CurrentLevel })
                    .ToListAsync();

                var buildings = new List<(int Id, int Level, Domain.Progression.VillageManagementEngine.UpgradeCostLine[] Lines)>();
                var wanted = new HashSet<string>();
                int townHall = levels.FirstOrDefault(l => l.BuildingId == Domain.Progression.VillageManagementEngine.TownHallBuildingId)?.CurrentLevel ?? 0;
                for (uint id = 1; id <= 10; id++)
                {
                    if (!Domain.Progression.VillageManagementEngine.IsValidBuildingId(id)) continue;
                    int level = levels.FirstOrDefault(l => l.BuildingId == (int)id)?.CurrentLevel ?? 0;
                    var lines = Domain.Progression.VillageManagementEngine.QuoteUpgrade((int)id, level);
                    buildings.Add(((int)id, level, lines));
                    foreach (var line in lines) wanted.Add(line.ItemId);
                }

                // The same two places TryConsumeUnifiedAsync spends from: the
                // backpack (CommodityRecords) first, then the village stash.
                var backpack = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && wanted.Contains(c.ItemId))
                    .Select(c => new { c.ItemId, c.Quantity })
                    .ToListAsync();
                var stash = await db.VillageStashInstances
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId && wanted.Contains(v.ItemId))
                    .Select(v => new { v.ItemId, v.Quantity })
                    .ToListAsync();

                var held = new Dictionary<string, long>();
                foreach (var row in backpack) held[row.ItemId] = held.GetValueOrDefault(row.ItemId) + row.Quantity;
                foreach (var row in stash) held[row.ItemId] = held.GetValueOrDefault(row.ItemId) + row.Quantity;

                var payload = new
                {
                    Buildings = buildings.ConvertAll(b => new
                    {
                        BuildingId = b.Id,
                        CurrentLevel = b.Level,
                        // Village 2: the cap and the timer come from the
                        // server, so the screen keeps no copy of either rule.
                        MaxLevel = Domain.Progression.VillageManagementEngine.MaxLevelFor(b.Id, townHall),
                        AbsoluteMaxLevel = Domain.Progression.VillageManagementEngine.AbsoluteMaxLevelFor(b.Id),
                        UpgradeSeconds = Domain.Progression.VillageManagementEngine.CalculateUpgradeDurationSeconds(
                            Domain.Progression.VillageManagementEngine.DurationLevelFor(b.Id, b.Level)),
                        Lines = Array.ConvertAll(b.Lines, line => new
                        {
                            line.ItemId,
                            line.Quantity,
                            // Gold here is the DURABLE balance; the client shows
                            // the live one, which is what the header reads.
                            Held = held.GetValueOrDefault(line.ItemId),
                        }),
                    }),
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Village quote error: {ex}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private async Task HandleVillageNewcomers(HttpListenerContext context)
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

                var rows = await db.VillageNewcomers
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .OrderByDescending(v => v.ArrivedAtEpoch)
                    .ToListAsync();

                int innLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(b => b.PlayerId == playerId
                             && b.BuildingId == Domain.Progression.VillageManagementEngine.InnBuildingId)
                    .Select(b => b.CurrentLevel)
                    .FirstOrDefaultAsync();

                // Modul: recruitment. The price escalates 1.6x per recruitment
                // WITHIN a season off a counter only the server has, so the
                // client cannot compute it and a button that guessed would
                // eventually quote the wrong number. The refusal comes from the
                // same function the command runs, so a disabled button and a
                // rolled-back command can never disagree about why.
                int recruitments = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.VillagerRecruitmentsThisSeason)
                    .FirstOrDefaultAsync();

                long heldGold = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                    .Select(c => c.Quantity)
                    .FirstOrDefaultAsync();

                var payload = new
                {
                    InnLevel = innLevel,
                    PopulationCap = Engine.VillagerArrivalRules.PopulationCapFor(innLevel),
                    IntervalSeconds = Engine.VillagerArrivalRules.IntervalSecondsFor(innLevel),
                    RecruitCostGold = Engine.VillagerArrivalRules.RecruitCostGold(recruitments),
                    RecruitBlockedReason = Engine.VillagerArrivalRules.RecruitBlockedReason(
                        innLevel, rows.Count(v => !v.IsElder), heldGold, recruitments) ?? string.Empty,
                    // Who takes a bed against PopulationCap - elders do not
                    // (VillageArrivalEngine.CountRoomTakersAsync), so the
                    // screen's "n / cap" must not count Newcomers.length.
                    Residents = rows.Count(v => !v.IsElder),
                    Newcomers = rows.ConvertAll(v => new
                    {
                        v.Id,
                        // Derived from the id, not stored - see FolkNameRegistry.ForNewcomer.
                        Name = Engine.FolkNameRegistry.ForNewcomer(v.Id, v.IsFemale),
                        v.RaceId,
                        v.IsFemale,
                        v.AptitudeStrength,
                        v.AptitudeSkill,
                        v.AptitudeEndurance,
                        v.AptitudeFortune,
                        v.ArrivedAtEpoch,
                        v.IsElder,
                        v.TraitMask,
                    }),
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleVillageNewcomers failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        /// <summary>
        /// The Hall of Ancestors: everyone the account owns, what they carry,
        /// and how many of them survive the next rollover.
        ///
        /// Returns the CAP and the marks together, because "11 / 14" and "these
        /// four are safe" are the same decision seen from two sides, and a
        /// client that had to derive the cap would be mirroring
        /// HallOfAncestorsRules into TypeScript for one label.
        ///
        /// Also returns the ranking the cull would use RIGHT NOW - see
        /// WouldCarry. A cap that only reveals what it did after a rollover has
        /// already deleted somebody is not a decision, it is a surprise.
        /// </summary>
        private async Task HandleRebirthPreview(HttpListenerContext context)
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

                var preview = await new RebirthEngine(_serviceProvider).PreviewAsync(playerId);
                if (preview == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, preview);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Rebirth preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// Task 88: the player ends their run. Body: {ExpectedRebirthCount},
        /// the count the preview showed - the idempotency token.
        ///
        /// Modul: EVERY OUTCOME ANSWERS WITH A RESULT, never a bare status and
        /// never silence (CLAUDE.md, "silent rollback"): 200 Ok, 409
        /// AlreadyReborn / InFlight, 503 when the tick did not answer in time.
        /// </summary>
        private async Task HandleRebirth(HttpListenerContext context)
        {
            long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
            if (playerId <= 0)
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }

            RebirthRequestBody? body;
            try
            {
                string raw = await ReadBodyAsync(context);
                body = JsonSerializer.Deserialize<RebirthRequestBody>(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            }
            catch (JsonException)
            {
                body = null;
            }
            if (body == null || body.ExpectedRebirthCount < 0)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            // Not without the typed word - a reason, not a bare 400, so the
            // panel can say what is missing.
            if (!RebirthRules.IsConfirmed(body.Confirm))
            {
                await WriteRebirthAsync(context, 400, new RebirthOutcome(RebirthResult.ConfirmationRequired, body.ExpectedRebirthCount, 0, 0, 0, false));
                return;
            }

            var engine = new RebirthEngine(_serviceProvider);
            RebirthOutcome? outcome = null;

            // A live session: the tick owns the payload, so the rebirth runs
            // as the continuation of that payload's own flush.
            if (_playerSessionRegistry != null && _playerSessionRegistry.IsPlayerOnline(playerId))
            {
                var request = new FolkIdle.Server.Domain.Progression.RebirthRequest
                {
                    PlayerId = playerId,
                    ExpectedRebirthCount = body.ExpectedRebirthCount,
                    KeepEquipmentIds = body.KeepEquipmentIds ?? Array.Empty<long>(),
                    TrialId = body.TrialId,
                    Engine = engine,
                };
                _playerSessionRegistry.RebirthRequestQueue.Enqueue(request);

                var finished = await Task.WhenAny(request.Completion.Task, Task.Delay(TimeSpan.FromSeconds(15)));
                if (finished != request.Completion.Task)
                {
                    await WriteRebirthAsync(context, 503, new RebirthOutcome(RebirthResult.Failed, body.ExpectedRebirthCount, 0, 0, 0, false));
                    return;
                }
                outcome = await request.Completion.Task;
            }

            // No live payload (offline, or between sign-in and the tick picking
            // the session up): wait out any checkpoint still queued for this
            // player, then reset directly.
            if (outcome == null)
            {
                if (_checkpointManager != null)
                {
                    await _checkpointManager.WaitForPendingFlushesAsync(playerId);
                }
                outcome = await engine.RebirthAsync(playerId, body.ExpectedRebirthCount, body.KeepEquipmentIds ?? Array.Empty<long>(), default, body.TrialId);
            }

            int status = outcome.Value.Result switch
            {
                RebirthResult.Ok => 200,
                RebirthResult.AlreadyReborn => 409,
                RebirthResult.InFlight => 409,
                RebirthResult.NotFound => 404,
                // Task 114: refusals the panel explains, never a 500.
                RebirthResult.Capped => 409,
                RebirthResult.NotEnoughGold => 409,
                RebirthResult.LevelTooLow => 409,
                RebirthResult.KeepNotOwned => 400,
                RebirthResult.TrialUnavailable => 409,
                RebirthResult.TrialForbidsVault => 400,
                _ => 500,
            };
            await WriteRebirthAsync(context, status, outcome.Value);
        }

        private static async Task WriteRebirthAsync(HttpListenerContext context, int status, RebirthOutcome outcome)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
            {
                Result = outcome.Result.ToString(),
                outcome.RebirthCount,
                outcome.RenownedRebirths,
                outcome.DamageBonusPct,
                outcome.ShardsEarned,
                outcome.Renowned,
            });
            context.Response.Close();
        }

        private async Task HandleAncestorsHall(HttpListenerContext context)
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

                var player = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new { p.PlayerGuid, p.AncestorSlotsPurchased, p.PremiumDiamonds })
                    .FirstOrDefaultAsync();

                if (player == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                await Engine.ChildGrowth.TrySettleAsync(db, playerId, GameClock.UtcNowOffset.ToUnixTimeSeconds());
                var characters = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .OrderBy(c => c.SlotIndex)
                    .ToListAsync();

                var characterIds = characters.ConvertAll(c => c.Id);
                var lineages = await db.CharacterLineages
                    .AsNoTracking()
                    .Where(l => characterIds.Contains(l.CharacterId))
                    .ToListAsync();

                var lineageById = new System.Collections.Generic.Dictionary<Guid, CharacterLineageRegistry>(lineages.Count);
                for (int i = 0; i < lineages.Count; i++) lineageById[lineages[i].CharacterId] = lineages[i];

                int greatWorkSlots = await Domain.Progression.GreatWorksEngine.HallSlotsAsync(db, playerId);
                int cap = Engine.HallOfAncestorsRules.CapFor(player.AncestorSlotsPurchased, greatWorkSlots);

                var ranking = new System.Collections.Generic.List<Engine.HallOfAncestorsRules.Member>(characters.Count);
                for (int i = 0; i < characters.Count; i++)
                {
                    lineageById.TryGetValue(characters[i].Id, out var lineage);
                    ranking.Add(new Engine.HallOfAncestorsRules.Member(
                        characters[i].Id,
                        characters[i].Id == player.PlayerGuid,
                        lineage?.IsKeptAtRollover ?? false,
                        lineage?.IsEpicMutation ?? false,
                        lineage is null ? 0 : lineage.AptitudeVector().Sum(),
                        lineage?.GenerationIndex ?? 0));
                }

                var carried = new System.Collections.Generic.HashSet<Guid>(
                    Engine.HallOfAncestorsRules.ChooseSurvivors(ranking, cap));
                var awayIds = await Engine.ExpeditionEngine.AwayIdsAsync(db, playerId);

                int townHallLevel = await db.VillageInfrastructures
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId
                             && v.BuildingId == Domain.Progression.VillageManagementEngine.TownHallBuildingId)
                    .Select(v => v.CurrentLevel)
                    .FirstOrDefaultAsync();

                var payload = new
                {
                    Cap = cap,
                    MaxCap = Engine.HallOfAncestorsRules.MaxCapFor(greatWorkSlots),
                    GreatWorkSlots = greatWorkSlots,
                    SlotsPurchased = player.AncestorSlotsPurchased,
                    NextSlotCostDiamonds = Engine.HallOfAncestorsRules.NextSlotCostDiamonds(player.AncestorSlotsPurchased),
                    Diamonds = player.PremiumDiamonds,
                    PlayableSlots = Domain.Combat.CharacterSlotEngine.GetUnlockedSlotCount(townHallLevel),
                    Members = characters.ConvertAll(c =>
                    {
                        lineageById.TryGetValue(c.Id, out var lineage);
                        var genes = new GeneticVector(lineage?.GeneticVector ?? 0L);

                        // Modul: the pedigree read "b6b704ca x 0214b4e9" - Guid
                        // prefixes, a day after characters got names. A parent
                        // culled at a rollover is no longer in the roster, so
                        // an empty name means "not here any more", not unknown.
                        string NameOf(Guid? id) =>
                            id is { } value && characters.Find(x => x.Id == value) is { } parent ? parent.Name : string.Empty;

                        return new
                        {
                            CharacterId = c.Id.ToString(),
                            c.Name,
                            c.AgePhase,
                            c.IsFemale,
                            c.SlotIndex,
                            // -1 rather than null for "not fielded": the client
                            // compares this against a slot number and a null
                            // would have to be special-cased at every use.
                            PlayableSlot = c.SlotIndex < Domain.Combat.CharacterSlotEngine.MaxCharacterSlots ? c.SlotIndex : -1,
                            RaceId = (int)genes.LocusRace.Dominant,
                            GenerationIndex = lineage?.GenerationIndex ?? 0,
                            IsEpicMutation = lineage?.IsEpicMutation ?? false,
                            IsInbred = lineage?.IsInbred ?? false,
                            TraitMask = lineage?.TraitMask ?? 0L,
                            IsKept = lineage?.IsKeptAtRollover ?? false,
                            WouldCarry = carried.Contains(c.Id),
                            // Away on an expedition: cannot be fielded or bred until back.
                            IsAway = awayIds.Contains(c.Id),
                            IsMainCharacter = c.Id == player.PlayerGuid,
                            AptitudeStrength = lineage?.AptitudeStrength ?? 0,
                            AptitudeSkill = lineage?.AptitudeSkill ?? 0,
                            AptitudeEndurance = lineage?.AptitudeEndurance ?? 0,
                            AptitudeFortune = lineage?.AptitudeFortune ?? 0,
                            // The pedigree. Empty rather than null for an
                            // unknown parent - a founder and a villager's child
                            // both have one, and neither is an error.
                            ParentPaternalId = lineage?.ParentPaternalId?.ToString() ?? string.Empty,
                            ParentMaternalId = lineage?.ParentMaternalId?.ToString() ?? string.Empty,
                            ParentPaternalName = NameOf(lineage?.ParentPaternalId),
                            ParentMaternalName = NameOf(lineage?.ParentMaternalId),
                        };
                    }),
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, payload);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HandleAncestorsHall failed: {ex.Message}");
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
        }

        private async Task HandleBreedingRosterSnapshot(HttpListenerContext context)
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

                await Engine.ChildGrowth.TrySettleAsync(db, playerId, GameClock.UtcNowOffset.ToUnixTimeSeconds());
                var characters = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var characterIds = new System.Collections.Generic.List<Guid>(characters.Count);
                for (int i = 0; i < characters.Count; i++)
                {
                    characterIds.Add(characters[i].Id);
                }

                var lineages = await db.CharacterLineages
                    .AsNoTracking()
                    .Where(l => characterIds.Contains(l.CharacterId))
                    .ToListAsync();

                var lineageByCharacterId = new System.Collections.Generic.Dictionary<Guid, CharacterLineageRegistry>(lineages.Count);
                for (int i = 0; i < lineages.Count; i++)
                {
                    lineageByCharacterId[lineages[i].CharacterId] = lineages[i];
                }

                var response = new System.Collections.Generic.List<BreedingRosterEntryResponse>(characters.Count);
                var awayOnExpedition = await Engine.ExpeditionEngine.AwayIdsAsync(db, playerId);
                for (int i = 0; i < characters.Count; i++)
                {
                    var character = characters[i];
                    if (!lineageByCharacterId.TryGetValue(character.Id, out var lineage))
                    {
                        continue;
                    }

                    var geneVec = new GeneticVector(lineage.GeneticVector);

                    response.Add(new BreedingRosterEntryResponse
                    {
                        CharacterId = character.Id.ToString(),
                        Name = character.Name,
                        AgePhase = character.AgePhase,
                        GenerationIndex = lineage.GenerationIndex,
                        IsBreedingActive = character.IsBreedingActive,
                        BreedingCooldownEndEpoch = character.BreedingCooldownEndEpoch,
                        // Away on an expedition (the cooldown runs to the return,
                        // so the picker can say why rather than "resting").
                        IsAway = awayOnExpedition.Contains(character.Id),
                        IsEpicMutation = lineage.IsEpicMutation,
                        IsInbred = lineage.IsInbred,
                        IsFemale = character.IsFemale,
                        AptitudeStrength = lineage.AptitudeStrength,
                        AptitudeSkill = lineage.AptitudeSkill,
                        AptitudeEndurance = lineage.AptitudeEndurance,
                        AptitudeFortune = lineage.AptitudeFortune,
                        LocusRaceDominant = geneVec.LocusRace.Dominant,
                        LocusRaceRecessive = geneVec.LocusRace.Recessive,
                        TraitMask = lineage.TraitMask
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding roster snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandleBreedingTraits(HttpListenerContext context)
        {
            try
            {
                // Modul: static content, not per-player data - same convention
                // as HandleStoreCatalog/HandleCodexRegionsSnapshot. Nothing
                // player-specific is returned, but every sibling handler still
                // 401s an unauthenticated request rather than serving anyone
                // who can reach the port, and the client always calls this
                // through authedGet anyway.
                long playerId = await TryResolveAuthenticatedPlayerAsync(context.Request);
                if (playerId <= 0)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                var catalogue = Engine.TraitRegistry.All.Select(t => new
                {
                    Id = t.Bit,
                    t.Key,
                    t.Name,
                    t.Description,
                    Rarity = t.Rarity.ToString(),
                    Effect = t.Effect.ToString(),
                    t.Value,
                }).ToList();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, catalogue);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding traits error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private static async Task AddAptitudePreviewsAsync(
            FolkIdleDbContext db, long playerId, BreedingPreviewResponse response, int[] hero, int[] partner)
        {
            int rebirths = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId).Select(p => p.RebirthCount).FirstOrDefaultAsync();
            int ceiling = Engine.BreedingAptitudes.CeilingFor(rebirths);
            response.BloodlineCeiling = ceiling;
            response.NextBloodlineCeiling = Engine.BreedingAptitudes.CeilingFor(rebirths + 1);

            // Modul: relatedness is set on the response before this runs, and
            // it changes the bands (no sure growth, reversed luck) - so the
            // preview must be filled after IsInbredRisk, never before.
            bool related = response.IsInbredRisk;
            for (int i = 0; i < Engine.BreedingAptitudes.Count; i++)
            {
                Engine.BreedingAptitudes.PreviewOne(hero[i], partner[i], out int min, out int max, ceiling, related);
                Engine.BreedingAptitudes.PreviewSelected(hero[i], partner[i], out int selMin, out int selMax, ceiling, related);
                int better = Math.Max(hero[i], partner[i]);
                response.Aptitudes.Add(new AptitudePreviewResponse
                {
                    AptitudeName = Engine.BreedingAptitudes.NameOf(i),
                    ParentHero = hero[i],
                    ParentPartner = partner[i],
                    PredictedMin = min,
                    PredictedMax = max,
                    SelectedMin = selMin,
                    SelectedMax = selMax,
                    AtCeiling = better >= Engine.BreedingAptitudes.ChildLimit(hero[i], partner[i], ceiling),
                });
            }
        }

        private static void AddTraitPreviews(BreedingPreviewResponse response, long heroMask, long partnerMask, int groundsLevel)
        {
            foreach (var odds in Engine.BreedingTraits.PreviewOdds(heroMask, partnerMask))
            {
                response.TraitOdds.Add(new TraitOddsResponse { TraitId = odds.Bit, ChancePct = odds.ChancePct, Source = odds.Source });
            }
            response.MutationChancePct = Engine.BreedingTraits.MutationPercentFor(groundsLevel);
            response.FlawChancePct = Engine.BreedingTraits.FlawPercentFor(response.IsInbredRisk);
        }

        private static async Task<int> BreedingGroundsLevelAsync(FolkIdleDbContext db, long playerId)
            => await db.VillageInfrastructures
                .AsNoTracking()
                .Where(v => v.PlayerId == playerId && v.BuildingId == Domain.Progression.VillageManagementEngine.BreedingGroundsBuildingId)
                .Select(v => (int?)v.CurrentLevel)
                .SingleOrDefaultAsync() ?? 0;

        // Modul 13.4.3: read-only preview of ExecuteBreedingAsync's outcome -
        // never writes to the DB. Mirrors that engine's own ownership,
        // eligibility, and inbreeding checks exactly (see BreedingEngine.
        // ExecuteBreedingAsync) so a preview can never promise a pairing the
        // real execute call would actually reject, but computes the gene
        // spectrum via GeneticSplicingEngine.PreviewLocus (an exact
        // enumeration of Breed()'s possible non-mutated outcomes) instead of
        // performing the real, single-sample random splice.
        private async Task HandleBreedingPreview(HttpListenerContext context)
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

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!Guid.TryParse(query["paternalId"], out Guid paternalId) || !Guid.TryParse(query["maternalId"], out Guid maternalId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (!ClientCommandValidator.ValidateBreedingPreviewQuery(playerId, paternalId, maternalId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var pChar = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == paternalId);
                var mChar = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == maternalId);
                var pLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == paternalId);
                var mLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == maternalId);

                if (pChar == null || mChar == null || pLineage == null || mLineage == null || pChar.PlayerId != playerId || mChar.PlayerId != playerId)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var response = new BreedingPreviewResponse();

                long nowEpoch = GameClock.UtcNowOffset.ToUnixTimeSeconds();

                var pVec = new GeneticVector(pLineage.GeneticVector);
                var mVec = new GeneticVector(mLineage.GeneticVector);

                // Modul: THE SAME GATE THE ENGINE USES. This block used to be a
                // hand-copied second version of the engine's rules, and a
                // comment beside it said the two "must refuse the same pairs or
                // the preview lies again" - which is a thing to enforce, not to
                // write down. It had already drifted once: race was checked and
                // sex was not, so a woman chosen as the paternal parent priced
                // an ELIGIBLE pairing that the engine then rolled back without
                // a word. One call now, and BreedingGateTests fails if either
                // side grows a rule of its own.
                var previewRefusal = Engine.BreedingGateRules.CheckPair(
                    ToGateParent(pChar, pLineage.GeneticVector),
                    ToGateParent(mChar, mLineage.GeneticVector),
                    nowEpoch);

                response.IsEligible = previewRefusal == Engine.BreedingRefusal.None;
                response.IneligibleReason = Engine.BreedingGateRules.ReasonSlugFor(previewRefusal);

                // The same relatedness check the engine runs - it used to be a
                // hand-copied expression here too, and both copies missed cousins.
                response.IsInbredRisk = await Engine.BreedingRelatedness.AreRelatedAsync(db, pLineage, mLineage);

                int maxGen = Math.Max(pLineage.GenerationIndex, mLineage.GenerationIndex);
                response.BreedingCostGold = Engine.BreedingEngine.CostFor(maxGen, Engine.HearthTreeRegistry.ValueAtLevel(Engine.HearthTreeRegistry.Matchmaker, await Engine.HearthTreeRegistry.StoredLevelAsync(db, playerId, Engine.HearthTreeRegistry.Matchmaker)));

                var goldRecord = await db.CommodityRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                response.HasSufficientGold = goldRecord != null && goldRecord.Quantity >= response.BreedingCostGold;

                // Modul: no locus rows (2026-11-13). The Speed/Crit/Yield loci
                // are read by nothing, and the Race row said what the race
                // picker now says. Loci stays on the response, empty, so an
                // older client renders nothing rather than failing.
                AddChildRaceOptions(response, pVec.LocusRace.Dominant, mVec.LocusRace.Dominant);
                await AddAptitudePreviewsAsync(db, playerId, response, pLineage.AptitudeVector(), mLineage.AptitudeVector());
                int rosterGrounds = await BreedingGroundsLevelAsync(db, playerId);
                response.SelectableCount = Engine.BreedingAptitudes.SelectableCount(rosterGrounds);
                AddTraitPreviews(response, pLineage.TraitMask, mLineage.TraitMask, rosterGrounds);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: hero x villager preview. Mirrors
        // BreedingEngine.ExecuteHeroVillagerBreedingAsync's refusals in the same
        // order, for the same reason the character preview mirrors its own
        // engine: a preview that can promise a pairing the execute call rejects
        // is worse than no preview, because the player learns nothing from the
        // silence that follows.
        private async Task HandleVillagerBreedingPreview(HttpListenerContext context)
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

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!Guid.TryParse(query["heroId"], out Guid heroId)
                    || !long.TryParse(query["newcomerId"], out long newcomerId)
                    || heroId == Guid.Empty
                    || newcomerId <= 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var hero = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == heroId);
                var heroLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == heroId);
                var newcomer = await db.VillageNewcomers.AsNoTracking().FirstOrDefaultAsync(v => v.Id == newcomerId);

                if (hero == null || heroLineage == null || newcomer == null
                    || hero.PlayerId != playerId || newcomer.PlayerId != playerId)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                var response = new BreedingPreviewResponse();

                long nowEpoch = GameClock.UtcNowOffset.ToUnixTimeSeconds();

                var heroVec = new GeneticVector(heroLineage.GeneticVector);
                var villagerVec = new GeneticVector(newcomer.Genome());

                // The same gate the engine uses - see the roster preview above
                // for why this is a call rather than a second copy of the rules.
                var previewRefusal = Engine.BreedingGateRules.CheckVillagerPair(
                    ToGateParent(hero, heroLineage.GeneticVector),
                    newcomer.IsFemale,
                    (byte)newcomer.RaceId,
                    newcomer.IsElder,
                    nowEpoch);

                response.IsEligible = previewRefusal == Engine.BreedingRefusal.None;
                response.IneligibleReason = Engine.BreedingGateRules.ReasonSlugFor(previewRefusal);

                // Never. A villager has no parents here and marries once.
                response.IsInbredRisk = false;

                int maxGen = heroLineage.GenerationIndex;
                response.BreedingCostGold = Engine.BreedingEngine.CostFor(maxGen, Engine.HearthTreeRegistry.ValueAtLevel(Engine.HearthTreeRegistry.Matchmaker, await Engine.HearthTreeRegistry.StoredLevelAsync(db, playerId, Engine.HearthTreeRegistry.Matchmaker)));

                var goldRecord = await db.CommodityRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                response.HasSufficientGold = goldRecord != null && goldRecord.Quantity >= response.BreedingCostGold;

                AddChildRaceOptions(response, heroVec.LocusRace.Dominant, (byte)newcomer.RaceId);
                await AddAptitudePreviewsAsync(db, playerId, response, heroLineage.AptitudeVector(), newcomer.AptitudeVector());
                int villagerGrounds = await BreedingGroundsLevelAsync(db, playerId);
                response.SelectableCount = Engine.BreedingAptitudes.SelectableCount(villagerGrounds);
                AddTraitPreviews(response, heroLineage.TraitMask, newcomer.TraitMask, villagerGrounds);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Villager breeding preview error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// The five best partners for a hero, eligible ones only: the same gate
        /// the engine runs, the same relatedness, the same bands
        /// (BreedingMatchmaker). Read-only.
        /// </summary>
        private async Task HandleBreedingSuggest(HttpListenerContext context)
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

                var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
                if (!Guid.TryParse(query["heroId"], out Guid heroId) || heroId == Guid.Empty)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                long nowEpoch = GameClock.UtcNowOffset.ToUnixTimeSeconds();
                await Engine.ChildGrowth.TrySettleAsync(db, playerId, nowEpoch);

                var hero = await db.CharacterRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == heroId);
                var heroLineage = await db.CharacterLineages.AsNoTracking().FirstOrDefaultAsync(l => l.CharacterId == heroId);
                if (hero == null || heroLineage == null || hero.PlayerId != playerId)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                int rebirths = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.RebirthCount).FirstOrDefaultAsync();
                int ceiling = Engine.BreedingAptitudes.CeilingFor(rebirths);
                int grounds = await BreedingGroundsLevelAsync(db, playerId);
                int selectable = Engine.BreedingAptitudes.SelectableCount(grounds);
                int discount = Engine.HearthTreeRegistry.ValueAtLevel(Engine.HearthTreeRegistry.Matchmaker, await Engine.HearthTreeRegistry.StoredLevelAsync(db, playerId, Engine.HearthTreeRegistry.Matchmaker));
                var heroGate = ToGateParent(hero, heroLineage.GeneticVector);
                int[] heroApt = heroLineage.AptitudeVector();

                var results = new System.Collections.Generic.List<BreedingSuggestionResponse>();

                // The player's own line: the roster gate (ordered by sex) and
                // the relatedness the engine would find.
                var others = await db.CharacterRecords.AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.Id != heroId && c.IsFemale != hero.IsFemale)
                    .ToListAsync();
                var otherIds = others.Select(o => o.Id).ToList();
                var otherLineages = await db.CharacterLineages.AsNoTracking()
                    .Where(l => otherIds.Contains(l.CharacterId))
                    .ToDictionaryAsync(l => l.CharacterId);
                foreach (var other in others)
                {
                    if (!otherLineages.TryGetValue(other.Id, out var lineage)) continue;
                    var otherGate = ToGateParent(other, lineage.GeneticVector);
                    var refusal = hero.IsFemale
                        ? Engine.BreedingGateRules.CheckPair(otherGate, heroGate, nowEpoch)
                        : Engine.BreedingGateRules.CheckPair(heroGate, otherGate, nowEpoch);
                    if (refusal != Engine.BreedingRefusal.None) continue;

                    bool related = await Engine.BreedingRelatedness.AreRelatedAsync(db, heroLineage, lineage);
                    var match = Engine.BreedingMatchmaker.Score(heroApt, lineage.AptitudeVector(), ceiling, related, selectable);
                    results.Add(new BreedingSuggestionResponse
                    {
                        Key = "c:" + other.Id,
                        Name = other.Name,
                        IsVillager = false,
                        IsRelated = related,
                        RaceId = new GeneticVector(lineage.GeneticVector).LocusRace.Dominant,
                        ExpectedTotal = match.Score / 10.0,
                        ChildFloorTotal = match.ChildFloorTotal,
                        SelectionMask = match.SelectionMask,
                        CostGold = Engine.BreedingEngine.CostFor(Math.Max(heroLineage.GenerationIndex, lineage.GenerationIndex), discount),
                    });
                }

                // The village: never related, marries once.
                var newcomers = await db.VillageNewcomers.AsNoTracking()
                    .Where(v => v.PlayerId == playerId && !v.IsElder && v.IsFemale != hero.IsFemale)
                    .ToListAsync();
                foreach (var person in newcomers)
                {
                    var refusal = Engine.BreedingGateRules.CheckVillagerPair(heroGate, person.IsFemale, (byte)person.RaceId, person.IsElder, nowEpoch);
                    if (refusal != Engine.BreedingRefusal.None) continue;
                    var match = Engine.BreedingMatchmaker.Score(heroApt, person.AptitudeVector(), ceiling, false, selectable);
                    results.Add(new BreedingSuggestionResponse
                    {
                        Key = "v:" + person.Id,
                        Name = Engine.FolkNameRegistry.ForNewcomer(person.Id, person.IsFemale),
                        IsVillager = true,
                        IsRelated = false,
                        RaceId = person.RaceId,
                        ExpectedTotal = match.Score / 10.0,
                        ChildFloorTotal = match.ChildFloorTotal,
                        SelectionMask = match.SelectionMask,
                        CostGold = Engine.BreedingEngine.CostFor(heroLineage.GenerationIndex, discount),
                    });
                }

                // Best expected child first; a relative only ranks where its
                // numbers put it (no sure growth already lowers them), and a
                // tie goes to the higher floor - the surer child.
                var top = results
                    .OrderByDescending(r => r.ExpectedTotal)
                    .ThenByDescending(r => r.ChildFloorTotal)
                    .ThenBy(r => r.IsRelated)
                    .Take(5)
                    .ToList();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, top);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Breeding suggest error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private static void AddChildRaceOptions(BreedingPreviewResponse response, byte defaultRace, byte otherRace)
        {
            response.ChildRaceOptions.Add(defaultRace);
            if (otherRace != defaultRace && otherRace != 0) response.ChildRaceOptions.Add(otherRace);
        }
    }
}
