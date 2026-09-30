// Task 84: the Great Works - REST for the reading, the DepositGreatWork command
// (commands.ts) for the doing.
//
// Modul: THE CLIENT KEEPS NO COPY OF THE MONUMENTS. Their names, what each
// stage costs and pays, and the two ceilings are all the server's sentence
// (GreatWorksRegistry); this file only types the answer. The one figure that
// looks like a mirror - the stage count the map draws - is read off the
// answer's own `Stages` array, so retuning the registry cannot leave it behind.
import { authedGet } from './auth';

export interface GreatWorkStage {
  Stage: number;
  Name: string;
  Cost: number;
  Built: boolean;
}

export interface GreatWork {
  Region: number;
  Name: string;
  /** Stages BUILT, 0 to Stages.length. */
  Stage: number;
  /** Deposited into the next stage. */
  Progress: number;
  /** What the next stage costs; 0 once the monument is complete. */
  NextCost: number;
  LogItem: string;
  OreItem: string;
  HeldLog: number;
  HeldOre: number;
  /** What ONE stage of this monument pays, as the server words it. */
  BonusPerStage: string;
  Stages: GreatWorkStage[];
  /** What completing the fifth stage pays, as the server words it: a bound
   * frame, and on The Ebon Crown a Hall of Ancestors slot as well. */
  CompletionReward: string;
  FrameId: string;
  FrameOwned: boolean;
}

export interface GreatWorks {
  YieldPct: number;
  MaxYieldPct: number;
  OfflineMinutes: number;
  MaxOfflineMinutes: number;
  /** Hall of Ancestors slots the monuments pay (0 or 1). */
  HallSlots: number;
  Works: GreatWork[];
}

export const greatWorksKeys = { all: ['greatWorks'] as const };

export function fetchGreatWorks(): Promise<GreatWorks> {
  return authedGet<GreatWorks>('/api/v1/great-works');
}

/** 0 to 1: how far through the monument as a whole, stages built plus the current one's progress. */
export function monumentFraction(work: GreatWork): number {
  const total = work.Stages.reduce((sum, s) => sum + s.Cost, 0);
  if (total <= 0) return 0;
  const built = work.Stages.filter((s) => s.Built).reduce((sum, s) => sum + s.Cost, 0);
  return Math.min(1, (built + work.Progress) / total);
}

/** 0 to 1: progress through the CURRENT stage; 1 once the monument is complete. */
export function stageFraction(work: GreatWork): number {
  if (work.NextCost <= 0) return 1;
  return Math.min(1, work.Progress / work.NextCost);
}

export function isComplete(work: GreatWork): boolean {
  return work.Stage >= work.Stages.length;
}
