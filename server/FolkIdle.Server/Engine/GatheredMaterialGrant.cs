namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// One material a gathering tick rolled, on its way to being written.
    ///
    /// The tick owns the roll and CombatLootEngine owns the transaction and
    /// the loot-feed publish - the same split CraftTickCompletion uses, so
    /// nothing on the 10Hz path opens a DbContext.
    /// </summary>
    public struct GatheredMaterialGrant
    {
        public long PlayerId;
        public long ActivityId;
        public int ItemId;
        public int Quantity;
    }
}
