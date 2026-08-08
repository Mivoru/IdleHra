// Deciding WHEN an achievement toast should fire, and for WHICH deed.
//
// Pure and separate from the store that wires it up, because the interesting
// part is entirely edge-detection and edge-detection is where this kind of
// feature goes wrong: fire on the wrong edge and every player gets a wall of
// cards for things they earned months ago, the first time they log in.
//
// THE SIGNAL is StateUpdatePacket.AchievementTierTotal - the sum of the three
// tiered achievements' current tiers, 0-12, computed live on the server from
// counters the tick payload already carries. It says THAT a tier moved, never
// which one; naming the deed needs the REST snapshot, which the card needs
// anyway to draw a title and a reward.
//
// TWO RULES, and both exist because of a real way this breaks:
//
//   1. THE FIRST VALUE OF A SESSION IS A BASELINE, never a trigger. A player
//      with eight tiers already earned would otherwise be greeted by eight
//      cards at every login. Same contract OfflineSummaryTick established.
//
//   2. ONLY A RISE ABOVE THE HIGH-WATER MARK COUNTS. The server computes the
//      total live, while the database's CompletedTier is a high-water mark -
//      so spending below a gold threshold drops the byte, and earning it back
//      raises it again. Without a high-water mark on this side, that second
//      crossing would toast a deed the player already owns.

/** One card's worth of content, resolved against the REST snapshot. */
export interface AchievementToast {
  id: number;
  achievementId: number;
  title: string;
  tier: number;
  /** Roman numeral for the tier, or '' for the untiered kill achievement. */
  tierLabel: string;
  reward: string;
}

/** The tracker's memory between packets. Serialisable on purpose. */
export interface ToastWatermark {
  /** Highest total ever seen this session; -1 means nothing seen yet. */
  highWater: number;
}

export function createWatermark(): ToastWatermark {
  return { highWater: -1 };
}

/**
 * Feeds the tracker the latest packet value.
 *
 * Returns true when this is a genuine new crossing worth toasting - which is
 * also the caller's cue to refetch the snapshot, since the packet cannot say
 * which deed moved.
 */
export function observeTierTotal(mark: ToastWatermark, total: number): boolean {
  if (!Number.isFinite(total) || total < 0) return false;

  // Rule 1: the first value seen is the baseline.
  if (mark.highWater < 0) {
    mark.highWater = total;
    return false;
  }

  // Rule 2: only a rise above everything seen so far.
  if (total > mark.highWater) {
    mark.highWater = total;
    return true;
  }

  return false;
}

const ROMAN = ['', 'I', 'II', 'III', 'IV'];

export function tierLabel(tier: number): string {
  return ROMAN[tier] ?? String(tier);
}

/** Mirrors AchievementMilestones' ids. */
export const ACHIEVEMENT_NAMES: Record<number, string> = {
  1: 'Monster Hunter',
  2: 'Treasury',
  3: 'Forging',
  4: 'Logistics',
};

export function achievementName(id: number): string {
  return ACHIEVEMENT_NAMES[id] ?? `Deed #${id}`;
}

/** A snapshot row, narrowed to what naming a card needs. */
export interface TierRow {
  AchievementId: number;
  CompletedTier: number;
}

/**
 * Which deeds advanced between two snapshots.
 *
 * Diffing snapshots rather than trusting the packet's total is what lets a
 * single packet tick produce two cards - crossing Treasury III and Logistics II
 * in the same checkpoint moves the total by two and must show both.
 */
export function diffTiers(before: TierRow[], after: TierRow[]): { achievementId: number; tier: number }[] {
  const previous = new Map(before.map((row) => [row.AchievementId, row.CompletedTier]));
  const crossed: { achievementId: number; tier: number }[] = [];

  for (const row of after) {
    const was = previous.get(row.AchievementId) ?? 0;
    // One card per deed, showing the tier LANDED ON. Two tiers crossed at once
    // is one card reading "III", not cards for II and III - the player did not
    // experience II, they flew past it.
    if (row.CompletedTier > was) {
      crossed.push({ achievementId: row.AchievementId, tier: row.CompletedTier });
    }
  }

  return crossed;
}
