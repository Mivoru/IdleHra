namespace FolkIdle.Client.UI
{
    // Lightweight view model for a single Codex list row. Populated by whatever
    // eventually streams the player's unlocked Codex entries down from the
    // server (no such channel exists yet - see UiCodexListBinder remarks).
    public readonly struct MonsterCodexEntryView
    {
        public readonly int MonsterId;
        public readonly string AssetKey;
        public readonly int Level;

        public MonsterCodexEntryView(int monsterId, string assetKey, int level)
        {
            MonsterId = monsterId;
            AssetKey = assetKey;
            Level = level;
        }
    }
}
