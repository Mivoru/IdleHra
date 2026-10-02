// Modul: THE PEOPLE WHO CAN BE GIVEN A JOB, named (task 101).
//
// Gathering always sent slot 1's character and never said who that was;
// Crafting offered "Slot 1 / Slot 2" in a native select. The server takes any
// unlocked, occupied slot for either job (SimulationEngine's ChangeActivity
// with a TargetGuid, and ProcessAllSlotSubTicks runs every working slot), so
// both screens pick a PERSON here, by name, with what they are doing now.
// Pure, so it is tested without a store.

import { EMPTY_GUID } from '../net/commands';
import { HALT_REASON_SHORT, SLOT_UNLOCK_TOWN_HALL, isCraftingActivity, isGatheringActivity } from './slots';
import { locationName, nodeLocation } from './locations';
import { raceName } from './races';
import { nodeYieldBaseId, PROFESSION_VERBS } from './gatheringNodes';
import { prettifyBaseId } from '../net/content';

/** The slice of StateUpdatePacket this reads. */
export interface WorkerSnapshot {
  TownHallLevel: number;
  Slot1_CharacterId: string;
  Slot2_CharacterId: string;
  Slot3_CharacterId: string;
  Slot1_RaceId: number;
  Slot2_RaceId: number;
  Slot3_RaceId: number;
  ActiveActivityId: number | bigint;
  Slot2ActivityId: number;
  Slot3ActivityId: number;
  ActivityHaltReason: number;
  Slot2ActivityHaltReason: number;
  Slot3ActivityHaltReason: number;
}

export interface Worker {
  slot: number;
  id: string;
  raceId: number;
  /** What they are doing now; 0 is idle. */
  activity: number;
  halt: number;
}

/** Every unlocked slot with a person in it, slot order. */
export function workersOf(snap: WorkerSnapshot | null | undefined): Worker[] {
  if (!snap) return [];
  const townHall = Number(snap.TownHallLevel ?? 0);
  return [
    { slot: 1, id: snap.Slot1_CharacterId, raceId: snap.Slot1_RaceId, activity: Number(snap.ActiveActivityId), halt: snap.ActivityHaltReason },
    { slot: 2, id: snap.Slot2_CharacterId, raceId: snap.Slot2_RaceId, activity: snap.Slot2ActivityId, halt: snap.Slot2ActivityHaltReason },
    { slot: 3, id: snap.Slot3_CharacterId, raceId: snap.Slot3_RaceId, activity: snap.Slot3ActivityId, halt: snap.Slot3ActivityHaltReason },
  ].filter((w) => w.id && w.id !== EMPTY_GUID && townHall >= (SLOT_UNLOCK_TOWN_HALL[w.slot - 1] ?? 99));
}

/** "Aila", or the race until the roster answers, or "Slot 2" as a last resort. */
export function workerName(worker: Worker, names: ReadonlyMap<string, string>): string {
  return names.get(worker.id) || (worker.raceId > 0 ? raceName(worker.raceId) : `Slot ${worker.slot}`);
}

/**
 * A job in a few words: "Mining Copper Ore - Sunlit Plains", "Fighting Wolf",
 * "Crafting Birch Axe", "Idle - Out of food". `recipeName` and `monsterName`
 * are the caller's lookups, because the screens hold different registries.
 */
export function describeJob(
  activity: number,
  halt: number,
  lookups: { monsterName?: (id: number) => string; recipeName?: (activityId: number) => string | null } = {},
): string {
  if (activity <= 0) {
    const reason = HALT_REASON_SHORT[halt];
    return reason ? `Idle - ${reason}` : 'Idle';
  }
  if (isGatheringActivity(activity)) {
    const verb = PROFESSION_VERBS[Math.floor(activity / 1000) - 1] ?? 'Gathering';
    const yieldId = nodeYieldBaseId(activity);
    const what = yieldId ? ` ${prettifyBaseId(yieldId)}` : '';
    return `${verb}${what} - ${locationName(nodeLocation(activity))}`;
  }
  if (isCraftingActivity(activity)) {
    const name = lookups.recipeName?.(activity);
    return name ? `Crafting ${name}` : 'Crafting';
  }
  return `Fighting ${lookups.monsterName?.(activity) ?? 'a monster'}`;
}
