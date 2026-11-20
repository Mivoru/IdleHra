using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: moved out of SimulationEngine.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md).
    public partial class SimulationEngine
    {
        private void BattlePassWorkerLoop()
        {
            while (IsRunning)
            {

                // Throttle to max 50 ops per second (i.e. ~1 op per 20ms).
                // We'll run every 20ms and do exactly 1 op.
                Thread.Sleep(20);

                if (GlobalEngineState.IsEraTransitionActive) continue;

                foreach (var kvp in _liveSessionContexts)
                {
                    if (kvp.Value.TryDequeueBattlePassClaim(out var req))
                    {
                        var t = ExecuteBattlePassClaimAsync(kvp.Key, req.TargetMilestoneIndex, req.AccumulatedSeasonalXp, req.ActiveChroniclePassLevel);
                        t.GetAwaiter().GetResult();
                        
                        _networkSystem.CommandQueue.Enqueue(new NetworkBroadcastSystem.PlayerCommand
                        {
                            PlayerId = kvp.Key,
                            Packet = new ClientCommandPacket { Command = CommandType.ReloadState }
                        });
                        break; // Only process one item every 20ms globally to strictly enforce the 50 ops/sec cap.
                    }
                }
            }
        }

        // Modul: Comprehensive Game System Audit, Part 4.3. Unlocks the
        // Chronicle Pass premium track by deducting
        // ChroniclePassEconomy.PremiumPassPriceDiamonds from the player's
        // PremiumDiamonds inside one Serializable FOR UPDATE transaction.
        // Rejections (already unlocked, insufficient balance) surface
        // through the generic command-result channel so UiCommandResultToast
        // can display them.
        internal async Task<bool> ExecutePassPurchaseAsync(long playerId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                var player = await context.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();
                if (player == null)
                {
                    await transaction.SafeRollbackAsync();
                    return false;
                }

                var pass = await context.PlayerChroniclePasses
                    .FromSqlRaw("SELECT * FROM \"PlayerChroniclePasses\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

                if (pass != null && pass.PremiumUnlocked)
                {
                    await transaction.SafeRollbackAsync();
                    _playerRegistry.EnqueueCommandResult(playerId, (byte)CommandResultCode.GenericValidationFailure);
                    return false;
                }

                if (player.PremiumDiamonds < ChroniclePassEconomy.PremiumPassPriceDiamonds)
                {
                    await transaction.SafeRollbackAsync();
                    _playerRegistry.EnqueueCommandResult(playerId, (byte)CommandResultCode.InsufficientGold);
                    return false;
                }

                int previousBalance = player.PremiumDiamonds;
                await Engine.DiamondLedger.LabelAsync(context, Engine.DiamondLedger.ChroniclePass);
                player.PremiumDiamonds -= ChroniclePassEconomy.PremiumPassPriceDiamonds;

                if (pass == null)
                {
                    pass = new PlayerChroniclePass
                    {
                        PlayerId = playerId,
                        PassLevel = 0,
                        AccumulatedXp = 0,
                        ClaimedMilestonesBitmask = 0UL,
                        PremiumUnlocked = true
                    };
                    context.PlayerChroniclePasses.Add(pass);
                }
                else
                {
                    pass.PremiumUnlocked = true;
                }

                context.EventHorizonPremiumLedgers.Add(new EventHorizonPremiumLedger
                {
                    TransactionId = $"pass_purchase_{playerId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                    PlayerId = playerId,
                    PreviousBalance = previousBalance,
                    NewBalance = player.PremiumDiamonds,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });

                await context.SaveChangesAsync();
                await transaction.CommitAsync();

                // Modul: Play Mode audit fix. This transaction runs off the
                // tick thread against its own DbContext - without this, the
                // diamond deduction landed in Postgres but the live
                // in-memory TickStatePayload.PremiumCurrency (and the
                // packet built from it) never changed, so a player who
                // just spent 950 diamonds would see their old balance
                // until their next reconnect. BillingSyncNotification/
                // BillingSyncQueue already exists for exactly this
                // PlayerId+new-balance push (see the IAP billing path's
                // own use of it) - reused directly rather than adding a
                // near-duplicate notification type.
                _playerRegistry.BillingSyncQueue.Enqueue(new BillingSyncNotification
                {
                    PlayerId = playerId,
                    PremiumDiamondsBalance = player.PremiumDiamonds
                });

                return true;
            }
            catch (Exception ex)
            {
                await transaction.SafeRollbackAsync();
                Console.WriteLine($"Battle pass purchase failed for player {playerId}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ExecuteBattlePassClaimAsync(long playerId, uint milestoneIndex, uint seasonalXp, uint passLevel)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

            try
            {
                var player = await context.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();
                if (player == null || milestoneIndex >= 50)
                {
                    await transaction.SafeRollbackAsync();
                    return false;
                }

                var pass = await context.PlayerChroniclePasses
                    .FromSqlRaw("SELECT * FROM \"PlayerChroniclePasses\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                    .FirstOrDefaultAsync();

                if (pass == null)
                {
                    pass = new PlayerChroniclePass
                    {
                        PlayerId = playerId,
                        PassLevel = 0,
                        AccumulatedXp = 0,
                        ClaimedMilestonesBitmask = 0UL
                    };
                    context.PlayerChroniclePasses.Add(pass);
                }

                if (pass.AccumulatedXp < seasonalXp)
                {
                    pass.AccumulatedXp = (int)Math.Min(int.MaxValue, seasonalXp);
                }

                if (pass.PassLevel < passLevel)
                {
                    pass.PassLevel = (int)Math.Min(50U, passLevel);
                }

                int requiredXp = checked((int)((milestoneIndex + 1U) * 1000U));
                if (pass.AccumulatedXp < requiredXp)
                {
                    await transaction.SafeRollbackAsync();
                    return false;
                }

                ulong milestoneBit = 1UL << (int)milestoneIndex;
                if ((pass.ClaimedMilestonesBitmask & milestoneBit) != 0UL)
                {
                    await transaction.SafeRollbackAsync();
                    return false;
                }

                pass.ClaimedMilestonesBitmask |= milestoneBit;
                int resolvedLevel = (int)Math.Min(50U, Math.Max(milestoneIndex + 1U, (uint)(pass.AccumulatedXp / 1000)));
                if (pass.PassLevel < resolvedLevel)
                {
                    pass.PassLevel = resolvedLevel;
                }

                int qualityTier = 1 + (int)(milestoneIndex / 10U);
                context.EquipmentInstances.Add(new EquipmentInstance
                {
                    PlayerId = playerId,
                    BaseItemId = $"chronicle_free_{milestoneIndex + 1U}",
                    QualityTier = qualityTier,
                    AffixPayload = "{}",
                    IsAffixLocked = false
                });

                // Modul: the drop record (task 26). The chronicle's pieces top
                // out at tier 5, so counts only; one upsert for both rewards.
                var chronicleTally = new DropTally();
                chronicleTally.Count(DropSource.ChroniclePass, 0, qualityTier);

                // Modul: Comprehensive Game System Audit, Part 4.2/4.3.
                // Premium rewards now gate on a real purchased unlock
                // (pass.PremiumUnlocked, set by ExecutePassPurchaseAsync
                // after deducting the pass price) - previously merely
                // HOLDING 1+ diamonds unlocked this branch without ever
                // spending anything. Premium milestones additionally pay
                // out the ChroniclePassEconomy diamond schedule, whose
                // 50-tier sum strictly exceeds the pass price - the
                // self-sustaining loop where a fully active player's
                // season rewards cover the next season's purchase.
                int previousDiamondBalance = player.PremiumDiamonds;
                if (pass.PremiumUnlocked)
                {
                    context.EquipmentInstances.Add(new EquipmentInstance
                    {
                        PlayerId = playerId,
                        BaseItemId = $"chronicle_premium_{milestoneIndex + 1U}",
                        QualityTier = qualityTier,
                        AffixPayload = "{}",
                        IsAffixLocked = false
                    });
                    chronicleTally.Count(DropSource.ChroniclePass, 0, qualityTier);

                    int diamondReward = ChroniclePassEconomy.GetPremiumDiamondReward((int)milestoneIndex);
                    if (diamondReward > 0)
                    {
                        await Engine.DiamondLedger.LabelAsync(context, Engine.DiamondLedger.ChroniclePass);
                        player.PremiumDiamonds += diamondReward;
                    }
                }

                context.EventHorizonPremiumLedgers.Add(new EventHorizonPremiumLedger
                {
                    TransactionId = $"chronicle_{playerId}_{milestoneIndex + 1U}",
                    PlayerId = playerId,
                    PreviousBalance = previousDiamondBalance,
                    NewBalance = player.PremiumDiamonds,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });

                await context.SaveChangesAsync();
                await DropRecord.WriteAsync(context, playerId, chronicleTally, GameClock.UtcNow);
                await transaction.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                await transaction.SafeRollbackAsync();
                Console.WriteLine($"Battle pass claim failed for player {playerId}: {ex.Message}");
                return false;
            }
        }

        internal static void AddSeasonalXp(ref TickStatePayload payload, int xp)
        {
            if (xp <= 0)
            {
                return;
            }

            ulong nextXp = payload.AccumulatedSeasonalXp + (ulong)(uint)xp;
            if (nextXp > int.MaxValue)
            {
                nextXp = int.MaxValue;
            }

            payload.AccumulatedSeasonalXp = (uint)nextXp;
            uint level = payload.AccumulatedSeasonalXp / 1000U;
            if (level > 50U)
            {
                level = 50U;
            }

            if (payload.ActiveChroniclePassLevel < level)
            {
                payload.ActiveChroniclePassLevel = level;
            }

            payload.IsDirty = true;
        }
    }
}
