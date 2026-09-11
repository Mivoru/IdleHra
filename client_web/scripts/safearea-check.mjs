// Modul: THE FIFTH GEOMETRY CHECKER - is anything underneath the status bar?
//
// The other four measure the page against its own viewport. A phone poses a
// question none of them can see: part of the glass is SPOKEN FOR. The clock,
// the battery icon, the camera cutout and the gesture bar are drawn by the OS
// on top of whatever the app puts there, and on Android 15+ an app targeting
// SDK 35 or later cannot decline to draw underneath them.
//
// This client asks for it explicitly, too: `viewport-fit=cover` in index.html
// is what Capacitor's SystemBars reads as consent to stop padding the WebView
// and hand the insets through to CSS instead. Before this checker existed the
// client handled exactly one of the four - the bottom - and the header, which
// carries the menu button, was going to render under the status bar on every
// modern phone. Nobody could have known: a Chromium tab has no notch.
//
// HOW IT SIMULATES ONE, AND WHY THAT IS HONEST.
//
// It sets `--safe-area-inset-top/right/bottom/left` on the root element. That
// is not an approximation of what the phone does - it is LITERALLY what the
// phone does: SystemBars.injectSafeAreaCSS evaluates a script setting those
// four properties on document.documentElement, and app.css reads them through
// `var(--safe-area-inset-*, env(...))` for exactly this reason. So a pass here
// is evidence about the real device, bounded by one assumption: that the
// numbers below are representative.
//
// WHAT IT ASSERTS. Nothing a player can see or press may intersect an inset
// band, and the page must not gain horizontal scroll from the side insets.
// Text is included deliberately - a header title half under the clock is not
// less broken than a button there.
import { SCREENS, open, signIn, go, assertMatchesNav } from './screens.mjs';

/**
 * A representative notched phone, in CSS pixels.
 *
 * Modul: MEASURED FROM REAL HARDWARE, not invented. 48/24 is a Pixel-class
 * portrait device on Android 15 (a 48dp status bar over a punch-hole, a 24dp
 * gesture bar). iPhone-class hardware is taller at the top (59) and shorter at
 * the bottom (34); 48/24 is inside both, and the point of the check is whether
 * the layout RESPONDS to an inset at all, which is a yes/no that does not turn
 * on the exact number.
 *
 * The sides are 0 in portrait on every phone made. They are non-zero in
 * LANDSCAPE on a notched device, which is why they are exercised at all - the
 * app allows landscape (Info.plist lists both orientations).
 */
const PORTRAIT = { top: 48, right: 0, bottom: 24, left: 0 };
const LANDSCAPE = { top: 0, right: 44, bottom: 21, left: 44 };

/** Below this a box is hidden rather than misplaced - the other checkers agree. */
const HIDDEN_EPSILON = 1.5;

/**
 * How far into a band something may poke before it is a failure.
 *
 * Modul: not zero, and the reason is subpixel arithmetic rather than
 * generosity. `calc(1rem + var(--sa-bottom))` lands on fractional device
 * pixels and getBoundingClientRect reports them, so an exactly-correct layout
 * reports intrusions of a few hundredths. One pixel is far below anything a
 * person could see and far above the noise.
 */
const TOLERANCE = 1;

const { browser, page } = await open({ width: 390, height: 844 });
await signIn(page);

const navCheck = await assertMatchesNav(page);
if (navCheck.missing.length > 0) {
  console.log(`FAIL nav has no button for: ${navCheck.missing.join(', ')}`);
}

/** Applies the insets the way the native layer does, and waits for a frame. */
async function applyInsets(insets) {
  await page.evaluate((i) => {
    const root = document.documentElement;
    root.style.setProperty('--safe-area-inset-top', `${i.top}px`);
    root.style.setProperty('--safe-area-inset-right', `${i.right}px`);
    root.style.setProperty('--safe-area-inset-bottom', `${i.bottom}px`);
    root.style.setProperty('--safe-area-inset-left', `${i.left}px`);
  }, insets);
  await page.waitForTimeout(120);
}

/**
 * Everything visible that lands inside a reserved band.
 *
 * Runs entirely in the page: one round trip per screen rather than one per
 * element, which is the difference between a checker people run and one they
 * do not.
 *
 * Modul: THE THREE BANDS ARE NOT THE SAME QUESTION, and treating them as one
 * is what made the first run of this file report 45 failures on a correct
 * layout.
 *
 *   TOP - judged AT REST, at scrollY 0. Content sliding under the status bar
 *   as the player scrolls is not a defect, it is what edge-to-edge means and
 *   what every native app does. What must never be under the clock is what
 *   greets somebody who opens the game: the header and the first screenful.
 *
 *   BOTTOM - judged only where scrolling cannot cure it, which is the rule
 *   check:touch already arrived at for the same reason. A control at the fold
 *   is reached by scrolling to it. Two cases are not: something PINNED
 *   (fixed or sticky, so no scroll moves it) and the very END of the document.
 *   Both are checked; nothing in between is.
 *
 *   SIDES - judged always. A cutout inset is horizontal and vertical scrolling
 *   does not move anything out of it.
 */
