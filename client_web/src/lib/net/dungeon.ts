// The equipment dungeon's REST surface (CHESTS_DUNGEONS_DIAMONDS.md 2.6a).
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

export interface DungeonView {
  Open: boolean;
  LockedReason: string;
  Online: boolean;
  Tickets: number;
  TicketCap: number;
  TicketsPerDay: number;
  BestStage: number;
  Boss: string;
  Waves: number;
  MealCap: number;
  Stages: DungeonStage[];
}

export interface DungeonEvent {
  AtSeconds: number;
  Kind: 'wave' | 'boss' | 'cleared' | 'ability' | 'meal' | 'defeat' | 'victory' | 'timeout';
  Text: string;
  Value: number;
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
}

export interface DungeonReward {
  PieceId: number;
  PieceBaseId: string;
  PieceRarity: string;
  ChestId: number;
  Chest: string;
  Diamonds: number;
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

export function runDungeon(stage: number): Promise<DungeonRunAnswer | null> {
  return authedPost<DungeonRunAnswer>('/api/v1/dungeon/run', { Stage: stage });
}
