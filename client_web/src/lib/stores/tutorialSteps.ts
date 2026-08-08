// Modul: THE RULE, with nothing else in it.
//
// Which of the three onboarding steps is outstanding is a pure function of one
// state packet - no stores, no browser, no network. It lived in the store file
// next to localStorage and a derived over the live connection, which meant a
// test could not reach the rule without dragging in an AudioContext and a
// WebSocket that do not exist in a node runner.
//
// Testing the wiring is not the same as testing the rule, and only one of the
// two is worth a test here.
import type { StateUpdate } from '../net/protocol.generated';

export const TutorialStep = {
  Inactive: 0,
  WinAFight: 1,
  EquipADrop: 2,
  StockTheLarder: 3,
  Completed: 4,
} as const;

export type TutorialStepValue = (typeof TutorialStep)[keyof typeof TutorialStep];





/** Each step, and the fact on the wire that means it is done. */
const STEPS = [
  {
    step: TutorialStep.WinAFight,
    /** Level 2 is the first thing that cannot happen without a kill. */
    done: (s: StateUpdate) => s.CurrentLevel >= 2,
    screen: 'combat' as const,
    title: 'Pick a fight',
    body:
      'Open Combat and press Fight on Field Mouse. Your character keeps fighting on its own, ' +
      'even after you close the page.',
  },
  {
    step: TutorialStep.EquipADrop,
    done: (s: StateUpdate) => Number(s.EquippedWeaponId) > 0,
    screen: 'character' as const,
    title: 'Put something on',
    body:
      'Monsters drop equipment. Open Character and click a slot to wear it - gear is where ' +
      'nearly all of your power comes from, not levels.',
  },
  {
    step: TutorialStep.StockTheLarder,
    // Modul: ALL THREE SLOTS. This read Food1_Count alone, so a player whose
    // food sat in slot two or three was told to fill a larder that was full -
    // caught by a screenshot where the banner asked for food beside a briefing
    // reporting 1,609 bites. The larder is three slots and any of them counts.
    done: (s: StateUpdate) =>
      Number(s.Food1_Count) + Number(s.Food2_Count) + Number(s.Food3_Count) > 0,
    screen: 'larder' as const,
    title: 'Fill the larder',
    body:
      'Fish, then load the food into Auto-Eat. It heals you mid-fight, and without it the ' +
      'fourth monster of a region will kill you.',
  },
] as const;

export type TutorialPrompt = {
  step: TutorialStepValue;
  index: number;
  total: number;
  screen: 'combat' | 'character' | 'larder';
  title: string;
  body: string;
};

/**
 * The current step, or null when there is nothing to say.
 *
 * Modul: split into a PURE function and the store that feeds it. The logic is
 * "given a snapshot, what is outstanding", which needs no stores and no
 * browser - and a test that had to import the state store to reach it dragged
 * in localStorage, an AudioContext and a WebSocket, none of which exist in a
 * node test runner. Testing the wiring is not the same as testing the rule.
 *
 * Modul: it does NOT arm from IsFreshAccount any more. That flag is true only
 * while the first character has never aged, so it goes false on its own after
 * a while and took the tutorial with it, mid-onboarding, for a player who had
 * done none of it. What matters is whether the three things are done, not how
 * old the account is - and a returning player who has done them sees nothing
 * either way.
 */
export function nextTutorialStep(snapshot: StateUpdate | null): TutorialPrompt | null {
  if (!snapshot) return null;

  for (let i = 0; i < STEPS.length; i++) {
    if (!STEPS[i].done(snapshot)) {
      return {
        step: STEPS[i].step,
        index: i + 1,
        total: STEPS.length,
        screen: STEPS[i].screen,
        title: STEPS[i].title,
        body: STEPS[i].body,
      };
    }
  }
  return null;
}
