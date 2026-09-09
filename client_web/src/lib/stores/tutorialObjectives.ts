// Modul: TIER THREE - the objective track, and why two tiers were not enough.
//
// Tier one (tutorialSteps.ts) gives a new player three INSTRUCTIONS and then
// stops, about ten minutes in. Tier two (tutorialDiscoveries.ts) explains a
// system the first time the player REACHES it - which means it is reactive by
// construction: it can tell you what the Forge is once you own one, and it can
// say nothing at all about a system you have not stumbled into.
//
// Between "wear a drop" and the world boss there was therefore no answer to the
// only question a player in a game this size actually asks: WHAT SHOULD I DO
// NOW. That is the reported problem - "there is a lot of content and I do not
// want the player to be lost" - and neither existing tier addresses it, because
// both are triggered by things the player has already done.
//
// So this table is the mirror image of tier two. Every entry fires when the
// player is READY for something and has not done it yet, and each one names a
// concrete next act rather than describing a screen.
//
// THREE RULES CARRIED OVER FROM THE TIERS THAT WORK:
//
//   1. An objective is a PREDICATE OVER THE STATE PACKET. No stores, no
//      browser, no network - so the whole table is testable in a node runner,
//      and a player who did the thing in another tab is never asked twice.
//   2. It is acknowledged, once, and never returns. Every entry here is a
//      first-time act: place your points, beat a boss, try the Delve. Once you
//      know the system is there you do not need telling again, and a panel that
//      keeps asking is worse than one that never existed.
//   3. Tier one still wins outright, then tier two, then this. A player who has
//      not yet won a fight is not asked to think about guild buffs.
import type { StateUpdate } from '../net/protocol.generated';
import type { OnboardingFacts } from './tutorialDiscoveries';

export type ObjectiveId =
  | 'place_attribute_points'
  | 'spend_skill_points'
  | 'first_region_boss'
  | 'try_the_delve'
  | 'join_a_guild'
  | 'sell_on_the_market'
  | 'read_your_mail'
  | 'raise_the_town_hall'
  | 'reroll_an_affix';

export interface Objective {
  id: ObjectiveId;
  /** The system in one word, for the Settings list. */
  system: string;
  /** A nav key from lib/ui/screens.ts. */
  screen: string;
  title: string;
  body: string;
}

interface ObjectiveRule extends Objective {
  /** True when this is worth doing NOW and has not been done. */
  due: (s: StateUpdate, facts: OnboardingFacts) => boolean;
}

/**
 * The cheapest Delve run, mirroring DelveRegistry.EntryFeeByRegion.
 *
 * Modul: only the FIRST entry is mirrored, deliberately. A full copy of the fee
 * table on the client would be the two-sources-of-truth surface this codebase
 * loses most of its bugs to; the objective only has to know when the gate is
 * plausibly affordable, and the screen itself asks the server for the real
 * price. Being a little early here costs nothing - being wrong about the price
 * on a button would cost a refused run.
 */
const CHEAPEST_DELVE_ENTRY = 7000;

/**
 * Ordered by what is worth doing first, not by when it unlocks.
 *
 * The order IS the design. A player with unplaced attribute points is holding
 * power they already own, which beats anything that asks them to go and earn
 * some - so that is first, and it is also the objective that fires earliest.
 */