async function intrude(insets, tolerance, { bottomMode }) {
  return page.evaluate(
    ({ i, tol, eps, bottomMode }) => {
      const vw = window.innerWidth;
      const vh = window.innerHeight;
      const hits = [];

      const visible = (el, r) => {
        if (r.width < eps || r.height < eps) return false;
        // Modul: TALLER THAN THE GLASS IS NOT "UNDER THE STATUS BAR".
        //
        // VirtualList sizes a spacer to the full height of the data - 117,000
        // pixels on the dev fixture's Chest - so it crosses every band on
        // every screen while being nothing the player can see or press. The
        // same is true of any full-height wrapper. A box has to FIT on the
        // screen before where it sits on the screen means anything.
        if (r.height > vh) return false;
        const s = getComputedStyle(el);
        if (s.display === 'none' || s.visibility === 'hidden') return false;
        if (Number(s.opacity) === 0) return false;
        return true;
      };

      // Leaf elements only: a full-height wrapper legitimately spans the whole
      // page, and reporting it says nothing about what the player can see. What
      // matters is the text node or the control at the end of the tree.
      const candidates = [...document.body.querySelectorAll('*')].filter((el) => {
        if (el.children.length > 0) return false;
        const tag = el.tagName;
        return tag !== 'SCRIPT' && tag !== 'STYLE' && tag !== 'BR';
      });

      // Plus every control, leaf or not - a button wrapping an icon has a
      // child, and the button is the thing being pressed.
      const controls = [...document.body.querySelectorAll('button, a[href], input, select, textarea')];

      /** Does this element scroll its own children? */
      const scrolls = (el) => {
        const s = getComputedStyle(el);
        return /(auto|scroll)/.test(s.overflowY) && el.scrollHeight > el.clientHeight + 2;
      };

      // Modul: AND THE SCROLLERS THEMSELVES, which is the whole answer for a
      // list.
      //
      // Leaderboards puts 26 rows in an `ol.board` that scrolls inside the
      // page. Scrolling the WINDOW to its end does not move that list, so its
      // rows sit wherever they were - reported, on the first run of this file,
      // as eight separate findings per screen. None of them was a defect: the
      // player scrolls the list, exactly as they already do to read it.
      //
      // What IS a defect is the list's own bottom EDGE landing under the
      // gesture bar, because then its last visible row is unreachable no
      // matter how far it is scrolled. One measurement, on the container,
      // replaces a row-by-row report that was all noise.
      const scrollers = [...document.body.querySelectorAll('*')].filter(scrolls);

      /** The scroll container a box actually lives in - the document, or a panel. */
      const scrollerOf = (el) => {
        for (let n = el.parentElement; n && n !== document.body; n = n.parentElement) {
          if (scrolls(n)) return n;
        }
        return null;
      };

      for (const el of new Set([...candidates, ...controls, ...scrollers])) {
        const r = el.getBoundingClientRect();
        if (!visible(el, r)) continue;
        // Nothing off-screen: a list row scrolled out of view is not under the
        // clock, it is nowhere.
        if (r.bottom <= 0 || r.top >= vh || r.right <= 0 || r.left >= vw) continue;

        const label = (el.textContent || el.tagName).trim().slice(0, 40) || el.tagName;

        if (i.top > 0 && r.top < i.top - tol && r.bottom > 0) {
          hits.push({ band: 'top', label, at: Math.round(r.top), reserved: i.top });
        }

        // Walks up, because the offending box is usually a leaf inside the
        // pinned container. The tag itself was placed by markPinned(), which
        // decided it by scrolling rather than by reading a style.
        const pinned = el.closest('[data-sa-pinned]') !== null;

        // A box inside a panel that scrolls independently is judged by that
        // panel's edge, measured separately above - not by where the row
        // happened to be when the window stopped scrolling.
        const ownScroller = scrollerOf(el);
        const bottomCounts = pinned || (bottomMode === 'end' && (ownScroller === null || scrolls(el)));

        if (bottomCounts && i.bottom > 0 && r.bottom > vh - i.bottom + tol && r.top < vh) {
          hits.push({
            band: 'bottom',
            label: pinned ? `${label} (pinned)` : label,
            at: Math.round(vh - r.bottom),
            reserved: i.bottom,
          });
        }
        if (i.left > 0 && r.left < i.left - tol && r.right > 0) {
          hits.push({ band: 'left', label, at: Math.round(r.left), reserved: i.left });
        }
        if (i.right > 0 && r.right > vw - i.right + tol && r.left < vw) {
          hits.push({ band: 'right', label, at: Math.round(vw - r.right), reserved: i.right });
        }
      }
      return hits;
    },
    { i: insets, tol: tolerance, eps: HIDDEN_EPSILON, bottomMode },
  );
}

