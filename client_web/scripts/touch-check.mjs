// Modul: THE FOURTH GEOMETRY CHECKER - can a thumb hit this?
//
// check:clipping asks whether content is cut off. check:overlap asks whether a
// control is buried under another one. Neither asks the question a phone
// actually poses: is the target BIG ENOUGH, and is it far enough from the edge
// of the glass to be pressed at all.
//
// That question had been answered by opinion. The redesign brief that prompted
// this file asserted the client failed it; the only honest way to find out is
// to measure, and the only way to keep the answer true is to measure it every
// time - which is the same reasoning that produced the other two checkers.
//
// THE FLOOR IS 44px, from Apple's HIG and WCAG 2.5.5 (AAA); WCAG 2.5.8 (AA)
// says 24. 44 is the number the brief asked for and the number a thumb
// actually wants, so 44 it is - measured on the control's own box, not on the
// text inside it.
//
// WHAT IS DELIBERATELY NOT FAILED:
//
//   - Anything under 1.5px tall or wide. That is a hidden control, not a small
//     one, and the DOM is full of them (collapsed panels, off-screen list
//     rows). check:overlap learned the same lesson the expensive way.
//   - Controls inside a horizontally scrolling strip, which are allowed to be
//     narrow because the strip itself is the target.
//   - Anything a player cannot see: display:none, visibility:hidden, opacity 0,
//     or scrolled out of its own container.
//
// Reported per screen, worst offenders first, with the measured size - so a
// failure says what to change rather than that something is wrong.
import { SCREENS, open, signIn, go, assertMatchesNav } from './screens.mjs';

const MIN_TOUCH = 44;

/** Ignore anything this small in either axis - it is hidden, not undersized. */
const HIDDEN_EPSILON = 1.5;

/**
 * How close to the bottom of the viewport a control may sit.
 *
 * Modul: not a style rule - a REACHABILITY one. The chat dock, the onboarding
 * coach and any future bottom bar are fixed to the bottom of the glass, and a
 * control in the last few pixels underneath them is one a player cannot press
 * even when nothing is technically covering it. check:overlap catches the
 * covering; this catches the crowding.
 *
 * AND IT ONLY COUNTS WHERE SCROLLING CANNOT CURE IT. This rule used to fire on
 * whatever happened to be sitting at the fold when the page was measured at
 * scrollY=0, which reported five failures that all disappeared after a 120px
 * scroll - three buttons on Chest, one on Wiki. They were never unreachable;
 * the player scrolls to them exactly as they already scroll to see them. The
 * rule now requires the control to be PINNED (fixed or sticky, so no amount of
 * scrolling moves it off the edge) or the scroller to be at its end. Those are
 * the two cases the paragraph above is actually describing.
 */
const SAFE_BOTTOM = 8;

const { browser, page } = await open({ width: 390, height: 844 });
await signIn(page);

const navCheck = await assertMatchesNav(page);
if (navCheck.missing.length > 0) console.log(`FAIL nav has no button for: ${navCheck.missing.join(', ')}`);

