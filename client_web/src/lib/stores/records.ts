/*
 * Modul: TASK 51 - "New record: ..." toasts. Pure, so every edge is tested.
 *
 * The highest hit and the boss times arrive on StateUpdate, hydrated at login
 * (PersonalRecords.Hydrate on the server). The FIRST packet of a session is a
 * baseline, never a trigger - the same contract as LastVictoryTick - or every
 * relogin would announce every old record as new.
 *
 * THROTTLED, because a record that breaks constantly is not news. A new
 * player's highest hit rises with nearly every level for the first hour, so
 * hit records are said at most once every two minutes (the current figure,
 * whatever it has reached by then). Boss times and drops are rare enough to
 * say every time. A drop only counts from Rare up: a new account's first
 * Common "beating" its Normal is not a record anyone asked to hear about.
 */
import { rarityName } from '../ui/rarity';
import { formatNumber } from '../ui/format';

export const HIT_TOAST_INTERVAL_MS = 120_000;
export const DROP_RECORD_MIN_TIER = 4; // Rare

export interface RecordFields {
  BestHit: number;
  BossBestKillTenthsR1: number;
  BossBestKillTenthsR2: number;
  BossBestKillTenthsR3: number;
  BossBestKillTenthsR4: number;
  BossBestKillTenthsR5: number;
}

export function bossTimes(p: RecordFields): number[] {
  return [p.BossBestKillTenthsR1, p.BossBestKillTenthsR2, p.BossBestKillTenthsR3, p.BossBestKillTenthsR4, p.BossBestKillTenthsR5].map(
    (n) => Number(n ?? 0),
  );
}

/** "1:23.4" or "42.0 s" from tenths of a second. */
export function formatTenths(tenths: number): string {
  if (!tenths) return '-';
  const seconds = tenths / 10;
  if (seconds < 60) return `${seconds.toFixed(1)} s`;
  const m = Math.floor(seconds / 60);
  const s = (seconds - m * 60).toFixed(1).padStart(4, '0');
  return `${m}:${s}`;
}

export class RecordWatch {
  private hit: number | null = null;
  private bosses: number[] | null = null;
  private lastHitToastAt = Number.NEGATIVE_INFINITY;
  private pendingHit = false;
  private dropTier: number | null = null;

  /** A new session: the next packet is a baseline again. */
  reset(): void {
    this.hit = null;
    this.bosses = null;
    this.pendingHit = false;
    this.dropTier = null;
  }

  observe(packet: RecordFields, nowMs: number): string[] {
    const out: string[] = [];
    const hit = Number(packet.BestHit ?? 0);
    const bosses = bossTimes(packet);

    if (this.hit === null || this.bosses === null) {
      this.hit = hit;
      this.bosses = bosses;
      return out;
    }

    if (hit > this.hit) {
      this.hit = hit;
      this.pendingHit = true;
    }
    if (this.pendingHit && nowMs - this.lastHitToastAt >= HIT_TOAST_INTERVAL_MS) {
      out.push(`New record: highest hit ${formatNumber(this.hit)}`);
      this.lastHitToastAt = nowMs;
      this.pendingHit = false;
    }

    for (let i = 0; i < bosses.length; i++) {
      const before = this.bosses[i];
      const now = bosses[i];
      if (now > 0 && (before === 0 || now < before)) {
        out.push(`New record: region ${i + 1} boss in ${formatTenths(now)}`);
      }
    }
    this.bosses = bosses;
    return out;
  }

  /** The durable best drop, from /player/records. Until it arrives, drops say nothing. */
  seedDrop(tier: number): void {
    this.dropTier = Math.max(this.dropTier ?? 0, tier);
  }

  /** True when this drop is a record worth saying. Synchronous, so two drops in one burst are judged in order. */
  observeDrop(tier: number, dropKind: number): boolean {
    if (dropKind !== 1 || this.dropTier === null) return false;
    if (tier <= this.dropTier) return false;
    this.dropTier = tier;
    return tier >= DROP_RECORD_MIN_TIER;
  }
}

export function dropRecordMessage(tier: number, name: string): string {
  return `New record: best drop - ${rarityName(tier)} ${name}`;
}
