// Modul: A NUMBER THAT COUNTS TO ITS NEW VALUE INSTEAD OF JUMPING (2026-10-14).
//
// The header wallet changed by replacing one figure with another, so a kill
// worth 40 gold and a sale worth 40,000 looked identical: the digits swapped
// and the eye, which was on the fight, never saw it. Counting the figure up
// over a fraction of a second makes the change itself visible, and its size
// readable from how long the count runs.
//
// What this does NOT do is change the number anything reads. Money.svelte
// keeps `data-exact` and the title on the real value, so exercise.mjs and a
// hovering player see the truth while the digits are still travelling.
//
// Two cheapenings, both measured against the 10 Hz packet stream:
// - when the DISPLAYED text would not change (5.04M -> 5.04M, which is most
//   kills once a balance is compacted) the value is set instantly, so no
//   animation frame runs to redraw identical characters;
// - reduced motion makes every set instant, through motionMs.
import { Tween } from 'svelte/motion';
import { cubicOut } from 'svelte/easing';
import { untrack } from 'svelte';
import { motionMs, MOTION } from './motion';

/**
 * Follows `source()`, easing towards each new value. `sameText` says whether
 * two values would render identically, in which case the move is instant.
 * Call during component initialisation - it registers an effect.
 */
export function countUp(
  source: () => number,
  sameText: (a: number, b: number) => boolean = (a, b) => Math.round(a) === Math.round(b),
): { readonly current: number } {
  const tween = new Tween(source(), { easing: cubicOut });
  $effect(() => {
    const target = source();
    const from = untrack(() => tween.current);
    const duration = sameText(from, target) ? 0 : motionMs(MOTION.count);
    void tween.set(target, { duration });
  });
  return {
    get current() {
      return tween.current;
    },
  };
}
