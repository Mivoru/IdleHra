// Modul: the live-state stores, fed by the WebSocket. The descendant of
// VisualSyncProxy (1100 lines), minus everything the browser already does.
//
// One store per packet domain, as the port plan specifies. The raw snapshot
// and the smoothed one are deliberately SEPARATE stores rather than one merged
// object: mixing an interpolated health value into the same object as
// authoritative fields is how a UI ends up making decisions ("is it dead?")
// from a value that is a rendering artefact rather than a fact. Anything that
// decides reads `playerState`; anything that animates reads `visualState`.

import { writable, derived, get, type Readable } from 'svelte/store';
import { connection, fromBase64, type ConnectionStatus } from '../net/connection';
import {
  SnapshotInterpolator,
  extractInterpolated,
  type InterpolatedFields,
} from '../net/interpolation';
import { DamageFeed, type DamageEvent } from './damage';
import type { StateUpdate, ResponseChatMessage, ResponseLootDrop } from '../net/protocol.generated';

// ---------------------------------------------------------------------------
// Connection
// ---------------------------------------------------------------------------

export const connectionStatus = writable<ConnectionStatus>({
  phase: 'idle',
  detail: '',
  attempt: 0,
});

export const isLive: Readable<boolean> = derived(
  connectionStatus,
  ($status) => $status.phase === 'live',
);

// ---------------------------------------------------------------------------
// Authoritative state
// ---------------------------------------------------------------------------

export const playerState = writable<StateUpdate | null>(null);

// ---------------------------------------------------------------------------
// Smoothed state
// ---------------------------------------------------------------------------

const interpolator = new SnapshotInterpolator();
export const visualState = writable<InterpolatedFields | null>(null);

let animationHandle = 0;

// ---------------------------------------------------------------------------
// Floating damage
// ---------------------------------------------------------------------------

const damageFeed = new DamageFeed();
export const damageEvents = writable<DamageEvent[]>([]);

function pump(): void {
  const now = performance.timeOrigin + performance.now();

  const sampled = interpolator.sample(now);
  if (sampled !== null) visualState.set(sampled);

  // Expiry is driven from the render loop rather than a setTimeout per hit:
  // one timer per damage number would be dozens of live timers a minute, and
  // a backgrounded tab throttles them into a burst on return.
  const before = damageFeed.current.length;
  const kept = damageFeed.prune(now);
  if (kept.length !== before) damageEvents.set(kept);

  animationHandle = requestAnimationFrame(pump);
}

function startPump(): void {
  if (animationHandle === 0) animationHandle = requestAnimationFrame(pump);
}

function stopPump(): void {
  if (animationHandle !== 0) {
    cancelAnimationFrame(animationHandle);
    animationHandle = 0;
  }
}

// ---------------------------------------------------------------------------
// Loot feed
// ---------------------------------------------------------------------------

export interface LootEntry {
  id: number;
  itemId: number;
  quantity: number;
  monsterId: number;
  qualityTier: number;
  dropKind: number;
  atMs: number;
}

const MAX_LOOT_ENTRIES = 100;
let lootSequence = 0;

export const lootLog = writable<LootEntry[]>([]);

// ---------------------------------------------------------------------------
// Chat
// ---------------------------------------------------------------------------

export interface ChatEntry {
  id: number;
  senderPlayerId: number;
  channelType: number;
  text: string;
  atMs: number;
}

const MAX_CHAT_ENTRIES = 200;
let chatSequence = 0;

export const chatLog = writable<ChatEntry[]>([]);

// ---------------------------------------------------------------------------
// Offline summary
// ---------------------------------------------------------------------------

export interface OfflineSummary {
  elapsedSeconds: number;
  goldEarned: number;
  xpEarned: number;
  materialDropsGranted: number;
  /** True when the catch-up granted nothing at all - see the note below. */
  earnedNothing: boolean;
}

/**
 * Below this, a zero-earning catch-up is not worth interrupting for - it is a
 * page refresh or a dropped connection, not "time away".
 */
export const IDLE_WARNING_THRESHOLD_SECONDS = 300;

export const offlineSummary = writable<OfflineSummary | null>(null);

// Modul: OfflineSummaryTick is an EDGE, not a value. The server increments it
// only when a real, non-zero catch-up ran, and then never resets it - so every
// subsequent broadcast for the rest of the session repeats the same number.
// The client is responsible for showing the summary exactly once per login by
// comparing against the last value it saw, which is the same idiom
// LastSkillCastResultTick uses. Binding the panel to "tick != 0" instead would
// reopen it on all ~10 packets a second forever.
let lastOfflineSummaryTick = -1;

export function dismissOfflineSummary(): void {
  offlineSummary.set(null);
}

// ---------------------------------------------------------------------------
// Wiring
// ---------------------------------------------------------------------------

