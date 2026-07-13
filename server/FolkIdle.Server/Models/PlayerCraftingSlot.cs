namespace FolkIdle.Server.Models
{
    public class PlayerCraftingSlot
    {
        public long PlayerId { get; set; }
        public byte SlotIndex { get; set; }
        public int ActiveRecipeId { get; set; }
        public long CompletionEpoch { get; set; }
        public bool IsReady { get; set; }
    }
}
