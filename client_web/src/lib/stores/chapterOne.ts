// Book of Deeds, Chapter I - The Village Road.
//
// This is the onboarding, expressed as content instead of as popups. See
// docs/architecture/LONG_GAME_SPEC.md: a checklist with rewards teaches the
// ORDER of the game, survives past the first five minutes, and is still there
// the next day when the player has forgotten what they were doing. Three
// dismissible tooltips are not.
//
// PURE, for the same reason tutorialSteps.ts is pure: "given a snapshot, what
// is done" needs no stores, no browser and no network, and a test that had to
// import the live store would drag in an AudioContext and a WebSocket that do
// not exist in a node runner.
//
// EVERY DEED READS A FIELD ALREADY ON THE WIRE. That is a deliberate
// constraint on this first pass, not a coincidence - it means the chapter
// ships without a packet change, a migration or a server round trip, and the
// rewards (a Seal and a set of Common tools) can follow as their own piece of
// work rather than holding the teaching part hostage.
//
// ONE SUBSTITUTION FROM THE SPEC: the spec lists "cook and eat a meal", and
// nothing on the wire counts meals eaten. "Stock the larder" stands in - it
// is the step that actually prevents the death the spec was worried about,
// and it is already the tutorial's third prompt.
import type { StateUpdate } from '../net/protocol.generated';

export interface Deed {
  id: string;
  title: string;
  /** What to do, in the imperative, naming the screen to open. */
  body: string;
  /** The screen this deed is performed on, for a jump link. */
  screen: 'combat' | 'character' | 'larder' | 'gathering' | 'crafting';
  /** Progress so far, and the target. Both 1 for a yes/no deed. */
  progress: (s: StateUpdate) => number;
  target: number;
}

/**
 * The six deeds, in the order a player should meet them.
 *
 * The order is the lesson: fight, wear what drops, eat, gather, make
 * something, then keep going. A new player who does these in sequence has
 * touched every loop the game has.
 */
export const CHAPTER_ONE: readonly Deed[] = [
  {
    id: 'first-blood',
    title: 'Win your first fight',
    body: 'Open Combat and send your character at Field Mouse. It keeps fighting on its own, even after you close the page.',
    screen: 'combat',
    // Level 2 is the first thing that cannot happen without a kill, and there
    // is no lifetime kill total on the wire to count against instead.
    progress: (s) => Math.min(Number(s.CurrentLevel) || 0, 2),
    target: 2,
  },
  {
    id: 'dress-up',
    title: 'Wear a weapon',
    body: 'Monsters drop equipment. Open Character and click the weapon slot - gear is where nearly all of your power comes from, not levels.',
    screen: 'character',
    progress: (s) => (Number(s.EquippedWeaponId) > 0 ? 1 : 0),
    target: 1,
  },
  {
    id: 'stock-larder',
    title: 'Fill the larder',
    body: 'Load food into Auto-Eat. It heals you mid-fight, and without it the fourth monster of a region will kill you.',
    screen: 'larder',
    // All three slots. Reading slot one alone told players with food in slots
    // two or three to fill a larder that was already full.
    progress: (s) =>
      Math.min(
        Number(s.Food1_Count || 0) + Number(s.Food2_Count || 0) + Number(s.Food3_Count || 0),
        1,
      ),
    target: 1,
  },
  {
    id: 'hundred-logs',
    title: 'Gather 100 wood',
    body: 'Open Gathering and set your character to chop. Wood is what the village and half of crafting are built from.',
    screen: 'gathering',
    // Current stock rather than a lifetime total, which the wire does not
    // carry. Spending the wood un-completes the deed, and that is the right
    // behaviour for a teaching step: it means "have 100 wood", which is what
    // the next deed needs.
    progress: (s) => Math.min(Number(s.CachedWoodStock) || 0, 100),
    target: 100,
  },
  {
    id: 'first-craft',
    title: 'Craft something',
    body: 'Take your materials to Crafting. Made gear beats found gear at the same level, and it is how you choose what you get.',
    screen: 'crafting',
    progress: (s) => Math.min(Number(s.TotalItemsCraftedCount) || 0, 1),
    target: 1,
  },
  {
    id: 'level-ten',
    title: 'Reach level 10',
    body: 'Keep a fight running. Everything above happens once; this one just means you have settled in.',
    screen: 'combat',
    progress: (s) => Math.min(Number(s.CurrentLevel) || 0, 10),
    target: 10,
  },
] as const;

export interface DeedState extends Deed {
  current: number;
  done: boolean;
}

/** Every deed with its live progress attached. */
export function chapterOneState(snapshot: StateUpdate | null): DeedState[] {
  return CHAPTER_ONE.map((deed) => {
    const current = snapshot ? deed.progress(snapshot) : 0;
    return { ...deed, current, done: current >= deed.target };
  });
}

/** How many of the six are done. */
export function completedCount(snapshot: StateUpdate | null): number {
  return chapterOneState(snapshot).filter((d) => d.done).length;
}

/** The whole chapter is finished. */
export function isChapterComplete(snapshot: StateUpdate | null): boolean {
  return snapshot !== null && completedCount(snapshot) === CHAPTER_ONE.length;
}

/**
 * The one deed to point at next, or null when the chapter is done.
 *
 * The FIRST unfinished one, not the closest to completion: the order is the
 * teaching, so sending a player to "gather 100 wood" before they have won a
 * fight would undo the point of having an order at all.
 */
export function nextDeed(snapshot: StateUpdate | null): DeedState | null {
  if (!snapshot) return null;
  return chapterOneState(snapshot).find((d) => !d.done) ?? null;
}
