// The quest line (owner, 2026-10-07): REST for the reading, a claim route for
// the paying.
//
// Modul: THE CLIENT KEEPS NO COPY OF THE QUESTS. Their order, titles,
// explanations, target screens, `data-guide` targets, unlock hints and the
// reward are all the server's sentence (QuestLineRegistry / QuestLineEngine);
// this file only types the answer, hand-typed like the other REST bodies. What
// a step is worth and whether it is done are decided on the server from the
// database - a claim sends a step id and nothing else.
import { authedGet, authedPost, AuthError } from './auth';

export type QuestState = 'locked' | 'available' | 'done' | 'claimed';

export interface QuestStep {
  Id: string;
  Order: number;
  Title: string;
  Explanation: string;
  /** A nav key (lib/ui/screens.ts or an App tab key). */
  Screen: string;
  /** `data-guide` values, most specific first. */
  GuideTargets: string[];
  /** What unlocks it; empty unless the step is locked. */
  UnlockHint: string;
  State: QuestState;
  /** True only for a done step whose reward is waiting. */
  Claimable: boolean;
}

export interface QuestReward {
  Region: number;
  Gold: number;
  MaterialLog: string;
  MaterialOre: string;
  MaterialQuantity: number;
}

export type QuestClaimResultName =
  | 'Ok'
  | 'UnknownStep'
  | 'PlayerNotFound'
  | 'Restricted'
  | 'Locked'
  | 'NotDone'
  | 'AlreadyClaimed';

export interface QuestLine {
  /** Set on a claim's answer: the engine's verdict. */
  Result: QuestClaimResultName | null;
  /** What ONE claim pays right now, for the player's highest unlocked region. */
  Reward: QuestReward;
  Done: number;
  Claimed: number;
  Total: number;
  Steps: QuestStep[];
}

export const questKeys = { all: ['quests'] as const };

export function fetchQuests(): Promise<QuestLine> {
  return authedGet<QuestLine>('/api/v1/quests');
}

/** Claims one step. A refusal still answers 200 with the whole line and its `Result`. */
export async function claimQuest(stepId: string): Promise<QuestLine> {
  const line = await authedPost<QuestLine>(`/api/v1/quests/${encodeURIComponent(stepId)}/claim`, {});
  if (!line) throw new AuthError(`POST /api/v1/quests/${stepId}/claim answered nothing`, 500);
  return line;
}
