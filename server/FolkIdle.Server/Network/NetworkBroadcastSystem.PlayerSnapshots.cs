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
        /// <summary>
        /// Task 78: GET /api/v1/combat/projection?slot=N (0-based, default 0).
        /// Every canonical monster, projected against this character's live
        /// payload. 409 with Reason NoSession when the player has no running
        /// session, because there is nothing live to project from.
        /// </summary>
        private async Task HandleCombatProjection(HttpListenerContext context)
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

                int slot = 0;
                string? slotText = context.Request.QueryString["slot"];
                if (slotText != null && (!int.TryParse(slotText, out slot) || slot < 0 || slot > 2))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                CombatProjectionResponse body;
                // Modul: ONE GAME MINUTE, NOT ONE WALL MINUTE (2026-11-01). At
                // the bots' tick multiplier a wall minute is up to 100 game
                // minutes, so a cached answer went on describing a character
                // several levels behind the live one. GameClock.Speed is 1 in
                // production, where nothing changes.
                if (_projectionCache.TryGetValue((playerId, slot), out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1) / GameClock.Speed)
                {
                    body = cached.Body;
                }
                else
                {
                    if (_playerSessionRegistry == null)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        return;
                    }

                    var order = new Domain.Combat.PayloadSnapshotOrder { PlayerId = playerId };
                    _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
                    var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
                    var snapshot = finished == order.Completion.Task ? order.Completion.Task.Result : null;
                    if (snapshot == null)
                    {
                        context.Response.StatusCode = 409;
                        context.Response.ContentType = "application/json";
                        await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Reason = "NoSession" });
                        context.Response.Close();
                        return;
                    }

                    var payload = snapshot.Value.Payload;
                    // Slots 2 and 3 are parked beside the register; swap the
                    // COPY so the projection fights in that character's gear.
                    Domain.Combat.SimulationEngine.SwapSlotIntoActiveRegister(ref payload, slot);

                    body = new CombatProjectionResponse { Slot = slot };
                    for (int id = ContentRegistry.FirstCanonicalMonsterId; id <= ContentRegistry.Monsters.Length; id++)
                    {
                        var e = Domain.Combat.HuntingProjection.Project(
                            in payload, id, snapshot.Value.GlobalXpMultiplier, snapshot.Value.ActiveGlobalEventId);
                        body.Monsters.Add(new HuntingEstimateResponse
                        {
                            MonsterId = e.MonsterId,
                            CanDamage = e.CanDamage,
                            SecondsPerKill = Math.Round(e.SecondsPerKill, 1),
                            SecondsPerKillLow = Math.Round(e.SecondsPerKillLow, 1),
                            SecondsPerKillHigh = Math.Round(e.SecondsPerKillHigh, 1),
                            XpPerHour = e.XpPerHour,
                            GoldPerHour = e.GoldPerHour,
                            SurvivesWithFood = e.SurvivesWithFood,
                            SurvivesWithoutFood = e.SurvivesWithoutFood,
                            KillsBeforeDeathWithoutFood = e.KillsBeforeDeathWithoutFood,
                            FoodPerHour = Math.Round(e.FoodPerHour, 1),
                        });
                    }
                    _projectionCache[(playerId, slot)] = (DateTime.UtcNow, body);
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, body);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Combat projection error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        /// <summary>
        /// GET /api/v1/character/stats?slot=N (0-based, default 0): the
        /// character stat sheet, from the live payload with that slot swapped
        /// into the register - the same snapshot the hunting advisor reads.
        /// 409 NoSession without a running session.
        /// </summary>
        private async Task HandleCharacterStats(HttpListenerContext context)
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

                int slot = 0;
                string? slotText = context.Request.QueryString["slot"];
                if (slotText != null && (!int.TryParse(slotText, out slot) || slot < 0 || slot > 2))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                if (_playerSessionRegistry == null)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                var order = new Domain.Combat.PayloadSnapshotOrder { PlayerId = playerId };
                _playerSessionRegistry.PayloadSnapshotQueue.Enqueue(order);
                var finished = await Task.WhenAny(order.Completion.Task, Task.Delay(3000));
                var snapshot = finished == order.Completion.Task ? order.Completion.Task.Result : null;
                if (snapshot == null)
                {
                    context.Response.StatusCode = 409;
                    context.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Reason = "NoSession" });
                    context.Response.Close();
                    return;
                }

                var payload = snapshot.Value.Payload;
                Domain.Combat.SimulationEngine.SwapSlotIntoActiveRegister(ref payload, slot);
                var body = Domain.Combat.CharacterStatSheet.Build(payload, slot);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, body);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Character stats error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: excludes rows already claimed or with a claim currently in
        // flight (IsPending) - matches ClaimMailItemAsync's own rejection
        // condition exactly, so the list a player sees only ever contains
        // ids that a claim request against them can actually succeed on.
        private async Task HandleMailboxListSnapshot(HttpListenerContext context)
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

                var entries = await db.MailboxInstances
                    .AsNoTracking()
                    .Where(m => m.PlayerId == playerId && !m.IsClaimed && !m.IsPending)
                    .OrderByDescending(m => m.ReceivedTimestamp)
                    .Select(m => new MailboxEntryResponse
                    {
                        Id = m.Id,
                        BaseItemId = m.BaseItemId,
                        QualityTier = m.QualityTier,
                        Quantity = m.Quantity,
                        GoldAttachment = m.GoldAttachment,
                        DiamondAttachment = m.DiamondAttachment,
                        TitleAttachment = m.TitleAttachment,
                        HasEquipmentAttachment = m.AttachedEquipmentId.HasValue,
                        SenderName = m.SenderName,
                        MessageText = m.MessageText,
                        ReceivedTimestamp = m.ReceivedTimestamp
                    })
                    .ToListAsync();

                foreach (var entry in entries)
                {
                    var title = FolkIdle.Server.Domain.Progression.TitleRegistry.Find(entry.TitleAttachment);
                    entry.TitleAttachment = title?.DisplayName;
                    entry.TitleAttachmentColor = title?.Color;
                }

                await transaction.CommitAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, entries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mailbox list snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Production Release Hardening, Part 2. ActiveChroniclePassLevel/
        // AccumulatedSeasonalXp were removed from StateUpdatePacket - this
        // is their new home. EventHorizonTransactionCount was never
        // actually populated on the old packet field at all (dead code -
        // always sent as 0); this computes the real value for the first
        // time, from EventHorizonPremiumLedgers (the same ledger every
        // premium-balance change already writes to).
        private async Task HandlePlayerMetadata(HttpListenerContext context)
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

                var pass = await db.PlayerChroniclePasses
                    .AsNoTracking()
                    .Where(p => p.PlayerId == playerId)
                    .Select(p => new { p.PassLevel, p.AccumulatedXp })
                    .SingleOrDefaultAsync();

                int transactionCount = await db.EventHorizonPremiumLedgers
                    .AsNoTracking()
                    .CountAsync(l => l.PlayerId == playerId);

                await transaction.CommitAsync();

                var response = new PlayerMetadataResponse
                {
                    ChroniclePassLevel = pass?.PassLevel ?? 0,
                    AccumulatedSeasonalXp = pass?.AccumulatedXp ?? 0,
                    EventHorizonTransactionCount = transactionCount
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player metadata error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul 13: authorized snapshot of the player's real Race Mastery
        // progress. PlayerRaceMasteries is already populated by CodexEngine's
        // kill-event cron. Uses plain LINQ rather than raw SQL - the table is
        // mapped via [Table("player_race_masteries")] (lowercase, unlike every
        // other table in this codebase), and EF Core resolves that mapping
        // correctly on its own, sidestepping manual identifier quoting entirely.
        private async Task HandleMasterySnapshot(HttpListenerContext context)
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

                var entries = await db.PlayerRaceMasteries
                    .AsNoTracking()
                    .Where(m => m.PlayerId == playerId)
                    .ToListAsync();

                var response = new System.Collections.Generic.List<RaceMasterySnapshotEntryResponse>(entries.Count);

                foreach (var entry in entries)
                {
                    response.Add(new RaceMasterySnapshotEntryResponse
                    {
                        RaceId = entry.RaceId,
                        Level = entry.MasteryLevel,
                        Experience = entry.CumulativeXp,
                        NextLevelExperience = CodexEngine.GetRaceMasteryRequiredXp(entry.MasteryLevel)
                    });
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Race mastery snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Inventory screen. One read-only snapshot covering all three
        // places a player's belongings actually live: EquipmentInstances
        // (backpack gear), CommodityRecords (carried material stacks) and
        // VillageStashInstances (the overflow stash CombatLootEngine spills
        // into when the backpack is full). Equipped state comes from
        // PlayerRecord's three equipped-id columns, which is what
        // EquipmentSlotEngine itself treats as authoritative.
        private async Task HandlePlayerInventorySnapshot(HttpListenerContext context)
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

                var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(pr => pr.Id == playerId);

                var equipment = await db.EquipmentInstances
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .ToListAsync();

                var commodities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var stash = await db.VillageStashInstances
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .ToListAsync();

                // Modul: TWO DIFFERENT QUESTIONS, TWO DIFFERENT QUERIES.
                //
                // "Who wears this item" must consider EVERY character the player
                // owns, including the benched ones past slot 2. A benched
                // character really does keep its EquippedWeaponId - measured: a
                // SlotIndex 4 character on the dev fixture holds a weapon and an
                // axe - so narrowing this to the playable three would report
                // those items as free, offer them in the picker, and let one
                // EquipmentInstance end up worn by two characters at once the
                // moment that ancestor is fielded.
                //
                // "What is this character's combat rating" is only ever asked
                // about the three PLAYABLE slots, which is what the paper doll
                // has tabs for - and computing it walks the gear of every row it
                // is given, so handing it the whole lineage would cost 20+
                // needless stat computations per inventory fetch on a long-played
                // account.
                // Two queries rather than one filtered in memory: the worn-item
                // pass needs no lineage at all, and joining it for a whole
                // lineage - 29 rows on the dev fixture, more on a long-played
                // account - to read race off three of them is work this endpoint
                // does on every inventory refresh.
                var allCharacters = await db.CharacterRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var rosterCharacters = await db.CharacterRecords
                    .AsNoTracking()
                    .Include(c => c.Lineage)
                    .Where(c => c.PlayerId == playerId
                        && c.SlotIndex >= 0
                        && c.SlotIndex < CharacterSlotEngine.MaxCharacterSlots)
                    .OrderBy(c => c.SlotIndex)
                    .ThenBy(c => c.Id)
                    .ToListAsync();

                // Modul: paper-doll combat rating, per character. STR/DEX/CON/
                // LCK, potions, area completions and race mastery are all
                // account-wide (see PlayerRecord/PlayerRaceMastery) - only age
                // phase, race/genetics and gear differ per roster slot - so
                // these are fetched once here rather than once per character.
                var raceMasteries = await db.PlayerRaceMasteries
                    .AsNoTracking()
                    .Where(m => m.PlayerId == playerId)
                    .ToListAsync();
                int humanMastery = 0, vilaMastery = 0, draugrMastery = 0;
                for (int i = 0; i < raceMasteries.Count; i++)
                {
                    if (raceMasteries[i].RaceId == RaceIds.Human) humanMastery = raceMasteries[i].MasteryLevel;
                    else if (raceMasteries[i].RaceId == RaceIds.Vila) vilaMastery = raceMasteries[i].MasteryLevel;
                    else if (raceMasteries[i].RaceId == RaceIds.Draugr) draugrMastery = raceMasteries[i].MasteryLevel;
                }
                // Modul: the skill tree is account-wide too - read once, so the
                // roster's per-character stats come from the same
                // SimulationEngine.ApplyTreeStats that LiveCombatStats uses.
                byte[] rosterRanks = await SkillTreeEngine.LoadRanksAsync(db, playerId);
                var rosterTree = new TickStatePayload { SkillTreeVersion = WarTreeRegistry.CurrentVersion };
                foreach (var warNode in WarTreeRegistry.All()) rosterTree.SkillRanks[warNode.Id] = rosterRanks[warNode.Id];
                var completedRegionIds = await db.PlayerRegionCompletions
                    .AsNoTracking()
                    .Where(r => r.PlayerId == playerId)
                    .Select(r => r.RegionId)
                    .ToListAsync();
                int completedAreaFlags = 0;
                for (int i = 0; i < completedRegionIds.Count; i++)
                {
                    completedAreaFlags |= 1 << completedRegionIds[i];
                }

                await transaction.CommitAsync();

                // Modul: per-character equipment. "Is this item equipped" is an
                // account-wide question for the inventory screen - an item worn
                // by the second character is just as unavailable as one worn by
                // the first, and showing it as free to sell would be a lie.
                // Modul: roster loadouts. Maps each worn item to the SLOT INDEX
                // of the character wearing it, rather than only recording that
                // somebody wears it. The wire deliberately carries only the
                // active character's gear (gear changes on a button press, not
                // at 10Hz), so this snapshot is the only place the Roster screen
                // can learn what characters 2 and 3 are actually wearing.
                //
                // -1 means "not worn". Slot index rather than character Guid
                // because the Roster screen is laid out by slot and would only
                // have to map the Guid back anyway.
                var wornItemSlotIndices = new Dictionary<long, int>();
                var wornItemEquipSlots = new Dictionary<long, int>();
                foreach (var rosterCharacter in allCharacters)
                {
                    void RecordWorn(long? itemId, int equipSlotIndex)
                    {
                        if (!itemId.HasValue) return;
                        wornItemSlotIndices[itemId.Value] = rosterCharacter.SlotIndex;
                        wornItemEquipSlots[itemId.Value] = equipSlotIndex;
                    }

                    RecordWorn(rosterCharacter.EquippedWeaponId, EquipmentSlotEngine.SlotWeapon);
                    RecordWorn(rosterCharacter.EquippedHelmetId, EquipmentSlotEngine.SlotHelmet);
                    RecordWorn(rosterCharacter.EquippedChestId, EquipmentSlotEngine.SlotChest);
                    RecordWorn(rosterCharacter.EquippedGlovesId, EquipmentSlotEngine.SlotGloves);
                    RecordWorn(rosterCharacter.EquippedLeggingsId, EquipmentSlotEngine.SlotLeggings);
                    RecordWorn(rosterCharacter.EquippedBootsId, EquipmentSlotEngine.SlotBoots);
                    RecordWorn(rosterCharacter.EquippedAmuletId, EquipmentSlotEngine.SlotAmulet);
                    RecordWorn(rosterCharacter.EquippedRingId, EquipmentSlotEngine.SlotRing);

                    // Modul: THE THREE TOOL SLOTS, missing here since tools
                    // were added. This is the whole of "I equip a tool and
                    // nothing appears in the slot": the equip SUCCEEDS and
                    // EquippedAxeId is written, but this snapshot is the only
                    // thing the Character screen reads to decide what a slot
                    // holds. Unrecorded meant EquippedByCharacterSlot came back
                    // -1, so the doll drew the slot empty AND the tool stayed
                    // in its own picker as available - the item was worn and
                    // offered at the same time.
                    RecordWorn(rosterCharacter.EquippedAxeId, EquipmentSlotEngine.SlotAxe);
                    RecordWorn(rosterCharacter.EquippedPickaxeId, EquipmentSlotEngine.SlotPickaxe);
                    RecordWorn(rosterCharacter.EquippedRodId, EquipmentSlotEngine.SlotRod);
                }

                var rosterCombatStats = new System.Collections.Generic.List<RosterCombatStatsResponse>();
                if (player != null)
                {
                    foreach (var rosterCharacter in rosterCharacters)
                    {
                        (CombatStats stats, EquippedSetIds wornSets) = await EquipmentSlotEngine.ComputeCharacterCombatStatsWithSetsAsync(
                            db, player, rosterCharacter, humanMastery, vilaMastery, draugrMastery, completedAreaFlags);
                        SimulationEngine.ApplyTreeStats(ref stats, in rosterTree);

                        var activeSets = new System.Collections.Generic.List<ActiveSetResponse>();
                        foreach (var set in SetBonusEngine.DescribeActive(wornSets))
                        {
                            activeSets.Add(new ActiveSetResponse
                            {
                                SetId = set.SetId,
                                Family = ArmourSetRegistry.FamilyOfSetId(set.SetId),
                                Offensive = SetBonusEngine.IsOffensiveSet(set.SetId),
                                Pieces = set.Pieces,
                                Tier = set.Tier,
                                NextTierPieces = set.Tier == 1 ? SetBonusEngine.TierTwoPieces : set.Tier == 2 ? SetBonusEngine.TierThreePieces : 0,
                                QualityScale = Math.Round(set.QualityScale, 2),
                                DamagePct = Math.Round(set.Bonus.FireDamageMultiplierPct, 1),
                                ArmorPct = Math.Round(set.Bonus.TotalArmorMultiplierPct, 1),
                                Burn = set.Bonus.BurnApplicationActive,
                                Thorns = set.Bonus.ThornsReflectionActive,
                                DamageCap = set.Bonus.DamageCapActive,
                            });
                        }

                        rosterCombatStats.Add(new RosterCombatStatsResponse
                        {
                            SlotIndex = rosterCharacter.SlotIndex,
                            Accuracy = stats.AccuracyRating,
                            Armor = stats.FlatPhysicalArmor,
                            BlockPct = stats.BlockStrengthPct,
                            ActiveSets = activeSets
                        });
                    }
                }

                var response = new PlayerInventorySnapshotResponse
                {
                    BackpackSlotsUsed = equipment.Count,
                    // Modul: unlimited village chest. Was
                    // VillageStashInstance.MaxStackQuantity (9999), which the
                    // Inventory screen rendered as "stacks cap at 9999". There
                    // is no cap any more, so 0 is the agreed "unbounded"
                    // sentinel and the client suppresses the line entirely.
                    MaxStackQuantity = 0L,
                    RosterCombatStats = rosterCombatStats,
                    LifeHighestRegion = player?.LifeHighestRegion ?? 0
                };

                for (int i = 0; i < equipment.Count; i++)
                {
                    var item = equipment[i];

                    var affixes = new Dictionary<string, int>();
                    bool payloadLockFlag = false;
                    if (!string.IsNullOrWhiteSpace(item.AffixPayload) &&
                        System.Text.Json.Nodes.JsonNode.Parse(item.AffixPayload) is System.Text.Json.Nodes.JsonObject affixObject)
                    {
                        foreach (var kvp in affixObject)
                        {
                            if (kvp.Value is not System.Text.Json.Nodes.JsonValue affixValue) continue;

                            if (kvp.Key == "is_affix_locked")
                            {
                                payloadLockFlag = affixValue.TryGetValue(out bool lockedFlag) && lockedFlag;
                                continue;
                            }

                            if (affixValue.TryGetValue(out int magnitude))
                            {
                                affixes[kvp.Key] = magnitude;
                            }
                        }
                    }

                    response.Equipment.Add(new InventoryEquipmentResponse
                    {
                        Id = item.Id,
                        BaseItemId = item.BaseItemId,
                        QualityTier = item.QualityTier,
                        IsEquipped = wornItemSlotIndices.ContainsKey(item.Id),
                        // Modul: roster loadouts. -1 when carried rather than worn.
                        EquippedByCharacterSlot = wornItemSlotIndices.TryGetValue(item.Id, out int wearerSlotIndex) ? wearerSlotIndex : -1,
                        EquippedInSlotIndex = wornItemEquipSlots.TryGetValue(item.Id, out int wornEquipSlot) ? wornEquipSlot : -1,
                        VaultLockRegion = item.VaultLockRegion,
                        Affixes = affixes,
                        IsAffixLocked = item.IsAffixLocked || payloadLockFlag
                    });
                }

                // Both tables are summed into one row per item. They are one
                // store as far as every consumer is concerned - see
                // InventoryStackResponse.Quantity.
                var stacksByItemId = new System.Collections.Generic.Dictionary<string, InventoryStackResponse>(commodities.Count + stash.Count);

                for (int i = 0; i < commodities.Count; i++)
                {
                    var row = commodities[i];
                    if (!stacksByItemId.TryGetValue(row.ItemId, out var stack))
                    {
                        stack = new InventoryStackResponse { ItemId = row.ItemId };
                        stacksByItemId[row.ItemId] = stack;
                    }
                    stack.Quantity += row.Quantity;
                }

                for (int i = 0; i < stash.Count; i++)
                {
                    var row = stash[i];
                    if (!stacksByItemId.TryGetValue(row.ItemId, out var stack))
                    {
                        stack = new InventoryStackResponse { ItemId = row.ItemId };
                        stacksByItemId[row.ItemId] = stack;
                    }
                    stack.Quantity += row.Quantity;
                }

                response.Stacks.AddRange(stacksByItemId.Values);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player inventory snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: THE MAIN CHARACTER's gear, because that is who EquipItem with
        // Guid.Empty dresses (ResolveCharacterForUpdateAsync: the character whose
        // Id is PlayerRecords.PlayerGuid). The loot list's Wear sends no target,
        // so comparing against anyone else would compare against a piece the
        // press does not replace. All ELEVEN slots - the tools included.
        private async Task HandlePlayerWornSnapshot(HttpListenerContext context)
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

                // Modul: ?characterId= names whose gear to compare against
                // (2026-10-08). A drop belongs to whichever character is
                // fighting, and that is not always the main one - comparing a
                // fighter's boots drop against the main character's empty boot
                // slot told the player "+1000 DEF, boots slot is empty" while
                // the fighter wore Transcendent boots. Must be this player's
                // own character; anything else falls back to the main one.
                // The fallback is slot 1's character, the one an empty-Guid
                // equip dresses (EquipmentSlotEngine.FieldedCharacterIdAsync) -
                // not PlayerGuid's founder, who may be resting on the bench.
                var mainGuid = await EquipmentSlotEngine.FieldedCharacterIdAsync(db, playerId, 0);
                if (Guid.TryParse(context.Request.QueryString["characterId"], out var askedFor) && askedFor != Guid.Empty)
                {
                    mainGuid = askedFor;
                }
                var main = mainGuid == Guid.Empty
                    ? null
                    : await db.CharacterRecords.AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == mainGuid && c.PlayerId == playerId);

                var pieces = new List<WornPieceResponse>(11);
                if (main != null)
                {
                    var slotByInstance = new Dictionary<long, int>(11);
                    void Worn(long? id, int slot)
                    {
                        if (id.HasValue) slotByInstance[id.Value] = slot;
                    }
                    Worn(main.EquippedWeaponId, EquipmentSlotEngine.SlotWeapon);
                    Worn(main.EquippedHelmetId, EquipmentSlotEngine.SlotHelmet);
                    Worn(main.EquippedChestId, EquipmentSlotEngine.SlotChest);
                    Worn(main.EquippedGlovesId, EquipmentSlotEngine.SlotGloves);
                    Worn(main.EquippedLeggingsId, EquipmentSlotEngine.SlotLeggings);
                    Worn(main.EquippedBootsId, EquipmentSlotEngine.SlotBoots);
                    Worn(main.EquippedAmuletId, EquipmentSlotEngine.SlotAmulet);
                    Worn(main.EquippedRingId, EquipmentSlotEngine.SlotRing);
                    Worn(main.EquippedAxeId, EquipmentSlotEngine.SlotAxe);
                    Worn(main.EquippedPickaxeId, EquipmentSlotEngine.SlotPickaxe);
                    Worn(main.EquippedRodId, EquipmentSlotEngine.SlotRod);

                    var ids = slotByInstance.Keys.ToList();
                    var rows = await db.EquipmentInstances.AsNoTracking()
                        .Where(e => e.PlayerId == playerId && ids.Contains(e.Id))
                        .Select(e => new { e.Id, e.BaseItemId, e.QualityTier, e.AffixPayload })
                        .ToListAsync();
                    foreach (var row in rows)
                    {
                        var rarities = new List<int>(5);
                        try
                        {
                            using var affixes = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.AffixPayload) ? "{}" : row.AffixPayload);
                            if (affixes.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var affix in affixes.RootElement.EnumerateObject())
                                {
                                    rarities.Add((int)AffixRegistry.ParseRarity(affix.Name));
                                }
                            }
                        }
                        catch (JsonException) { }
                        pieces.Add(new WornPieceResponse
                        {
                            InstanceId = row.Id,
                            BaseItemId = row.BaseItemId,
                            QualityTier = row.QualityTier,
                            SlotIndex = slotByInstance[row.Id],
                            AffixRarities = rarities,
                        });
                    }
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new { Pieces = pieces });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player worn snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandlePlayerRecords(HttpListenerContext context)
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
                var row = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new
                    {
                        p.BestHit,
                        p.BossBestKillTenthsR1,
                        p.BossBestKillTenthsR2,
                        p.BossBestKillTenthsR3,
                        p.BossBestKillTenthsR4,
                        p.BossBestKillTenthsR5,
                        p.BestDropTier,
                        p.BestDropBaseId,
                        p.BestDropAtUtc,
                        p.DelveDeepestFloor,
                    })
                    .FirstOrDefaultAsync();

                if (row == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, new
                {
                    row.BestHit,
                    BossBestKillTenths = new[] { row.BossBestKillTenthsR1, row.BossBestKillTenthsR2, row.BossBestKillTenthsR3, row.BossBestKillTenthsR4, row.BossBestKillTenthsR5 },
                    row.BestDropTier,
                    row.BestDropBaseId,
                    row.BestDropAtUtc,
                    row.DelveDeepestFloor,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player records error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private async Task HandlePlayerMaterialsSnapshot(HttpListenerContext context)
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

                var commodities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .Select(c => new { c.ItemId, c.Quantity })
                    .ToListAsync();

                var stash = await db.VillageStashInstances
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .Select(v => new { v.ItemId, v.Quantity })
                    .ToListAsync();

                var stacksByItemId = new System.Collections.Generic.Dictionary<string, InventoryStackResponse>(
                    commodities.Count + stash.Count);

                for (int i = 0; i < commodities.Count; i++)
                {
                    var row = commodities[i];
                    if (!stacksByItemId.TryGetValue(row.ItemId, out var stack))
                    {
                        stack = new InventoryStackResponse { ItemId = row.ItemId };
                        stacksByItemId[row.ItemId] = stack;
                    }
                    stack.Quantity += row.Quantity;
                }

                for (int i = 0; i < stash.Count; i++)
                {
                    var row = stash[i];
                    if (!stacksByItemId.TryGetValue(row.ItemId, out var stack))
                    {
                        stack = new InventoryStackResponse { ItemId = row.ItemId };
                        stacksByItemId[row.ItemId] = stack;
                    }
                    stack.Quantity += row.Quantity;
                }

                var response = new PlayerMaterialsSnapshotResponse();
                response.Stacks.AddRange(stacksByItemId.Values);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player materials snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: Crafting Tree screen. Joins ContentRegistry's static recipe
        // table against this player's live unified material balance in one
        // response, so the client never has to guess at either half.
        private async Task HandleCraftingRecipeSnapshot(HttpListenerContext context)
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

                var player = await db.PlayerRecords.AsNoTracking().FirstOrDefaultAsync(pr => pr.Id == playerId);

                var commodities = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId)
                    .ToListAsync();

                var stash = await db.VillageStashInstances
                    .AsNoTracking()
                    .Where(v => v.PlayerId == playerId)
                    .ToListAsync();

                await transaction.CommitAsync();

                var unifiedStock = new System.Collections.Generic.Dictionary<string, long>(commodities.Count + stash.Count);
                for (int i = 0; i < commodities.Count; i++)
                {
                    unifiedStock.TryGetValue(commodities[i].ItemId, out long existing);
                    unifiedStock[commodities[i].ItemId] = existing + commodities[i].Quantity;
                }
                for (int i = 0; i < stash.Count; i++)
                {
                    unifiedStock.TryGetValue(stash[i].ItemId, out long existing);
                    unifiedStock[stash[i].ItemId] = existing + stash[i].Quantity;
                }

                var response = new CraftingRecipeSnapshotResponse
                {
                    PlayerLevel = player?.CurrentLevel ?? 0
                };

                // Extracted to a synchronous helper: ContentRegistry.Recipes
                // is a ReadOnlySpan, and a ref struct local is not permitted
                // inside an async method under this language version.
                AppendCraftingRecipes(response.Recipes, unifiedStock);

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Crafting recipe snapshot error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        private static void AppendCraftingRecipes(
            System.Collections.Generic.List<CraftingRecipeResponse> target,
            System.Collections.Generic.Dictionary<string, long> unifiedStock)
        {
            ReadOnlySpan<ContentRegistry.RecipeDefinition> recipes = ContentRegistry.Recipes;
            for (int i = 0; i < recipes.Length; i++)
            {
                ContentRegistry.RecipeDefinition recipe = recipes[i];

                string mat1BaseId = recipe.Mat1Id > 0 ? ContentRegistry.GetItemBaseId(recipe.Mat1Id) : string.Empty;
                string mat2BaseId = recipe.Mat2Id > 0 ? ContentRegistry.GetItemBaseId(recipe.Mat2Id) : string.Empty;

                unifiedStock.TryGetValue(mat1BaseId, out long mat1Stock);
                unifiedStock.TryGetValue(mat2BaseId, out long mat2Stock);

                target.Add(new CraftingRecipeResponse
                {
                    ResultItemId = recipe.ResultItemId,
                    ResultBaseItemId = ContentRegistry.GetItemBaseId(recipe.ResultItemId),
                    ProfessionType = recipe.ProfessionType,
                    RequiredLevel = recipe.RequiredLevel,
                    CraftingTimeMs = recipe.CraftingTimeMs,
                    Mat1Id = recipe.Mat1Id,
                    Mat1BaseItemId = mat1BaseId,
                    Mat1Count = recipe.Mat1Count,
                    Mat1CurrentStock = mat1Stock,
                    Mat2Id = recipe.Mat2Id,
                    Mat2BaseItemId = mat2BaseId,
                    Mat2Count = recipe.Mat2Count,
                    Mat2CurrentStock = mat2Stock
                });
            }
        }

        private async Task HandlePlayerNames(HttpListenerContext context)
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
                string rawIds = (query["ids"] ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(rawIds))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                string[] parts = rawIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var requestedIds = new System.Collections.Generic.List<long>(parts.Length);
                for (int i = 0; i < parts.Length && requestedIds.Count < NameLookupBatchLimit; i++)
                {
                    if (long.TryParse(parts[i], out long parsed) && parsed > 0 && !requestedIds.Contains(parsed))
                    {
                        requestedIds.Add(parsed);
                    }
                }

                if (requestedIds.Count == 0)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

                var rows = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => requestedIds.Contains(p.Id))
                    .Select(p => new PlayerNameEntryResponse { PlayerId = p.Id, Username = p.Username ?? string.Empty })
                    .ToListAsync();

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, rows);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player names lookup error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }

        // Modul: UI audit follow-up. Player Statistics - see this route's
        // registration comment. Read-only aggregate over data every field
        // already persisted for another system's own purposes; nothing new
        // is tracked to build this.
        private async Task HandlePlayerStatistics(HttpListenerContext context)
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

                var player = await db.PlayerRecords
                    .AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => new
                    {
                        p.CurrentLevel,
                        p.CurrentXp,
                        p.PremiumDiamonds,
                        p.LoginStreakDays,
                        p.GuildId,
                        p.AvailableSkillPoints,
                        p.TotalItemsCrafted,
                        p.TotalDeaths,
                        p.TotalPlayTimeSeconds
                    })
                    .SingleOrDefaultAsync();

                if (player == null)
                {
                    await transaction.CommitAsync();
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                long gold = await db.CommodityRecords
                    .AsNoTracking()
                    .Where(c => c.PlayerId == playerId && c.ItemId == "gold")
                    .Select(c => c.Quantity)
                    .FirstOrDefaultAsync();

                int achievementsClaimedCount = await db.PlayerLifetimeAchievements
                    .AsNoTracking()
                    .CountAsync(a => a.PlayerId == playerId && a.IsClaimed);

                int regionsCompletedCount = await db.PlayerRegionCompletions
                    .AsNoTracking()
                    .CountAsync(r => r.PlayerId == playerId);

                int characterCount = await db.CharacterRecords
                    .AsNoTracking()
                    .CountAsync(c => c.PlayerId == playerId);

                string guildName = string.Empty;
                if (player.GuildId > 0)
                {
                    guildName = await db.GuildRecords
                        .AsNoTracking()
                        .Where(g => g.Id == player.GuildId)
                        .Select(g => g.Name)
                        .FirstOrDefaultAsync() ?? string.Empty;
                }

                // Modul: lifetime statistics. Kills come from the codex rather
                // than a dedicated counter: monster_codex_entries has recorded a
                // per-monster KillCount since the codex shipped, so summing it
                // is retroactively correct for every existing player and cannot
                // drift from a second source of truth.
                long totalKills = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId)
                    .SumAsync(e => (long)e.KillCount);

                // The five canonical region bosses. Ids 91-115 are the five
                // regions of four regulars plus a boss, so every fifth id
                // starting at 95 is a boss - see ContentRegistry's monster
                // block and Test_Content_RegionBossesAreContinuousWithTheirRegionCurve.
                long bossesSlain = await db.MonsterCodexEntries
                    .AsNoTracking()
                    .Where(e => e.PlayerId == playerId && CanonicalBossMonsterIds.Contains(e.MonsterId))
                    .SumAsync(e => (long)e.KillCount);

                // Modul: there was a `Villagers` roster here - every character,
                // as "Slot N · working/idle" - feeding the Village screen's
                // "Work slots" panel, which called those people "production
                // slots". Removed with the panel (task 77); the Character screen
                // owns the roster and CharacterCount above is the number.

                await transaction.CommitAsync();

                var response = new PlayerStatisticsResponse
                {
                    Level = player.CurrentLevel,
                    Xp = player.CurrentXp,
                    Gold = gold,
                    PremiumDiamonds = player.PremiumDiamonds,
                    LoginStreakDays = player.LoginStreakDays,
                    AchievementsClaimedCount = achievementsClaimedCount,
                    RegionsCompletedCount = regionsCompletedCount,
                    CharacterCount = characterCount,
                    AvailableSkillPoints = player.AvailableSkillPoints,
                    GuildName = guildName,

                    // Modul: lifetime statistics. Playtime is reported as of the
                    // last checkpoint rather than including the live session's
                    // current stretch: the authoritative session start lives on
                    // the tick thread's payload, and reaching across to it from
                    // an HTTP handler to gain sub-checkpoint precision on a
                    // number displayed in whole hours is not worth the coupling.
                    TotalKills = totalKills,
                    BossesSlain = bossesSlain,
                    TotalItemsCrafted = player.TotalItemsCrafted,
                    TotalDeaths = player.TotalDeaths,
                    TotalPlayTimeSeconds = player.TotalPlayTimeSeconds
                };

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.OutputStream, response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Player statistics error: {ex}");
                context.Response.StatusCode = 500;
            }

            context.Response.Close();
        }
    }
}
