import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// Modul: THE SITE SENDS `script-src 'self'` (ops/oracle/caddy/Caddyfile), so
// an inline script in index.html is refused by the browser - silently, as far
// as anything but the console is concerned. The one that used to live there
// drove the loading screen, and refused it would leave the opaque picture
// over the game for ever. This keeps index.html to external scripts and JSON
// data blocks (which CSP does not execute), and keeps the policy in the
// Caddyfile from losing the directives PageSpeed asked for.
const root = resolve(__dirname, '..');

describe('content security policy', () => {
  it('index.html has no inline executable script', () => {
    const html = readFileSync(resolve(root, 'index.html'), 'utf8');
    const tags = [...html.matchAll(/<script\b([^>]*)>/g)].map((m) => m[1]);
    expect(tags.length).toBeGreaterThan(0);
    for (const attrs of tags) {
      const external = /\bsrc=/.test(attrs);
      const data = /type="application\/ld\+json"/.test(attrs);
      expect(external || data, `inline <script${attrs}> would be blocked by script-src 'self'`).toBe(true);
    }
  });

  it('the Caddyfile policy keeps script-src, object-src and frame-ancestors', () => {
    const caddy = readFileSync(resolve(root, '..', 'ops', 'oracle', 'caddy', 'Caddyfile'), 'utf8');
    const policy = caddy.match(/Content-Security-Policy "([^"]+)"/);
    expect(policy).not.toBeNull();
    expect(policy![1]).toContain("script-src 'self'");
    expect(policy![1]).toContain("object-src 'none'");
    expect(policy![1]).toContain("frame-ancestors 'none'");
  });
});
