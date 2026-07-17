using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    // Modul: single authoritative anti-replay ledger for IAP receipts -
    // TransactionId as the primary key means a duplicate submission fails
    // atomically on INSERT (a genuine database constraint, not a
    // check-then-insert race decided only by application logic) rather than
    // relying on a select-then-insert window. See
    // BillingVerificationEngine.VerifyReceiptAsync, which inserts here
    // before crediting any currency - if the insert fails, no diamonds are
    // ever granted for that transaction a second time.
    public class ProcessedTransaction
    {
        [Key]
        [StringLength(256)]
        public string TransactionId { get; set; } = string.Empty;
        public long PlayerId { get; set; }
        [StringLength(64)]
        public string ProductId { get; set; } = string.Empty;
        public int PremiumDiamondsGranted { get; set; }
        public long ProcessedAtEpoch { get; set; }
    }
}
