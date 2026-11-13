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
    version: '1.70.0',
    date: '2026-11-13',
    headline: 'Aptitude milestones: 30, 40 and 50 points each unlock a second bonus.',
    sections: [
      {
        title: 'Bloodline',
        items: [
          'Strength - Keen Edge: +0.10x / +0.20x / +0.30x crit damage at 30 / 40 / 50.',
          'Skill - Deft Hands: +5% / +10% / +15% gathering yield.',
          'Endurance - Stout Frame: +3% / +6% / +10% block.',
          'Fortune - Lucky Purse: +5% / +10% / +15% gold from kills.',
          'The Bloodline panel shows each step and lights the ones your playing character has reached. The climb past 35 is worth it now.',
        ],
      },
    ],
  },
  {
    version: '1.69.0',
    date: '2026-11-13',
    headline: 'Keep a better child the moment it is born.',
    sections: [
      {
        title: 'Bloodline',
        items: [
          'When a child is born better than an ancestor you marked to keep, the birth banner offers to keep the child in their place - one tap.',
          'The Hall of Ancestors warning about better blood being lost has a Swap the marks button that fixes it in one tap.',
        ],
      },
    ],
  },
  {
    version: '1.68.0',
    date: '2026-11-13',
    headline: 'Buy Ancestral Boons several ranks at once, and a warning before better blood is lost.',
    sections: [
      {
        title: 'Bloodline and Rebirth',
        items: [
          'The Hall of Ancestors warns you when an ancestor better than one you marked to keep would be lost at your next rebirth, and names who to unmark and who to mark.',
          'Ancestral Boons can be bought several ranks at once: a second button buys as many ranks as your shards cover, in one go.',
        ],
      },
    ],
  },
  {
    version: '1.67.0',
    date: '2026-11-13',
    headline: 'A quicker breeding lab and a shorter Rebirth screen.',
    sections: [
      {
        title: 'Bloodline and Rebirth',
        items: [
          'The breeding lab opens with your strongest free adult already chosen as the hero, so Best matches is there straight away.',
          'The road on the Rebirth screen shows your next four steps and the last one instead of every card. A button opens the whole road.',
          'The Rebirth screen now warns you when your strongest ancestor would be lost because weaker ones are marked to keep, and says so again before you confirm.',
        ],
      },
    ],
  },
  {
    version: '1.66.0',
    date: '2026-11-13',
    headline: 'The Stats tab lists your lifetime bonuses.',
    sections: [
      {
        title: 'Character',
        items: [
          'The Stats tab has a new part, Lifetime bonuses: each Ancestral Boon with its rank, the Rebirth Trials you have finished and what each one pays, and the rule of the trial you are under this life.',
        ],
      },
    ],
  },
  {
    version: '1.65.0',
    date: '2026-11-13',
    headline: 'Best blood this week: a weekly race for breeders.',
    sections: [
      {
        title: 'Leaderboards',
        items: [
          'New tab, Best blood this week: the best child of each bloodline, born since Monday, ranked by its four aptitudes added up, then by its weakest one. It starts again every Monday and pays nothing - it is for bragging rights.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'A child you put into a slot now grows up an hour after birth even while it stands idle, and its stats change the moment it does, without signing in again.',
          'Your road on the Home screen now shows your real Dungeon stages for each gate and your waiting tickets. It used to show 0.',
        ],
      },
    ],
  },
  {
    version: '1.64.0',
    date: '2026-11-13',
    headline: 'Your road: every long goal on one card.',
    sections: [
      {
        title: 'Home',
        items: [
          'From level 40 (or after your first rebirth) the Home screen shows Your road: the Dungeon, the Delve, the Book of Deeds, your bloodline, the Rebirth Trials and the Ancestral Boons, each with how far you are and a button that takes you there.',
        ],
      },
    ],
  },
  {
    version: '1.63.0',
    date: '2026-11-13',
    headline: 'The Hall of Ancestors tells you when your best blood is sitting on the bench.',
    sections: [
      {
        title: 'Hall of Ancestors',
        items: [
          'Aptitudes only count on the character who plays. When a grown ancestor clearly out-bloods whoever stands in slot 1, the Hall now says so and takes you to them in one tap.',
          'The aptitude columns read St, Sk, En and Fo, so Strength and Skill no longer share a letter.',
          'A child on the roster now says "Growing up" - children grow up on their own.',
        ],
      },
    ],
  },
  {
    version: '1.62.0',
    date: '2026-11-13',
    headline: 'A new leaderboard: Fastest lives.',
    sections: [
      {
        title: 'Leaderboards',
        items: [
          'New tab, Fastest lives: who got from one rebirth to the next at level 50 the fastest, and the fastest to level 100 beside it. Glory only.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'The Bloodline steps and the Rebirth page no longer tell you to field children to raise them, or that rebirth stops at twenty.',
        ],
      },
    ],
  },
  {
    version: '1.61.0',
    date: '2026-11-13',
    headline: 'Rebirth Trials: take on a harder life for a reward that lasts.',
    sections: [
      {
        title: 'Rebirth Trials',
        items: [
          'When you are reborn you can take a trial for the next life: Want (kills pay half the gold), the Slow Hand (40% less combat XP), Frailty (25% less health) or Iron (no gear kept in the Vault).',
          'Reach the rebirth level under the trial and be reborn to finish it. Each pays once, for good: +5% gold, +5% XP, +5% health or +2% damage, and its own title.',
          'The Rebirth screen shows every trial, the one your life is under, and the ones you have finished.',
        ],
      },
      {
        title: 'Life records',
        items: [
          'The Rebirth screen now times each life and keeps your fastest life to a rebirth at level 50 and at level 100. The clock starts at your next rebirth.',
        ],
      },
    ],
  },
  {
    version: '1.60.0',
    date: '2026-11-13',
    headline: 'Breeding and rebirth, rebuilt: any two races can marry, a chosen aptitude always grows, and rebirth never ends.',
    sections: [
      {
        title: 'Breeding',
        items: [
          'Any two races can now have a child together. You pick which of the two races the child is born as; its aptitudes and traits carry over either way.',
          'An aptitude you breed for always grows: +1 a generation, or +2 while it is far below your bloodline ceiling, and it never drops. Reaching 50 in all four is now a real goal instead of a lottery.',
          'You can breed for one aptitude from Breeding Grounds level 1, two from level 4 and three from level 7.',
          'Children grow up on their own within an hour of birth, online or away. No more giving up a working slot to raise them.',
          'New: Best matches. Pick a hero and the game lists the five partners who would give the best child, and which aptitudes to breed for. One tap picks both.',
        ],
      },
      {
        title: 'Rebirth',
        items: [
          'There is no last rebirth any more. The bloodline ceiling still tops out at 50 after twenty rebirths.',
          'Every rebirth pays Legacy Shards by the level your life reached: half its square, so a life ended at level 90 pays 4,050.',
          'New: the Ancestral Boons on the Rebirth screen. Spend shards on +XP, +gold and +damage that last through every rebirth.',
          'Titles at 20, 30 and 50 rebirths: Keeper of the Blood, The Undying and Wheel of Ages.',
        ],
      },
      {
        title: 'Book of Deeds',
        items: [
          'A sixth chapter, The Long Blood, opens after the Ledger of Legends: aptitudes at 30, 40 and 50 in all four, every race at 30, a tenth generation and twenty rebirths. It pays a Seal.',
        ],
      },
    ],
  },
  {
    version: '1.59.0',
    date: '2026-11-09',
    headline: 'Rebirth now opens at level 50.',
    sections: [
      {
        title: 'Rebirth',
        items: [
          'A rebirth can now be taken only at level 50 or above. Below that you can still read the terms, but the confirm button stays shut.',
          'The Vault can also show only the gear from one region, and every piece in it says which region it comes from.',
        ],
      },
    ],
  },
  {
    version: '1.58.0',
    date: '2026-11-09',
    headline: 'Saved builds, full health for big characters, and a Vault you can sort.',
    sections: [
      {
        title: 'Builds',
        items: [
          'Save up to three builds of your attributes and War skill tree in the new Builds panel on the Character screen, and switch between them with one tap.',
          'Switching uses your free respec when you have one, and gold otherwise. A build that no longer fits your points is refused before anything is spent.',
        ],
      },
      {
        title: 'Rebirth',
        items: [
          'The Vault can now show only one kind of gear - weapons, helmets, chests, boots and so on - and only one rarity, so you no longer scroll through everything to find what to keep.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'Health above 2,147,483 now counts in full. Characters with more health than that were fighting, eating and respawning with a bar stopped at 2.15 million.',
        ],
      },
    ],
  },
  {
    version: '1.57.0',
    date: '2026-11-07',
    headline: 'Loot chests worth opening, and a Warehouse that keeps what your village makes.',
    sections: [
      {
        title: 'Loot chests',
        items: [
          'Every Silver, Gold and Ancient chest now holds a piece of equipment, and a Bronze chest holds one half the time.',
          'Chest equipment is much rarer than before: Bronze starts at Epic, Silver at Legendary, Gold at Mythic and Ancient at Relic, and every chest can hold a Godly or Transcendent piece.',
          'The piece comes from the highest region you have reached, and the chest screen lists the exact chance of every rarity.',
        ],
      },
      {
        title: 'Village',
        items: [
          'The Warehouse is now a store for what the Lumberjack and the Mine make while you are away: 1,000 of each material per level, moved into your chest when you return. What your chest already holds no longer counts against it.',
          'While you play, the Lumberjack and the Mine send everything straight to your chest.',
          'Each Warehouse level also makes the Lumberjack and the Mine produce 5% more.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'Gathered materials, loot and purchases are no longer lost or doubled when many things happen at once, and a busy server now says so instead of failing.',
          'Loot and materials earned in the last seconds before a server update are no longer lost.',
        ],
      },
    ],
  },
  {
    version: '1.56.2',
    date: '2026-11-03',
    headline: 'A tidier guild list on phones.',
    sections: [
      {
        title: 'Fixes',
        items: [
          'Guild members show their rank as a coloured tag, and the buttons sit on their own line under the name instead of squeezing it.',
          'The tab rows in the app no longer show a scrollbar, and the Community tabs fit on a phone.',
        ],
      },
    ],
  },
  {
    version: '1.56.1',
    date: '2026-11-03',
    headline: 'Cleaner artwork.',
    sections: [
      {
        title: 'Art',
        items: [
          'Monster and item pictures are cropped properly: they fill their icons and sit in the middle instead of floating small to one side.',
          'The faint sparkle in the corner of many pictures and the stray specks around them are gone.',
        ],
      },
    ],
  },
  {
    version: '1.56.0',
    date: '2026-11-03',
    headline: 'Affinities: the skill tree now strengthens the affixes on your gear.',
    sections: [
      {
        title: 'Skill tree',
        items: [
          "Every War column has a new Affinity rune: Tempered Steel (Might), Honed Edge (Precision), Warded Plate (Guard) and Fortune's Favour (Fortune). The Hearth Workshop has Toolwright for your tools. Each makes the affixes of its family on your gear 3%, 6% or 10% stronger.",
          "Only the affixes grow, never an item's own attack or armour.",
          'Rune names no longer get cut off.',
        ],
      },
      {
        title: 'Gear',
        items: [
          'Every affix shows its family (Power, Precision, Guard, Fortune or Craft) in its tree colour. When your tree boosts it, you see the value it really fights with.',
          'The Forge lists each affix once, says the Forge level limit once instead of on every item, and fits your worn gear on one phone screen.',
        ],
      },
      {
        title: 'Interface',
        items: [
          'The "Do this next" hint folds into a small pill in the corner instead of sitting over the middle of the screen.',
        ],
      },
    ],
  },
  {
    version: '1.55.0',
    date: '2026-11-03',
    headline: 'A clearer Forge, guild ranks, and fairer reroll prices.',
    sections: [
      {
        title: 'Forge',
        items: [
          'The Forge has two tabs, Affixes and Fusion, instead of two long columns side by side.',
          'Your worn gear is listed by character, so rerolling a second or third character no longer means searching your whole chest.',
          'Every affix has its own Reroll and Refine buttons, apart from each other and each with its price.',
          'A reroll now costs more on rarer items: the region price is multiplied by the item rarity, from x1 for Normal up to x50 for Relic and above. Refine still costs a quarter of a reroll.',
        ],
      },
      {
        title: 'Guilds',
        items: [
          'Guilds have four ranks: Chieftain, Elder (up to two), Warden (up to five) and Member. The Wiki explains who can do what.',
          'The Chieftain can hand the guild to another member. If the Chieftain does not log in for 14 days, the guild passes to the highest rank still playing.',
          'A guild that had nobody leading it gets a Chieftain.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'With three potions running, the countdowns no longer run off a phone screen.',
        ],
      },
    ],
  },
  {
    version: '1.54.1',
    date: '2026-11-03',
    headline: 'The welcome-back summary counts XP correctly again.',
    sections: [
      {
        title: 'Fixes',
        items: [
          'If your characters gained a level while you were away, the welcome-back summary could show +0 XP, or even say you earned nothing. It now shows all the XP you earned, including the levels it paid for.',
        ],
      },
    ],
  },
  {
    version: '1.54.0',
    date: '2026-11-02',
    headline: "Malakor's ascension climbs more steeply, and a few rare stuck states are gone.",
    sections: [
      {
        title: 'Ascension',
        items: [
          "Malakor's ascension steps were re-measured against today's characters, who had outgrown the old ladder. Each step now hits harder than before, so the top of the ladder is a real goal again.",
        ],
      },
      {
        title: 'Fixes',
        items: [
          'Claiming mail at a busy moment could leave it stuck as pending forever. It now retries and never stays stuck.',
          'The chest says why an action was refused instead of failing silently, and busy moments ask you to try again instead of reporting an item missing.',
        ],
      },
    ],
  },
  {
    version: '1.53.0',
    date: '2026-11-02',
    headline: 'The chest can now clear Legendary and Mythic gear in bulk.',
    sections: [
      {
        title: 'Chest',
        items: [
          'Bulk sell and the auto-sell rules now reach up to Mythic, not just Epic. Once your gear outgrows a region, its Legendary and Mythic pieces no longer have to be sold one by one.',
          'Relic and above are still never sold in bulk. Worn pieces are always skipped, and so is anything you lock - lock a piece to keep it out of every sweep.',
        ],
      },
    ],
  },
  {
    version: '1.52.1',
    date: '2026-11-01',
    headline: 'A new character starts with forty fish instead of ten.',
    sections: [
      {
        title: 'Getting started',
        items: [
          'The starter fish in the chest went from ten to forty. Ten won a new character one or two fights; forty carry it through its first level before it has to go fishing.',
        ],
      },
    ],
  },
  {
    version: '1.52.0',
    date: '2026-11-01',
    headline: 'The dungeons have their own sounds, and stage 15 of each gate pays a title.',
    sections: [
      {
        title: 'Dungeons',
        items: [
          "Every run now sounds like a dungeon: the gate grinding open, a horn for each wave, a voice for each boss - Koschei's laugh, Zmey's three roars, Baba Yaga's cackle - a warning when a big blow winds up, a whoosh for each swipe, and its own sound for a dodge, a blow that lands, a blessing, a win and a loss.",
          `Win stage 15 of a gate for the first time and you earn its title: "Deathless" from Koschei's Tower, "Dragonbane" from Zmey's Hoard, "Witch-bane" from Baba Yaga's Hut. Wear it from the title picker in The Delve.`,
        ],
      },
    ],
  },
  {
    version: '1.51.2',
    date: '2026-11-01',
    sections: [
      {
        title: 'Dungeons',
        items: [
          "A red dot on Dungeon in the menu tells you when a gate is full of tickets. A full gate cannot hold the next day's tickets, so spend one before midnight UTC.",
          'A menu group now shows the dot of anything inside it, so you see it without opening the menu.',
          'A champion without full gear now stays home from the dungeon unless you send them. Bosses hit them through no armour, so they made the party weaker than going alone. One tap still sends them.',
        ],
      },
    ],
  },
  {
    version: '1.51.1',
    date: '2026-11-01',
    sections: [
      {
        title: 'Dungeons',
        items: [
          "Every gate's tickets have their own picture: Koschei's sealed scroll, Zmey's bronze token and Baba Yaga's smith key. They show on the gate, on the Enter and Sweep buttons, and when a chest gives you a ticket.",
        ],
      },
    ],
  },
  {
    version: '1.51.0',
    date: '2026-11-01',
    headline: 'Inheritance milestones: levels 5 and 10 of every bonus now add an extra effect.',
    sections: [
      {
        title: 'Inheritance milestones',
        items: [
          'Damage: +5% critical chance at level 5, +25% critical damage at level 10.',
          'Max health: food heals 15% more at level 5, +10% armour at level 10.',
          "Experience: +5% XP while away at level 5, +25% XP from Koschei's gate at level 10.",
          "Gold: +25% gold from Zmey's gate at level 5, +20 Delve diamonds a week at level 10.",
          'Gathering yield: +10% gathering speed at level 5, rare finds twice as likely at level 10.',
          'Loot luck: +5% chance a drop is one rarity higher at level 5, a 10% chance a loot chest opens one tier higher at level 10.',
          'If you already own those levels, the effects are active now. The Inheritance screen shows which ones you have.',
        ],
      },
      {
        title: 'Thorns',
        items: [
          'Thorns can roll on chest armour again, at 1-4% (it reflects part of every hit you take). Thorns you already have are unchanged.',
          'The region 2 and 3 bosses hit about half a percent softer on their first clear, to make room for it.',
        ],
      },
    ],
  },
  {
    version: '1.50.0',
    date: '2026-10-31',
    headline: 'The Dungeons open: three gates, a fight you can see, and dodges that build your run.',
    sections: [
      {
        title: 'Three gates',
        items: [
          "Koschei's Tower pays experience, Zmey's Hoard pays gold, and Baba Yaga's Hut pays a piece of gear and a loot chest. Fifteen stages each, and every stage pays diamonds the first time you clear it.",
          'Each boss fights in his own way - Koschei rises again, Zmey has three heads, Baba Yaga hexes your blows - and every stage shows his abilities with their numbers.',
          'Two tickets a day for each gate, up to four. Loot chests can hold an extra ticket (up to eight). A stage you have beaten can be swept for its reward without the fight.',
          'The gates open once you have beaten the region 2 boss.',
        ],
      },
      {
        title: 'Dodge and build',
        items: [
          'The enemy winds up a few times a run: swipe (or press WASD or the arrow keys) in the direction shown, one after another, before the time runs out.',
          'A clean dodge lets you pick one of two blessings for the rest of the run; a miss lands a heavy blow and a curse.',
          'Blessings belong to four schools - Berserk, Guardian, Blood and Storm. Two of one school unlock its set bonus, three a stronger one. Risky cards are strong, with a price.',
          'Beat stage 6 of a gate and a second hero from your roster can fight beside you; stage 11 opens the third. Their own gear counts, so dress them.',
        ],
      },
    ],
  },
  {
    version: '1.49.1',
    date: '2026-10-31',
    sections: [
      {
        title: 'Dungeon',
        items: [
          'The Equipment Dungeon is closed again while it is rebuilt to look the way it should. Tickets, first-clear rewards and everything you won stay yours.',
        ],
      },
    ],
  },
  {
    version: '1.49.0',
    date: '2026-10-31',
    headline: 'The Equipment Dungeon opens.',
    sections: [
      {
        title: 'Dungeon',
        items: [
          'A new screen under Play: the Equipment Dungeon. Five waves and Baba Yaga, with your health carried from fight to fight and at most five meals from the larder.',
          "Fifteen stages in three bands - the regions, the Ascension, and beyond anything else in the game. Each stage shows Baba Yaga's abilities with their numbers, so you can see what beat you.",
          'Win and she drops a piece of gear (Rare, Epic or Legendary or better by band) and a loot chest; every stage also pays diamonds the first time you clear it.',
          'Two tickets a day, up to four. It opens once you have beaten the region 2 boss.',
        ],
      },
    ],
  },
  {
    version: '1.48.0',
    date: '2026-10-31',
    headline: 'One Book of goals, and diamonds that go further.',
    sections: [
      {
        title: 'Book of Deeds',
        items: [
          'The quest line is now the first chapter of the Book of Deeds, "Getting started" - claim its rewards there or on Home.',
          'The Book opens with "Closest to done": the goals you are nearest to finishing, across every chapter and achievement.',
          'A new achievement, Alchemist, counts the potions you drink.',
        ],
      },
      {
        title: 'Diamonds',
        items: [
          'Achievements pay 5, 15, 40 or 100 diamonds a tier. The big ones are split into more steps, so you are paid along the way. Tiers you already reached stay paid.',
          'The Delve pays up to 80 diamonds a week, up from 60.',
          "Inheritance has ten levels of +4% instead of twenty of +2%, and costs far less: a whole bonus is about 3,300 diamonds instead of 19,800. Your levels were converted, rounded up - nobody's bonus went down.",
        ],
      },
    ],
  },
  {
    version: '1.47.0',
    date: '2026-10-31',
    headline: 'Herbs and potions.',
    sections: [
      {
        title: 'Herbalism',
        items: [
          'Herbalism is back on the Gathering screen: every region has a common herb and a rare one. Herbs are picked by hand, with no tool - your Herbalism mastery is what makes you faster.',
          'Green Thumb on the Hearth tree works again.',
          'Loot chests now carry herbs of their region too.',
        ],
      },
      {
        title: 'Potions',
        items: [
          "Brew sixteen potions at Alchemy on the Crafting screen: Elixir of the Hunt, Draught of Fortune, Goldmoss Tonic, Ironbark Brew, Swiftroot Tea, Boss-bane Philtre, Gatherer's Draught and Kupala Night, each in a normal and a strong version. Every recipe says what the potion does and for how long.",
          'Put potions on a shelf on the Auto-Eat screen and drink them there. One potion runs in each of three slots - rewards, combat and gathering - and it works offline exactly as online.',
          'Turn on auto-drink and the next potion on the shelf is drunk the moment the last one runs out, also while you are away.',
          'Running potions and their time left show under the header. Tap them to open the shelves.',
          'A new quest line step teaches the whole thing: pick, brew, shelve, drink.',
        ],
      },
    ],
  },
  {
    version: '1.46.0',
    date: '2026-10-31',
    headline: 'Quieter, clearer sounds.',
    sections: [
      {
        title: 'Sound',
        items: [
          "Coming back after a long time no longer plays every sound at once. A burst of drops makes one sound, for the best thing in it, and the big sounds wait their turn.",
          'Rare drops have new sounds in three steps: Rare to Epic, Legendary to Mythic, and Relic and better. The old sound got higher and shriller with every tier.',
          'New sounds for opening a loot chest, finding a pet, the Forge, a child being born and a building being finished.',
        ],
      },
      {
        title: 'Chests',
        items: [
          'The cosmetic chest fits the screen on phones and stands in the middle.',
          'What a loot chest gives you is in colour: gold, diamonds, rare materials and the piece in its rarity.',
        ],
      },
      {
        title: 'Chat',
        items: ['News announces drops from Relic up. Mythic drops no longer fill the channel.'],
      },
    ],
  },
  {
    version: '1.45.0',
    date: '2026-10-30',
    headline: 'Ascension rebuilt.',
    sections: [
      {
        title: 'Ascension',
        items: [
          'Ascension starts over with a new ladder. From step 4 a monster hits a third harder once it drops below 30% health. From step 7 it also raises a short ward each time it loses a quarter of its health.',
          'Clearing step 10 of a region gives you 2% more strength against that region for good.',
          'Every step you clear drops a loot chest, and the chests get better from step 4, from step 7 and at step 10.',
          'Every step you had already cleared has been paid out as a loot chest. A letter in your mail explains it. Your titles and frames stay.',
        ],
      },
      {
        title: 'Gear and crafting',
        items: [
          'New gloves can roll Crushing Blow.',
          'A recipe made with a rare material now crafts a piece one quality higher, with one extra affix.',
          "Swift boss challenges in regions 3, 4 and 5 now allow 210, 232 and 301 seconds.",
        ],
      },
      {
        title: 'Chests',
        items: [
          'Cosmetic chests now open the same way as loot chests.',
          'The light behind an opening chest is centred on it and glows in the colour of its rarity.',
          'A chest from a boss or from Ascension holds loot from its own region. A chest from the Deep holds loot from the highest region.',
        ],
      },
      {
        title: 'Hearth',
        items: [
          'Every Hearth skill has its own icon.',
          'Green Thumb is locked until herbs arrive in the game, so no points are spent on it for nothing.',
          'Bloodline Memory now describes what it actually does.',
        ],
      },
    ],
  },
  {
    version: '1.44.0',
    date: '2026-10-30',
    sections: [
      {
        title: 'Forge',
        items: [
          "New: Refine. It keeps an affix and its rarity and rerolls only the number, up to 20% up or down, for a quarter of a reroll's price.",
          'Loot chests open in front of a shaft of light with dust drifting through it.',
        ],
      },
    ],
  },
  {
    version: '1.43.0',
    date: '2026-10-30',
    headline: 'Loot chests.',
    sections: [
      {
        title: 'Loot chests',
        items: [
          'Bronze, Silver, Gold and Ancient chests hold materials, gold, sometimes equipment and, in the better ones, diamonds.',
          'Earn them by reaching a new deepest floor of the Deep each week from floor 10, and by beating a region boss for the first time.',
          'Open them from the Chest screen, one at a time or five at once. Every chest shows its odds.',
          'Gold from a chest grows with your own income, so a chest stays worth opening at any level.',
        ],
      },
    ],
  },
  {
    version: '1.42.0',
    date: '2026-10-30',
    sections: [
      {
        title: 'Gear',
        items: [
          'New rings can roll World Boss Damage, Loot Luck and Gold Find. New amulets can roll Loot Luck and Gold Find.',
          "New weapons roll Damage once instead of under three names that all did the same thing. Your old weapons keep what they have.",
          'Swift boss challenges in regions 3 and 4 now allow 205 and 225 seconds.',
        ],
      },
    ],
  },
  {
    version: '1.41.0',
    date: '2026-10-30',
    headline: 'The skill tree has a second half: the Hearth.',
    sections: [
      {
        title: 'The Hearth',
        items: [
          'Switch the Skill Tree between War and Hearth with the tabs at the top.',
          'Hearth is paid with craft points. You get one for every 10 levels of Woodcutting, Mining, Fishing and Herbalism added together.',
          'Its four columns make gathering faster and richer, crafting quicker and thriftier, Workshop commissions cheaper, and breeding and Great Works go further.',
          'Your Hearth stays when you are reborn. Resetting it costs gold and never touches your War skills.',
        ],
      },
    ],
  },
  {
    version: '1.40.0',
    date: '2026-10-30',
    sections: [
      {
        title: 'The Deep',
        items: [
          'Each floor of the Deep now costs less than the last one did: the toll grows by 25% a floor instead of 35%.',
          'The Deep pays its own diamonds: 2 for every floor from 10 to 19 you reach for the first time each week, up to 20 a week.',
          "These diamonds are extra. They do not count against the Delve's weekly 60.",
        ],
      },
    ],
  },
  {
    version: '1.39.1',
    date: '2026-10-30',
    sections: [
      {
        title: 'Skill Tree',
        items: [
          'Ruin hits harder (every 8th hit lands for x1.7) and costs less speed (attack speed counts 85%). Fast builds no longer lose kills by taking it.',
          'Riposte, Counterstroke, Piercing Crit and Know Your Prey are a little smaller, which keeps the whole tree as strong as before.',
          'On a phone, the runes in each row sit in the middle of their branch.',
        ],
      },
    ],
  },
  {
    version: '1.39.0',
    date: '2026-10-30',
    headline: 'A new skill tree. Every point you spent has been given back.',
    sections: [
      {
        title: 'Skill Tree',
        items: [
          'The skill tree is new: four columns (Fortune, Might, Precision and Guard) of five rows each. A row opens once its column holds enough points.',
          'Some rows are a fork, where you take one side. Three bridges join neighbouring columns and count for both.',
          'New effects to build around: Crushing Blow, Echo, Riposte, Ruin, Perfect Sequence, Living Fortress, Know Your Prey and more. Watch for their callouts in a fight.',
          'Every point you had spent on the old tree is back to spend, and your free respec is ready again.',
          'Once your free respec and any paid respecs are used, you can respec for gold. The price grows with the points spent and your level.',
          'Crafting speed, gathering speed and the offline bonus have left the tree. They will return in a crafting and gathering tree of their own.',
        ],
      },
    ],
  },
  {
    version: '1.38.2',
    date: '2026-10-29',
    sections: [
      {
        title: 'Screens',
        items: [
          'Screens open faster: once you are signed in, the game quietly fetches every screen in the background, so moving between them rarely waits.',
          'A screen that is slow to arrive now tells you so after a few seconds and offers a Reload, instead of sitting on "Loading..." forever.',
        ],
      },
    ],
  },
  {
    version: '1.38.1',
    date: '2026-10-29',
    sections: [
      {
        title: 'Skill Tree',
        items: [
          'The tree in the background no longer shifts when you tap a rune.',
          'The buttons under a rune (+1, Max, Confirm and the rest) are readable in the dark look as well.',
          'Tapping a rune near the bottom scrolls it into view, so its details never cover the branch you tapped.',
        ],
      },
    ],
  },
  {
    version: '1.38.0',
    date: '2026-10-29',
    headline: 'A new Skill Tree screen: plan your build first, then confirm it.',
    sections: [
      {
        title: 'Skill Tree',
        items: [
          'Skills are now runes. Tap one to see what it does now and at its next level, and why it is locked if it is. Hold it, or point at it with a mouse, for a quick look.',
          'Nothing is spent when you tap. Add levels to as many runes as you like, see exactly how your stats change, then press Confirm. Discard throws the plan away, so a misclick never costs a point.',
          'On a wide screen the five limbs stand side by side like a tree, roots at the bottom and crowns on top.',
          'The runes have their own sounds now.',
        ],
      },
      {
        title: 'Combat',
        items: [
          'Critical hits have their own heavier sound, and a Double Strike has one too.',
        ],
      },
    ],
  },
  {
    version: '1.37.0',
    date: '2026-10-29',
    headline: 'Gathering bonuses count in full, and the Fairy gets the dodge she was missing.',
    sections: [
      {
        title: 'Gathering',
        items: [
          'Gathering speed bonuses from tools, the Harvest branch, the Skill aptitude, traits and pets now multiply your speed instead of being added to it. A +5% bonus is a real 5% at every stage - before, it shrank to almost nothing once your tool and mastery were high.',
          'The speed bonus on tools now shows its real size: a tool that says +55% gives +55%. It always did; the number on the tool used to read ten times too small.',
          'Each tool now speeds up only its own work: an axe for woodcutting, a pickaxe for mining, a rod for fishing.',
          'The Clumsy Hands flaw now really slows gathering by 6%, as its card says.',
          'The Gathering screen shows the real harvest time for the character you picked, with every bonus included.',
        ],
      },
      {
        title: 'Races',
        items: [
          'The Fairy now has +15 dodge. Her "+20% ranged damage" did nothing - there is no separate ranged damage in this game - so it became +5 dodge.',
        ],
      },
    ],
  },
  {
    version: '1.36.0',
    date: '2026-10-29',
    headline: 'Every point of dodge counts now.',
    sections: [
      {
        title: 'Combat',
        items: [
          'Dodge works from the first point. Before, the first five points of dodge did nothing at all - small dodge bonuses on gear, the Nimble trait and the defensive potion were wasted.',
          'The defensive potion now gives 1 dodge per tier (it gave a hundredth of that).',
          'To keep the boss walls fair with the stronger dodge, the region 2 and region 3 bosses hit a little harder on their first clear, and the sixth tier of the seasonal boss hits harder.',
        ],
      },
    ],
  },
  {
    version: '1.35.0',
    date: '2026-10-29',
    headline: 'Tool bonuses for gathering now work.',
    sections: [
      {
        title: 'Gathering',
        items: [
          'The gathering yield bonus on tools now gives what it says: more harvest rolls, online and while you are away. It used to do nothing.',
          'The rare find bonus on tools now makes rare materials more likely. It used to do nothing.',
          'Each tool helps only its own work: an axe for woodcutting, a pickaxe for mining, a rod for fishing.',
        ],
      },
    ],
  },
  {
    version: '1.34.0',
    date: '2026-10-29',
    headline: 'Races now say what they do - and two of them finally do it.',
    sections: [
      {
        title: 'Races',
        items: [
          'The Wiki has a card for every race: its own bonuses and what each race mastery level adds. The race mastery panel in Progression lists the milestones and ticks the ones you have reached.',
          'Draugr mastery lifesteal now heals what it says: 10% at level 10 and 12% at level 50 (at most 1% of your health per hit). It used to heal a hundredth of that.',
          'Fairy mastery 10 now gives +15% attack speed, as intended - it was worth almost nothing before.',
          'Draugr now have -15% attack speed, as the race was designed - the price of their extra health and armour. It never makes them slower than the base swing.',
        ],
      },
    ],
  },
  {
    version: '1.33.0',
    date: '2026-10-29',
    headline: 'Two bonuses that did nothing now work.',
    sections: [
      {
        title: 'Fixes',
        items: [
          'Gathering yield from the Inheritance store now pays: +2% more harvest rolls per level, online and while you are away. If you bought it, the levels you already own start paying today.',
          'The Fortitude branch of the skill tree now gives the armour its card promised: +2% armour per level, on top of the health it already gave.',
          'Weapon damage bonuses are all called "Damage" now. Melee, ranged and magic damage were always the same bonus to your attack, and the old names suggested otherwise.',
        ],
      },
    ],
  },
  {
    version: '1.32.1',
    date: '2026-10-28',
    sections: [
      {
        title: 'Polish',
        items: [
          'Opening five chests: Next now switches the glowing chest behind the card instantly, with no flicker.',
          'The chest buttons sit side by side in one row, so the card is smaller.',
          'In the Wardrobe, Open and Open ×5 sit side by side.',
          'The rare drop card is easier to read - no more glow behind the item name; the card itself wears the rarity colour.',
        ],
      },
    ],
  },
  {
    version: '1.32.0',
    date: '2026-10-28',
    headline: 'Open five chests at once.',
    sections: [
      {
        title: 'Wardrobe',
        items: [
          'New Open ×5 button: shake the first chest open, then press Next to see each of the five pieces. The chest behind glows in the colour of the piece on show.',
          'When the five are done, Open another 5 starts the next batch.',
        ],
      },
      {
        title: 'Combat',
        items: [
          'The monster picture sits above its Fight button, bigger, and the text has the full width.',
          'A struck monster recoils more gently.',
          'Damage numbers are a little smaller and always stay fully on screen.',
        ],
      },
    ],
  },
  {
    version: '1.31.1',
    date: '2026-10-28',
    sections: [
      {
        title: 'Polish',
        items: [
          'Each monster is one card with its Fight button inside it. XP an hour is shown in blue, gold an hour in gold.',
          'The village explains what STR, SKL, END and FOR pass on to a child.',
          'Chest items no longer have a double frame around their picture.',
          'The Character screen no longer shifts when you open Work & orders.',
          'The Event shop button sits under the game title on a phone, and is slimmer.',
        ],
      },
    ],
  },
  {
    version: '1.31.0',
    date: '2026-10-28',
    headline: 'Tidier combat list, more colour everywhere.',
    sections: [
      {
        title: 'Combat',
        items: [
          'Each region in the monster list folds open and shut. Open as many as you like - the game remembers which.',
          'Every Fight button is the same size now, and the XP bar and your level are easier to read.',
          'Best XP/h now counts fights that need food, as long as your larder has some.',
        ],
      },
      {
        title: 'Look',
        items: [
          'The More menu, the Character tabs and each gathering trade have their own colours.',
          'Items in the chest show their rarity colour along the edge and around the picture.',
          'Every person in the village gets the same size card; tap a name to see what their traits do.',
          'The chat button is quieter until a message arrives.',
        ],
      },
    ],
  },
  {
    version: '1.30.0',
    date: '2026-10-28',
    headline: 'Every part of the game has its own colour.',
    sections: [
      {
        title: 'Look',
        items: [
          'Combat is red, Gathering green, your heroes purple, the village and workshops blue, and the community teal - on the panels, the main buttons and the bar at the bottom of a phone.',
          'Main buttons have a richer, coloured gradient, so the action you came for stands out.',
        ],
      },
      {
        title: 'Chat',
        items: [
          'On a phone, chat is one tap away again: a round chat button in the bottom-left corner, with a count of unread messages.',
        ],
      },
    ],
  },
  {
    version: '1.29.0',
    date: '2026-10-28',
    headline: 'Fights fill the screen, and you choose your colours.',
    sections: [
      {
        title: 'Combat',
        items: [
          'The monster you fight now stands large in front of its own region’s landscape, and only the monster flinches when you hit it.',
          'Damage numbers stay up longer and have a dark outline, so they read over the scenery. Critical hits say CRIT!, and a Double Strike says DOUBLE CRIT!!',
          'Sword blows leave a red slash, and a critical hit crosses it with a second one. Arrows fly in and strike, and the magic burst is smaller.',
        ],
      },
      {
        title: 'Settings',
        items: [
          'New Appearance section: pick dark or light colours, or follow your device.',
          'You can turn off an event’s colours (like Samhain’s pumpkin orange) and keep the ordinary look. The event shop stays.',
        ],
      },
      {
        title: 'Fixes',
        items: [
          'Skill tree names on a phone no longer break in the middle of a word.',
          'The Event shop button on a phone is no longer stretched across the whole screen.',
        ],
      },
    ],
  },
  {
    version: '1.28.1',
    date: '2026-10-21',
    headline: 'Children grow up while they work, even when you are away.',
    sections: [
      {
        title: 'Families',
        items: [
          'A character ages only while their own slot is working - fighting, gathering or crafting. Someone standing idle in a slot no longer grows older because another slot is busy.',
          'Characters in slots 2 and 3 now age while you are offline too, not only the first slot. A child given a job grows up in an hour, online or offline.',
        ],
      },
    ],
  },
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
