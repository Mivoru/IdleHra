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
import { chromium } from 'playwright';

const SCREENS = [
  'Map', 'Combat', 'Gathering', 'World Boss', 'Boosts',
  'Character', 'Chest', 'Auto-Eat', 'Crafting', 'Forge',
  'Market', 'Social', 'Guild', 'Mail',
  'Village', 'Skill Tree', 'Progress', 'Inheritance', 'Codex',
  'Breeding', 'Ancestors', 'Store', 'Settings',
];

const OVERLAP_RATIO = 0.18;   // ignore hairline touches
const MIN_AREA = 120;         // ignore slivers

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });

await page.goto('http://localhost:5173/', { waitUntil: 'networkidle' });
await page.getByRole('button', { name: 'Sign in' }).click();
await page.locator('input[type="email"]').fill('dev@folkidle.local');
await page.locator('input[type="password"]').fill('FolkIdleDev123!');
await page.getByRole('button', { name: 'Sign in', exact: true }).last().click();
await page.waitForSelector('text=Combat', { timeout: 20000 });
{
  const deadline = Date.now() + 6000;
  while (Date.now() < deadline) {
    const c = page.getByRole('button', { name: 'Continue', exact: true });
    if ((await c.count()) > 0) { await c.first().click(); break; }
    await page.waitForTimeout(250);
  }
}

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
      if (blockedCount >= 2) {
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
