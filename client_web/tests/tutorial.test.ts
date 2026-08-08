import { describe, it, expect } from 'vitest';
import { nextTutorialStep, TutorialStep } from '../src/lib/stores/tutorialSteps';

// Modul: this file used to hold PARITY tests against TutorialStateMachine.cs,
// which the server's csproj links out of client/Assets/Scripts/Engine and
// tests with xUnit. That machine belongs to the UNITY client, which is
// abandoned - so the parity being defended was with a program nobody runs, and
// the port it was defending is gone.
//
// The C# original and its server-side test are left in place: deleting Unity
// code is a separate decision from replacing the web client's onboarding, and
// it is not one to make quietly inside a tutorial change.
//
// What is tested here instead is the thing that now decides what a new player
// is told: given a state packet, which of the three steps is outstanding. It
// is a pure function of the snapshot, which is exactly why it was worth
// rewriting this way.

/** The fields the tutorial reads, with everything else left off. */
function snapshot(fields: Record<string, number>): any {
  return {
    CurrentLevel: 1,
    EquippedWeaponId: 0,
    Food1_Count: 0,
    Food2_Count: 0,
    Food3_Count: 0,
    ...fields,
  };
}

describe('what a new player is told next', () => {
  it('says nothing before the first packet arrives', () => {
    expect(nextTutorialStep(null)).toBeNull();
  });

  it('starts with the fight, because nothing else can happen first', () => {
    const prompt = nextTutorialStep(snapshot({}))!;
    expect(prompt.step).toBe(TutorialStep.WinAFight);
    expect(prompt.screen).toBe('combat');
    expect(prompt.index).toBe(1);
  });

  it('moves to gear once a level has been earned', () => {
    expect(nextTutorialStep(snapshot({ CurrentLevel: 2 }))!.step).toBe(TutorialStep.EquipADrop);
  });

  it('moves to the larder once something is worn', () => {
    const prompt = nextTutorialStep(snapshot({ CurrentLevel: 2, EquippedWeaponId: 41 }))!;
    expect(prompt.step).toBe(TutorialStep.StockTheLarder);
    expect(prompt.screen).toBe('larder');
  });

  it('falls silent when all three are done', () => {
    expect(
      nextTutorialStep(snapshot({ CurrentLevel: 2, EquippedWeaponId: 41, Food1_Count: 30 })),
    ).toBeNull();
  });

  // Modul: THE POINT OF READING STATE RATHER THAN EVENTS. A player who did the
  // first two things in a closed tab - or before any of this shipped - is
  // shown the step they are actually on. The event-driven version had to be
  // watching at the moment each thing happened, and a missed moment was
  // missed for good.
  it('skips ahead for a player who arrives having already done the work', () => {
    expect(nextTutorialStep(snapshot({ CurrentLevel: 40, EquippedWeaponId: 7 }))!.step).toBe(
      TutorialStep.StockTheLarder,
    );
  });

  // Modul: dismissal is NOT tested here. It lives in the store, behind
  // localStorage, and reaching it would drag the whole state store into a node
  // test runner - which is the coupling this split exists to remove.
});
