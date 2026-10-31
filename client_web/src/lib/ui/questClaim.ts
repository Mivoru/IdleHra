// Modul: ONE CLAIM, TWO PLACES (achievements PR 1, 2026-10-31). The quest
// line is the Book of Deeds' first chapter now as well as the Home panel, and
// both claim the same reward - so the claim, its answer and its wording live
// here once instead of drifting apart in two components.
import type { QueryClient } from '@tanstack/svelte-query';
import { claimQuest, questKeys, type QuestLine } from '../net/quests';
import { invalidateOwnedItems } from '../net/queryClient';
import { prettifyBaseId } from '../net/content';
import { pushLocalNotice } from '../stores/game';
import { describeReward } from './questLine';

const FAILURE_TEXT: Record<string, string> = {
  Locked: 'That step is not open yet.',
  NotDone: 'Do the step first, then claim it.',
  AlreadyClaimed: 'That reward was already claimed.',
  Restricted: 'This account cannot claim rewards right now.',
  UnknownStep: 'That step no longer exists.',
  PlayerNotFound: 'Could not find your account.',
};

/** Claims a step's reward and tells the player what happened. */
export async function claimQuestStep(client: QueryClient, stepId: string): Promise<void> {
  try {
    const answer: QuestLine = await claimQuest(stepId);
    client.setQueryData(questKeys.all, answer);
    if (answer.Result === 'Ok') {
      invalidateOwnedItems(client);
      pushLocalNotice(`Quest reward: ${describeReward(answer.Reward, prettifyBaseId)}.`, 'info');
    } else {
      pushLocalNotice(FAILURE_TEXT[answer.Result ?? ''] ?? 'The reward was not paid.', 'error');
    }
  } catch {
    pushLocalNotice('The reward could not be claimed. Nothing was lost - try again.', 'error');
  }
}
