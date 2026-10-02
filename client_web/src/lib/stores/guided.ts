import { writable } from 'svelte/store';
import type { StateUpdate } from '../net/protocol.generated';
import { nextTutorialStep, TutorialStep } from './tutorialSteps';

/*
 * Modul: THE GUIDED FIRST MINUTE (owner, 2026-09-28).
 *
 * The first two tutorial steps are not advice, they are a walk-through in the
 * style of a mobile game's tutorial: the screen dims, ONE control is lit, and
 * nothing else can be pressed until the player presses it. An earlier pass
 * deliberately removed a fence like this ("the panel points; it does not
 * fence" - tutorial.ts), because a fence that lasts locks a player out of their
 * own village. The owner chose the fence knowingly, so it is kept small instead:
 * two actions, about twenty seconds, a Skip on screen the whole time, and a
 * layer that FAILS OPEN - if the control it wants is not on the page, it does
 * not block at all (GuidedOverlay.svelte).
 *
 * Pure, like tutorialSteps.ts: given a snapshot, which stage is due. The DOM
 * half lives in the component.
 */

export interface GuideStage {
  /** Nav key of the screen the stage happens on. */
  screen: string;
  /** What the "take me there" button says. */
  screenLabel: string;
  /**
   * `data-guide` values, MOST specific first. The overlay lights the first one
   * present on the page - so "wear this" wins over "open the slot" once the
   * picker is open.
   */
  targets: readonly string[];
  /** One line per target, same order. */
  captions: readonly string[];
}

const LARDER: GuideStage = {
  screen: 'larder',
  screenLabel: 'Go to Auto-Eat',
  targets: ['larder-load'],
  captions: ['Load your fish into the larder. You eat them automatically when you are hurt.'],
};

const WEAPON: GuideStage = {
  screen: 'character',
  screenLabel: 'Go to Character',
  targets: ['wear-first', 'slot-0'],
  captions: ['Wear your claymore.', 'Tap the weapon slot - your claymore is in the chest.'],
};

export function guidedStage(snapshot: StateUpdate | null, dismissed: boolean): GuideStage | null {
  if (dismissed || !snapshot) return null;
  const step = nextTutorialStep(snapshot);
  if (!step) return null;
  if (step.step === TutorialStep.StockTheLarder) return LARDER;
  if (step.step === TutorialStep.EquipADrop) return WEAPON;
  return null;
}

/**
 * Modul: task 109 - whether GuidedOverlay is covering the screen right now,
 * written by the overlay alone. The coach panel hides while it is: the guided
 * card and the coach pill used to show at once, two tutorials talking over
 * each other. A stage can be due while the overlay is off (a modal is up, or
 * the control never appeared), so this is the overlay's own `mode`, not
 * `guidedStage() !== null`.
 */
export const guidedShowing = writable(false);
