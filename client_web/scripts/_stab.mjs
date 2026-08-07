import { chromium } from 'playwright';
const b = await chromium.launch();
const page = await b.newPage({ viewport: { width: 1400, height: 950 } });
await page.goto('http://localhost:5173/', { waitUntil: 'domcontentloaded' });
await page.getByRole('button', { name: 'Sign in' }).click();
await page.locator('input[type="email"]').fill('dev@folkidle.local');
await page.locator('input[type="password"]').fill('FolkIdleDev123!');
await page.getByRole('button', { name: 'Sign in', exact: true }).last().click();
await page.waitForSelector('text=Combat', { timeout: 20000 });
await page.waitForTimeout(2500);
const d = Date.now() + 6000;
while (Date.now() < d) { const c = page.getByRole('button', { name: 'Continue', exact: true });
  if ((await c.count()) > 0) { await c.first().click(); break; } await page.waitForTimeout(250); }
await page.locator('header').getByRole('button', { name: 'Map', exact: true }).first().click();
await page.waitForTimeout(1500);
try {
  await page.locator('.place').filter({ hasText: 'Market' }).first().click({ timeout: 6000 });
  console.log('CLICKED OK');
} catch (e) {
  console.log('CLICK FAILED:', String(e.message).split(String.fromCharCode(10)).slice(0,6).join(' | '));
}
await b.close();
