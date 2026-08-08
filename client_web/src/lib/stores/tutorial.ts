// Modul: the WIRING for onboarding. The rule lives in tutorialSteps.ts.
//
// THE ORDER WAS THE PROBLEM, not the plumbing. The previous version was a
// three-step machine - loot, then craft, then win a fight - advanced by
// notifyItemLooted, notifyItemCrafted and notifyCombatWon, and those three
// were wired correctly from the state store. It worked. It just asked for the
// wrong things in the wrong order:
//
//   - Crafting was step two, and a brand-new account has no materials. The
//     second instruction was one the player could not follow for a long time.
//     (game.ts still carries a note about that step having been outright
//     impossible until a counter was fixed.)
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
import { derived, get, writable } from 'svelte/store';
import { playerState } from './game';
import { nextTutorialStep, TutorialStep, type TutorialPrompt } from './tutorialSteps';

export { TutorialStep, nextTutorialStep };
export type { TutorialPrompt };

const STORAGE_KEY = 'folkidle.tutorialDismissed';

/**
 * Dismissal is the ONLY thing worth persisting. The steps themselves are read
 * from the packet every frame, so there is no progress here to lose - which is
 * also why the old storage key is not reused: it held a step NUMBER, and
 * restoring one would resurrect the very staleness this replaces.
 */
const dismissed = writable<boolean>(readDismissed());

function readDismissed(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

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

/**
 * The current step, or null when there is nothing to say.
 *
 * Modul: it does NOT arm from IsFreshAccount any more. That flag is true only
 * while the first character has never aged, so it turns false on its own after
 * a while - and took the tutorial with it, mid-onboarding, for a player who
 * had done none of it. What matters is whether the three things are done, not
 * how old the account is.
 */
export const tutorialPrompt = derived(
  [playerState, dismissed],
  ([snapshot, isDismissed]): TutorialPrompt | null =>
    isDismissed ? null : nextTutorialStep(snapshot),
);

/** True while any step is outstanding - for anything that wants to know. */
export const tutorialActive = derived(tutorialPrompt, (prompt) => prompt !== null);

/**
 * Modul: kept as a no-op with its old name, and it was ALREADY a no-op in
 * practice - nothing ever called it. Retained deliberately so the idea does
 * not come back: it funnelled the player by DISABLING every screen except the
 * one the current step needed, which in a game whose whole promise is that it
 * runs without you means locking someone out of their own village to teach
 * them about fishing. The banner points; it does not fence.
 */
export function isInteractionAllowed(): boolean {
  return true;
}

/** Compatibility for callers that only need "is onboarding finished". */
export const tutorialStep = derived(tutorialPrompt, (prompt) =>
  prompt === null ? TutorialStep.Completed : prompt.step,
);

export function currentPrompt(): string {
  return get(tutorialPrompt)?.body ?? '';
}
