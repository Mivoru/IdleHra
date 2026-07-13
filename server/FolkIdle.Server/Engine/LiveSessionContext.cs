using System.Threading;

namespace FolkIdle.Server.Engine
{
    public unsafe struct TelemetryRingBuffer
    {
        public fixed long TelemetryEvents[64];
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct BattlePassClaimRequest
    {
        public uint TargetMilestoneIndex;
        public uint AccumulatedSeasonalXp;
        public uint ActiveChroniclePassLevel;
        public uint Padding; // alignment
    }

    public unsafe struct BattlePassClaimRingBuffer
    {
        public fixed byte Requests[16 * 16]; // 16 items * 16 bytes = 256 bytes
    }

    public class LiveSessionContext
    {
        public long PlayerId { get; }
        public System.Guid AccountId { get; private set; }
        
        // Unmanaged memory cache for mastery vectors
        public uint ActiveMasteryBitmask { get; set; }

        public StatusEffectBuffer ActiveStatusEffects;
        public TelemetryRingBuffer TelemetryBuffer;
        public BattlePassClaimRingBuffer BattlePassClaimBuffer;
        public FolkIdle.Server.Network.TokenBucket ConnectionTokenBucket;
        public System.Collections.Concurrent.ConcurrentQueue<ConsumableApplicationSignal> ConsumableIngestionQueue { get; }

        // Multithreaded lock-free counters for hot-path progression
        private long _unmanagedMonsterKills;
        private long _telemetryWriteCursor;
        private int _telemetryOccupiedSlots;
        public uint DroppedEventsCount;
        
        private long _battlePassWriteCursor;
        private long _battlePassReadCursor;

        private int _isMigrating;

        public LiveSessionContext(long playerId, System.Guid accountId = default)
        {
            PlayerId = playerId;
            AccountId = accountId;
            _unmanagedMonsterKills = 0;
            _telemetryWriteCursor = 0;
            _telemetryOccupiedSlots = 0;
            DroppedEventsCount = 0;
            _battlePassWriteCursor = 0;
            _battlePassReadCursor = 0;
            ConnectionTokenBucket = FolkIdle.Server.Network.NetworkThrottlingEngine.CreateBucket();
            ActiveMasteryBitmask = 0;
            _isMigrating = 0;
            ConsumableIngestionQueue = new System.Collections.Concurrent.ConcurrentQueue<ConsumableApplicationSignal>();
        }

        public void UpdateAccountId(System.Guid accountId)
        {
            if (accountId != System.Guid.Empty)
            {
                AccountId = accountId;
            }
        }

        public bool TryStartMigration()
        {
            return Interlocked.CompareExchange(ref _isMigrating, 1, 0) == 0;
        }

        public bool IsMigrating => Volatile.Read(ref _isMigrating) == 1;

        public void ThreadSafeAddMonsterKill()
        {
            Interlocked.Increment(ref _unmanagedMonsterKills);
        }

        public long GetAndClearMonsterKills()
        {
            return Interlocked.Exchange(ref _unmanagedMonsterKills, 0);
        }

        public long GetCurrentMonsterKills()
        {
            return Interlocked.Read(ref _unmanagedMonsterKills);
        }

        public unsafe void WriteTelemetryEvent(long packedMetric)
        {
            if (packedMetric == 0L)
            {
                return;
            }

            long next = Interlocked.Increment(ref _telemetryWriteCursor) - 1L;
            int index = (int)(next & 63L);
            fixed (long* telemetryEvents = TelemetryBuffer.TelemetryEvents)
            {
                ref long target = ref System.Runtime.CompilerServices.Unsafe.AsRef<long>(telemetryEvents + index);
                long previous = Interlocked.CompareExchange(ref target, packedMetric, 0L);
                if (previous == 0L)
                {
                    Interlocked.Increment(ref _telemetryOccupiedSlots);
                }
                else
                {
                    Interlocked.Increment(ref DroppedEventsCount);
                    GlobalEngineState.AddTelemetryDroppedEvents(1L);
                }
            }
        }

        public unsafe bool TryDrainTelemetryEvent(int index, out long packedMetric)
        {
            packedMetric = 0L;
            if ((uint)index >= 64U)
            {
                return false;
            }

            fixed (long* telemetryEvents = TelemetryBuffer.TelemetryEvents)
            {
                ref long target = ref System.Runtime.CompilerServices.Unsafe.AsRef<long>(telemetryEvents + index);
                packedMetric = Interlocked.Exchange(ref target, 0L);
            }

            if (packedMetric != 0L)
            {
                Interlocked.Decrement(ref _telemetryOccupiedSlots);
            }

            return packedMetric != 0L;
        }

        public int GetTelemetryOccupiedSlots()
        {
            return Volatile.Read(ref _telemetryOccupiedSlots);
        }

        public bool ShouldPrioritizeTelemetryFlush()
        {
            return Volatile.Read(ref _telemetryOccupiedSlots) >= 52;
        }

        public long GetTelemetryDroppedEvents()
        {
            return Volatile.Read(ref DroppedEventsCount);
        }

        public unsafe bool TryEnqueueBattlePassClaim(in BattlePassClaimRequest req)
        {
            while (true)
            {
                long write = Interlocked.Read(ref _battlePassWriteCursor);
                long read = Interlocked.Read(ref _battlePassReadCursor);
                
                if (write - read >= 16)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _battlePassWriteCursor, write + 1, write) == write)
                {
                    int index = (int)(write & 15L);
                    fixed (byte* bufferPtr = BattlePassClaimBuffer.Requests)
                    {
                        BattlePassClaimRequest* target = (BattlePassClaimRequest*)bufferPtr + index;
                        *target = req;
                    }
                    return true;
                }
            }
        }

        public unsafe bool TryDequeueBattlePassClaim(out BattlePassClaimRequest req)
        {
            req = default;
            while (true)
            {
                long read = Interlocked.Read(ref _battlePassReadCursor);
                long write = Interlocked.Read(ref _battlePassWriteCursor);
                
                if (read >= write)
                {
                    return false;
                }
                
                if (Interlocked.CompareExchange(ref _battlePassReadCursor, read + 1, read) == read)
                {
                    int index = (int)(read & 15L);
                    fixed (byte* bufferPtr = BattlePassClaimBuffer.Requests)
                    {
                        BattlePassClaimRequest* source = (BattlePassClaimRequest*)bufferPtr + index;
                        req = *source;
                    }
                    return true;
                }
            }
        }
    }
}
