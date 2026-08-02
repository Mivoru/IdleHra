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

function pump(): void {
  const sampled = interpolator.sample(performance.timeOrigin + performance.now());
  if (sampled !== null) visualState.set(sampled);
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
// Wiring
// ---------------------------------------------------------------------------

export function startSession(token: string): void {
  interpolator.reset();
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
      }
      if (status.phase === 'idle') stopPump();
    },

    onStateUpdate: (packet: StateUpdate) => {
      playerState.set(packet);
      interpolator.push(
        extractInterpolated(packet as unknown as Record<string, unknown>),
        packet.CurrentMonsterId,
        performance.timeOrigin + performance.now(),
      );
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
  playerState.set(null);
  visualState.set(null);
}

/** Current authoritative snapshot without subscribing. */
export function snapshot(): StateUpdate | null {
  return get(playerState);
}