/**
 * Tags the elements that are ACTUALLY pinned, by scrolling and looking.
 *
 * Modul: `position: sticky` IS NOT THE TEST, and check:touch learned this
 * first - its note says so outright. The Wiki's sidebar is declared sticky and
 * at 390px the layout stacks, so it never sticks to anything and scrolls away
 * with the rest of the page. Reading the computed style called it pinned and
 * produced the only two findings left on an otherwise clean run, both false.
 *
 * So: measure where a box is at rest, scroll the page to its end, measure
 * again. Anything that did not move while the page moved is pinned - which is
 * the property the bottom band actually cares about, stated as the observation
 * it is rather than as a declaration that may not have taken effect.
 */
async function markPinned() {
  await page.evaluate(() => {
    for (const el of document.querySelectorAll('[data-sa-pinned]')) el.removeAttribute('data-sa-pinned');

    const boxes = [...document.body.querySelectorAll('*')];
    window.scrollTo(0, 0);
    const before = boxes.map((el) => el.getBoundingClientRect().top);
    const startedAt = window.scrollY;

    window.scrollTo(0, document.documentElement.scrollHeight);
    const moved = Math.abs(window.scrollY - startedAt);
    const after = boxes.map((el) => el.getBoundingClientRect().top);

    // A page with nothing to scroll cannot distinguish pinned from placed, and
    // nothing on it can be scrolled out from under a bar either - so on such a
    // page everything counts, which is what leaving the tags off achieves via
    // the `bottomMode === 'end'` arm.
    if (moved < 2) return;

    for (let i = 0; i < boxes.length; i++) {
      if (Math.abs(after[i] - before[i]) < 2) boxes[i].setAttribute('data-sa-pinned', '1');
    }
  });
  await page.waitForTimeout(80);
}

/** Horizontal scroll the side insets caused - a phone must never scroll sideways. */
async function overflows() {
  return page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
}

let failures = 0;
let checked = 0;

// Modul: THE LAYOUT MUST ACTUALLY MOVE, and that is checked before anything
// else. Every assertion below passes trivially on a page that ignores the
// properties AND on a page that handles them - the difference is whether the
// header went down by 48px. Without this, deleting the whole :root block would
// leave this checker green, which is the failure mode a geometry checker is
// most prone to.
await applyInsets({ top: 0, right: 0, bottom: 0, left: 0 });
const headerBefore = await page.evaluate(() => document.querySelector('header').getBoundingClientRect().top);
await applyInsets(PORTRAIT);
const headerAfter = await page.evaluate(() => document.querySelector('header').getBoundingClientRect().top);

if (headerAfter - headerBefore < PORTRAIT.top - TOLERANCE) {
  failures += 1;
  console.log(
    `FAIL the layout does not respond to the top inset at all: header moved ` +
      `${Math.round(headerAfter - headerBefore)}px for a ${PORTRAIT.top}px status bar. ` +
      `Check that app.css still defines --sa-top and that body reads it.`,
  );
} else {
  console.log(`ok   the layout responds: header moved ${Math.round(headerAfter - headerBefore)}px for a ${PORTRAIT.top}px status bar`);
}

for (const screen of SCREENS) {
  await go(page, screen);

  for (const [name, insets, size] of [
    ['portrait', PORTRAIT, { width: 390, height: 844 }],
    ['landscape', LANDSCAPE, { width: 844, height: 390 }],
  ]) {
    await page.setViewportSize(size);
    await applyInsets(insets);
    await page.waitForTimeout(150);

    // Decided by scrolling, before either pass reads the answer.
    await markPinned();

    // Pass one: at rest. Answers the top and side bands, and the bottom band
    // for anything pinned there.
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.waitForTimeout(120);
    const hits = await intrude(insets, TOLERANCE, { bottomMode: 'pinned' });

    // Pass two: scrolled to the very end, where there is nothing left to
    // scroll and body's own bottom padding is the only thing holding content
    // clear of the home indicator.
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await page.waitForTimeout(150);
    hits.push(...(await intrude(insets, TOLERANCE, { bottomMode: 'end' })).filter((h) => h.band === 'bottom'));

    const scrolls = await overflows();
    checked += 1;

    if (hits.length === 0 && !scrolls) continue;

    failures += 1;
    console.log(`FAIL ${screen} (${name}): ${hits.length} under a system bar${scrolls ? ', and the page scrolls sideways' : ''}`);
    // Worst first, and only a handful - a hundred lines of the same finding is
    // a report nobody reads.
    for (const h of [...hits].sort((a, b) => a.at - b.at).slice(0, 5)) {
      console.log(`       ${h.band}: "${h.label}" at ${h.at}px, ${h.reserved}px reserved`);
    }
  }

  await page.setViewportSize({ width: 390, height: 844 });
}

console.log(`\n${checked} screen/orientation pairs checked, ${failures} with findings`);
await browser.close();
process.exit(failures > 0 ? 1 : 0);
