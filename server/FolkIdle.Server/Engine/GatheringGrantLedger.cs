using System.Threading;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Process-lifetime totals of gathered material on its way from the tick to
    /// the chest, so a run can prove that every unit was credited once.
    /// </summary>
    /// <remarks>
    /// Modul: ACCOUNTING, NOT A CHECK THAT ANYTHING READS (2026-11-06, owner's
    /// condition for merging the outbox reservation). The quantities balance
    /// once the queue and the outbox are empty:
    ///   enqueued + requeued = taken + still queued
    ///   taken = credited inline + reserved for the drain + requeued + unnamed + clipped
    ///   credited by the drain = reserved for the drain - still in the outbox
    /// so enqueued - (inline + drain + unnamed + clipped) is what is still in
    /// flight, and must be 0 at rest. Cumulative and never reset, so the last
    /// line of a run is its total. Every counter is an interlocked add; the
    /// tick's two are one each per grant, like CombatLootEngine.NoteKillEnqueued.
    /// </remarks>
    public static class GatheringGrantLedger
    {
        private static long _grantsEnqueued;
        private static long _quantityEnqueued;
        private static long _quantityTaken;
        private static long _quantityClipped;
        private static long _quantityUnnamed;
        private static long _batchesReserved;
        private static long _batchesCreditedInline;
        private static long _quantityCreditedInline;
        private static long _batchesToDrain;
        private static long _quantityToDrain;
        private static long _batchesCreditedByDrain;
        private static long _quantityCreditedByDrain;
        private static long _batchesRequeued;
        private static long _quantityRequeued;

        /// <summary>Tick thread: one grant joined the queue.</summary>
        public static void NoteEnqueued(int quantity)
        {
            Interlocked.Increment(ref _grantsEnqueued);
            if (quantity > 0) Interlocked.Add(ref _quantityEnqueued, quantity);
        }

        internal static void NoteTaken(int quantity) => Interlocked.Add(ref _quantityTaken, quantity);
        internal static void NoteClipped(long quantity) => Interlocked.Add(ref _quantityClipped, quantity);
        internal static void NoteUnnamed(int quantity) => Interlocked.Add(ref _quantityUnnamed, quantity);
        internal static void NoteReserved() => Interlocked.Increment(ref _batchesReserved);

        internal static void NoteCreditedInline(long quantity)
        {
            Interlocked.Increment(ref _batchesCreditedInline);
            Interlocked.Add(ref _quantityCreditedInline, quantity);
        }

        /// <summary>A reserved batch the inline grant did not credit: it is the drain's now.</summary>
        internal static void NoteLeftToDrain(long quantity)
        {
            Interlocked.Increment(ref _batchesToDrain);
            Interlocked.Add(ref _quantityToDrain, quantity);
        }

        internal static void NoteCreditedByDrain(long quantity)
        {
            Interlocked.Increment(ref _batchesCreditedByDrain);
            Interlocked.Add(ref _quantityCreditedByDrain, quantity);
        }

        internal static void NoteRequeued(long quantity)
        {
            Interlocked.Increment(ref _batchesRequeued);
            Interlocked.Add(ref _quantityRequeued, quantity);
        }

        /// <summary>What is neither credited nor accounted as dropped: the queue, open reservations, and anything lost.</summary>
        public static long InFlight =>
            Interlocked.Read(ref _quantityEnqueued)
            - Interlocked.Read(ref _quantityCreditedInline)
            - Interlocked.Read(ref _quantityCreditedByDrain)
            - Interlocked.Read(ref _quantityUnnamed)
            - Interlocked.Read(ref _quantityClipped);

        public static string Report(int stillQueued) =>
            $"Gathering ledger: enqueued {Interlocked.Read(ref _grantsEnqueued)} grants / {Interlocked.Read(ref _quantityEnqueued)} units; "
            + $"taken {Interlocked.Read(ref _quantityTaken)}; "
            + $"reserved {Interlocked.Read(ref _batchesReserved)} batches; "
            + $"credited inline {Interlocked.Read(ref _batchesCreditedInline)} batches / {Interlocked.Read(ref _quantityCreditedInline)} units; "
            + $"left to the drain {Interlocked.Read(ref _batchesToDrain)} / {Interlocked.Read(ref _quantityToDrain)}; "
            + $"credited by the drain {Interlocked.Read(ref _batchesCreditedByDrain)} / {Interlocked.Read(ref _quantityCreditedByDrain)}; "
            + $"requeued {Interlocked.Read(ref _batchesRequeued)} / {Interlocked.Read(ref _quantityRequeued)}; "
            + $"unnamed {Interlocked.Read(ref _quantityUnnamed)}; clipped {Interlocked.Read(ref _quantityClipped)}; "
            + $"in flight {InFlight} units ({stillQueued} grants queued)";
    }
}
