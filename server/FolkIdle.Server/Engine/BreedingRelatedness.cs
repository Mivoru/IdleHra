using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// WHETHER A PAIR IS RELATED, answered from the database. One copy, called
    /// by the breeding engine and by the roster preview.
    ///
    /// Modul: there used to be two inline copies of this - a nine-clause boolean
    /// in BreedingEngine and the same nine clauses in the preview endpoint -
    /// and both stopped at "shares a parent", while BreedingAptitudes.AreRelated
    /// (which also checks grandparents) was called by nothing but its tests. So
    /// cousins bred at full mutation odds and a 5% epic chance, against a
    /// preview that said "not related". The pure rule stays in
    /// BreedingAptitudes; this is only the load that feeds it.
    ///
    /// A GRANDPARENT CULLED AT A ROLLOVER IS UNKNOWN, not a stranger. The cull
    /// deletes the lineage row, so the parents' parents can no longer be read
    /// and the check sees less of the pedigree. That errs toward "not related",
    /// which only ever makes a pairing better than it should be - never refuses
    /// one or charges for one.
    /// </summary>
    public static class BreedingRelatedness
    {
        public static async Task<bool> AreRelatedAsync(
            FolkIdleDbContext db,
            CharacterLineageRegistry a,
            CharacterLineageRegistry b)
        {
            var parentIds = new List<Guid>(4);
            AddKnown(parentIds, a.ParentPaternalId);
            AddKnown(parentIds, a.ParentMaternalId);
            AddKnown(parentIds, b.ParentPaternalId);
            AddKnown(parentIds, b.ParentMaternalId);

            var parentsOfParents = new Dictionary<Guid, (Guid? Father, Guid? Mother)>();
            if (parentIds.Count > 0)
            {
                var rows = await db.CharacterLineages
                    .AsNoTracking()
                    .Where(l => parentIds.Contains(l.CharacterId))
                    .Select(l => new { l.CharacterId, l.ParentPaternalId, l.ParentMaternalId })
                    .ToListAsync();

                foreach (var row in rows)
                {
                    parentsOfParents[row.CharacterId] = (row.ParentPaternalId, row.ParentMaternalId);
                }
            }

            Guid[] Grandparents(CharacterLineageRegistry person)
            {
                var found = new List<Guid>(4);
                foreach (Guid? parent in new[] { person.ParentPaternalId, person.ParentMaternalId })
                {
                    if (parent is { } id && parentsOfParents.TryGetValue(id, out var theirs))
                    {
                        AddKnown(found, theirs.Father);
                        AddKnown(found, theirs.Mother);
                    }
                }
                return found.ToArray();
            }

            return BreedingAptitudes.AreRelated(
                a.CharacterId, a.ParentPaternalId, a.ParentMaternalId,
                b.CharacterId, b.ParentPaternalId, b.ParentMaternalId,
                Grandparents(a),
                Grandparents(b));
        }

        private static void AddKnown(List<Guid> into, Guid? id)
        {
            if (id is { } value && value != Guid.Empty) into.Add(value);
        }
    }
}
