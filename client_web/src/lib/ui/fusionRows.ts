// Modul: THE FORGE AS ONE ROW PER ITEM (task 100), and the ledger that keeps a
// quick second Fuse from naming a piece the first one already ate.
//
// The Fusion panel used to open with a chip per (item, rarity) pair - about 75
// of them, 1,750 px at 390 px - and a chip only filled three native selects
// further down. A row per BASE item says the one thing a player is deciding
// ("3 Mythic -> 1 Relic, up to 12k gold") and carries its own Fuse. Pure, so
// the grouping, the choice of target and the ledger are tested without a DOM.

/** ForgeSplicingEngine.FusionFee: BaseGoldCost * 1.35^currentTier, rounded up. */
export const FORGE_BASE_FEE = 200;
export const FORGE_FEE_GROWTH = 1.35;

/**
 * The fee before discounts. Luck and the Diamond Star event take up to 25 %
 * off server-side, so this is a ceiling and the UI says "up to".
 */
export function fusionFeeCeiling(currentTier: number): number {
  return Math.ceil(FORGE_BASE_FEE * Math.pow(FORGE_FEE_GROWTH, currentTier));
}

export interface FusionPiece {
  Id: number;
  BaseItemId: string;
  QualityTier: number;
  IsAffixLocked: boolean;
  Affixes?: Record<string, number> | null;
}

export interface FusionTierCount {
  tier: number;
  count: number;
}

export interface FusionRow {
  baseItemId: string;
  /** A piece of this item is worn by the active character. */
  worn: boolean;
  /** The worn piece's rarity, 0 when nothing of this item is worn. */
  wornTier: number;
  /** Fusable pieces (loose, unlocked) per rarity, highest rarity first. */
  counts: FusionTierCount[];
  /**
   * The single fusion the row offers: the HIGHEST rarity with three fusable
   * pieces, below the top of the ladder. Null when no rarity has three.
   */
  next: { tier: number; target: number; sacrifices: [number, number] } | null;
  /**
   * The lowest rarity with three fusable pieces - where "the whole stack"
   * starts - and one piece of it, which is all the stack command needs.
   */
  stack: { tier: number; sampleId: number } | null;
  /** Every fusable piece of this item. */
  total: number;
}

function affixCount(piece: FusionPiece): number {
  return piece.Affixes ? Object.keys(piece.Affixes).length : 0;
}

/**
 * Group what the player owns into one row per base item.
 *
 * `worn` is the active character's equipped instance ids: those pieces are
 * never offered as a target or a sacrifice (ForgeSplicingEngine refuses an
 * equipped piece), and they put their item's line FIRST, because the fusion a
 * player wants most is the one for the gear they are fighting in. `hidden` is
 * the ledger's - pieces a fusion still in flight has already claimed.
 *
 * Rows with no fusion are dropped, except a worn line, which stays so the
 * player can see how far it is from the next step.
 */
