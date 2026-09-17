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
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// Modul: THIS FILE CANNOT `import` i18n.ts DIRECTLY.
//
// screens.mjs runs under plain `node`, not Vite - and Node 24's native loader
// does strip TypeScript syntax, but it still enforces real ESM resolution,
// which refuses i18n.ts's own extensionless relative import
// (`from '../net/config'`) that only a bundler resolves. Confirmed by hand:
// `node -e "import('./src/lib/ui/i18n.ts')"` throws
// "Cannot find module '...\\net\\config'" before it ever reaches this file.
//
// So LANGUAGES' codes and STORAGE_KEY are pulled out of the real source with
// a regex instead of retyped by hand - the same reasoning
// tests/serverMirrors.test.ts uses to read C# source without importing it. A
// second hand-typed copy of either constant is exactly how FOLKIDLE_E2E_LANG
// would go on silently no-op-ing after the next rename.
const i18nSource = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'lib', 'ui', 'i18n.ts'),
  'utf8',
);

const STORAGE_KEY_MATCH = i18nSource.match(/export const STORAGE_KEY = '([^']+)'/);
if (!STORAGE_KEY_MATCH) {
  throw new Error('could not find STORAGE_KEY in i18n.ts - the regex needs updating, not deleting');
}
/** Mirrors i18n.ts's own STORAGE_KEY, read from source rather than retyped. */
export const STORAGE_KEY = STORAGE_KEY_MATCH[1];

const LANGUAGE_CODES = [...i18nSource.matchAll(/code: '([A-Za-z]+)' as const/g)].map((m) => m[1]);
if (LANGUAGE_CODES.length === 0) {
  throw new Error('could not find any LANGUAGES codes in i18n.ts - the regex needs updating, not deleting');
}

/** Every navigable destination, in the header's own order and grouping. */
export const SCREENS = [
  'Map', 'Combat', 'Gathering', 'World Boss', 'Boosts', 'The Delve',
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

/**
 * Which language to boot the browser into. Unset (the default) means
 * English, i18n.ts's own default - set FOLKIDLE_E2E_LANG to one of En, Cs,
 * De, Pl, Es, Fr to sweep the geometry checkers in another language, since
 * none of clipping-check.mjs / overlap-check.mjs / touch-check.mjs are
 * language-aware on their own and German/Polish text tends to run longer
 * than English.
 */
export const E2E_LANG = process.env.FOLKIDLE_E2E_LANG || null;
// Modul: an unvalidated FOLKIDLE_E2E_LANG silently no-ops. i18n.ts's
// initialLanguage() only recognises an exact match against LANGUAGES' `code`
// values ('En', 'Cs', ...), and the server's OWN resolver spells the same
// languages lowercase ("es", "fr" in ContentRegistry.cs) - so
// `FOLKIDLE_E2E_LANG=de` is the natural typo, and it used to fall through to
// English with the geometry checker reporting a clean sweep of a language it
// never actually rendered.
if (E2E_LANG && !LANGUAGE_CODES.includes(E2E_LANG)) {
  throw new Error(
    `FOLKIDLE_E2E_LANG='${E2E_LANG}' is not one of: ${LANGUAGE_CODES.join(', ')}`,
  );
}

/** A browser and a page, with console/pageerror collection wired up. */
export async function open({ width = 1500, height = 1000 } = {}) {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width, height } });
  if (E2E_LANG) {
    // Modul: i18n.ts's initLanguage() runs at App.svelte's component-script
    // top level, before any onMount - so setting localStorage AFTER
    // page.goto (e.g. via page.evaluate) would run after the app already
    // read it and defaulted to English. addInitScript runs before every
    // script on the page, including the first one the bundle runs.
    // Modul: addInitScript takes exactly ONE arg - passing STORAGE_KEY and
    // E2E_LANG as two positional arguments would silently pass `undefined`
    // as the second, so both travel together in one object instead.
    await page.addInitScript(
      ({ key, lang }) => localStorage.setItem(key, lang),
      { key: STORAGE_KEY, lang: E2E_LANG },
    );
  }
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
  // Modul: THE BADGE, again. go() below learned that "Mail" renders as "Mail 3"
  // with something unclaimed; this read the raw text, so every geometry check
  // printed "FAIL nav has no button for: Mail" and then "ok Mail" on the next
  // line - whenever a previous exercise run had left the fixture a message.
  const navLabels = await page.evaluate(() =>
    [...document.querySelectorAll('header button')]
      .map((b) => b.textContent.trim().replace(/\s*\d+$/, ''))
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
  // Modul: a destination's button may carry a BADGE. Mail renders as "Mail 3"
  // when something is unclaimed, so `exact: true` matched it on an empty
  // mailbox and timed out the moment the fixture had a message waiting - which
  // took every geometry check down with it, on a client with nothing wrong.
  // Anchored at both ends so "Guild" still cannot match "Guild Ops".
  const badged = new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(\\s+\\d+)?$`);
  const target = page.locator('header').getByRole('button', { name: badged }).first();
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
