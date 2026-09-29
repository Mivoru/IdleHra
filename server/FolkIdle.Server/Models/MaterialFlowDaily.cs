using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: MATERIAL FLOW PER DAY (task 79, phase 2). One row per player, UTC
    // day, material id and direction (Engine.MaterialFlowDirection): gathered,
    // spent, lost to the Warehouse cap, sold or binned from the chest. ItemId
    // is whatever string the CommodityRecords row uses - gathering slugs and
    // catalogued ids share that space (CLAUDE.md, "check which material
    // namespace"), and this table does not care which.
    //
    // Written ONLY through Engine.MaterialLedger, inside the transaction that
    // moves the stack, so a rollback takes the row with it.
    [Table("material_flow_daily")]
    public class MaterialFlowDaily
    {
        public long PlayerId { get; set; }

        public DateOnly Day { get; set; }

        public string ItemId { get; set; } = string.Empty;

        /// <summary>Engine.MaterialFlowDirection.</summary>
        public short Direction { get; set; }

        public long Amount { get; set; }
    }
}
