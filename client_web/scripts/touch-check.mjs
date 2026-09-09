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
 */
const SAFE_BOTTOM = 8;

const { browser, page } = await open({ width: 390, height: 844 });
await signIn(page);

const navCheck = await assertMatchesNav(page);
if (navCheck.missing.length > 0) console.log(`FAIL nav has no button for: ${navCheck.missing.join(', ')}`);

const measure = () =>
  page.evaluate(
    ({ minTouch, hiddenEpsilon, safeBottom }) => {
      const results = [];
      const controls = document.querySelectorAll(
        'button, a[href], input:not([type="hidden"]), select, textarea, [role="button"], [tabindex]:not([tabindex="-1"])',
      );

      for (const el of controls) {
        const style = getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) continue;
        if (el.disabled) continue;

        const rect = el.getBoundingClientRect();
        if (rect.width < hiddenEpsilon || rect.height < hiddenEpsilon) continue;

        // Off the top or bottom of its own scroller, or off the viewport: not
        // a control the player is being offered right now.
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
        const crowdsBottom = window.innerHeight - rect.bottom < safeBottom && rect.bottom <= window.innerHeight;

        if (tooShort || tooNarrow || crowdsBottom) {
          results.push({
            label,
            tag: el.tagName.toLowerCase(),
            width: Math.round(rect.width),
            height: Math.round(rect.height),
            tooShort,
            tooNarrow,
            crowdsBottom,
          });
        }
      }
      return results;
    },
    { minTouch: MIN_TOUCH, hiddenEpsilon: HIDDEN_EPSILON, safeBottom: SAFE_BOTTOM },
  );

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
