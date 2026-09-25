// Modul: EVERY RESULT THE SHIELD WHEEL CAN ANSWER HAS A SENTENCE (task 36,
// spec 5.7). A result the screen cannot say is a silent rollback with extra
// steps. tests/worldBossResults.test.ts compares these keys to the list the
// server exports from its enum (server/FolkIdle.Server.Tests/Fixtures/
// world_boss_results.json), so a new server result without a sentence fails
// the client build rather than reaching a player as a blank.
import type { WorldBossStrikeResult } from '../net/rest';

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
  NoAttemptsLeft: 'You have used all three attempts for this encounter.',
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

export function worldBossResultSentence(result: WorldBossStrikeResult, damage?: number): string {
  const sentence = WORLD_BOSS_RESULT_SENTENCES[result] ?? '';
  return sentence.replace('{damage}', damage === undefined ? '0' : damage.toLocaleString());
}
