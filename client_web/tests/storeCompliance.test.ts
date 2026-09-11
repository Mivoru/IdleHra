// Modul: THE TWO PAGES A STORE REVIEWER OPENS, AND NOTHING EVER READ THEM.
//
// public/privacy.html and public/delete-account.html are served as real files
// by Caddy's static handle. Play requires the deletion route to work for
// somebody who has ALREADY UNINSTALLED - an in-app path alone is a rejection -
// so these two are part of the product, not documentation about it.
//
// Both shipped with the literal string `CONTACT_EMAIL` where the address
// belongs, from the day they were written until 2026-09-11. Both files said so
// in an HTML comment, which is the most reliable way there is to be ignored:
// a comment is a note to a person who is already reading the file, and nobody
// reads a finished page again.
//
// So the note became this.
import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const PAGES = ['privacy.html', 'delete-account.html'] as const;

/** A plain address, not an href - both pages print it as text for a human. */
const EMAIL = /[\w.+-]+@[\w-]+\.[\w.]+/;

describe('the pages a store reviewer opens', () => {
  for (const page of PAGES) {
    const html = readFileSync(join(root, 'public', page), 'utf8');

    it(`${page} names a real contact address`, () => {
      // The placeholder is named explicitly rather than caught by the regex
      // below, so the failure says which mistake was made.
      expect(html, 'the CONTACT_EMAIL placeholder is back').not.toMatch(/>\s*CONTACT_EMAIL/);
      expect(html).toMatch(EMAIL);
    });
  }

  it('quotes the SAME address on both pages', () => {
    // Two addresses is the failure mode that survives the check above: one
    // page updated, the other left behind, and the store reads whichever it
    // reads. Comments are stripped first - they discuss the history of the
    // placeholder and would otherwise be mined for addresses that are not the
    // page's own.
    const addresses = PAGES.map((p) => {
      const body = readFileSync(join(root, 'public', p), 'utf8').replace(/<!--[\s\S]*?-->/g, '');
      return body.match(EMAIL)?.[0];
    });

    expect(addresses[0]).toBeDefined();
    expect(addresses[1]).toBe(addresses[0]);
  });

  it('delete-account.html describes the in-app route that actually exists', () => {
    // Settings.svelte gates erasure behind typing PURGE_PHRASE. The page tells
    // the player to type "the confirmation phrase it asks for", so the two
    // agree today - this fails if the in-app path is ever removed while the
    // page goes on promising it, which is the shape of defect this repository
    // calls "a thoroughly wired read side is not evidence a feature exists".
    const settings = readFileSync(join(root, 'src', 'routes', 'Settings.svelte'), 'utf8');
    expect(settings).toMatch(/PURGE_PHRASE\s*=/);
    expect(settings).toMatch(/triggerGdprPurge/);
  });
});
