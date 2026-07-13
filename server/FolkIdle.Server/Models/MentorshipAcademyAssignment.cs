using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    [Table("MentorshipAcademyAssignments")]
    public class MentorshipAcademyAssignment
    {
        public long PlayerId { get; set; }
        public Guid CharacterId { get; set; }
        public int SlotIndex { get; set; }
    }
}
