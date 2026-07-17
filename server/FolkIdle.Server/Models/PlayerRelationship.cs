using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    // Modul: Full-Stack Social Layer, Part 2. One directed edge per row -
    // "PlayerId feels RelationType about TargetPlayerId" - so a block is
    // never mutual by construction (Player A blocking Player B does not
    // block A from B's perspective unless B creates their own row). A
    // composite unique index on (PlayerId, TargetPlayerId) in
    // FolkIdleDbContext enforces at most one relationship row per directed
    // pair; changing Friend to Blocked (or back) updates RelationType on
    // the existing row rather than inserting a second one for the same
    // pair.
    public class PlayerRelationship
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        public long TargetPlayerId { get; set; }

        // 0 = Friend, 1 = Blocked.
        public int RelationType { get; set; }
    }

    public static class RelationType
    {
        public const int Friend = 0;
        public const int Blocked = 1;
    }
}
