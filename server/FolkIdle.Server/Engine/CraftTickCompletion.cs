namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// One finished craft, handed from the 10Hz tick to CraftingEngine.
    ///
    /// The tick owns the clock and CraftingEngine owns the transaction, so
    /// nothing on the hot path opens a DbContext - the same split
    /// CombatLootEngine's drop queue uses.
    /// </summary>
    public struct CraftTickCompletion
    {
        public long PlayerId;
        public int ResultItemId;
    }
}
