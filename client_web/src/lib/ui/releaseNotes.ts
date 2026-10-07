/**
 * What changed, in the words a player would use.
 *
 * THIS FILE IS THE CHANGELOG. There is deliberately no CHANGELOG.md beside it:
 * two copies of one truth is this repo's dominant bug class, and a changelog
 * that has drifted from what players are actually shown is exactly that shape.
 * Anything that wants release notes reads this.
 *
 * WRITE FOR THE PLAYER, NOT FOR THE REPOSITORY. This project's commit subjects
 * are written for whoever debugs the code next - "the gate read a column
 * nothing ever wrote" - and mean nothing to somebody who just wants to know
 * whether their game got better. `releaseNotes.test.ts` fails on internal
 * jargon for that reason.
 *
 * The newest entry's version MUST equal package.json's, and the test enforces
 * it in both directions - so a release without notes fails CI, and notes for a
 * version nobody shipped fail it too. That pinning is the only thing that keeps
 * a changelog alive past its first month.
 */

export interface ReleaseSection {
  title: string;
  items: string[];
}

export interface Release {
  version: string;
  /** ISO date, YYYY-MM-DD. */
  date: string;
  /** A line shown under the heading. Optional - most releases do not need one. */
  headline?: string;
  sections: ReleaseSection[];
}

/** Newest first. */
export const RELEASE_NOTES: readonly Release[] = [
  {
    version: '1.5.0',
    date: '2026-10-07',
    headline: 'A harder road to the top, and a guide for everything on it.',
    sections: [
      {
        title: 'Challenge',
        items: [
          'Regions 3, 4 and 5 are tougher: their monsters and bosses have more health and hit harder (a quarter more in region 3, half again in region 4, double in region 5), and pay more XP and gold to match. A boss’s first clear is exactly as hard as before; farming it again is not.',
          'Malakor’s Ascension ladder is far steeper. Steps 1-3 are a real fight for a level-100 hero, step 5 and up needs a fully built endgame character, and step 10 is meant to be all but out of reach.',
          'Levels come more slowly, and every boss hits harder than it did a week ago.',
          'Fusing costs real money now: from about a million gold for a middling piece to hundreds of millions at the top.',
        ],
      },
      {
        title: 'Food',
        items: [
          'From region 2 on, a fighting hero eats a ration now and then whether hurt or not - one fish every 6 to 10 seconds. A ration heals nothing; it is upkeep.',
          'A ration wants a fish from the region you fight in or a later one. Older fish still work, at two for every region they are behind.',
          'With nothing to eat your hero goes hungry: it keeps fighting, but kills pay half XP and gold until the larder is stocked again. Region 1 eats no rations.',
        ],
      },
      {
        title: 'Learning the game',
        items: [
          'A quest line on Home walks you through every system, one hands-on step at a time - the chest, auto-sell, fusing, rerolling, the village, the market, breeding, inheritance, the Delve, the world boss, Ascension and Rebirth. Each step pays a reward scaled to your region.',
          'What’s New (this window) now opens after the Welcome Back summary instead of on top of it.',
        ],
      },
      {
        title: 'Fixes and comfort',
        items: [
          'Chat on phones keeps its layout when the keyboard opens, the newest message is no longer cut off at the bottom, and names sit apart from the text.',
          'The Home cards line up in one column instead of overlapping.',
          'The game uses far less mobile data: updates from the server are compressed, about thirty times smaller.',
          'The unobtainable boost items are gone from the store.',
        ],
      },
    ],
  },
  {
    version: '1.4.0',
    date: '2026-09-13',
    headline: 'Breeding works. It never has before.',
    sections: [
      {
        title: 'Breeding',
        items: [
          'Breeding is possible at last. It asked for a hero level that no character in the game could ever reach, so every attempt was refused in silence - for everyone, since the day the game opened.',
          'Any grown adult can now be a parent. Build the Breeding Grounds and that is the whole requirement.',
          'The Breeding Grounds finally does something as it grows: at level 4 you may choose one aptitude to breed FOR, and a chosen one always keeps the better parent’s value instead of leaving it to chance. Level 7 buys a second choice, level 10 a third.',
          'A better Inn now brings better people. A well-built Inn can attract newcomers of real quality, where before it quietly stopped short.',
          'The screen tells you what it is doing. One hero, one partner, and a plain answer whenever a pairing is refused.',
        ],
      },
      {
        title: 'Your characters',
        items: [
          'Everyone has a name. Your heroes were shown to you as strings of letters and numbers, which made your own roster impossible to read.',
          'Heroes last about a week of play instead of three hours before age slows them down, and old age costs far less than it used to.',
          'Every character you already own has been given back the strength that age had taken.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'A newly granted hero is a grown adult straight away rather than a child for its first hour.',
          'The end-of-season reward now counts how far you actually got, instead of how many characters you happened to own.',
          'The village build timer is a stopwatch again rather than a spinning coin.',
        ],
      },
    ],
  },
];

/**
 * Every release strictly newer than `seen`, newest first.
 *
 * All of them, not only the latest: somebody returning after three updates
 * should see what they missed rather than the tail of it.
 */
export function notesNewerThan(seen: string): readonly Release[] {
  return RELEASE_NOTES.filter((release) => compareVersions(release.version, seen) > 0);
}

/**
 * Whether to open the window at all.
 *
 * A MISSING STORED VERSION MEANS A NEW PLAYER, and a new player has missed
 * nothing - showing them a changelog for a game they have never played is noise
 * in the first thirty seconds, which is the worst place this game can spend a
 * player's attention. The caller records the current version silently instead.
 */
export function shouldShowNotes(seen: string | null | undefined, current: string): boolean {
  if (!seen) return false;
  return compareVersions(current, seen) > 0;
}

/**
 * Numeric semver compare. Numeric rather than lexicographic so that 1.10.0
 * sorts above 1.9.0 - the comparison every hand-rolled version check gets
 * wrong, usually about eight months in.
 */
export function compareVersions(a: string, b: string): number {
  const pa = a.split('.').map((n) => Number.parseInt(n, 10) || 0);
  const pb = b.split('.').map((n) => Number.parseInt(n, 10) || 0);
  for (let i = 0; i < 3; i++) {
    if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pa[i] ?? 0) - (pb[i] ?? 0);
  }
  return 0;
}
