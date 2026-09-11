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
      // Modul: A VIRTUALISED ROW TALLER THAN ITS SLOT LANDS ON ITS NEIGHBOUR,
      // and that was invisible to all five checkers.
      //
      // VirtualList positions every row by arithmetic - `top: index * stride`
      // - so `rowHeight` is a PROMISE about how tall the row will draw. Break
      // it and nothing errors, nothing is clipped, nothing overlaps in the
      // sense check:overlap means (it hunts controls covering controls), and
      // the page does not overflow. The rows simply draw on top of each other.
      //
      // Found on a real phone in the Forge's item picker: `.row` declared 44px
      // around a two-line grid needing about 49, so every item name lay across
      // the row beneath it. The file's own comment said "change one and change
      // the other, or the rows overlap" - it was right, and the numbers under
      // it were still wrong, which is the whole argument for measuring instead
      // of asserting in prose.
      for (const slot of document.querySelectorAll('li.slot')) {
        const slotH = slot.getBoundingClientRect().height;
        if (slotH < 1) continue;
        for (const child of slot.children) {
          const need = Math.max(child.scrollHeight, child.getBoundingClientRect().height);
          if (need - slotH > tolerance) {
            out.push({
              tag: child.tagName.toLowerCase(),
              cls: 'virtuallist-row-taller-than-its-slot',
              over: Math.round(need - slotH),
              width: Math.round(slotH),
              text: (child.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 60),
            });
          }
        }
      }

      for (const el of document.querySelectorAll('body *')) {
        // Modul: SVG IS NOT LAID OUT LIKE HTML, so it is skipped HERE and
        // measured differently BELOW - it is no longer skipped outright.
        //
        // The original reasoning still holds for this test: an <svg> child
        // reports clientWidth 0 and a scrollWidth taken from its own user-space
        // coordinates, so comparing the two reads as "Fortune 10 overflows a
        // 29px box by 91px" - an artefact of mixing coordinate systems, not a
        // clipped label.
        //
        // But "clientWidth is meaningless on SVG" was turned into "SVG cannot
        // be wrong", and that is how a real defect hid for as long as this file
        // has existed: the Skill Tree's "Fortune" label genuinely ran off the
        // LEFT EDGE OF THE PHONE, measured at left=-6px, with the word
        // unreadable. Reported by a player, not by this script.
        //
        // getBoundingClientRect is in VIEWPORT pixels for every element, SVG
        // included. So the viewport test below is valid where the box test is
        // not, and it is the one that catches this.
        const isSvg = el.ownerSVGElement !== null || el.tagName.toLowerCase() === 'svg';
        if (isSvg) {
          const r = el.getBoundingClientRect();
          if (r.width >= 1 && r.height >= 1) {
            const s = getComputedStyle(el);
            const hidden = s.display === 'none' || s.visibility === 'hidden' || Number(s.opacity) === 0;
            const offLeft = Math.round(-r.left);
            const offRight = Math.round(r.right - window.innerWidth);
            if (!hidden && (offLeft > tolerance || offRight > tolerance)) {
              out.push({
                tag: el.tagName.toLowerCase(),
                cls: offLeft > offRight ? 'svg-off-left-of-screen' : 'svg-off-right-of-screen',
                over: Math.max(offLeft, offRight),
                width: Math.round(r.width),
                text: (el.textContent || '').trim().slice(0, 40),
              });
            }
          }
          continue;
        }

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

        // Modul: COLLAPSED IS NOT CLIPPED, and no checker could see it.
        //
        // A clipped box has a width and hides part of its content. A COLLAPSED
        // one has been squeezed to no width at all, so there is nothing left to
        // clip - and that state was invisible to all five scripts at once:
        // nothing is cut off (the box is 0 wide), nothing overlaps, nothing is
        // under a system bar, no control is undersized, and the page does not
        // overflow. It simply is not there.
        //
        // Measured on the Chest at 360px, one equipment row: the item NAME span
        // is 0 wide with a scrollWidth of 99, and the rarity label is 0 wide and
        // 121 TALL - wrapped to six lines inside a row whose height is pinned to
        // 34px by VirtualList's rowHeight contract. Five 44px buttons that
        // cannot shrink leave nothing for the text, so flex takes it all. The
        // player sees a nameless row and a column of single letters. Reported
        // from a phone as "the text is like 2 letters wide horde of words".
        //
        // The line that hid it was `if (el.clientWidth === 0) continue`, added
        // because INLINE elements legitimately report 0. That is true, and it
        // is why this uses getBoundingClientRect instead: the rect is real
        // layout geometry for inline and flex children alike.
        const rect = el.getBoundingClientRect();
        const hasText = (el.textContent ?? '').trim().length > 0;
        const squeezed =
          hasText &&
          rect.width < 1 &&
          rect.height >= 1 &&
          el.scrollWidth > 1 &&
          Number(style.opacity) !== 0 &&
          getComputedStyle(el.parentElement ?? el).display.includes('flex');

        if (squeezed) {
          out.push({
            tag: el.tagName.toLowerCase(),
            cls: `collapsed-to-zero-width${rect.height > 40 ? '-and-wrapping' : ''}`,
            over: el.scrollWidth,
            width: 0,
            text: (el.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 60),
          });
          continue;
        }

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