export function startSession(token: string): void {
  interpolator.reset();
  damageFeed.reset();
  damageEvents.set([]);
  offlineSummary.set(null);
  lastOfflineSummaryTick = -1;
  visualState.set(null);
  playerState.set(null);

  connection.connect(token, {
    onStatus: (status) => {
      connectionStatus.set(status);
      if (status.phase === 'live') startPump();
      // The interpolator is reset (not just paused) on a drop: resuming with a
      // stale "previous" snapshot would animate every bar from wherever it was
      // minutes ago, across the whole gap.
      if (status.phase === 'reconnecting' || status.phase === 'failed') {
        stopPump();
        interpolator.reset();
        // Damage numbers describe a moment that is now over; replaying them
        // after a gap would attribute old hits to the reconnect.
        damageFeed.reset();
        damageEvents.set([]);
      }
      if (status.phase === 'idle') stopPump();
    },

    onStateUpdate: (packet: StateUpdate) => {
      const arrivedAtMs = performance.timeOrigin + performance.now();
      playerState.set(packet);

      interpolator.push(
        extractInterpolated(packet as unknown as Record<string, unknown>),
        packet.CurrentMonsterId,
        arrivedAtMs,
      );

      // Fed from the AUTHORITATIVE packet, never the interpolated value - the
      // smoothed number passes through every intermediate value on its way,
      // which would turn one hit into a blizzard of fictional tiny ones.
      const hit = damageFeed.push({
        monsterId: packet.CurrentMonsterId,
        monsterHp: packet.CurrentMonsterHp,
        atMs: arrivedAtMs,
      });
      if (hit !== null) damageEvents.set(damageFeed.current);

      if (packet.OfflineSummaryTick !== lastOfflineSummaryTick) {
        const isFirstPacketOfSession = lastOfflineSummaryTick === -1;
        lastOfflineSummaryTick = packet.OfflineSummaryTick;

        // Modul: the server bumps OfflineSummaryTick for ANY elapsed window,
        // earnings or not (see OfflineSimulationEngine - it increments
        // unconditionally once rawDeltaSeconds > 0). So "a catch-up ran" is
        // all the wire actually promises, and deciding whether that deserves
        // the player's attention is this client's job.
        //
        // Measured against the dev fixture: away 39 minutes, "+0 +0 +0",
        // because the character had no activity set. Presenting that as a
        // rewards panel is a dialog whose only purpose is to be dismissed.
        //
        // But silently swallowing it is also wrong, and this is the part
        // worth getting right for an idle game: earning nothing over 39
        // minutes is exactly what the player most needs to be told, because
        // the cause is a character they never deployed. So a zero-earning
        // catch-up still surfaces - phrased as the problem it is rather than
        // as a reward - once the window is long enough that it cannot just be
        // a page refresh.
        const earnedNothing =
          packet.OfflineGoldEarned <= 0 &&
          packet.OfflineXpEarned <= 0 &&
          packet.OfflineMaterialDropsGranted <= 0;

        const worthShowing =
          packet.OfflineSummaryTick !== 0 &&
          packet.OfflineElapsedSeconds > 0 &&
          (!earnedNothing || packet.OfflineElapsedSeconds >= IDLE_WARNING_THRESHOLD_SECONDS);

        if (worthShowing && isFirstPacketOfSession) {
          offlineSummary.set({
            elapsedSeconds: packet.OfflineElapsedSeconds,
            goldEarned: packet.OfflineGoldEarned,
            xpEarned: packet.OfflineXpEarned,
            materialDropsGranted: packet.OfflineMaterialDropsGranted,
            earnedNothing,
          });
        }
      }

      startPump();
    },

    onLootDrop: (packet: ResponseLootDrop) => {
      lootLog.update((entries) => {
        const next: LootEntry[] = [
          {
            id: ++lootSequence,
            itemId: packet.ItemId,
            quantity: packet.Quantity,
            monsterId: packet.MonsterId,
            qualityTier: packet.QualityTier,
            dropKind: packet.DropKind,
            atMs: connection.serverNowMs(),
          },
          ...entries,
        ];
        return next.length > MAX_LOOT_ENTRIES ? next.slice(0, MAX_LOOT_ENTRIES) : next;
      });
    },

    onChatMessage: (packet: ResponseChatMessage) => {
      chatLog.update((entries) => {
        const next: ChatEntry[] = [
          {
            id: ++chatSequence,
            senderPlayerId: packet.SenderPlayerId,
            channelType: packet.ChannelType,
            text: fromBase64(packet.MessageText, packet.MessageLength),
            atMs: packet.TimestampEpochMs,
          },
          ...entries,
        ];
        return next.length > MAX_CHAT_ENTRIES ? next.slice(0, MAX_CHAT_ENTRIES) : next;
      });
    },
  });
}

export function endSession(): void {
  connection.disconnect();
  stopPump();
  interpolator.reset();
  damageFeed.reset();
  damageEvents.set([]);
  offlineSummary.set(null);
  playerState.set(null);
  visualState.set(null);
}

/** Current authoritative snapshot without subscribing. */
export function snapshot(): StateUpdate | null {
  return get(playerState);
}
