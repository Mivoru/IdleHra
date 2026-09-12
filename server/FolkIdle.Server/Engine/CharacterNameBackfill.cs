using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Names the characters that existed before characters had names.
    ///
    /// WHY THIS IS C# AND NOT SQL IN THE MIGRATION. The name is drawn by
    /// hashing the character's id, and doing that in SQL would mean writing
    /// FNV-1a in plpgsql and repeating both name tables as SQL array literals -
    /// sixty-four strings and a hash, in a second language, that must agree
    /// with FolkNameRegistry for ever or a character silently changes name.
    /// That is two copies of one truth, which is this codebase's dominant bug
    /// class, for a pass that runs once.
    ///
    /// Running it here instead means there is exactly one namer. It sits on the
    /// `--migrate` entrypoint, which is where the container runs schema changes
    /// on every deploy, so it lands at the same moment the column does.
    ///
    /// IDEMPOTENT, and that is what makes it safe to leave in place: it only
    /// touches rows whose name is empty, so the second run does nothing and a
    /// character renamed by a player is never overwritten.
    /// </summary>
    public static class CharacterNameBackfill
    {
        public static async Task<int> RunAsync(FolkIdleDbContext db)
        {
            var unnamed = await db.CharacterRecords
                .Where(c => c.Name == string.Empty)
                .ToListAsync();

            if (unnamed.Count == 0) return 0;

            for (int i = 0; i < unnamed.Count; i++)
            {
                unnamed[i].Name = FolkNameRegistry.For(unnamed[i].Id, unnamed[i].IsFemale);
            }

            await db.SaveChangesAsync();
            System.Console.WriteLine($"Named {unnamed.Count} characters that predate the Name column.");
            return unnamed.Count;
        }
    }
}
