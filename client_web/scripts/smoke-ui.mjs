// Drives the real UI in a real browser - the thing Unity never allowed
// without the MCP harness, and the concrete reason the port plan lists
// Playwright as a Phase 1 benefit.
import { chromium } from 'playwright';

const OUT = process.argv[2] ?? 'shot';
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

const errors = [];
page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));

await page.goto('http://localhost:5173/', { waitUntil: 'networkidle' });
await page.screenshot({ path: `${OUT}-1-login.png` });
console.log('login screen:', await page.locator('h1').first().textContent());

// Play as guest -> real HTTP login -> real WebSocket JSON handshake.
await page.getByRole('button', { name: 'Play as guest' }).click();
await page.waitForSelector('text=Combat', { timeout: 20000 });
await page.waitForFunction(
  () => !document.body.innerText.includes('Waiting for the first state snapshot'),
  { timeout: 20000 },
);
await page.screenshot({ path: `${OUT}-2-connected.png` });
console.log('connection phase:', await page.locator('.phase').textContent());

// Fight the first canonical monster.
const firstFight = page.getByRole('button', { name: 'Fight' }).first();
await firstFight.click();
await page.waitForSelector('text=Fighting', { timeout: 20000 });
console.log('in combat:', (await page.locator('.hpblock').nth(1).innerText()).replace(/\n/g, ' | '));

// Watch the interpolated monster HP bar actually move between snapshots.
const widths = [];
for (let i = 0; i < 12; i++) {
  widths.push(
    await page.locator('.bar-fill').nth(1).evaluate((el) => el.style.width),
  );
  await page.waitForTimeout(120);
}
console.log('monster hp bar widths:', widths.join(' '));
const distinct = new Set(widths).size;
console.log('distinct widths across ~1.4s:', distinct);

// Wait for loot to land, proving the ResponseLootDrop path reaches the DOM.
try {
  await page.waitForFunction(
    () => !document.body.innerText.includes('Loot received\nNothing yet'),
    { timeout: 60000 },
  );
  console.log('loot panel populated');
} catch {
  console.log('no loot within 60s');
}

await page.screenshot({ path: `${OUT}-3-combat.png`, fullPage: true });

console.log('console errors:', errors.length ? errors : 'none');
await browser.close();
