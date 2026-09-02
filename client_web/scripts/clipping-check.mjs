// Modul: finds CONTENT THAT IS CUT OFF, on every screen, at every width.
//
// This is a different failure from overlap-check.mjs. Overlap is two controls
// sitting on top of each other; clipping is one box whose content is wider than
// itself with no way to reach the rest. Both render "fine" - no error, no blank
// page, nothing a type check or a smoke test can see. The reader simply never
// learns what the sentence said.
//
// The bug that prompted this: a Village building row carried `white-space:
// nowrap` on an `auto` grid track, which forced the row 151px past its panel and
// sliced the upgrade cost through the middle of a word. It was found by
// squinting at a screenshot. Squinting does not scale to 25 screens at 3 widths,
// and it does not run again next month.
//
// WHAT COUNTS AS CLIPPED, and why each condition is needed:
//
//   scrollWidth > clientWidth        content is wider than the box
//   AND overflow-x is not auto/scroll   ...and nothing lets you scroll to it
//
// The second half is the whole trick. A deliberately scrollable table - which
// CLAUDE.md actually asks for on wide content - has scrollWidth > clientWidth by
// design and is perfectly correct. Reporting those drowns the real ones: the
// first version of this flagged 300+ boxes, nearly all of them honest scrollers.
//
// Vertical overflow is NOT checked. Pages scroll down; that is what pages do.
import { SCREENS, assertMatchesNav, go, open, signIn } from './screens.mjs';

// The three widths that matter: a desktop panel grid, the tablet breakpoint
// where the grid collapses, and the narrowest phone the client claims to
// support. Most clipping only appears at the last one.
const WIDTHS = [1500, 900, 390];

// A couple of pixels of subpixel rounding is not a defect. The Village bug
// overflowed by 151.
const TOLERANCE = 4;

const { browser, page } = await open({ width: WIDTHS[0], height: 1000 });
await signIn(page);

const nav = await assertMatchesNav(page);
if (nav.missing.length > 0) console.log(`FAIL nav has no button for: ${nav.missing.join(', ')}`);
if (nav.unvisited.length > 0) console.log(`note: not visited: ${nav.unvisited.join(', ')}`);

const findings = [];

for (const width of WIDTHS) {
  await page.setViewportSize({ width, height: 1000 });
  for (const label of SCREENS) {
    if (nav.missing.includes(label)) continue;
    await go(page, label);
    // Let the grid settle at the new width before measuring.
    await page.waitForTimeout(250);

    const clipped = await page.evaluate((tolerance) => {
      const out = [];
      for (const el of document.querySelectorAll('body *')) {
        // Modul: SVG IS NOT LAID OUT LIKE HTML. An <svg> child reports
        // clientWidth 0 and a scrollWidth taken from its own user-space
        // coordinates, so the Skill Tree's node labels read as "Fortune 10
        // overflows a 29px box by 91px" - an arithmetic artefact of comparing
        // two different coordinate systems, not a clipped label. The skill
        // tree is a viewBox drawing and scales as one.
        if (el.ownerSVGElement || el.tagName.toLowerCase() === 'svg') continue;

        const style = getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden') continue;

        const overflowX = style.overflowX;
        // A box you can scroll is not a box that hides things.
        if (overflowX === 'auto' || overflowX === 'scroll') continue;

        // Modul: AN ELLIPSIS IS AN ANSWER, a hard slice is not. `text-overflow:
        // ellipsis` cuts the text and SAYS SO with a visible "...", which is a
        // deliberate choice about a long name in a narrow column - the Chest's
        // item list makes it 724 times on a 390px phone and is correct every
        // time. What this script is hunting is the other kind: content sliced
        // at the box edge with nothing to indicate anything is missing, which
        // is how a Village upgrade cost got cut through the middle of a word.
        if (style.textOverflow === 'ellipsis') continue;

        const over = el.scrollWidth - el.clientWidth;
        if (over <= tolerance) continue;
        // clientWidth is 0 for inline elements; their overflow is their
        // parent's business and would be reported twice.
        if (el.clientWidth === 0) continue;

        // Report the innermost offender only: if a child is already clipped,
        // the parent is usually just carrying it.
        if ([...el.children].some((c) => c.scrollWidth - c.clientWidth > tolerance
          && c.clientWidth > 0
          && !['auto', 'scroll'].includes(getComputedStyle(c).overflowX))) continue;

        const text = (el.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 60);
        out.push({
          tag: el.tagName.toLowerCase(),
          cls: (typeof el.className === 'string' ? el.className : '').slice(0, 40),
          over,
          width: el.clientWidth,
          text,
        });
      }
      return out;
    }, TOLERANCE);

    for (const c of clipped) findings.push({ width, label, ...c });

    if (clipped.length > 0) {
      console.log(`FAIL ${label} @ ${width}px - ${clipped.length} clipped`);
      for (const c of clipped.slice(0, 5)) {
        console.log(`      ${c.tag}.${c.cls} overflows by ${c.over}px (box ${c.width}px): "${c.text}"`);
      }
      await page.screenshot({ path: `clipping-${label.replace(/\W/g, '')}-${width}.png`, fullPage: true });
    } else {
      console.log(`ok   ${label} @ ${width}px`);
    }
  }
}

await browser.close();

const screens = new Set(findings.map((f) => f.label));
console.log(
  `\n${findings.length} clipped element(s) across ${screens.size} screen(s)`
  + ` in ${SCREENS.length} screens x ${WIDTHS.length} widths`,
);
if (findings.length > 0) process.exit(1);
