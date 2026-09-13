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
    /// eighty strings and a hash, in a second language, that must agree
    /// with FolkNameRegistry for ever or a character silently changes name.
    /// That is two copies of one truth, which is this codebase's dominant bug
    /// class, for a pass that runs once.
    ///
    /// Running it here instead means there is exactly one namer. It sits on the
    /// `--migrate` entrypoint, which is where the container runs schema changes
    /// on every deploy, so it lands at the same moment the column does.
    ///
    /// Modul: IT ALSO RENAMES THE CZECH NAMES, once. The registry's tables
    /// became Old Celtic on 2026-09-13, a day after the Czech ones shipped, and
    /// a name is STORED - so without this every existing character would keep
    /// a Czech name beside newborns with Celtic ones. Only a name the old
    /// tables could have produced is replaced (FolkNameRegistry.IsLegacyName),
    /// and the new name is never one of those, so the second run finds nothing.
    ///
    /// IDEMPOTENT, and that is what makes it safe to leave in place: it touches
    /// only empty names and legacy registry names, so a character renamed by a
    /// player - should that ever exist - is never overwritten.
    /// </summary>
    public static class CharacterNameBackfill
    {
        public static async Task<int> RunAsync(FolkIdleDbContext db)
        {
            string[] legacy = FolkNameRegistry.LegacyNames();

            var stale = await db.CharacterRecords
                .Where(c => c.Name == string.Empty || legacy.Contains(c.Name))
                .ToListAsync();

            if (stale.Count == 0) return 0;

            for (int i = 0; i < stale.Count; i++)
            {
                stale[i].Name = FolkNameRegistry.For(stale[i].Id, stale[i].IsFemale);
            }

            await db.SaveChangesAsync();
            System.Console.WriteLine($"Named {stale.Count} characters (unnamed, or carrying a retired Czech registry name).");
            return stale.Count;
        }
    }
}
