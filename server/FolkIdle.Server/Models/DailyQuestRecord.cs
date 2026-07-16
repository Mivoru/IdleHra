namespace FolkIdle.Server.Models
{
    // Modul: authoritative daily-quest ledger. Composite key
    // (PlayerId, QuestSlot) - exactly 3 rows per player at any time
    // (QuestSlot 0-2), overwritten in place by QuestEngine.GenerateDailyQuestsAsync
    // on the first login/report after a UTC-midnight rollover rather than
    // accumulating one row per day, since only the current day's quests are
    // ever queried. DateKeyUtc (the epoch day number the row belongs to) is
    // what QuestEngine compares against "today" to detect a stale row that
    // needs regenerating.
    public class DailyQuestRecord
    {
        public long PlayerId { get; set; }
        public int QuestSlot { get; set; }
        public int QuestType { get; set; }
        public int TargetAmount { get; set; }
        public int CurrentProgress { get; set; }
        public bool RewardClaimed { get; set; }
        public long DateKeyUtc { get; set; }
    }
}
