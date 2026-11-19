// The Descent into Nav (2026-11-19) - the server's NavEngine, over REST.
// Every route answers 404 unless the server runs with FOLKIDLE_NAV=on; the
// menu hides the entry on that answer (navAvailable below), so production
// shows nothing until the owner opens the mode.
import { AuthError, authedGet, authedPost } from './auth';

export const navKeys = {
  state: ['player', 'nav'] as const,
};

export interface NavDepth {
  Depth: number;
  /** A NavRules guardian key; art lives at /nav/{key}.webp. */
  Guardian: string;
  Elite: string;
  Seconds: number;
  Cleared: boolean;
  Outcome: string;
  Souls: number;
}

export interface NavRun {
  Circle: number;
  StartDepth: number;
  DeepestCleared: number;
  Souls: number;
  /** "time" | "fell" | "limit" */
  EndedBy: string;
  Seconds: number;
  RevivesUsed: number;
  /** The guardian key that ended the run; empty when it hit its limit. */
  StoppedBy: string;
  /** What would move the record past that guardian (NavRules.Hint). */
  Hint: string;
  Rewards: string[];
  Depths: NavDepth[];
}

export interface NavWorkshopUpgrade {
  Key: string;
  Name: string;
  Effect: string;
  Level: number;
  MaxLevel: number;
  Maxed: boolean;
  Cost: number;
  Total: number;
  /** For a bane: the guardian key it answers. Empty for the plain upgrades. */
  Guardian: string;
  /** A bane whose guardian has not yet stopped one of your runs. */
  Locked: boolean;
}

export interface NavResearchLine {
  Key: string;
  Name: string;
  Effect: string;
  Level: number;
  Gold: number;
  Seconds: number;
  /** The top level with the Circles open now: 0 = no top, -1 = not open yet. */
  Cap: number;
  Open: boolean;
  OpensAtCircle: number;
  /** Counts in the rest of the game (combat gold, XP, loot luck), not only in Nav. */
  Outward: boolean;
  Maxed: boolean;
}

export interface NavSpirit {
  Name: string;
  Strength: number;
  Skill: number;
  Endurance: number;
  Fortune: number;
  Total: number;
}

export interface NavFrame {
  Id: string;
  Name: string;
  Price: number;
  Circle: number;
  Depth: number;
  Reached: boolean;
  Owned: boolean;
}

export interface NavGuardian {
  Key: string;
  Name: string;
  /** The name at the start of a sentence: "The Vampire", "Lady Midday". */
  Subject: string;
  AbilityName: string;
  AbilityText: string;
}

/** A guardian's painted portrait (client_web/public/nav). */
export function guardianArt(key: string): string {
  return `/nav/${key}.webp`;
}

export interface NavBoard {
  Top: { Rank: number; Name: string; Circle: number; Depth: number; Me: boolean }[];
  /** 0 until the account has a record. */
  MyRank: number;
}

export interface NavWeekBoard {
  Top: { Rank: number; Name: string; Circle: number; Depth: number; Me: boolean }[];
  EndsInSeconds: number;
  /** The top three are paid only when at least this many descended that week. */
  MinRunners: number;
}

/** What a new depth pays the first time it is cleared. */
export interface NavDepthReward {
  Depth: number;
  Chest: 'Bronze' | 'Gold' | 'Ancient' | string;
  Diamonds: number;
  /** Dungeon tickets for each gate. */
  Tickets: number;
}

export interface NavState {
  /** False until Malakor falls; LockedReason says so. */
  Open: boolean;
  LockedReason: string;
  Circle: number;
  /** Each Circle's record and what its next four new depths pay (server NavRules.DepthRewardFor). */
  Records: { Circle: number; Depth: number; Next?: NavDepthReward[] }[];
  Souls: number;
  SoulsLifetime: number;
  Charges: number;
  ChargeCap: number;
  /** Seconds one charge takes to come back (Swift return shortens it). */
  ChargeSeconds: number;
  NextChargeInSeconds: number;
  RunsTotal: number;
  SecondsPerGuardian: number;
  CircleOpensAt: number;
  /** Every this-many depths stands the warden (the last entry of Guardians). */
  WardenEvery: number;
  /** The ten in order, then the warden. */
  Guardians: NavGuardian[];
  Bonuses: {
    DamagePct: number;
    HealthPct: number;
    AttackSpeedPct: number;
    CritDamagePct: number;
    SoulsPct: number;
    StartDepth: number;
    Revives: number;
  };
  Workshop: NavWorkshopUpgrade[];
  Research: NavResearchLine[];
  ResearchActive: string;
  ResearchLeftSeconds: number;
  /** ExpectedDepth: where a descent of ExpectedCircle should end (median of five seeds). */
  Roster: { Slot: number; Dps: number; MaxHp: number; Worn: number; ExpectedDepth: number; ExpectedCircle: number }[];
  Board: NavBoard | null;
  LastRun: NavRun | null;
  /** What the outward research adds everywhere, percent. */
  Blessings: { GoldPct: number; XpPct: number; LuckPct: number };
  Spirits: {
    Count: number;
    SpiritFloor: number;
    Walking: NavSpirit[];
    BenchAdults: number;
    DamagePct: number;
    HealthPct: number;
    SpeedPct: number;
    SoulsPct: number;
  };
  /** The week's restless guardian: its rule AbilityPct% as strong, its depths SoulsFactor x Souls. */
  Omen: { Key: string; Name: string; AbilityPct: number; SoulsFactor: number; EndsInSeconds: number };
  /** This week's best. */
  Week: { Circle: number; Depth: number };
  WeekBoard: NavWeekBoard | null;
  Frames: NavFrame[];
  /** Lines to show once: last week's board reward. */
  Notices: string[];
}

export function fetchNav(): Promise<NavState> {
  return authedGet<NavState>('/api/v1/nav');
}

/** True when the server has Nav switched on; a 404 means it does not. */
export async function navAvailable(): Promise<boolean> {
  try {
    await authedGet<NavState>('/api/v1/nav');
    return true;
  } catch (e) {
    if (e instanceof AuthError) return false;
    return false;
  }
}

export function navDescend(slot: number, circle: number): Promise<{ Result: string; Run: NavRun | null } | null> {
  return authedPost('/api/v1/nav/descend', { Slot: slot, Circle: circle });
}

export function navBuy(key: string, levels: number): Promise<{ Result: string; Level: number } | null> {
  return authedPost('/api/v1/nav/workshop', { Key: key, Levels: levels });
}

export function navResearch(key: string): Promise<{ Result: string; Gold: number } | null> {
  return authedPost('/api/v1/nav/research', { Key: key });
}

export function navBuyFrame(id: string): Promise<{ Result: string } | null> {
  return authedPost('/api/v1/nav/frame', { Id: id });
}