export function buildFusionRows(
  owned: readonly FusionPiece[],
  worn: ReadonlySet<number>,
  hidden: ReadonlySet<number>,
  maxTier: number,
): FusionRow[] {
  const byBase = new Map<string, { pieces: FusionPiece[]; wornTier: number }>();
  for (const piece of owned) {
    if (hidden.has(piece.Id)) continue;
    let group = byBase.get(piece.BaseItemId);
    if (!group) {
      group = { pieces: [], wornTier: 0 };
      byBase.set(piece.BaseItemId, group);
    }
    if (worn.has(piece.Id)) {
      group.wornTier = Math.max(group.wornTier, piece.QualityTier);
      continue;
    }
    if (piece.IsAffixLocked) continue;
    group.pieces.push(piece);
  }

  const rows: FusionRow[] = [];
  for (const [baseItemId, group] of byBase) {
    const byTier = new Map<number, FusionPiece[]>();
    for (const piece of group.pieces) {
      const list = byTier.get(piece.QualityTier) ?? [];
      list.push(piece);
      byTier.set(piece.QualityTier, list);
    }

    const counts = [...byTier.entries()]
      .map(([tier, list]) => ({ tier, count: list.length }))
      .sort((a, b) => b.tier - a.tier);

    const fusable = counts.filter((c) => c.count >= 3 && c.tier < maxTier);
    let next: FusionRow['next'] = null;
    if (fusable.length > 0) {
      const tier = fusable[0].tier;
      // Modul: THE TARGET IS THE PIECE WITH THE MOST AFFIXES. The target
      // survives the fusion with its roll intact plus one new affix; the two
      // sacrifices are destroyed. Keeping the best-rolled piece is the choice
      // a player would make by hand in the selects, so the one-tap path makes
      // it too. Ties go to the lowest id, so the pick is stable between
      // renders.
      const pieces = [...(byTier.get(tier) ?? [])].sort((a, b) => affixCount(b) - affixCount(a) || a.Id - b.Id);
      next = { tier, target: pieces[0].Id, sacrifices: [pieces[1].Id, pieces[2].Id] };
    }

    const lowest = fusable.length > 0 ? fusable[fusable.length - 1] : null;
    const stack = lowest ? { tier: lowest.tier, sampleId: Math.min(...(byTier.get(lowest.tier) ?? []).map((p) => p.Id)) } : null;

    if (!next && group.wornTier === 0) continue;
    rows.push({
      baseItemId,
      worn: group.wornTier > 0,
      wornTier: group.wornTier,
      counts,
      next,
      stack,
      total: group.pieces.length,
    });
  }

  return rows.sort(
    (a, b) =>
      Number(b.worn) - Number(a.worn) ||
      (b.next?.tier ?? 0) - (a.next?.tier ?? 0) ||
      a.baseItemId.localeCompare(b.baseItemId),
  );
}

// ---------------------------------------------------------------------------
// The in-flight ledger
// ---------------------------------------------------------------------------

/**
 * One fusion the server has been sent and whose result the Forge's list does
 * not show yet.
 */
export interface PendingFusion {
  target: number;
  sacrifices: readonly number[];
  /** Date.now() past which the entry is dropped whatever the list says. */
  expiresAt: number;
}

/**
 * How long a sent fusion hides its pieces if the list never confirms it. A
 * refused fusion leaves its sacrifices in place, so they come back after this;
 * a successful one is settled much sooner, by the refetch.
 */
export const PENDING_FUSION_TTL_MS = 8000;

/**
 * Modul: WHY THIS EXISTS - "when I fuse quickly the forge breaks for a second".
 *
 * A fusion is a WebSocket command; the list the next one is picked from is a
 * REST fetch that only refreshes after the server answers and commits. So a
 * quick second Fuse picked from the OLD list - which still held the two pieces
 * the first fusion destroyed - and sent their ids again. The server answered
 * TargetNotFound and then tore the session down (fixed server-side too:
 * ForgeTickCoordinator). Hiding the claimed pieces until the list confirms
 * they are gone means the second tap picks three pieces that still exist.
 */
export function hiddenByPending(pending: readonly PendingFusion[], now: number): Set<number> {
  const hidden = new Set<number>();
  for (const entry of pending) {
    if (entry.expiresAt <= now) continue;
    hidden.add(entry.target);
    for (const id of entry.sacrifices) hidden.add(id);
  }
  return hidden;
}

/**
 * Drop entries the list has caught up with - none of their sacrifices is in
 * it any more, so the fusion committed and the target's new rarity is what the
 * list shows - and entries past their expiry.
 */
export function settlePending(
  pending: readonly PendingFusion[],
  ownedIds: ReadonlySet<number>,
  now: number,
): PendingFusion[] {
  return pending.filter((entry) => entry.expiresAt > now && entry.sacrifices.some((id) => ownedIds.has(id)));
}
