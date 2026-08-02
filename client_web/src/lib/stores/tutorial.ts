// Modul: the FTUE state machine, ported from
// client/Assets/Scripts/Engine/TutorialStateMachine.cs.
//
// A PORT, NOT A REIMPLEMENTATION. That file is pure C# with no UnityEngine
// reference precisely so it can be shared - the server's xUnit suite compiles
// it verbatim through a csproj link and asserts on its behaviour. This is the
// same rules in TypeScript, and where the original's comments explain WHY a
// rule exists they are carried across rather than summarised, because the
// reasons are the parts a reimplementation loses.
//
// Ideally this would be generated like the protocol types are. It is not, and
// that is a real (small) second source of truth - so the rules below are
// mirrored exactly and covered by tests that mirror the server's own.

import { writable, get } from 'svelte/store';

// Values are explicit and contiguous because the server-side integration tests
// assert on the numeric ordering Inactive < LootFirstItem < CraftFirstItem <
// WinFirstCombat < Completed rather than on names.
export const TutorialStep = {
  Inactive: 0,
  LootFirstItem: 1,
  CraftFirstItem: 2,
  WinFirstCombat: 3,
  Completed: 4,
} as const;

export type TutorialStepValue = (typeof TutorialStep)[keyof typeof TutorialStep];

// The coarse UI surfaces the interaction gate can block. Deliberately a closed
// list of top-level screens, not per-button ids - the tutorial only funnels the
// player toward one screen at a time.
export type TutorialUiElement =
  | 'Inventory'
  | 'Forge'
  | 'Arena'
  | 'Market'
  | 'Guild'
  | 'SkillTree'
  | 'Chat'
  | 'Settings';

const STORAGE_KEY = 'folkidle.tutorialStep';

export class TutorialStateMachine {
  private step: TutorialStepValue = TutorialStep.Inactive;

  get currentStep(): TutorialStepValue {
    return this.step;
  }

  /** "Active" means inside the guided flow (steps 1-3). Inactive and Completed are both unrestricted. */
  get isActive(): boolean {
    return this.step >= TutorialStep.LootFirstItem && this.step <= TutorialStep.WinFirstCombat;
  }

  onStepChanged: ((step: TutorialStepValue) => void) | null = null;

  /**
   * Idempotent - it only arms the tutorial from the pristine Inactive state.
   * A re-login on an account that already progressed, completed or skipped
   * must never restart the flow.
   */
  begin(): void {
    if (this.step !== TutorialStep.Inactive) return;
    this.transition(TutorialStep.LootFirstItem);
  }

  // Each notify advances only when the machine is sitting on exactly the
  // matching step. Out-of-order signals are DROPPED, not queued: crafting
  // during LootFirstItem must not let the player skip the loot step, and a
  // stale combat win arriving after completion is a harmless no-op.
  notifyItemLooted(): void {
    if (this.step !== TutorialStep.LootFirstItem) return;
    this.transition(TutorialStep.CraftFirstItem);
  }

  notifyItemCrafted(): void {
    if (this.step !== TutorialStep.CraftFirstItem) return;
    this.transition(TutorialStep.WinFirstCombat);
  }

  notifyCombatWon(): void {
    if (this.step !== TutorialStep.WinFirstCombat) return;
    this.transition(TutorialStep.Completed);
  }

  /**
   * Opt-out escape hatch, valid from any state including Inactive - a player
   * may skip before the first step ever arms. The only no-op is already being
   * Completed, so the completion event never fires twice.
   */
  skip(): void {
    if (this.step === TutorialStep.Completed) return;
    this.transition(TutorialStep.Completed);
  }

  /**
   * While active, ONLY the single screen the current step needs is
   * interactable. Settings is exempt unconditionally - a tutorial must never
   * trap a player away from settings or sign-out. Outside the active range
   * everything is allowed.
   */
  isInteractionAllowed(element: TutorialUiElement): boolean {
    if (!this.isActive) return true;
    if (element === 'Settings') return true;

    switch (this.step) {
      case TutorialStep.LootFirstItem:
        return element === 'Inventory';
      case TutorialStep.CraftFirstItem:
        return element === 'Forge';
      case TutorialStep.WinFirstCombat:
        return element === 'Arena';
      default:
        return true;
    }
  }

  /** Restores a persisted step. The C# original leaves persistence to its driver. */
  restore(step: TutorialStepValue): void {
    this.step = step;
  }

  private transition(next: TutorialStepValue): void {
    this.step = next;
    this.onStepChanged?.(next);
  }
}

// ---------------------------------------------------------------------------
// The driver - everything the C# version deliberately kept out of the machine
// ---------------------------------------------------------------------------

export const tutorialStep = writable<TutorialStepValue>(TutorialStep.Inactive);

const machine = new TutorialStateMachine();
machine.onStepChanged = (step) => {
  tutorialStep.set(step);
  localStorage.setItem(STORAGE_KEY, String(step));
};

export function initTutorial(isFreshAccount: boolean): void {
  const stored = Number(localStorage.getItem(STORAGE_KEY));
  if (Number.isInteger(stored) && stored >= 0 && stored <= TutorialStep.Completed) {
    machine.restore(stored as TutorialStepValue);
    tutorialStep.set(machine.currentStep);
    return;
  }

  // IsFreshAccount is the server's own signal - true when this account's first
  // character has never aged - and is what the Unity controller arms from too.
  if (isFreshAccount) machine.begin();
  tutorialStep.set(machine.currentStep);
}

export const notifyItemLooted = () => machine.notifyItemLooted();
export const notifyItemCrafted = () => machine.notifyItemCrafted();
export const notifyCombatWon = () => machine.notifyCombatWon();
export const skipTutorial = () => machine.skip();

export function isInteractionAllowed(element: TutorialUiElement): boolean {
  return machine.isInteractionAllowed(element);
}

export const STEP_PROMPTS: Record<number, string> = {
  [TutorialStep.LootFirstItem]: 'Fight something and pick up your first drop.',
  [TutorialStep.CraftFirstItem]: 'Take what you found to the Forge and make something.',
  [TutorialStep.WinFirstCombat]: 'Win a fight with what you made.',
};

export function currentPrompt(): string {
  return STEP_PROMPTS[get(tutorialStep)] ?? '';
}
