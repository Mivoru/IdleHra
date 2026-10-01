import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { splitHall, claimedCount, hallBadge, compareCullRank, type HallRankable } from '../src/lib/ui/hallSections';

const here = dirname(fileURLToPath(import.meta.url));
const rules = readFileSync(
  join(here, '..', '..', 'server', 'FolkIdle.Server', 'Engine', 'HallOfAncestorsRules.cs'),
  'utf8',
);

let seq = 0;
function member(over: Partial<HallRankable & { PlayableSlot: number }> = {}): HallRankable & { PlayableSlot: number } {
  seq += 1;
  return {
    CharacterId: `id-${String(seq).padStart(3, '0')}`,
    IsMainCharacter: false,
    IsKept: false,
    IsEpicMutation: false,
    GenerationIndex: 0,
    WouldCarry: true,
    AptitudeStrength: 1,
    AptitudeSkill: 1,
    AptitudeEndurance: 1,
    AptitudeFortune: 1,
    PlayableSlot: -1,
    ...over,
  };
}

describe('Hall of Ancestors sections (task 104)', () => {
  it('cuts the sections on the server WouldCarry, never on a re-ranking', () => {
    // A strong member the server says is lost stays lost: the client does not
    // second-guess the cull.
    const strongButLost = member({ AptitudeStrength: 40, WouldCarry: false });
    const weakButCarried = member({ WouldCarry: true });
    const { carried, lost } = splitHall([strongButLost, weakButCarried]);
    expect(carried).toEqual([weakButCarried]);
    expect(lost).toEqual([strongButLost]);
  });

  it('orders the carried section main, marked, total, epic, later generation', () => {
    const main = member({ IsMainCharacter: true });
    const kept = member({ IsKept: true });
    const strong = member({ AptitudeStrength: 20 });
    const epic = member({ IsEpicMutation: true });
    const late = member({ GenerationIndex: 3 });
    const plain = member();
    const shuffled = [plain, late, epic, strong, kept, main];
    expect(splitHall(shuffled).carried).toEqual([main, kept, strong, epic, late, plain]);
  });

  it('sorts the lost section by total, highest first', () => {
    const a = member({ WouldCarry: false, AptitudeSkill: 9 });
    const b = member({ WouldCarry: false, AptitudeSkill: 30 });
    const c = member({ WouldCarry: false });
    expect(splitHall([a, b, c]).lost).toEqual([b, a, c]);
  });

  it('counts the main character and every mark as a claim on a slot', () => {
    expect(claimedCount([member({ IsMainCharacter: true }), member({ IsKept: true }), member()])).toBe(2);
  });

  it('never lets a mark that will not carry read as safe', () => {
    expect(hallBadge(member({ IsKept: true, WouldCarry: false, PlayableSlot: 0 }))).toEqual({
      kind: 'over-cap',
      text: 'Kept - over the cap',
    });
    expect(hallBadge(member({ PlayableSlot: 1 })).text).toBe('In slot 2');
    expect(hallBadge(member({ IsMainCharacter: true })).kind).toBe('first');
    expect(hallBadge(member()).kind).toBe('bench');
    expect(hallBadge(member({ WouldCarry: false })).kind).toBe('lost');
  });

  // Modul: the display order is a copy of HallOfAncestorsRules.Compare, so it
  // is pinned to the server's sequence of comparisons, not to a comment.
  it('mirrors the order of comparisons in HallOfAncestorsRules.Compare', () => {
    const start = rules.indexOf('private static int Compare(');
    expect(start).toBeGreaterThan(0);
    const body = rules.slice(start, rules.indexOf('\n        }', start));
    const fields = [...body.matchAll(/if \(a\.(\w+) != b\.\1\)/g)].map((m) => m[1]);
    expect(fields).toEqual(['IsMainCharacter', 'IsKept', 'AptitudeTotal', 'IsEpicMutation', 'GenerationIndex']);
    // And the client function exists to be compared against.
    expect(typeof compareCullRank).toBe('function');
  });
});
