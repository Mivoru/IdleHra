// Modul: A BARE "g" AFTER A COMPACTED NUMBER IS A MASS UNIT.
//
// formatNumber compacts above 100,000 and its suffix is a letter, so
// `{formatNumber(x)}g` renders 150,000 gold as "150 kg" and 1.24M as
// "1.24 Mg". Money.svelte fixed this once, for its own call sites, after a
// player asked whether "Mg" was this game's word for a million - and 27 other
// places kept writing the bare g by hand: market prices, forge fees, mail.
//
// The fix is <Money> in markup and formatGold() in a string. This is the grep
// that stops a 28th site: a formatNumber(...) call closed by `}` and followed
// by `g` or ` g` that is not the start of a word ("gold" is fine).
import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');

function sourceFiles(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...sourceFiles(full));
    else if (/\.(svelte|ts)$/.test(entry.name)) out.push(full);
  }
  return out;
}

/** Every `formatNumber(...)` whose call is followed by `}g` / `} g` (not a word). */
function bareGoldSuffixes(text: string): string[] {
  const hits: string[] = [];
  const needle = 'formatNumber(';
  let at = text.indexOf(needle);
  while (at >= 0) {
    // Walk to the matching close paren - arguments nest (Math.max(1, x)).
    let depth = 0;
    let end = -1;
    for (let i = at + needle.length - 1; i < text.length; i++) {
      if (text[i] === '(') depth++;
      else if (text[i] === ')' && --depth === 0) {
        end = i;
        break;
      }
    }
    if (end < 0) break;
    const tail = text.slice(end + 1, end + 5);
    if (/^\} ?g(?![a-zA-Z])/.test(tail)) hits.push(text.slice(at, end + 4));
    at = text.indexOf(needle, end);
  }
  return hits;
}

describe('gold is never a bare g after formatNumber', () => {
  const files = sourceFiles(srcRoot);

  it('finds source files at all - an empty sweep proves nothing', () => {
    expect(files.length).toBeGreaterThan(50);
  });

  it('recognises the pattern it exists to catch', () => {
    expect(bareGoldSuffixes('<b>{formatNumber(fee)}g</b>')).toHaveLength(1);
    expect(bareGoldSuffixes('{formatNumber(Math.max(1, x))} g/h')).toHaveLength(1);
    expect(bareGoldSuffixes('`for ${formatNumber(gold)}g.`')).toHaveLength(1);
    expect(bareGoldSuffixes('{formatNumber(x)} gold')).toHaveLength(0);
    expect(bareGoldSuffixes('{formatNumber(x)} XP/h')).toHaveLength(0);
  });

  it('routes every gold amount through <Money> or formatGold()', () => {
    // format.ts is exempt: formatGold() is the one place allowed to write the
    // terse g, and only below the compaction threshold.
    const offenders = files.filter((file) => !/[\\/]lib[\\/]ui[\\/]format\.ts$/.test(file)).flatMap((file) =>
      bareGoldSuffixes(readFileSync(file, 'utf8')).map((hit) => `${relative(srcRoot, file)}: ${hit}`),
    );
    expect(offenders, `use <Money amount=...> in markup, formatGold() in a string:\n${offenders.join('\n')}`).toEqual([]);
  });
});
