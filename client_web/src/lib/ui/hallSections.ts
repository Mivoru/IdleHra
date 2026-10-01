// Modul: THE HALL OF ANCESTORS, SPLIT THE WAY THE CULL SPLITS IT (task 104).
//
// WHO carries is never decided here. `WouldCarry` is the server's answer
// (HallOfAncestorsRules.ChooseSurvivors, run by the hall endpoint against the
// live cap), and the two sections are cut on it alone - a client that re-ran
// the ranking to decide membership would be the second source of truth this
// codebase keeps getting bitten by.
//
// What IS mirrored is the ORDER inside the carried section, because "cull-rank
// order" is what tells a player who is next to fall off the end. It follows
// HallOfAncestorsRules.Compare: main character, then marked, then aptitude
// total, then epic, then later generation. The server's last tie-break is
// Guid.CompareTo, which does not order the same way as a string compare of
// the Guid's text; the string compare here only makes the DISPLAY stable, and
// cannot move anybody between sections.

export interface HallRankable {
  CharacterId: string;
  IsMainCharacter: boolean;
  IsKept: boolean;
  IsEpicMutation: boolean;
  GenerationIndex: number;
  WouldCarry: boolean;
  AptitudeStrength: number;
  AptitudeSkill: number;
  AptitudeEndurance: number;
  AptitudeFortune: number;
}

export function aptitudeTotal(m: HallRankable): number {
  return m.AptitudeStrength + m.AptitudeSkill + m.AptitudeEndurance + m.AptitudeFortune;
}

export function compareCullRank(a: HallRankable, b: HallRankable): number {
  if (a.IsMainCharacter !== b.IsMainCharacter) return a.IsMainCharacter ? -1 : 1;
  if (a.IsKept !== b.IsKept) return a.IsKept ? -1 : 1;
  const totals = aptitudeTotal(b) - aptitudeTotal(a);
  if (totals !== 0) return totals;
  if (a.IsEpicMutation !== b.IsEpicMutation) return a.IsEpicMutation ? -1 : 1;
  if (a.GenerationIndex !== b.GenerationIndex) return b.GenerationIndex - a.GenerationIndex;
  return a.CharacterId < b.CharacterId ? -1 : a.CharacterId > b.CharacterId ? 1 : 0;
}

/** The lost list sorts by blood alone - a mark is moot on a row that is lost anyway. */
export function compareByTotal(a: HallRankable, b: HallRankable): number {
  const totals = aptitudeTotal(b) - aptitudeTotal(a);
  if (totals !== 0) return totals;
  if (a.GenerationIndex !== b.GenerationIndex) return b.GenerationIndex - a.GenerationIndex;
  return a.CharacterId < b.CharacterId ? -1 : a.CharacterId > b.CharacterId ? 1 : 0;
}

export function splitHall<T extends HallRankable>(members: readonly T[]): { carried: T[]; lost: T[] } {
  return {
    carried: members.filter((m) => m.WouldCarry).sort(compareCullRank),
    lost: members.filter((m) => !m.WouldCarry).sort(compareByTotal),
  };
}

/**
 * Who claims a slot ahead of the ranking: the main character plus every mark.
 * "Kept 14/10" is this over the cap - more claims than slots is legal, and the
 * same ranking settles it (HallOfAncestorsRules, "MORE MARKED THAN SLOTS").
 */
export function claimedCount(members: readonly HallRankable[]): number {
  return members.filter((m) => m.IsKept || m.IsMainCharacter).length;
}

export type HallBadge =
  | { kind: 'over-cap'; text: string }
  | { kind: 'slot'; text: string }
  | { kind: 'first'; text: string }
  | { kind: 'bench'; text: string }
  | { kind: 'lost'; text: string };

/**
 * The ONE state a row shows. A mark that will not carry is the one that must
 * never read as safe - "Kept" on a row the cull deletes was the defect - so it
 * outranks everything else.
 */
export function hallBadge(m: HallRankable & { PlayableSlot: number }): HallBadge {
  if (m.IsKept && !m.WouldCarry) return { kind: 'over-cap', text: 'Kept - over the cap' };
  if (m.PlayableSlot >= 0) return { kind: 'slot', text: `In slot ${m.PlayableSlot + 1}` };
  if (m.IsMainCharacter) return { kind: 'first', text: 'First character' };
  return m.WouldCarry ? { kind: 'bench', text: 'Bench' } : { kind: 'lost', text: 'Lost at rebirth' };
}
