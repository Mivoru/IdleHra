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
    version: '1.15.0',
    date: '2026-10-10',
    sections: [
      {
        title: 'Your stats',
        items: [
          'The Character screen has a new Stats tab. It shows every stat of the character you picked - attack, crit, attack speed, lifesteal, block, dodge, health, armour, loot luck, gold and XP bonuses, and gathering speed and yield - with gear, pet, skills and bloodline included.',
          'Where a stat has a limit, the tab shows it, and turns red when you have reached it. Attack speed stops at 60%, for example, so you know when to improve something else.',
        ],
      },
      {
        title: 'Samhain',
        items: [
          'Pumpkins drop five times as often as before. Pumpkins you already have are kept.',
          'The event shop now lists the two pets you cannot buy: the Witch, who can come with any pumpkin you earn (1 in 10,000 per pumpkin), and the Mini Vampire, the reward for beating the sixth winter.',
          'The bonus that said "drop chance" was always loot luck: it makes rarer drops likelier, not drops more frequent. It now says so.',
        ],
      },
      {
        title: 'Wiki',
        items: [
          'New pages on seasonal events, The Cailleach, pets, Boss Ascension, orders, Workshop commissions and the Great Works.',
        ],
      },
    ],
  },
  {
    version: '1.14.0',
    date: '2026-10-10',
    sections: [
      {
        title: 'The Cailleach',
        items: [
          'You now fight The Cailleach in her own window on the World Boss screen. Before, Fight sent you to the Combat screen, where you saw the region boss she borrows her strength from.',
          'When you win, the window tells you what you got and offers the next winter. When you lose, you can try again.',
          'Each fight is one attempt. When it ends, your character goes back to what it was doing before.',
        ],
      },
    ],
  },
  {
    version: '1.13.0',
    date: '2026-10-10',
    sections: [
      {
        title: 'Samhain',
        items: [
          'Pets have their own slot on the Character screen: the twelfth tile in the Gear grid, beside the tools. Tap it to choose who follows your character.',
          'Pumpkins are much rarer now - 0.1% a kill and 0.02% a harvest - so a busy house can afford a few pets and avatars over the whole event, not the whole shop.',
          'Everyone starts again from zero pumpkins, so nobody keeps what the old rate paid.',
          'The Witch now comes with about one pumpkin in 2,000.',
        ],
      },
    ],
  },
  {
    version: '1.12.0',
    date: '2026-10-10',
    sections: [
      {
        title: 'Samhain',
        items: [
          'Samhain runs until the night of 1 November; the shop stays open three days after.',
          'Pumpkins drop at half the rate of the first evening, and time away counts only from the start of the event. To keep it fair, everyone starts again from zero.',
          'A Samhain window now explains the event once: what pumpkins buy, where the shop and pets are, and where to find The Cailleach.',
          'The event shop is split into Avatars and Pets, and the avatars are framed on their faces.',
          'On a phone, the pumpkins and the Event shop button have their own row under the header.',
          "The Cailleach's six winters are numbered; the region boss each one matches is in its description.",
        ],
      },
    ],
  },
  {
    version: '1.11.0',
    date: '2026-10-10',
    sections: [
      {
        title: 'Samhain: pets and The Cailleach',
        items: [
          'Pets have arrived in the event shop. Each one gives the character it follows a bonus - give one to every character on the Character screen. Pets stay yours after the event.',
          'Keep an eye out for a Witch: any kill or harvest during Samhain might bring her.',
          'The Cailleach has come down from the mountains. Six winters await on the World Boss screen, each as hard as a region boss the first time you met it. Her first fall at each winter pays diamonds, gold and pumpkins - and the sixth, the Mini Vampire.',
        ],
      },
      {
        title: 'Fixes',
        items: ['Equipping gear no longer resets your tools until you sign in again.'],
      },
    ],
  },
  {
    version: '1.10.0',
    date: '2026-10-09',
    sections: [
      {
        title: 'Samhain',
        items: [
          'Samhain has begun. Every kill and every harvest can drop a pumpkin, while you play and while you are away.',
          'Tap the pumpkin beside your gold to open the event shop. Eight Samhain avatars are on sale, and pets are coming later in the event.',
          'The Cailleach stirs. Read her story on the event screen.',
        ],
      },
    ],
  },
  {
    version: '1.9.0',
    date: '2026-10-09',
    sections: [
      {
        title: 'Fixes',
        items: [
          'The Book of Deeds checks the weapon and the armour set on the characters you actually play, not on someone resting in the village.',
          'The closest goal on Home no longer points back into a chapter you have already sealed.',
        ],
      },
    ],
  },
  {
    version: '1.8.0',
    date: '2026-10-09',
    headline: 'Titles you can see.',
    sections: [
      {
        title: 'Titles',
        items: [
          'The title you wear now shows next to your name in chat, on the leaderboards, in your guild and on your profile, each in its own colour.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'The chest cellar on a phone shows the tall painting instead of a squashed wide one.',
          'Your profile shows the three characters in your roster, not a character you swapped out.',
        ],
      },
    ],
  },
  {
    version: '1.7.0',
    date: '2026-10-08',
    headline: 'Opening a chest is an event now.',
    sections: [
      {
        title: 'Chests',
        items: [
          'Opening a cosmetic chest takes you down to the cellar: tap (or click) the chest three times to shake it open, and the light that bursts out shows what you got.',
          'A chest’s rarity is now its odds, not a promise. A Common chest is mostly Common but can surprise you; a Legendary chest gives a Legendary 60% of the time and never anything below Rare. Each chest shows its odds before you open it.',
          'After opening you can wear the prize at once or open the next chest straight away.',
        ],
      },
    ],
  },
  {
    version: '1.6.0',
    date: '2026-10-08',
    headline: 'Your characters each have one job, and the one you pick is the one that goes.',
    sections: [
      {
        title: 'Characters at work',
        items: [
          'One character per kind of work: only one of your characters can fight at a time, one can chop wood, one can mine, one can fish and one can craft. Pick a different job for the others.',
          'The character you choose on the Character screen is now the one that fights, gathers or crafts. Fight used to send your first character whoever you had picked.',
          'Combat has a "Who fights" choice at the top and shows that character’s own health and monster.',
          'A character put to work on crafting shows a progress bar, and the gathering bar finally moves.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'Your maximum health no longer drops and creeps back up. Each character keeps its own, and a character coming back from work or from being away starts the next fight at full health.',
          'A new drop is compared with what your fighting character wears, and Wear puts it on that character. It used to say a slot was empty when it was not.',
        ],
      },
      {
        title: 'Rebirth',
        items: [
          'Rebirth now warns that it cannot be undone and asks you to type REBIRTH before it goes through, so a double tap can no longer reset your run by accident.',
        ],
      },
    ],
  },
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
