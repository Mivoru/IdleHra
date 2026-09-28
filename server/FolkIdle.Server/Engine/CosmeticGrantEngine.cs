using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Task 54: pays the level chest - one cosmetic chest every fifth level -
    /// off the tick.
    ///
    /// Modul: ONE HOOK FOR THREE LEVEL PATHS. CLAUDE.md: "three paths grow a
    /// level, and each one has to be told separately". Each of them already
    /// calls RaceAttributeGrowth.ApplyLevelUpGrowth, and each now calls
    /// NoteLevel beside it; CosmeticLevelChestTests fails if a file calls one
    /// without the other. Login calls it too, after the offline catch-up,
    /// which is what pays the BACKFILL: an existing account's
    /// LevelChestsGranted starts at 0, so its first login pays every chest its
    /// level already owed.
    ///
    /// The grant itself (CosmeticEngine.GrantLevelChestsAsync) is idempotent
    /// under a row lock, so a request enqueued twice, or a relogin that
    /// re-enqueues, can never pay twice. The queue only has to be cheap.
    /// </summary>
    public sealed class CosmeticGrantEngine
    {
        public readonly struct LevelNote
        {
            public LevelNote(long playerId, int level) { PlayerId = playerId; Level = level; }
            public long PlayerId { get; }
            public int Level { get; }
        }

        // Static like the other worker queues: the tick and the offline path
        // are static code with no instance to hand. CLAUDE.md: a test that
        // starts this worker must stop it in a finally.
        public static readonly ConcurrentQueue<LevelNote> Queue = new();

        public const int MaxNotesPerCycle = 500;

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;
        private CancellationTokenSource _cts = new();
        private long _granted;
        private long _failed;

        public CosmeticGrantEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        /// <summary>
        /// Called wherever a level can grow, and at login. Below the first
        /// milestone there is nothing to pay, so nothing is queued.
        /// </summary>
        public static void NoteLevel(long playerId, int level)
        {
            if (playerId <= 0 || level < CosmeticRegistry.LevelsPerChest) return;
            Queue.Enqueue(new LevelNote(playerId, level));
        }

        public void StartCron()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ExecuteAsync(_cts.Token));
            Console.WriteLine("Cosmetic level-chest worker started.");
        }

        public void StopCron()
        {
            _cts.Cancel();
        }

        private async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Modul: the try opens BEFORE the delay and before the drain's
                // CreateScope (CronWorkerGuardTests), and each player is
                // isolated in its own try inside the drain.
                try
                {
                    await Task.Delay(1000, stoppingToken);
                    await DrainOneCycleAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    Console.WriteLine($"Cosmetic level-chest cycle failed: {ex.Message}");
                }
            }

            Console.WriteLine("Cosmetic level-chest worker STOPPED.");
        }

        /// <summary>
        /// One budgeted drain (the depth read once - never `while (TryDequeue)`),
        /// coalesced to the highest level per player. Returns the chests granted.
        /// </summary>
        internal async Task<int> DrainOneCycleAsync()
        {
            int budget = Math.Min(Queue.Count, MaxNotesPerCycle);
            if (budget == 0) return 0;

            var highest = new Dictionary<long, int>();
            for (int i = 0; i < budget && Queue.TryDequeue(out var note); i++)
            {
                if (!highest.TryGetValue(note.PlayerId, out int seen) || note.Level > seen)
                {
                    highest[note.PlayerId] = note.Level;
                }
            }

            int grantedThisCycle = 0;
            foreach (var (playerId, level) in highest)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
                    var granted = await CosmeticEngine.GrantLevelChestsAsync(db, playerId, level, Random.Shared, DateTime.UtcNow);
                    grantedThisCycle += granted.Count;

                    // The same feed a monster's chest arrives on, so the loot
                    // list and the reveal show a level chest too. MonsterId 0
                    // is how the client tells "a level reward" from a kill.
                    foreach (var (id, rarity) in granted)
                    {
                        _playerRegistry?.OutboundLootDropQueue.Enqueue(new Network.ResponseLootDropPacket
                        {
                            PlayerId = playerId,
                            ItemId = 0,
                            Quantity = 1,
                            MonsterId = 0,
                            QualityTier = (byte)rarity,
                            DropKind = Network.ResponseLootDropPacket.DropKindCosmeticChest,
                            InstanceId = id,
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Not re-queued: the grant is idempotent against the row,
                    // so the next level-up or login pays whatever this missed.
                    Interlocked.Increment(ref _failed);
                    Console.WriteLine($"Level chest grant failed for {playerId}: {ex.Message}");
                }
            }

            if (grantedThisCycle > 0)
            {
                Interlocked.Add(ref _granted, grantedThisCycle);
                Console.WriteLine($"Cosmetics: {grantedThisCycle} level chest(s) granted this cycle, {_granted} total, {_failed} failed, {Queue.Count} still queued");
            }
            return grantedThisCycle;
        }
    }
}
