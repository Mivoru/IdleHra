// Pressing Fight on a winter: the command, then the window. Shared by the
// ladder and the window's own "Try again" / "Next winter", so all three open
// the same fight the same way.
import { startSeasonalBoss } from '../net/commands';
import { tierCleared, type SeasonalBossTier } from '../net/seasonalBoss';
import { beginSeasonalFight } from '../stores/seasonalFight';
import { pushLocalNotice } from '../stores/game';

export function fightSeasonalTier(t: SeasonalBossTier, eventId: number, clearedMask: number): void {
  const outcome = startSeasonalBoss(t.Tier, eventId);
  if (!outcome.ok) {
    pushLocalNotice(outcome.reason, 'error');
    return;
  }
  beginSeasonalFight(t.Tier, t.BossMonsterId, eventId, !(t.Cleared || tierCleared(clearedMask, t.Tier)));
}