const OBJECTIVES: readonly ObjectiveRule[] = [
  {
    id: 'place_attribute_points',
    system: 'Attributes',
    screen: 'character',
    title: 'You have points waiting',
    // Modul: a level pays SEVEN points (RaceAttributeGrowth
    // .AttributePointsPerLevel), so a full level's worth unplaced is the
    // threshold - one stray point is not worth a panel.
    due: (s) => Number(s.UnspentAttributePoints) >= 7,
    body:
      'Every level pays attribute points and they do nothing until you place them. Might, ' +
      'Finesse, Vigour and Fortune each buy something different, and each one has milestones ' +
      'along the way.',
  },
  {
    id: 'spend_skill_points',
    system: 'Skill tree',
    screen: 'skills',
    title: 'Unspent skill points',
    due: (s) => Number(s.AvailableSkillPoints) >= 1,
    body:
      'The skill tree is separate from attributes and is spent on permanent bonuses. Ring 2 ' +
      'forks: taking one side locks the other for the season, so it is worth reading before ' +
      'you buy.',
  },
  {
    id: 'first_region_boss',
    system: 'Regions',
    screen: 'combat',
    title: 'The next region is behind a boss',
    // Ready for it, and has not done it. Level 8 is comfortably past the first
    // region's regulars.
    due: (s) => Number(s.CurrentLevel) >= 8 && Number(s.HighestUnlockedRegion) < 2,
    body:
      'Region 2 opens when you beat region 1’s boss — not at a level. Everything past ' +
      'it drops better gear, and a region step is worth more than the whole rarity ladder.',
  },
  {
    id: 'try_the_delve',
    system: 'The Delve',
    screen: 'delve',
    title: 'Gold is piling up',
    due: (s) => Number(s.Gold) >= CHEAPEST_DELVE_ENTRY * 3,
    body:
      'The Delve turns gold into diamonds, if your nerve holds. Eight floors, three doors each, ' +
      'and every door wants one of your attributes — bank what you have or push for more, ' +
      'and three failures lose the lot.',
  },
  {
    id: 'reroll_an_affix',
    system: 'Forge',
    screen: 'forge',
    title: 'Your gear can be rerolled',
    // A Forge exists and the worn weapon has never had its affixes locked -
    // the one packet fact that means "has used this feature".
    due: (s) => Number(s.ForgeLevel) >= 1 && Number(s.EquippedWeaponId) > 0,
    body:
      'The Forge rerolls the affixes on a piece you already own and fuses two into a better ' +
      'one. Its building level is the rarity ceiling, so raising it raises what you can make.',
  },
  {
    id: 'raise_the_town_hall',
    system: 'Village',
    screen: 'village',
    title: 'The Town Hall is holding the village back',
    // Every other building is pinned at the Hall's ceiling, so the Hall is the
    // only upgrade that unblocks anything.
    due: (s) => Number(s.TownHallLevel) >= 1 && Number(s.LumberjackLevel) >= Number(s.TownHallLevel),
    body:
      'No building may pass the Town Hall’s level, and yours are at the ceiling. Raising ' +
      'the Hall is what lets everything else move again.',
  },
  {
    id: 'join_a_guild',
    system: 'Guild',
    screen: 'guildops',
    title: 'Guilds pay bonuses you cannot get alone',
    due: (s, facts) => Number(s.CurrentLevel) >= 10 && !facts.hasGuild,
    body:
      'A guild gives every member buffs funded by what the members donate — gathering ' +
      'speed, combat damage, crafting. Joining one costs nothing.',
  },
  {
    id: 'sell_on_the_market',
    system: 'Market',
    screen: 'market',
    title: 'Other players will buy what you do not need',
    due: (s) => Number(s.InventoryCapacity) > 0 && Number(s.InventorySpaceRemaining) <= 10,
    body:
      'Your bags are nearly full. The market is player-to-player: list what you will not wear ' +
      'rather than salvaging it, and buy the piece you are missing.',
  },
  {
    id: 'read_your_mail',
    system: 'Mail',
    screen: 'mailbox',
    title: 'There is mail waiting',
    // Modul: the ONE objective with no packet fact behind it. Unread mail is a
    // REST badge, so this fires on the condition that PRODUCES mail instead -
    // a completed market sale pays into the mailbox, and so do compensation
    // grants. Being a little early is the right failure here: an empty mailbox
    // explains itself in one look, and a player who never learns the mailbox
    // exists loses whatever is sitting in it.
    due: (s) => Number(s.CurrentLevel) >= 5,
    body:
      'Market payouts, compensation and event rewards all arrive in the mailbox rather than ' +
      'straight into your bags. Nothing there expires, but nothing there is yours until you ' +
      'claim it.',
  },
];

/** Every objective, for the Settings list of explanations. */
export const ALL_OBJECTIVES: readonly Objective[] = OBJECTIVES.map(({ due: _due, ...rest }) => rest);

/**
 * The next thing worth doing, or null.
 *
 * Due and not yet seen, earliest in the table first - the same shape as
 * nextDiscovery, and pure for the same reason: the seen-set is handed in rather
 * than read from storage, so the whole table is testable in a node runner.
 */
export function nextObjective(
  snapshot: StateUpdate | null,
  facts: OnboardingFacts,
  seen: ReadonlySet<string>,
): Objective | null {
  if (!snapshot) return null;
  for (const rule of OBJECTIVES) {
    if (seen.has(rule.id)) continue;
    if (!rule.due(snapshot, facts)) continue;
    const { due: _due, ...objective } = rule;
    return objective;
  }
  return null;
}
