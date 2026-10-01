import { formatNumber } from './format';
// Modul: SCREENS UNLOCK AS THEY BECOME USEFUL (task 60).
//
// A brand-new player saw every destination in the game - the Market, a
// Guild, the Delve, breeding - before owning anything to sell, any gold to
// dive with or a building to breed in. Each of those screens then explained
// at length why it could do nothing yet.
//
// A locked screen is still IN the menu, greyed, with the condition beside it,
// so the game says what is coming rather than hiding it. It is a menu
// decision only: a cross-screen link (the Chest's Reroll button, a death
// card) still opens its screen, and the server gates nothing on this.
//
// ONE PREDICATE PER SCREEN, SHARED WITH THE "NEW" CARD. The discovery moment
// for each system (tutorialDiscoveries.ts) fires on exactly the rule that
// opens its screen, so "the Forge is open" is announced once, the moment the
// button stops being grey. The predicates live here and the discovery table
// imports them - two copies of "when is the Forge open" would drift, which is
// this codebase's dominant bug class.
//
// DERIVED FROM STATE, PLUS THE SEEN-SET. A rule reads the state packet the
// same way the tutorial does. A few of them read things that can go back
// down (gold, a season reset of levels and buildings), so a screen whose
// "new" card has been shown STAYS open - the seen-set is already synced to
// the server, so this holds across devices and seasons. And a player adopted
// on a new device has every reached moment marked seen at once, which is the
// same rule from the other side.
//
// The dev fixture is level 40 with every building, so it sees everything -
// which is itself a check that no rule is unreachable.
import type { StateUpdate } from '../net/protocol.generated';

/** The same shape tutorialDiscoveries.ts passes; restated to avoid a cycle. */
export interface UnlockFacts {
  hasGuild: boolean;
}

/** DelveRegistry.EntryFeeByRegion[0] on the server - the cheapest dive. */
export const DELVE_FIRST_ENTRY_GOLD = 7_000;

export const FORGE_OPEN_LEVEL = 5;
export const MARKET_OPEN_LEVEL = 10;

/** GuildManagementEngine.MinGuildInteractionLevel - the floor under every
 * guild's own MinApplicationLevel (serverMirrors.test.ts holds them together). */
export const GUILD_JOIN_MIN_LEVEL = 10;

/** AGE_PHASES in ui/slots.ts is Child / Adult / Veteran / Elder. */
const AGE_PHASE_VETERAN = 2;

/** inheritanceUpgradeCost(0) - the cheapest thing Inheritance sells. */
export const CHEAPEST_INHERITANCE_LEVEL = 40;

export const forgeOpen = (s: StateUpdate): boolean =>
  Number(s.CurrentLevel) >= FORGE_OPEN_LEVEL || Number(s.ForgeLevel) >= 1;

// Gold alone: once the card has been seen the screen stays open however the
// purse moves (see lockedRequirement).
export const delveOpen = (s: StateUpdate): boolean => Number(s.Gold) >= DELVE_FIRST_ENTRY_GOLD;

export const marketOpen = (s: StateUpdate): boolean => Number(s.CurrentLevel) >= MARKET_OPEN_LEVEL;

export const guildOpen = (s: StateUpdate, facts: UnlockFacts): boolean =>
  marketOpen(s) || facts.hasGuild;

export const breedingGroundsBuilt = (s: StateUpdate): boolean => Number(s.BreedingLevel) >= 1;

export const characterIsAgeing = (s: StateUpdate): boolean =>
  Math.max(Number(s.Slot1_AgePhase), Number(s.Slot2_AgePhase), Number(s.Slot3_AgePhase)) >=
  AGE_PHASE_VETERAN;

export const canBuyInheritance = (s: StateUpdate): boolean =>
  Number(s.PremiumCurrencyBalance) >= CHEAPEST_INHERITANCE_LEVEL;

interface UnlockRule {
  /** Nav keys this rule locks - a tabbed family shares one rule. */
  screens: readonly string[];
  /** What the greyed button says. Short: it sits beside the label. */
  requirement: string;
  open: (s: StateUpdate, facts: UnlockFacts) => boolean;
  /** Discovery ids whose card, once shown, keeps the screen open. */
  discoveries: readonly string[];
}

export const UNLOCK_RULES: readonly UnlockRule[] = [
  {
    screens: ['forge'],
    requirement: `Level ${FORGE_OPEN_LEVEL}`,
    open: forgeOpen,
    discoveries: ['forge'],
  },
  {
    screens: ['delve'],
    requirement: `${formatNumber(DELVE_FIRST_ENTRY_GOLD)} gold`,
    open: delveOpen,
    discoveries: ['delve'],
  },
  {
    screens: ['market'],
    requirement: `Level ${MARKET_OPEN_LEVEL}`,
    open: marketOpen,
    discoveries: ['market'],
  },
  {
    screens: ['guildops'],
    requirement: `Level ${MARKET_OPEN_LEVEL}`,
    open: guildOpen,
    discoveries: ['market', 'guild'],
  },
  {
    // Bloodline: breeding, the Hall of Ancestors and Inheritance. Any one of
    // the three becoming useful opens all three tabs.
    screens: ['breeding', 'ancestors', 'inheritance'],
    requirement: 'Breeding Grounds',
    open: (s) => breedingGroundsBuilt(s) || characterIsAgeing(s) || canBuyInheritance(s),
    discoveries: ['breeding', 'first_child', 'ancestors', 'inheritance'],
  },
];

/**
 * Why this screen is locked, or null when it is open. A missing snapshot
 * locks nothing: the menu must not flash grey while the first packet is on
 * its way.
 */
export function lockedRequirement(
  screen: string,
  snapshot: StateUpdate | null,
  facts: UnlockFacts,
  seen: ReadonlySet<string>,
): string | null {
  if (!snapshot) return null;
  const rule = UNLOCK_RULES.find((r) => r.screens.includes(screen));
  if (!rule) return null;
  if (rule.open(snapshot, facts)) return null;
  if (rule.discoveries.some((id) => seen.has(id))) return null;
  return rule.requirement;
}