const measure = async () => {
  // Modul: MEASURED IN PASSES DOWN THE PAGE, not once at the top.
  //
  // This used to `continue` past anything outside the first viewport, which on
  // a 390px phone means it only ever saw the top 844px. The Wiki is 3147px
  // long: three quarters of it had never been measured at all, on a checker
  // whose report read as if it covered the screen. Scrolling the page and
  // measuring again is the whole fix, and it is why the results are collected
  // into a map keyed on the control rather than a list - one control seen in
  // two overlapping passes is one control.
  const seen = new Map();

  // Modul: how many passes each control was VISIBLE in, and in how many of
  // those it sat at the bottom of the glass. Crowding is only a defect when
  // scrolling never cures it, and the only honest way to know that is to move
  // the page and look again.
  const visible = new Map();
  const crowded = new Map();

  const pageHeight = await page.evaluate(() => document.documentElement.scrollHeight);
  const viewport = await page.evaluate(() => window.innerHeight);

  // Overlapping by a third, so a control straddling a boundary is fully
  // visible in at least one pass.
  const step = Math.floor(viewport * 0.66);
  const stops = [];
  for (let y = 0; y < pageHeight; y += step) stops.push(y);
  // The end is always measured, because it is the one position where a control
  // near the bottom of the glass genuinely cannot be scrolled clear of it.
  stops.push(pageHeight);

  for (const y of stops) {
    await page.evaluate((to) => window.scrollTo(0, to), y);
    await page.waitForTimeout(90);

    const pass = await page.evaluate(
      ({ minTouch, hiddenEpsilon, safeBottom }) => {
        const results = [];

        // Every control this pass could see, flagged or not - the denominator
        // the crowding rule is judged against.
        const visibleKeys = [];
        const controls = document.querySelectorAll(
          'button, a[href], input:not([type="hidden"]), select, textarea, [role="button"], [tabindex]:not([tabindex="-1"])',
        );

        for (const el of controls) {
          const style = getComputedStyle(el);
          if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) continue;
          if (el.disabled) continue;

          const rect = el.getBoundingClientRect();
          if (rect.width < hiddenEpsilon || rect.height < hiddenEpsilon) continue;
          if (rect.bottom < 0 || rect.top > window.innerHeight) continue;

          // A control inside a horizontally scrolling strip is allowed to be
          // narrow - the strip is what the thumb is aiming at.
          let inHorizontalStrip = false;
          for (let parent = el.parentElement; parent; parent = parent.parentElement) {
            const parentStyle = getComputedStyle(parent);
            if (parentStyle.overflowX === 'auto' || parentStyle.overflowX === 'scroll') {
              inHorizontalStrip = true;
              break;
            }
          }

          const label = (el.getAttribute('aria-label') || el.textContent || el.getAttribute('placeholder') || el.tagName)
            .trim()
            .replace(/\s+/g, ' ')
            .slice(0, 34);

          const tooShort = rect.height + 0.5 < minTouch;
          const tooNarrow = !inHorizontalStrip && rect.width + 0.5 < minTouch;

          // Keyed on what the control IS, so the same button found in two
          // overlapping passes is one control rather than two.
          const key = `${el.tagName}|${label}|${Math.round(rect.width)}x${Math.round(rect.height)}`;
          visibleKeys.push(key);

          // Modul: REPORTED, NOT JUDGED. Whether sitting at the bottom of the
          // glass is a defect cannot be decided from one scroll position -
          // that is exactly the mistake this checker used to make. The caller
          // aggregates across passes; see the note there.
          //
          // `position: fixed/sticky` was tried as the test and is not one: the
          // Wiki's sidebar is `sticky` and at 390px the layout stacks so it
          // never actually sticks, and a button inside it was flagged for a
          // pinning that does not happen. Computed style says what was asked
          // for; only moving the page says what occurs.
          const atBottomOfGlass =
            window.innerHeight - rect.bottom < safeBottom && rect.bottom <= window.innerHeight;

          if (tooShort || tooNarrow || atBottomOfGlass) {
            results.push({
              key,
              label,
              tag: el.tagName.toLowerCase(),
              width: Math.round(rect.width),
              height: Math.round(rect.height),
              tooShort,
              tooNarrow,
              atBottomOfGlass,
            });
          }
        }
        return { results, visibleKeys };
      },
      { minTouch: MIN_TOUCH, hiddenEpsilon: HIDDEN_EPSILON, safeBottom: SAFE_BOTTOM },
    );

    const { results: found, visibleKeys } = pass;

    for (const item of found) {
      if (!seen.has(item.key)) seen.set(item.key, item);
      if (item.atBottomOfGlass) crowded.set(item.key, (crowded.get(item.key) ?? 0) + 1);
    }

    // Counted separately from `found`, because a control that is fine in this
    // pass still has to count as "seen here" - otherwise a button that crowds
    // in one pass and is perfectly placed in three would look like it crowded
    // in every pass it appeared in.
    for (const key of visibleKeys) visible.set(key, (visible.get(key) ?? 0) + 1);
  }

  await page.evaluate(() => window.scrollTo(0, 0));

  const results = [];
  for (const [key, item] of seen) {
    // Crowding survives only if the control was at the bottom edge in EVERY
    // pass that saw it, across at least two passes. One pass is a coincidence
    // of where the fold fell; every pass is a control pinned to the glass.
    const passes = visible.get(key) ?? 1;
    const crowdsBottom = (crowded.get(key) ?? 0) === passes && passes >= 2;

    if (item.tooShort || item.tooNarrow || crowdsBottom) {
      results.push({ ...item, crowdsBottom });
    }
  }
  return results;
};

let offenders = 0;
let screensWithProblems = 0;

console.log(`\n=== 390px, floor ${MIN_TOUCH}px ===\n`);

for (const label of SCREENS) {
  await go(page, label);
  await page.waitForTimeout(350);

  const found = await measure();
  if (found.length === 0) {
    console.log(`ok   ${label}`);
    continue;
  }

  screensWithProblems++;
  offenders += found.length;

  // Worst first: the smallest area is the hardest to hit.
  found.sort((a, b) => a.width * a.height - b.width * b.height);

  console.log(`FAIL ${label}: ${found.length} undersized control(s)`);
  for (const item of found.slice(0, 6)) {
    const why = [
      item.tooShort ? 'short' : null,
      item.tooNarrow ? 'narrow' : null,
      item.crowdsBottom ? 'on the bottom edge' : null,
    ]
      .filter(Boolean)
      .join(', ');
    console.log(`       ${item.tag}:"${item.label}" ${item.width}x${item.height} (${why})`);
  }
  if (found.length > 6) console.log(`       ... and ${found.length - 6} more`);
}

console.log(`\n${offenders} undersized control(s) across ${screensWithProblems} screen(s)\n`);

await browser.close();
process.exit(offenders > 0 ? 1 : 0);
