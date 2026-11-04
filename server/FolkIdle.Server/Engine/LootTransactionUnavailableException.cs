using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The loot worker could not get a database scope or transaction for a drop
    /// batch, so NOTHING of it was rolled. Distinct from a failure inside the
    /// transaction (whose rolls the PendingGrantOutbox keeps): this one means
    /// the batch is still owed whole and goes back for the next cycle
    /// (CombatLootEngine.DrainDropRequestsAsync).
    /// </summary>
    public sealed class LootTransactionUnavailableException : Exception
    {
        public LootTransactionUnavailableException(Exception inner)
            : base("No database transaction for a loot batch: " + inner.Message, inner)
        {
        }
    }
}
