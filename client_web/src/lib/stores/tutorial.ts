// Modul: the WIRING for onboarding. The rules live in tutorialSteps.ts (tier
// one) and tutorialDiscoveries.ts (tier two).
//
// THE ORDER WAS THE PROBLEM, not the plumbing. The first version was a
// three-step machine - loot, then craft, then win a fight - advanced by
// notifyItemLooted, notifyItemCrafted and notifyCombatWon, and those three
// were wired correctly from the state store. It worked. It just asked for the
// wrong things in the wrong order:
//
//   - Crafting was step two, and a brand-new account has no materials. The
//     second instruction was one the player could not follow for a long time.
//   - "Win a fight" was step three, after looting - but you cannot loot before
//     you fight. The sequence described the middle of the game.
//   - Nothing in it mentioned the larder, which is the mechanic a new player
//     most needs and is least likely to find on their own. They die instead.
//
// Its interaction gate WAS dead: isInteractionAllowed had no callers anywhere.
// That is the one part of the old design worth being glad about - see below.
//
// The steps are re-chosen and read off the state packet rather than off
// events, which makes onboarding SELF-HEALING: a player who levelled in a
// closed tab, or who equipped something before any of this shipped, is shown
// the step they are actually on. An event-driven machine has to catch the
// moment or lose it forever.
//
// TIER TWO, added 2026-09-02, extends that same rule rather than replacing it.
// Seventeen discovery moments explain a system the first time the player
// reaches it. Everything about them is decided by a predicate over the packet;
// the only thing this file persists is which ones have been shown.
import { derived, get, writable } from 'svelte/store';
import { playerState } from './game';
import { nextTutorialStep, TutorialStep, type TutorialPrompt } from './tutorialSteps';
import {
  nextDiscovery,
  reachedDiscoveries,
  type DiscoveryMoment,
  type OnboardingFacts,
} from './tutorialDiscoveries';
import {
  adoptPlayer,
  markAllSeen,
  markSeen,
  seenExplanations,
} from './tutorialSeen';

export { TutorialStep, nextTutorialStep };
export type { TutorialPrompt };

const STORAGE_KEY = 'folkidle.tutorialDismissed';

/**
 * Dismissal is the ONLY global switch worth persisting. The steps themselves
 * are read from the packet every frame, so there is no progress here to lose -
 * which is also why the old storage key is not reused: it held a step NUMBER,
 * and restoring one would resurrect the very staleness this replaces.
 *
 * It now silences tier two as well. An idle game gets replayed by people who
 * already know it, and "skip onboarding" that only skipped the first three
 * steps would be a lie.
 */
const dismissed = writable<boolean>(readDismissed());

function readDismissed(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

export const onboardingDismissed = { subscribe: dismissed.subscribe };

export function skipTutorial(): void {
  dismissed.set(true);
  try {
    localStorage.setItem(STORAGE_KEY, '1');
  } catch {
    // A browser refusing storage should not break the game; the tutorial will
    // simply come back next session, which is the harmless direction to fail.
  }
}

/**
 * Undo a dismissal. Worth having because dismissing is one click, and a player
 * who did it by accident had no way back at all.
 */
export function unskipTutorial(): void {
  dismissed.set(false);
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // See skipTutorial.
  }
}

// ---------------------------------------------------------------------------
// The one fact the packet cannot carry
// ---------------------------------------------------------------------------

/**
 * Guild membership, supplied by whoever is already fetching it.
 *
 * Modul: there is no guild id on StateUpdate - see the comment on
 * OnboardingFacts. Rather than add a wire field to a packet already near its
 * 800-byte layout guard, the component that renders the coach panel asks
 * /api/v1/player/statistics (the same query GuildOps.svelte uses, so it shares
 * a cache) and pushes the answer in here.
 *
 * Null means "not asked yet". That distinction matters: baselining before the
 * answer arrives would decide a veteran has no guild.
 */
const facts = writable<OnboardingFacts | null>(null);

export function setOnboardingFacts(next: OnboardingFacts): void {
  const previous = get(facts);
  if (previous && previous.hasGuild === next.hasGuild) return;
  facts.set(next);
}

// ---------------------------------------------------------------------------
// Baseline
// ---------------------------------------------------------------------------

