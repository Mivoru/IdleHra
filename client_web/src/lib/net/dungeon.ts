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
  Stages: DungeonStage[];
}

export interface DungeonView {
  Open: boolean;
  LockedReason: string;
  Online: boolean;
  TicketCap: number;
  TicketsPerDay: number;
  Waves: number;
  MealCap: number;
  /** XP, Gold, Equipment - the server's order, which is the gates' order. */
  Dungeons: DungeonInfo[];
}

export interface DungeonEvent {
  AtSeconds: number;
  Kind: 'wave' | 'boss' | 'cleared' | 'ability' | 'meal' | 'defeat' | 'victory' | 'timeout';
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

export interface DungeonRunAnswer {
  Outcome: 'Ok' | 'NoTickets' | 'Locked' | 'PlayerNotFound' | 'NoSession';
  Reason: string;
  Result: DungeonRunResult | null;
  Reward: DungeonReward | null;
  Tickets: number;
}

export const dungeonKeys = { view: ['dungeon'] as const };

export function fetchDungeon(): Promise<DungeonView> {
  return authedGet<DungeonView>('/api/v1/dungeon');
}

export function runDungeon(kind: DungeonKind, stage: number): Promise<DungeonRunAnswer | null> {
  return authedPost<DungeonRunAnswer>('/api/v1/dungeon/run', { Kind: kind, Stage: stage });
}
