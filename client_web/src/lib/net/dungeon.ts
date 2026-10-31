// The three dungeons' REST surface (CHESTS_DUNGEONS_DIAMONDS.md 2.6a, 2.8).
// The server decides every blow and every reward; this only asks and shows.
import { authedGet, authedPost } from './auth';

export interface DungeonAbility {
  Id: string;
  Name: string;
  FromStage: number;
  Line: string;
  Active: boolean;
}

export interface DungeonStage {
  Stage: number;
  Band: 'Regions' | 'Ascension' | 'Beyond';
  /** Empty when the stage can be entered. */
  LockedReason: string;
  Cleared: boolean;
  FirstClearPaid: boolean;
  Reward: string;
  Abilities: DungeonAbility[];
}

/** Server DungeonKind - the number is stored and sent, never reorder. */
export type DungeonKind = 0 | 1 | 2;
export const DUNGEON_EQUIPMENT: DungeonKind = 0;
export const DUNGEON_XP: DungeonKind = 1;
export const DUNGEON_GOLD: DungeonKind = 2;

export interface DungeonInfo {
  Kind: DungeonKind;
  Key: 'Equipment' | 'Xp' | 'Gold';
  Name: string;
  Boss: string;
  Tickets: number;
  BestStage: number;
  /** How many champions this dungeon takes now: 1, then 2 after stage 6, 3 after stage 11. */
  PartySlots: number;
  Stages: DungeonStage[];
}

export interface DungeonView {
  Open: boolean;
  LockedReason: string;
  Online: boolean;
  TicketCap: number;
  TicketsPerDay: number;
  /** An admin testing in production: runs spend no ticket. */
  FreeRuns?: boolean;
  Waves: number;
  MealCap: number;
  /** The roster slots holding a character, and the stage that opens each for a dungeon. */
  Roster: { Slot: number; UnlockStage: number; Dps: number; MaxHp: number; Worn: number }[];
  /** XP, Gold, Equipment - the server's order, which is the gates' order. */
  Dungeons: DungeonInfo[];
}

export interface DungeonEvent {
  AtSeconds: number;
  Kind:
    | 'wave'
    | 'boss'
    | 'cleared'
    | 'ability'
    | 'meal'
    | 'defeat'
    | 'victory'
    | 'timeout'
    | 'challenge'
    | 'dodged'
    | 'missed'
    | 'perk'
    | 'penalty';
  Text: string;
  Value: number;
  /** Both bars right after the event, in percent. */
  PlayerHpPct: number;
  EnemyHpPct: number;
  ShieldPct: number;
}

export interface DungeonRunResult {
  Won: boolean;
  Stage: number;
  WavesCleared: number;
  ReachedBoss: boolean;
  BossHpLeftPct: number;
  Seconds: number;
  MealsEaten: number;
  Events: DungeonEvent[];
  Kind: DungeonKind;
  /** Whose face each of the five waves wears (the server picks them). */
  WaveMonsterIds: number[];
  PartySize: number;
  Dodged: number;
  Missed: number;
}

export interface DungeonReward {
  PieceId: number;
  PieceBaseId: string;
  PieceRarity: string;
  ChestId: number;
  Chest: string;
  Diamonds: number;
  Xp: number;
  Gold: number;
}

/** A bonus on a card, or a penalty in a banner. */
export interface DungeonPerk {
  Id: string;
  Name: string;
  Line: string;
  Penalty: boolean;
}

/**
 * One segment of a live run: the events to play since the last answer, then
 * a pause (a telegraph to dodge, or bonuses to choose) or the end and its reward.
 */
export interface DungeonRunAnswer {
  Outcome: 'Ok' | 'NoTickets' | 'Locked' | 'PlayerNotFound' | 'NoSession' | 'NoRun' | 'Refused';
  Reason: string;
  RunId?: string;
  Events?: DungeonEvent[];
  Paused?: boolean;
  Offer?: DungeonPerk[] | null;
  Finished?: boolean;
  Result: DungeonRunResult | null;
  Reward: DungeonReward | null;
  Tickets: number | null;
  PartySize?: number;
  WaveMonsterIds?: number[];
}

export interface DungeonTelegraph {
  Outcome: 'Ok' | 'NoRun' | 'Refused';
  Dirs: ('Up' | 'Down' | 'Left' | 'Right')[];
  /** What is left of the window, in ms, as the server counts it. */
  WindowMs: number;
}

export const dungeonKeys = { view: ['dungeon'] as const };

export function fetchDungeon(): Promise<DungeonView> {
  return authedGet<DungeonView>('/api/v1/dungeon');
}

export function runDungeon(kind: DungeonKind, stage: number, party: number[] = [0], sweep = false): Promise<DungeonRunAnswer | null> {
  return authedPost<DungeonRunAnswer>('/api/v1/dungeon/run', { Kind: kind, Stage: stage, Party: party, Sweep: sweep });
}

export function showTelegraph(runId: string): Promise<DungeonTelegraph | null> {
  return authedPost<DungeonTelegraph>('/api/v1/dungeon/act', { RunId: runId, Action: 'telegraph' });
}

export function answerTelegraph(runId: string, dirs: string[]): Promise<DungeonRunAnswer | null> {
  return authedPost<DungeonRunAnswer>('/api/v1/dungeon/act', { RunId: runId, Action: 'answer', Dirs: dirs });
}

export function pickPerk(runId: string, perk: string): Promise<DungeonRunAnswer | null> {
  return authedPost<DungeonRunAnswer>('/api/v1/dungeon/act', { RunId: runId, Action: 'pick', Perk: perk });
}
