// Modul: THE PEOPLE WHO CAN BE GIVEN A JOB, named (task 101).
//
// Gathering always sent slot 1's character and never said who that was;
// Crafting offered "Slot 1 / Slot 2" in a native select. The server takes any
// unlocked, occupied slot for either job (SimulationEngine's ChangeActivity
// with a TargetGuid, and ProcessAllSlotSubTicks runs every working slot), so
// both screens pick a PERSON here, by name, with what they are doing now.
// Pure, so it is tested without a store.

import { EMPTY_GUID } from '../net/commands';
import { HALT_REASON_SHORT, SLOT_UNLOCK_TOWN_HALL, WORK_KIND_NAMES, isCraftingActivity, isGatheringActivity, isSameKindOfWork, kindOfWork } from './slots';
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
  // Each character's bars (slot 1's are the original fields).
  PlayerHp?: number;
  PlayerMaxHp?: number;
  CurrentMonsterId?: number;
  CurrentMonsterHp?: number;
  CurrentMonsterMaxHp?: number;
  GatheringProgressTicks?: number;
  RequiredProgressTicks?: number;
  Slot2PlayerHp?: number;
  Slot2PlayerMaxHp?: number;
  Slot2MonsterId?: number;
  Slot2MonsterHp?: number;
  Slot2MonsterMaxHp?: number;
  Slot2WorkProgressTicks?: number;
  Slot2WorkRequiredTicks?: number;
  Slot3PlayerHp?: number;
  Slot3PlayerMaxHp?: number;
  Slot3MonsterId?: number;
  Slot3MonsterHp?: number;
  Slot3MonsterMaxHp?: number;
  Slot3WorkProgressTicks?: number;
  Slot3WorkRequiredTicks?: number;
}

/**
 * One character's live bars, whichever slot it stands in. Slot 1's come from
 * the original fields, 2 and 3 from their own (StateUpdatePacket, 2026-10-08):
 * the combat screen and the job progress bars show the PICKED character, not
 * always slot 1.
 */
export interface SlotBars {
  playerHp: number;
  playerMaxHp: number;
  monsterId: number;
  monsterHp: number;
  monsterMaxHp: number;
  progressTicks: number;
  requiredTicks: number;
}

export function slotBars(snap: WorkerSnapshot | null | undefined, slot: number): SlotBars {
  const n = (value: number | bigint | undefined) => Number(value ?? 0);
  if (!snap) return { playerHp: 0, playerMaxHp: 0, monsterId: 0, monsterHp: 0, monsterMaxHp: 0, progressTicks: 0, requiredTicks: 0 };
  if (slot === 2) {
    return {
      playerHp: n(snap.Slot2PlayerHp), playerMaxHp: n(snap.Slot2PlayerMaxHp),
      monsterId: n(snap.Slot2MonsterId), monsterHp: n(snap.Slot2MonsterHp), monsterMaxHp: n(snap.Slot2MonsterMaxHp),
      progressTicks: n(snap.Slot2WorkProgressTicks), requiredTicks: n(snap.Slot2WorkRequiredTicks),
    };
  }
  if (slot === 3) {
    return {
      playerHp: n(snap.Slot3PlayerHp), playerMaxHp: n(snap.Slot3PlayerMaxHp),
      monsterId: n(snap.Slot3MonsterId), monsterHp: n(snap.Slot3MonsterHp), monsterMaxHp: n(snap.Slot3MonsterMaxHp),
      progressTicks: n(snap.Slot3WorkProgressTicks), requiredTicks: n(snap.Slot3WorkRequiredTicks),
    };
  }
  return {
    playerHp: n(snap.PlayerHp), playerMaxHp: n(snap.PlayerMaxHp),
    monsterId: n(snap.CurrentMonsterId), monsterHp: n(snap.CurrentMonsterHp), monsterMaxHp: n(snap.CurrentMonsterMaxHp),
    progressTicks: n(snap.GatheringProgressTicks), requiredTicks: n(snap.RequiredProgressTicks),
  };
}

/**
 * Another of your people already does this KIND of work (CharacterSlotEngine:
 * one fighter, one woodcutter, one miner, one fisher, one crafter). The server
 * answers NodeOccupied; asking here first means the player is told why.
 */
export function kindTakenBy(workers: readonly Worker[], chosenSlot: number | undefined, activityId: number): Worker | null {
  if (!(activityId > 0)) return null;
  return workers.find((w) => w.slot !== chosenSlot && isSameKindOfWork(w.activity, activityId)) ?? null;
}

/**
 * Whose gear a fresh drop is held against: the character fighting (drops come
 * from fights, and there is one fighter), else the picked one, else the first.
 */
export function lootOwner(workers: readonly Worker[], pickedSlot: number): Worker | null {
  return workers.find((w) => kindOfWork(w.activity) === 1) ?? workers.find((w) => w.slot === pickedSlot) ?? workers[0] ?? null;
}

/** "Aila is already fighting." */
export function kindTakenMessage(other: Worker, names: ReadonlyMap<string, string>, activityId: number): string {
  const kind = WORK_KIND_NAMES[kindOfWork(activityId)] ?? 'doing that';
  return `${workerName(other, names)} is already ${kind}. One character per kind of work - stop them first.`;
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
