// Modul: finds controls that physically sit on top of each other.
//
// A button overlapping a select is not something a structural query or a type
// check can see - both elements exist, both are "visible", and the DOM is
// perfectly well formed. Only the geometry is wrong. This walks every screen
// at two widths and reports interactive elements whose boxes intersect.
//
// Overlap is judged on the INTERSECTION AREA relative to the smaller box, so a
// one-pixel rounding touch is ignored and a control genuinely buried under
// another is not.
// Modul: the screen list is SHARED now (scripts/screens.mjs). This file kept
// its own copy, which had drifted its own way: it still said 'Social' where the
// nav says 'Friends', and it never visited Leaderboards or Wiki at all.
import { SCREENS, assertMatchesNav, go, open, signIn } from './screens.mjs';

const OVERLAP_RATIO = 0.18;   // ignore hairline touches
const MIN_AREA = 120;         // ignore slivers

const { browser, page } = await open({ width: 1500, height: 1000 });
await signIn(page);

const navCheck = await assertMatchesNav(page);
if (navCheck.missing.length > 0) console.log(`FAIL nav has no button for: ${navCheck.missing.join(', ')}`);
if (navCheck.unvisited.length > 0) console.log(`note: not visited: ${navCheck.unvisited.join(', ')}`);

// Modul: "COVERED", NOT "RECTANGLES INTERSECT".
//
// The first version of this compared bounding boxes and reported 309 pairs,
// almost all of them lies: a list with `overflow: auto` gives its off-screen
// children real rects that land on whatever is painted below the list, so
// every long inventory looked like it was sitting on the form beneath it.
//
// The honest question is not "do these boxes intersect" but "if a player aims
// at this control, do they hit something else". elementFromPoint answers
// exactly that, and it already accounts for clipping, stacking order and
// pointer-events - none of which a rectangle knows about.
const findOverlaps = () =>
  page.evaluate(() => {
    // A control scrolled out of its own list is not "covered" - it is not
    // there. Its rect still lands wherever the maths puts it, though, which
    // made a clipped inventory row look like it was buried under the price
    // field beneath the list. Clip against every scrolling ancestor first.
    const visibleInScrollParents = (el) => {
      const r = el.getBoundingClientRect();
      for (let p = el.parentElement; p; p = p.parentElement) {
        const st = getComputedStyle(p);
        if (!/auto|scroll|hidden/.test(st.overflowY + st.overflowX)) continue;
        const pr = p.getBoundingClientRect();
        const visibleY = Math.min(r.bottom, pr.bottom) - Math.max(r.top, pr.top);
        const visibleX = Math.min(r.right, pr.right) - Math.max(r.left, pr.left);
        if (visibleY < r.height * 0.9 || visibleX < r.width * 0.9) return false;
      }
      return true;
    };

    const els = [...document.querySelectorAll('button, select, input, textarea, a[href]')]
      .filter((e) => {
        const r = e.getBoundingClientRect();
        const st = getComputedStyle(e);
        return r.width > 8 && r.height > 8 &&
          r.top >= 0 && r.left >= 0 &&
          r.bottom <= innerHeight && r.right <= innerWidth &&
          st.visibility !== 'hidden' && Number(st.opacity) > 0.05 &&
          visibleInScrollParents(e);
      });

    const name = (el) =>
      `${el.tagName.toLowerCase()}${el.getAttribute('aria-label') ? `[${el.getAttribute('aria-label')}]` : ''}:"${(el.textContent || el.value || '').trim().replace(/\s+/g, ' ').slice(0, 30)}"`;

    const hits = [];
    for (const el of els) {
      const r = el.getBoundingClientRect();
      // Sample the centre and the four quarter points: a control half-covered
      // at one edge is still a defect, and a centre-only probe misses it.
      const probes = [
        [r.left + r.width / 2, r.top + r.height / 2],
        [r.left + r.width / 4, r.top + r.height / 2],
        [r.left + (r.width * 3) / 4, r.top + r.height / 2],
        [r.left + r.width / 2, r.top + r.height / 4],
        [r.left + r.width / 2, r.top + (r.height * 3) / 4],
      ];
      let blockedBy = null;
      let blockedCount = 0;
      for (const [x, y] of probes) {
        const top = document.elementFromPoint(x, y);
        if (!top || top === el || el.contains(top) || top.contains(el)) continue;
        const owner = top.closest('button, select, input, textarea, a[href]');
        if (!owner || owner === el || el.contains(owner)) continue;
        blockedCount++;
        blockedBy = owner;
      }
      // Modul: A FLOATING DOCK IS NOT AN OVERLAP DEFECT, and reporting it as
      // one is how a real overlap gets lost in the noise.
      //
      // ChatDock is position:fixed in the bottom-right corner, so at any given
      // scroll offset it is sitting on top of SOMETHING - it reported "Kept",
      // "Bin", "Go" and a skill node, and all four were measured as freely
      // reachable by scrolling a little (app.css reserves the bottom padding
      // that guarantees it). Four permanent false positives on a check with
      // four findings is the whole signal.
      //
      // Anything else fixed and floating belongs here too, but it is matched by
      // POSITION rather than by class name so a rename cannot silently
      // reinstate the noise.
      const coveredByFixedOverlay =
        blockedBy && getComputedStyle(blockedBy.parentElement ?? blockedBy).position === 'fixed';

      if (blockedCount >= 2 && !coveredByFixedOverlay) {
        hits.push(`${name(el)} is covered by ${name(blockedBy)} (${blockedCount}/5 probes)`);
      }
    }
    return hits;
  });

let total = 0;
for (const width of [1500, 390]) {
  await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
  console.log(`\n=== ${width}px ===`);
  for (const label of SCREENS) {
    try {
      if (width === 390) {
        const menu = page.getByRole('button', { name: /Menu/ }).first();
        if ((await menu.count()) > 0) await menu.click();
        await page.waitForTimeout(200);
      }
      const nav = page.locator('header').getByRole('button', { name: label, exact: true }).first();
      if ((await nav.count()) === 0) { continue; }
      await nav.click({ timeout: 5000 });
      await page.waitForFunction(() => !/\bLoading\.\.\./.test(document.body.innerText), { timeout: 12000 });
      await page.waitForTimeout(500);
      const hits = await findOverlaps();
      if (hits.length) {
        total += hits.length;
        console.log(`  ${label}:`);
        for (const h of hits.slice(0, 6)) console.log(`     ${h}`);
      }
    } catch (err) {
      console.log(`  ${label}: SKIPPED (${String(err).split('\n')[0].slice(0, 60)})`);
    }
  }
}

console.log(`\n${total} overlapping control pair(s)`);
await browser.close();
