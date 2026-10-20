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
    version: '1.28.0',
    date: '2026-10-20',
    headline: 'Gear stays with its slot, and a fairer rebirth.',
    sections: [
      {
        title: 'Gear and tools',
        items: [
          'Gear belongs to the slot now. When you put a new person into a slot, they take over the armour and tools that were worn there.',
          'Someone sent to rest in the village wears nothing - their gear goes back to the chest. Axes, pickaxes and rods that were stuck on a resting character are back in your chest.',
          'Every character gathers with their own tools. A woodcutter in slot 2 uses the axe they wear, not the first character’s.',
          'Putting gear on and taking it off is much quicker while you fight.',
          'Gear from region 2 onwards asks for 25 points of its attribute per region (it was 20) - 100 for region 5.',
        ],
      },
      {
        title: 'Rebirth',
        items: [
          'The larder empties with a rebirth, like the chest. A new life starts with the starter fish only.',
          'The +50% XP bonus now lasts up to the level your last life reached, not your best ever.',
          'Gear kept in the Vault waits until the new life has killed a monster in the region it comes from.',
        ],
      },
      {
        title: 'Chest',
        items: [
          'A very full chest offers to clear itself: choose regions and a rarity - for example regions 1 to 3, Epic or lower - and see how many pieces that sells and how much faster the game loads. You can make it sell those automatically from then on.',
          'The Chest sweep never takes locked or Vault pieces.',
        ],
      },
    ],
  },
  {
    version: '1.27.0',
    date: '2026-10-14',
    headline: 'Easier to read at a glance.',
    sections: [
      {
        title: 'Combat',
        items: [
          'The monster list marks its best pick: the most XP an hour among the monsters you can farm without food. Its Fight button is the filled one.',
          'When you are not fighting, the top of the screen offers that fight in one tap.',
          'Each monster now leads with what it pays - XP and gold an hour - and keeps its health and kill time quieter beside it.',
        ],
      },
      {
        title: 'Everywhere',
        items: [
          'The small grey explanations across the game are a little larger and more readable.',
          'The Attributes tab starts with its heading instead of a footnote.',
        ],
      },
    ],
  },
  {
    version: '1.26.0',
    date: '2026-10-14',
    headline: 'The game moves a little more like it is alive.',
    sections: [
      {
        title: 'Feel',
        items: [
          'Screens now fade into each other instead of snapping.',
          'Your gold and diamonds count up to their new value, so you can see what a kill or a sale was worth.',
          'A newborn child is announced with the same light as a rare drop.',
          'If your device is set to reduce motion, all of this stays off.',
        ],
      },
    ],
  },
  {
    version: '1.25.0',
    date: '2026-10-14',
    headline: 'Your bloodline is now the long road - twenty rebirths of it.',
    sections: [
      {
        title: 'Rebirth',
        items: [
          'You can now be reborn twenty times. The first ten still pay skill points, Renown, the avatars and the Phoenix.',
          'Every rebirth raises your bloodline ceiling - the highest an aptitude can be bred: 20 before your first rebirth, 35 at the tenth, 50 at the twentieth.',
          'The Rebirth screen shows who of your line will carry into the next run.',
          'There are no more seasons. A run ends only when you choose to be reborn.',
        ],
      },
      {
        title: 'Breeding',
        items: [
          'The Breeding screen is redrawn: pick a hero and a partner and the child is drawn on one bar per aptitude, with one line saying where it beats your hero.',
          'Marry, Raise, Carry: the three steps from a wedding to an heir are shown at the top, with what is waiting at each.',
          'A newborn is announced with its aptitudes and a button to the Hall of Ancestors, where children still to be fielded are marked.',
          'Breeding now appears in your quests just before your first rebirth, at level 40.',
        ],
      },
    ],
  },
  {
    version: '1.24.0',
    date: '2026-10-13',
    headline: 'Chernobog breaks out of his chest every Monday.',
    sections: [
      {
        title: 'World Boss',
        items: [
          'The world boss is Chernobog, the black god Svarog sealed in a forged chest. Below a quarter of his health he becomes the Black Serpent.',
          'Monday and Tuesday are registration: his health is hidden, and striking him (level 10 or higher) signs you up for the week.',
          'On Wednesday his health is revealed, sized to how hard the registered players hit him - so he can really be brought down. Fight on until Sunday either way.',
          'Weekly damage milestones pay the moment you reach them: gold measured in hours of your own income, up to 60 diamonds a week, the Svarog, Black Serpent and Chernobog avatars, two titles - and at a million damage the Black Serpent pet, +12% damage and +12% critical damage.',
          'If he falls, everyone registered gets two hours of their income and a Legendary chest on Sunday. The week’s top three wear Svarog’s Hammer, Dazhbog’s Light and Simargl’s Wing until the next Sunday.',
          'Holding back on Monday to keep him weak is noticed - and Chernobog will say so in World chat.',
        ],
      },
      {
        title: 'Daily bonus',
        items: [
          'The daily login gold now doubles in your second week and triples from your third, and stays there.',
        ],
      },
    ],
  },
  {
    version: '1.23.0',
    date: '2026-10-12',
    headline: 'A pet that finds you now says so.',
    sections: [
      {
        title: 'Pets',
        items: [
          'New: when a pet finds you - from a kill, the Witch, The Cailleach or the last rebirth - a window shows it, what it gives and how it is found, with a button to put it on a character straight away.',
          'A pet found while you were away is shown the next time you come back, once, on whichever device you use.',
          'A monster pet you already have says so too, and how many diamonds it left in your mail.',
          'Player profiles now show each character’s pets next to their gear.',
        ],
      },
    ],
  },
  {
    version: '1.22.0',
    date: '2026-10-11',
    headline: 'The Wiki, checked against the game.',
    sections: [
      {
        title: 'Wiki',
        items: [
          'New: a Pets section - every pet, its bonus and where it comes from, the second pet slot after five rebirths, and the diamonds a duplicate pays.',
          'Attributes now carry their real names (Might, Finesse, Vigour, Fortune), how each one grows, and every milestone.',
          'Fixed: diamonds cannot upgrade an affix - a gold reroll is the only way to a rarer affix.',
          'Fixed: boss strength before the first kill, the gear rarity each boss asks for, the cosmetic chest odds, crafting, Inheritance prices and the Logistics bonus (+15% in all).',
          'Rebirth: what a rebirth takes now mentions the Vault, and your pumpkins and event-shop buys are listed as kept.',
        ],
      },
      {
        title: 'Fixed',
        items: [
          'The attribute panel counted placed points from the wrong starting values.',
          'The boss hint and the reroll quest step no longer mention a diamond upgrade that does not exist.',
        ],
      },
    ],
  },
  {
    version: '1.21.0',
    date: '2026-10-11',
    sections: [
      {
        title: 'Fixed',
        items: [
          'Wear in the Chest now dresses the character you have picked, and says which one - it used to always dress your first character.',
          'Take off in the Chest now takes the piece off whoever actually wears it.',
          'A gear slot now also lists pieces your other characters wear, marked with who wears them, with Move here to hand it over.',
        ],
      },
    ],
  },
  {
    version: '1.20.0',
    date: '2026-10-11',
    headline: 'Rebirth has its own screen.',
    sections: [
      {
        title: 'Rebirth',
        items: [
          'Rebirth is now its own screen in the Hero menu, no longer hidden at the bottom of the Hall of Ancestors.',
          'The road to ten shows what every rebirth brings, with the avatars and the Phoenix pictured, and marks your next one.',
          'The Vault: choose the gear you keep through a rebirth before you confirm, with the price for every rarity listed.',
          'The terms now show what you lose, keep and gain side by side.',
          'The Wiki explains every rule: the rewards, the road to ten and the Vault prices.',
        ],
      },
    ],
  },
  {
    version: '1.19.0',
    date: '2026-10-11',
    sections: [
      {
        title: 'New',
        items: [
          'Your rebirth count now shows as a badge beside your name, in chat and on the boards. Take all ten rebirths and the badge turns gold and your name glows.',
          'Rebirths 3, 5, 7 and 10 each bring a new avatar: Jarilo, Vesna, Étaín and Morana.',
          'The tenth and last rebirth also brings the Phoenix, a pet with +8% damage, +8% health and +8% XP.',
        ],
      },
      {
        title: 'Clearer',
        items: [
          'A boss you have not beaten now names the gear rarity it asks for in words, and explains that affixes have their own rarity (Common up to Legendary).',
          'The region wall on Combat now also counts how many of your affixes are rare enough for the next boss.',
          'The "Reroll an affix" quest step now explains affix rarity, and that bosses 4 and 5 want Epic and Legendary affixes.',
        ],
      },
    ],
  },
  {
    version: '1.18.0',
    date: '2026-10-11',
    headline: 'A rebirth is now worth taking.',
    sections: [
      {
        title: 'Rebirth',
        items: [
          'Each rebirth gives +2 skill points for good, up to 20 in total.',
          'After a rebirth, every level below your best ever earns 50% more combat XP, so you climb back faster.',
          'Keep your best gear through a rebirth: tick it on the Rebirth panel and pay gold by rarity, from 5 million for the commonest piece to 100 million for the rarest.',
          'Five rebirths open a second pet slot on every character.',
          'You can be reborn ten times. The tenth gives the title The Ever-Returning.',
          'Your pets always stay with you.',
        ],
      },
    ],
  },
  {
    version: '1.17.0',
    date: '2026-10-11',
    sections: [
      {
        title: 'Fixed',
        items: [
          'The Logistics achievement now counts every harvest - from all your characters, and while you are away. It used to restart from zero each session, so nobody could reach its first tier. Harvests count from today.',
          'Forge upgrades for the Forging achievement are now remembered between sessions.',
          'Progress -> Insights now shows your real harvests per hour.',
        ],
      },
    ],
  },
  {
    version: '1.16.0',
    date: '2026-10-10',
    sections: [
      {
        title: 'Pets from monsters',
        items: [
          'Ten new pets can be found on any kill of any monster, any time of year - while you play and while you are away. The chance is small: 1 in 100,000 kills.',
          'Each gives one character +8% to one thing: combat XP, gold, damage, crit damage, loot luck, gathering speed, world boss damage, health, armour or gathering yield.',
          'If a pet you already have finds you again, you get 50 diamonds in your mail instead.',
          'The Wiki lists them all under Events & rewards, with which ones you already have.',
        ],
      },
    ],
  },
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
