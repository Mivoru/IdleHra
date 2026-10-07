// Modul: the quest panel's RULES, as pure functions over the server's answer.
// No stores, no network, no DOM - so the panel logic is testable in a node
// runner, the same split tutorialSteps.ts makes.
//
// Nothing here decides whether a step is done or what it pays: that is the
// server's (QuestLineEngine). This only chooses WHAT TO SHOW from the answer.
import type { QuestLine, QuestStep } from '../net/quests';

/** The steps whose reward is waiting, in the owner's order. */
export function claimableSteps(line: QuestLine | null | undefined): QuestStep[] {
  return (line?.Steps ?? []).filter((s) => s.State === 'done');
}

/**
 * The step to put in front of the player.
 *
 * Modul: A SKIPPED STEP STAYS IN THE LIST UNTIL DONE, and "what is next" is the
 * EARLIEST step that is neither done nor locked - so skipping step 2 does not
 * hide it, it just stops being the highlight while step 3 is possible. A claim
 * waiting beats everything: money on the table is the one thing a player
 * should not have to look for.
 */
export function nextQuest(line: QuestLine | null | undefined): QuestStep | null {
  const steps = [...(line?.Steps ?? [])].sort((a, b) => a.Order - b.Order);
  const waiting = steps.find((s) => s.State === 'done');
  if (waiting) return waiting;
  const available = steps.find((s) => s.State === 'available');
  if (available) return available;
  // Everything possible is claimed; show the nearest thing still locked, with
  // its unlock hint, so the player knows what to work towards.
  return steps.find((s) => s.State === 'locked') ?? null;
}

/** True once every step has been claimed - the panel retires itself. */
export function questLineFinished(line: QuestLine | null | undefined): boolean {
  return !!line && line.Total > 0 && line.Claimed >= line.Total;
}

export function questSummary(line: QuestLine): string {
  const waiting = claimableSteps(line).length;
  const base = `${line.Done} of ${line.Total} done`;
  return waiting > 0 ? `${base} - ${waiting} to claim` : base;
}

/** The first `data-guide` target the page has, or null. `has` asks the DOM. */
export function pickGuideTarget(targets: readonly string[], has: (target: string) => boolean): string | null {
  for (const target of targets) if (has(target)) return target;
  return null;
}

/** "1,750 gold + 25 birch log + 25 copper ore", from the server's reward. */
export function describeReward(reward: QuestLine['Reward'], pretty: (itemId: string) => string): string {
  return `${reward.Gold.toLocaleString('en-US')} gold + ${reward.MaterialQuantity} ${pretty(reward.MaterialLog)} + ${reward.MaterialQuantity} ${pretty(reward.MaterialOre)}`;
}

/** Whether the spotlight for a step is still worth showing: only while the act is open. */
export function spotlightStillWanted(line: QuestLine | null | undefined, stepId: string): boolean {
  const step = line?.Steps.find((s) => s.Id === stepId);
  // Unknown (not loaded yet) keeps it; done/claimed/locked retire it.
  return step === undefined ? true : step.State === 'available';
}
