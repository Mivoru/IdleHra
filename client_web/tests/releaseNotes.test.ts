import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { RELEASE_NOTES, notesNewerThan, shouldShowNotes, type Release } from '../src/lib/ui/releaseNotes';

const here = dirname(fileURLToPath(import.meta.url));
const pkg = JSON.parse(readFileSync(join(here, '..', 'package.json'), 'utf8'));

// Modul: RELEASE NOTES ARE A PROMISE THAT ROTS.
//
// Every project starts one and every project stops writing it, because nothing
// breaks when you skip an entry. So the version in package.json and the newest
// entry here are pinned to each other: bumping the version without writing what
// changed fails CI, and writing notes for a version nobody shipped fails it too.
// That is the only mechanism that keeps a changelog alive for longer than a
// month.
//
// There is deliberately NO separate CHANGELOG.md. Two copies of one truth is
// this repo's dominant bug class, and a changelog that has drifted from what
// players are actually shown is exactly that shape.
describe('release notes', () => {
  it('has an entry for the version being shipped', () => {
    expect(RELEASE_NOTES.length).toBeGreaterThan(0);
    expect(RELEASE_NOTES[0].version).toBe(pkg.version);
  });

  it('is ordered newest first', () => {
    const versions = RELEASE_NOTES.map((r: Release) => r.version);
    const sorted = [...versions].sort(compareVersions).reverse();
    expect(versions).toEqual(sorted);
  });

  it('never repeats a version', () => {
    const versions = RELEASE_NOTES.map((r: Release) => r.version);
    expect(new Set(versions).size).toBe(versions.length);
  });

  it('says something in every entry', () => {
    for (const release of RELEASE_NOTES) {
      expect(release.date, `${release.version} has no date`).toMatch(/^\d{4}-\d{2}-\d{2}$/);
      expect(release.sections.length, `${release.version} has no sections`).toBeGreaterThan(0);
      for (const section of release.sections) {
        expect(section.title.length, `${release.version} has an unnamed section`).toBeGreaterThan(0);
        expect(section.items.length, `${release.version}/${section.title} is empty`).toBeGreaterThan(0);
        for (const item of section.items) {
          // A note nobody can read is not a note. Catches placeholders.
          expect(item.length, `${release.version}: "${item}" is too short to mean anything`).toBeGreaterThan(12);
          expect(item).not.toMatch(/^(TODO|TBD|WIP)/i);
        }
      }
    }
  });

  // Modul: PLAYER PROSE, NOT COMMIT PROSE. This repo's commit subjects are
  // written for whoever debugs the code next - "the gate read a column nothing
  // ever wrote" - and are actively unhelpful to a player. A note that mentions
  // internals is a note written for the wrong audience.
  it('is written for players rather than for the repository', () => {
    const jargon = /\b(refactor|migration|endpoint|packet|nullable|backfill|regression|null|SQL|struct|enum|API|opcode|commit)\b/i;
    for (const release of RELEASE_NOTES) {
      for (const section of release.sections) {
        for (const item of section.items) {
          expect(item, `"${item}" reads like a commit message`).not.toMatch(jargon);
        }
      }
    }
  });
});

describe('which notes a player is shown', () => {
  // THE CASE THAT MATTERS MOST. Somebody installing for the first time has not
  // missed anything, so showing them a changelog for a game they have never
  // played is noise at the worst possible moment - the first thirty seconds.
  it('shows a brand-new player nothing at all', () => {
    expect(shouldShowNotes(null, '1.4.0')).toBe(false);
    expect(shouldShowNotes(undefined, '1.4.0')).toBe(false);
    expect(shouldShowNotes('', '1.4.0')).toBe(false);
  });

  it('shows nothing when the player has already seen this version', () => {
    expect(shouldShowNotes('1.4.0', '1.4.0')).toBe(false);
  });

  it('shows the notes when the version has moved on', () => {
    expect(shouldShowNotes('1.3.0', '1.4.0')).toBe(true);
  });

  // A player who somehow holds a NEWER stored version than the bundle - a
  // rollback, or two tabs on different builds - is not shown a changelog for
  // something they have already had.
  it('shows nothing when the stored version is ahead', () => {
    expect(shouldShowNotes('1.5.0', '1.4.0')).toBe(false);
  });

  it('gathers every release the player missed, not only the newest', () => {
    // Driven off the OLDEST entry rather than a literal, so this keeps meaning
    // the same thing when the list is one long and when it is thirty.
    const oldest = RELEASE_NOTES[RELEASE_NOTES.length - 1].version;
    const missed = notesNewerThan(oldest);

    expect(missed.length).toBe(RELEASE_NOTES.length - 1);
    expect(missed.map((r: Release) => r.version)).not.toContain(oldest);
  });

  it('gathers nothing for somebody already current', () => {
    expect(notesNewerThan(RELEASE_NOTES[0].version)).toHaveLength(0);
  });
});

/** Numeric semver compare, so 1.10.0 sorts above 1.9.0 rather than below it. */
function compareVersions(a: string, b: string): number {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) {
    if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pa[i] ?? 0) - (pb[i] ?? 0);
  }
  return 0;
}
