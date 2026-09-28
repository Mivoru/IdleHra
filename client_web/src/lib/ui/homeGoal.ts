import type { DeedChapterEntry, DeedEntry } from '../net/rest';

/**
 * The unfinished deed closest to done, from the chapters that are open.
 *
 * Ties go to the earlier chapter and the earlier deed, which is the order the
 * Book reads in. A deed with no target cannot be measured, so it is skipped
 * rather than read as 0% or as done.
 */
export function closestDeed(chapters: readonly DeedChapterEntry[]): DeedEntry | null {
  let best: DeedEntry | null = null;
  let bestShare = -1;
  for (const chapter of chapters) {
    if (!chapter.IsOpen || chapter.IsComplete) continue;
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
