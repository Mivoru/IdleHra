using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    // Modul: the ProcessedTransactions ledger already rejects a replayed
    // TransactionId at the constraint level - TransactionId is [Key], so a
    // second INSERT of the same value fails outright regardless of what any
    // caller believes about the request. This class adds a proactive check
    // in front of that constraint: BillingVerificationEngine calls
    // TryMarkProcessedAsync BEFORE crediting PremiumDiamonds, so an obvious
    // duplicate (a store API returning 200 OK twice for one purchase, a
    // retried client request) is rejected without ever mutating the
    // player's balance or writing the other ledger rows, rather than
    // discovering the duplicate only after doing that work and having to
    // unwind it via a caught DbUpdateException. The constraint itself
    // remains the actual source of truth under concurrent requests for the
    // same TransactionId - a Serializable-isolation race that slips past
    // this check still fails at INSERT time, which the caller must still
    // handle (see BillingVerificationEngine.VerifyReceiptAsync's
    // UniqueViolation catch).
    //
    // Must be called on an already-open FolkIdleDbContext/transaction
    // supplied by the caller, never one this class opens itself - the
    // dedup check and the currency grant it gates have to commit or roll
    // back together as a single unit. A separately-transacted dedup check
    // would reopen the exact check-then-act race the [Key] constraint
    // exists to close.
    public static class TransactionDedupEngine
    {
        public static async Task<bool> TryMarkProcessedAsync(FolkIdleDbContext context, ProcessedTransaction transaction)
        {
            bool alreadyProcessed = await context.ProcessedTransactions
                .AnyAsync(t => t.TransactionId == transaction.TransactionId);

            if (alreadyProcessed)
            {
                return false;
            }

            context.ProcessedTransactions.Add(transaction);
            return true;
        }
    }
}
