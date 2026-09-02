// Modul: ONE list of the game's screens, and the sign-in that reaches them.
//
// This existed three times - in smoke-screens.mjs, overlap-check.mjs and now a
// third checker - and every copy rotted separately. smoke-screens was still
// asking for 'Larder' (the nav has said 'Auto-Eat' for a long time), 'Social'
// (it is 'Friends'), 'Chat' (a dock, not a screen) and 'Bank' (the chrono bank,
// deleted 2026-09-02), while never visiting Map, Leaderboards, Ancestors,
// Inheritance, Skill Tree or Wiki at all. overlap-check had its own different
// subset with its own different gaps.
//
// A missing label announces itself as a click timeout eventually. A screen no
// checker visits announces nothing ever, which is the half that matters: six of
// them were going unchecked. So the list lives once, and `assertMatchesNav`
// makes the nav itself the authority rather than this file.
import { chromium } from 'playwright';

/** Every navigable destination, in the header's own order and grouping. */
export const SCREENS = [
  'Map', 'Combat', 'Gathering', 'World Boss', 'Boosts',
  'Character', 'Chest', 'Auto-Eat', 'Crafting', 'Forge',
  'Market', 'Friends', 'Guild', 'Mail', 'Leaderboards',
  'Breeding', 'Ancestors', 'Inheritance',
  'Village', 'Skill Tree', 'Progress', 'Codex', 'Store', 'Settings',
  'Wiki',
];

/**
 * Header buttons that are not destinations, and so are not expected in SCREENS.
 * Named rather than pattern-matched, so a new one has to be looked at once.
 */
const NON_DESTINATIONS = ['Menu · Map', 'Sign out'];

export const BASE = process.env.FOLKIDLE_E2E_BASE ?? 'http://localhost:5173/';

export const DEV_EMAIL = 'dev@folkidle.local';
export const DEV_PASSWORD = 'FolkIdleDev123!';

/** A browser and a page, with console/pageerror collection wired up. */
export async function open({ width = 1500, height = 1000 } = {}) {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width, height } });
  const errors = [];
  page.on('console', (m) => {
    // A 403 is the server saying no CORRECTLY: every client asks
    // /api/v1/admin/status whether this account may see the admin tools, and an
    // ordinary account is told no. The browser logs the refusal regardless.
    if (m.type() === 'error' && /status of 403/.test(m.text())) return;
    if (m.type() === 'error') errors.push(`console: ${m.text()}`);
  });
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  return { browser, page, errors };
}

/**
 * Signs in as a throwaway guest. This is the one to use against PRODUCTION:
 * a guest owns nothing, so nothing a checker does can spend a real player's
 * items. It is useless for anything that needs possessions.
 */
export async function signInAsGuest(page) {
  await page.goto(BASE, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: 'Play as guest' }).click();
  await waitForShell(page);
  await page.waitForTimeout(500);
}

/**
 * Waits for the signed-in shell, WITHOUT depending on any nav label being
 * visible.
 *
 * Modul: this used to wait for `text=Combat`. Below the mobile breakpoint the
 * nav collapses behind a hamburger, so that button exists but is never
 * visible - and a narrow-viewport run died on a 25-second timeout that named a
 * locator and said nothing about the breakpoint. The header and the first
 * state packet are what "signed in" actually means.
 */
async function waitForShell(page) {
  await page.waitForSelector('header', { timeout: 25000 });
  await page.waitForFunction(
    () => !document.body.innerText.includes('Waiting for the first state snapshot'),
    { timeout: 25000 },
  );
}

/** Signs in as the stocked dev fixture and clears the offline summary. */
export async function signIn(page) {
  await page.goto(BASE, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.locator('input[type="email"]').fill(DEV_EMAIL);
  await page.locator('input[type="password"]').fill(DEV_PASSWORD);
  await page.getByRole('button', { name: 'Sign in', exact: true }).last().click();
  await waitForShell(page);
  // The offline summary is a modal with a backdrop that swallows every click,
  // and it ARRIVES LATE - built from the first state packet, so a single count()
  // straight after sign-in can run before it exists.
  const deadline = Date.now() + 8000;
  while (Date.now() < deadline) {
    const cont = page.getByRole('button', { name: 'Continue', exact: true });
    if ((await cont.count()) > 0) {
      await cont.first().click().catch(() => {});
      break;
    }
    await page.waitForTimeout(300);
  }
  await page.waitForTimeout(500);
}

/**
 * Reads the nav and reports where SCREENS and reality disagree, in both
 * directions. Returns { missing, unvisited }.
 */
export async function assertMatchesNav(page) {
  const navLabels = await page.evaluate(() =>
    [...document.querySelectorAll('header button')]
      .map((b) => b.textContent.trim())
      .filter((t) => t.length > 0),
  );
  return {
    missing: SCREENS.filter((s) => !navLabels.includes(s)),
    unvisited: navLabels.filter((n) => !SCREENS.includes(n) && !NON_DESTINATIONS.includes(n)),
  };
}

/**
 * Navigates by nav label and waits for the screen's own queries to settle.
 *
 * Modul: OPENS THE HAMBURGER FIRST when the nav is collapsed. Below the mobile
 * breakpoint the header folds every destination behind a "Menu · <screen>"
 * toggle, so a direct click waits the full thirty seconds and dies on a
 * timeout that names the locator and says nothing about the breakpoint - which
 * is what stopped the first narrow-width sweep dead.
 */
export async function go(page, label) {
  const target = page.locator('header').getByRole('button', { name: label, exact: true }).first();
  if (!(await target.isVisible().catch(() => false))) {
    const menu = page.locator('header').getByRole('button', { name: /^Menu( ·|$)/ }).first();
    if ((await menu.count()) > 0) {
      await menu.click();
      await page.waitForTimeout(300);
    }
  }
  await target.click();
  await page
    .waitForFunction(() => !/\bLoading\.\.\./.test(document.body.innerText), { timeout: 15000 })
    .catch(() => {});
  await page.waitForTimeout(600);
}