// Modul: a permanent subscription rather than a side effect inside a derived.
// A derived that mutated storage would run only while something was watching
// it, so a player who dismissed the panel would never get baselined - and
// would then be buried the day they un-dismissed it.
derived([playerState, facts], ([snapshot, factValue]) => ({ snapshot, factValue })).subscribe(
  ({ snapshot, factValue }) => {
    // Waits for the guild answer. It arrives once, early, and the cost of
    // waiting is at most a second of no coach panel on a cold start.
    if (!snapshot || !factValue) return;
    const playerId = Number(snapshot.PlayerId) || 0;
    if (playerId <= 0) return;
    if (adoptPlayer(playerId)) {
      markAllSeen(reachedDiscoveries(snapshot, factValue));
    }
  },
);

// ---------------------------------------------------------------------------
// What to show
// ---------------------------------------------------------------------------

/**
 * One cue at a time, whichever tier it comes from.
 *
 * Tier one wins outright: a player who has not yet hit anything is not asked
 * to think about the Hall of Ancestors. Only once all three first-session
 * steps are done does the discovery table get a turn, and then it hands over
 * the earliest unseen moment the player has reached.
 */
export interface OnboardingCue {
  kind: 'step' | 'discovery';
  /** Stable id - a discovery id, or `step:<n>` for the first-session chain. */
  id: string;
  /** 1-based position in the first-session chain; 0 for a discovery. */
  index: number;
  total: number;
  /** The nav key of the screen this is about. */
  screen: string;
  title: string;
  body: string;
}

export const onboardingCue = derived(
  [playerState, facts, dismissed, seenExplanations],
  ([snapshot, factValue, isDismissed, seen]): OnboardingCue | null => {
    if (isDismissed) return null;

    const step = nextTutorialStep(snapshot);
    if (step) {
      return {
        kind: 'step',
        id: `step:${step.step}`,
        index: step.index,
        total: step.total,
        screen: step.screen,
        title: step.title,
        body: step.body,
      };
    }

    if (!snapshot || !factValue) return null;
    const moment: DiscoveryMoment | null = nextDiscovery(snapshot, factValue, seen);
    if (!moment) return null;
    return {
      kind: 'discovery',
      id: moment.id,
      index: 0,
      total: 0,
      screen: moment.screen,
      title: moment.title,
      body: moment.body,
    };
  },
);

/** Acknowledge the cue on screen. Steps cannot be acknowledged - they are done
 * by playing - so this only ever marks a discovery. */
export function acknowledgeCue(): void {
  const cue = get(onboardingCue);
  if (cue && cue.kind === 'discovery') markSeen(cue.id);
}

/**
 * The nav key the coach panel is pointing at, or null.
 *
 * Modul: this is the coach-mark. The panel pulses the real control rather than
 * floating a bubble next to it, which gets the "points at the thing" benefit
 * with no positioning maths - and therefore no way to clip at a narrow
 * container width, which is precisely the bug class an anchored bubble creates.
 */
export const coachTargetScreen = derived(onboardingCue, (cue) => cue?.screen ?? null);

/**
 * The current step, or null when there is nothing to say.
 *
 * Kept under its old name and old shape for Settings.svelte, which asks only
 * about the first-session chain.
 *
 * Modul: it does NOT arm from IsFreshAccount. That flag is true only while the
 * first character has never aged, so it turns false on its own after a while -
 * and took the tutorial with it, mid-onboarding, for a player who had done
 * none of it. What matters is whether the three things are done, not how old
 * the account is.
 */
export const tutorialPrompt = derived(
  [playerState, dismissed],
  ([snapshot, isDismissed]): TutorialPrompt | null =>
    isDismissed ? null : nextTutorialStep(snapshot),
);

/** True while anything is outstanding - for anything that wants to know. */
export const tutorialActive = derived(onboardingCue, (cue) => cue !== null);

/**
 * Modul: kept as a no-op with its old name, and it was ALREADY a no-op in
 * practice - nothing ever called it. Retained deliberately so the idea does
 * not come back: it funnelled the player by DISABLING every screen except the
 * one the current step needed, which in a game whose whole promise is that it
 * runs without you means locking someone out of their own village to teach
 * them about fishing. The panel points; it does not fence.
 */
export function isInteractionAllowed(): boolean {
  return true;
}

/** Compatibility for callers that only need "is onboarding finished". */
export const tutorialStep = derived(tutorialPrompt, (prompt) =>
  prompt === null ? TutorialStep.Completed : prompt.step,
);

export function currentPrompt(): string {
  return get(onboardingCue)?.body ?? '';
}
