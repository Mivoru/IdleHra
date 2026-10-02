// Modul: EVERY RESULT THE SHIELD WHEEL CAN ANSWER HAS A SENTENCE (task 36,
// spec 5.7). A result the screen cannot say is a silent rollback with extra
// steps. tests/worldBossResults.test.ts compares these keys to the list the
// server exports from its enum (server/FolkIdle.Server.Tests/Fixtures/
// world_boss_results.json), so a new server result without a sentence fails
// the client build rather than reaching a player as a blank.
import type { WorldBossStrikeResult } from '../net/rest';
import { formatNumber } from '../ui/format';

/**
 * What to tell the player. `{damage}` is filled in where the server reports
 * one. An empty string means the result is shown some other way (the wheel
 * opens, the result card appears), or it is a client bug that is logged and
 * not worth a sentence (TooEarly, OutOfSpears).
 */
export const WORLD_BOSS_RESULT_SENTENCES: Record<WorldBossStrikeResult, string> = {
  Issued: '',
  Outstanding: '',
  Disabled: 'The boss fight is not open to the wheel yet. Use the plate buttons.',
  NotActive: 'The boss is not here right now.',
  AlreadyDefeated: 'The boss has already fallen.',
  NoAttemptsLeft: "You have used today's strike. It comes back at the next daily reset - the World Boss screen says when.",
  TooLateInWindow: 'The encounter ends before a strike could finish.',
  ChallengeOutstanding: 'Finish your open strike first.',
  NoChallenge: 'That strike has expired. Start a new one.',
  TooEarly: '',
  OutOfSpears: '',
  Landed: '',
  ResolvedAtFloor: 'Your unfinished strike was resolved at the base multiplier: {damage} damage.',
  Refused: 'That strike could not be scored and was resolved at the base multiplier: {damage} damage.',
  Queued: 'Strike sent. The result will show on the board.',
  Failed: 'The strike could not be recorded. Nothing was spent. Try again.',
  PracticeScored: '',
  NoGameSession: 'The game is not connected right now. Wait for it to reconnect, then strike.',
};

/** The results that are client bugs rather than something to tell the player. */
export const SILENT_WORLD_BOSS_RESULTS: ReadonlySet<WorldBossStrikeResult> = new Set<WorldBossStrikeResult>([
  'Issued',
  'Outstanding',
  'TooEarly',
  'OutOfSpears',
  'Landed',
  'PracticeScored',
]);

/**
 * Modul: the results that are NEWS, not refusals. A strike that went through
 * (Queued) or was scored at the base multiplier still dealt damage, so the
 * toast is informational - pushLocalNotice's error tone plays the error sound
 * and draws a red border, which read "Strike sent" as a failure.
 */
const GOOD_NEWS_RESULTS: ReadonlySet<WorldBossStrikeResult> = new Set<WorldBossStrikeResult>([
  'Queued',
  'ResolvedAtFloor',
  'Refused',
]);

export function worldBossResultTone(result: WorldBossStrikeResult): 'info' | 'error' {
  return GOOD_NEWS_RESULTS.has(result) ? 'info' : 'error';
}

export function worldBossResultSentence(result: WorldBossStrikeResult, damage?: number): string {
  const sentence = WORLD_BOSS_RESULT_SENTENCES[result] ?? '';
  return sentence.replace('{damage}', damage === undefined ? '0' : formatNumber(damage));
}
