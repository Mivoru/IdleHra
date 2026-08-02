import { describe, it, expect } from 'vitest';
import {
  TutorialStateMachine,
  TutorialStep,
  type TutorialUiElement,
} from '../src/lib/stores/tutorial';

// Modul: TutorialStateMachine.cs is pure C# specifically so the server's xUnit
// suite can compile it verbatim through a csproj link and assert on it. This
// TypeScript version is a PORT, which means it is a second source of truth -
// small, but real. These tests exist to hold it to the same rules the C# suite
// holds the original to, so the two cannot drift silently.
//
// Every case below is one of the original's own documented rules, not a
// property invented here.

const ALL_ELEMENTS: TutorialUiElement[] = [
  'Inventory',
  'Forge',
  'Arena',
  'Market',
  'Guild',
  'SkillTree',
  'Chat',
  'Settings',
];

describe('step ordering', () => {
  it('keeps the numeric ordering the server tests assert on', () => {
    // The C# enum's values are explicit and contiguous because the server
    // asserts on the ORDER, not the names.
    expect(TutorialStep.Inactive).toBeLessThan(TutorialStep.LootFirstItem);
    expect(TutorialStep.LootFirstItem).toBeLessThan(TutorialStep.CraftFirstItem);
    expect(TutorialStep.CraftFirstItem).toBeLessThan(TutorialStep.WinFirstCombat);
    expect(TutorialStep.WinFirstCombat).toBeLessThan(TutorialStep.Completed);
  });
});

describe('begin', () => {
  it('arms from the pristine state', () => {
    const machine = new TutorialStateMachine();
    machine.begin();
    expect(machine.currentStep).toBe(TutorialStep.LootFirstItem);
  });

  it('is idempotent - a re-login never restarts a flow in progress', () => {
    const machine = new TutorialStateMachine();
    machine.begin();
    machine.notifyItemLooted();
    machine.begin();
    expect(machine.currentStep).toBe(TutorialStep.CraftFirstItem);
  });

  it('never restarts a completed flow', () => {
    const machine = new TutorialStateMachine();
    machine.skip();
    machine.begin();
    expect(machine.currentStep).toBe(TutorialStep.Completed);
  });
});

describe('signals', () => {
  it('advances through the ladder in order', () => {
    const machine = new TutorialStateMachine();
    machine.begin();
    machine.notifyItemLooted();
    expect(machine.currentStep).toBe(TutorialStep.CraftFirstItem);
    machine.notifyItemCrafted();
    expect(machine.currentStep).toBe(TutorialStep.WinFirstCombat);
    machine.notifyCombatWon();
    expect(machine.currentStep).toBe(TutorialStep.Completed);
  });

  it('DROPS out-of-order signals rather than queueing them', () => {
    // Crafting during LootFirstItem must not let the player skip the loot
    // step - the original is explicit that these are dropped, not queued.
    const machine = new TutorialStateMachine();
    machine.begin();
    machine.notifyItemCrafted();
    machine.notifyCombatWon();
    expect(machine.currentStep).toBe(TutorialStep.LootFirstItem);
  });

  it('treats a stale signal after completion as a harmless no-op', () => {
    const machine = new TutorialStateMachine();
    machine.skip();
    machine.notifyCombatWon();
    expect(machine.currentStep).toBe(TutorialStep.Completed);
  });

  it('fires the change event exactly once per transition', () => {
    // Every transition funnels through one place in the original precisely so
    // this holds.
    const machine = new TutorialStateMachine();
    const seen: number[] = [];
    machine.onStepChanged = (step) => seen.push(step);

    machine.begin();
    machine.begin(); // idempotent, must not fire again
    machine.notifyItemLooted();
    machine.notifyItemCrafted();
    machine.notifyCombatWon();
    machine.skip(); // already Completed, must not fire again

    expect(seen).toEqual([
      TutorialStep.LootFirstItem,
      TutorialStep.CraftFirstItem,
      TutorialStep.WinFirstCombat,
      TutorialStep.Completed,
    ]);
  });
});

describe('interaction gate', () => {
  it('allows everything when inactive or completed', () => {
    const inactive = new TutorialStateMachine();
    for (const element of ALL_ELEMENTS) {
      expect(inactive.isInteractionAllowed(element)).toBe(true);
    }

    const done = new TutorialStateMachine();
    done.skip();
    for (const element of ALL_ELEMENTS) {
      expect(done.isInteractionAllowed(element)).toBe(true);
    }
  });

  it('funnels to exactly one screen per active step', () => {
    const expected: [number, TutorialUiElement][] = [
      [TutorialStep.LootFirstItem, 'Inventory'],
      [TutorialStep.CraftFirstItem, 'Forge'],
      [TutorialStep.WinFirstCombat, 'Arena'],
    ];

    const machine = new TutorialStateMachine();
    machine.begin();

    for (const [step, allowed] of expected) {
      expect(machine.currentStep).toBe(step);
      for (const element of ALL_ELEMENTS) {
        // Settings is exempt; see below.
        const shouldAllow = element === allowed || element === 'Settings';
        expect(machine.isInteractionAllowed(element), `${step} / ${element}`).toBe(shouldAllow);
      }

      if (step === TutorialStep.LootFirstItem) machine.notifyItemLooted();
      else if (step === TutorialStep.CraftFirstItem) machine.notifyItemCrafted();
    }
  });

  it('NEVER blocks Settings, at any active step', () => {
    // A tutorial must never trap a player away from settings or sign-out.
    const machine = new TutorialStateMachine();
    machine.begin();
    expect(machine.isInteractionAllowed('Settings')).toBe(true);
    machine.notifyItemLooted();
    expect(machine.isInteractionAllowed('Settings')).toBe(true);
    machine.notifyItemCrafted();
    expect(machine.isInteractionAllowed('Settings')).toBe(true);
  });
});

describe('skip', () => {
  it('works from any state, including before the flow ever armed', () => {
    const fresh = new TutorialStateMachine();
    fresh.skip();
    expect(fresh.currentStep).toBe(TutorialStep.Completed);

    const midway = new TutorialStateMachine();
    midway.begin();
    midway.notifyItemLooted();
    midway.skip();
    expect(midway.currentStep).toBe(TutorialStep.Completed);
  });
});
