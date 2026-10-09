import type { DeedChapterEntry, DeedEntry } from '../net/rest';

/**
 * The unfinished deed closest to done, from the chapters that are open.
 *
 * Ties go to the earlier chapter and the earlier deed, which is the order the
 * Book reads in. A deed with no target cannot be measured, so it is skipped
 * rather than read as 0% or as done.
 *
 * A SEALED chapter is finished for good, so it is skipped too. Several deeds
 * are state ("wear a weapon") and can read undone again after the Seal; the
 * Book folds such a chapter away, and the Home card must not dig it back up
 * as the next thing to do.
 */
export function closestDeed(chapters: readonly DeedChapterEntry[]): DeedEntry | null {
  let best: DeedEntry | null = null;
  let bestShare = -1;
  for (const chapter of chapters) {
    if (!chapter.IsOpen || chapter.IsComplete || chapter.HasSeal) continue;
    for (const deed of chapter.Deeds) {
      if (deed.Done || deed.Target <= 0) continue;
      const share = Math.min(1, deed.Current / deed.Target);
      if (share > bestShare) {
        best = deed;
        bestShare = share;
      }
    }
  }
  return best;
}
