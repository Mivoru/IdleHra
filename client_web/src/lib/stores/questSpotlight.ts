import { writable } from 'svelte/store';
import { requestScreen } from './navigation';
import type { QuestStep } from '../net/quests';

// Modul: "SHOW ME" - the quest line's highlight, built on the same `data-guide`
// attributes the guided first minute uses (GuidedOverlay.svelte) and NOT on its
// fence. The guided overlay covers the screen and only the lit control can be
// pressed, which is right for twenty seconds of "wear your claymore" and wrong
// for a feature tour a player may want to put down: this one lights the control
// with a ring that cannot take a click (pointer-events: none) and leaves every
// other control working. Dismissing it, finishing the act or the step leaving
// "available" all retire it.
//
// The server names the screen and the targets (QuestLineRegistry); nothing about
// where a button lives is written down here.
export interface QuestSpotlight {
  stepId: string;
  screen: string;
  /** `data-guide` values, most specific first. */
  targets: readonly string[];
  title: string;
  caption: string;
}

const spotlight = writable<QuestSpotlight | null>(null);

export const questSpotlight = { subscribe: spotlight.subscribe };

/** Navigates to the step's screen and starts lighting its control. */
export function showMe(step: QuestStep): void {
  spotlight.set({
    stepId: step.Id,
    screen: step.Screen,
    targets: step.GuideTargets,
    title: step.Title,
    caption: step.Explanation,
  });
  requestScreen(step.Screen);
}

export function clearSpotlight(): void {
  spotlight.set(null);
}
