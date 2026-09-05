// Modul: the WebSocket client. The reduced descendant of the Unity client's
// WebSocketClient (1398 lines) - connect, reconnect, auth, packet dispatch,
// and the two wire obligations that are written down nowhere else.
//
// It speaks the JSON protocol added in Phase 0. The mode switch is the frame
// type of the handshake: sending the handshake as text is what puts this
// connection in JSON mode. `mode: 'json'` rides along so the intent is legible
// in a packet capture.

import { WS_URL } from './config';
import { computeChallengeHash } from './antiCheat';
import {
  PacketType,
  CommandType,
  TYPE_PROPERTY,
  MODE_PROPERTY,
  type StateUpdate,
  type ResponseChatMessage,
  type ResponseLootDrop,
  type ResponseCombatEvent,
  type ClientCommandDraft,
  type RequestChatMessageDraft,
} from './protocol.generated';

export type ConnectionPhase =
  | 'idle'
  | 'connecting'
  | 'authenticating'
  | 'live'
  | 'reconnecting'
  | 'failed'
  /** The server rejected the token. Reconnecting cannot fix it; signing in can. */
  | 'signedout';

/**
 * What a socket close MEANS, separated from what the connection does about it.
 *
 * Modul: THE CLIENT BLAMED THE WRONG THING AND THEN RETRIED FOREVER.
 *
 * Close code 1008 carries both wire obligations AND a rejected token, and this
 * message asserted the first regardless - so a player whose 24-hour JWT had
 * simply expired was told "this is almost always a stale LogicEpochCounter or
 * an unanswered anti-cheat challenge", which sent the next hour of debugging
 * into the anti-cheat path. Worse, the reconnect loop then retried the same
 * dead token with exponential backoff forever: it can never succeed, and the
 * one screen that could fix it - the login form - never appeared.
 *
 * There is no refresh token (see auth.ts), so an expired JWT means signing in
 * again, and the honest thing is to say so and stop.
 */
export function interpretClose(
  code: number,
  reason: string,
): { phase: ConnectionPhase; detail: string; reconnect: boolean } {
  if (code === 1008 && /token/i.test(reason)) {
    return {
      phase: 'signedout',
      detail: 'Your session expired. Please sign in again.',
      reconnect: false,
    };
  }

  if (code === 1008) {
    return {
      phase: 'reconnecting',
      detail:
        `Server terminated the session (${reason || 'no reason given'}). ` +
        'This is almost always a stale LogicEpochCounter or an unanswered anti-cheat challenge.',
      reconnect: true,
    };
  }

  return {
    phase: 'reconnecting',
    detail: `Connection lost (code ${code}${reason ? `: ${reason}` : ''})`,
    reconnect: true,
  };
}

export interface ConnectionStatus {
  phase: ConnectionPhase;
  /** Human-readable reason the connection ended, when it ended badly. */
  detail: string;
  /** Attempt number of the reconnect currently in flight; 0 when live. */
  attempt: number;
}

export interface ConnectionHandlers {
  onStateUpdate?: (packet: StateUpdate) => void;
  onChatMessage?: (packet: ResponseChatMessage) => void;
  onLootDrop?: (packet: ResponseLootDrop) => void;
  onCombatEvent?: (packet: ResponseCombatEvent) => void;
  onStatus?: (status: ConnectionStatus) => void;
}

const MAX_RECONNECT_DELAY_MS = 15_000;
const BASE_RECONNECT_DELAY_MS = 500;

function toBase64(text: string): string {
  // btoa is latin1-only; JWTs and chat text are not. Encode to UTF-8 bytes
  // first, which is what the server's base64 decode expects on the other side.
  const bytes = new TextEncoder().encode(text);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

export function fromBase64(encoded: string, byteLength?: number): string {
  const binary = atob(encoded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  // Fixed buffers arrive at full capacity, zero-padded; the paired *Length
  // field says how much of it is real. Decoding the whole thing would append
  // hundreds of NUL characters to every chat message.
  return new TextDecoder().decode(bytes.subarray(0, byteLength ?? bytes.length));
}

export class GameConnection {
  private socket: WebSocket | null = null;
  private token = '';
  private handlers: ConnectionHandlers = {};

  private closedByUs = false;
  private attempt = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;

  // Modul: OBLIGATION 1. Every ClientCommand must carry the LogicEpochCounter
  // from the most recent StateUpdate. ValidateEpochSynchronization's "epoch
  // interception gate" calls TerminateSessionForSecurity otherwise - the
  // socket closes with 1008 "Violent termination" and the server logs
  // absolutely nothing. A brand new account hides this bug, because its
  // counter is still 0 and 0 is exactly what an unaware client sends; a
  // played-in account is killed on its first command.
  private epoch = 0;

  // Modul: OBLIGATION 2. The seed most recently answered, so each challenge is
  // answered exactly once. The server issues a fresh seed on the next
  // broadcast after one is answered, so this changes constantly and an
  // "answer every time we see a non-zero seed" loop would spam the socket.
  private answeredChallengeSeed = 0;

  private playerId = 0;

  // Modul: server time synchronisation (port plan 4d). Cooldowns and event
  // windows on this wire are epoch-based, and a browser clock can be
  // arbitrarily wrong with no platform guarantee to fall back on. Offset is
  // computed from the first packet that carries a server timestamp; until
  // then it is 0, which is exactly as wrong as trusting Date.now() and no
  // worse.
  private serverTimeOffsetMs = 0;

  get currentEpoch(): number {
    return this.epoch;
  }

  /** Server-corrected wall clock. Never call Date.now() directly for game time. */
  serverNowMs(): number {
    return Date.now() + this.serverTimeOffsetMs;
  }

  connect(token: string, handlers: ConnectionHandlers): void {
    this.token = token;
    this.handlers = handlers;
    this.closedByUs = false;
    this.attempt = 0;
    this.open();
  }

  disconnect(): void {
    this.closedByUs = true;
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.socket?.close();
    this.socket = null;
    this.report('idle', '');
  }

  private report(phase: ConnectionPhase, detail: string): void {
    this.handlers.onStatus?.({ phase, detail, attempt: this.attempt });
  }

  private open(): void {
    this.report(this.attempt === 0 ? 'connecting' : 'reconnecting', '');

    const socket = new WebSocket(WS_URL);
    this.socket = socket;

    socket.onopen = () => {
      this.report('authenticating', '');
      const tokenBytes = new TextEncoder().encode(this.token).length;
      socket.send(
        JSON.stringify({
          [TYPE_PROPERTY]: PacketType.AuthHandshake,
          [MODE_PROPERTY]: 'json',
          JwtToken: toBase64(this.token),
          JwtTokenLength: tokenBytes,
          AssetHash: 0,
          PlatformSignature: 0,
        }),
      );
    };

    socket.onmessage = (event) => this.receive(event);

    socket.onclose = (event) => {
      if (this.socket !== socket) return;
      this.socket = null;

      if (this.closedByUs) {
        this.report('idle', '');
        return;
      }

      const outcome = interpretClose(event.code, event.reason ?? '');
      if (!outcome.reconnect) {
        // Nothing to retry - App.svelte watches for this phase and hands the
        // player the login form instead of a spinner counting attempts.
        this.report(outcome.phase, outcome.detail);
        return;
      }

      this.scheduleReconnect(outcome.detail);
    };

    socket.onerror = () => {
      // onerror carries no useful detail in browsers by design; onclose
      // always follows it, so the reconnect is driven from there alone.
    };
  }

  private scheduleReconnect(detail: string): void {
    this.attempt += 1;

    // Exponential backoff with jitter. Jitter matters even for a single
    // player: a server restart reconnects every client at once, and
    // synchronised retries turn a rolling restart into a thundering herd.
    const backoff = Math.min(BASE_RECONNECT_DELAY_MS * 2 ** (this.attempt - 1), MAX_RECONNECT_DELAY_MS);
    const delay = backoff * (0.5 + Math.random() * 0.5);

    this.report('reconnecting', detail);
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.open();
    }, delay);
  }

  private receive(event: MessageEvent): void {
    if (typeof event.data !== 'string') {
      // A JSON session never receives binary. If one arrives, the connection
      // is not in the mode we asked for and guessing would be worse.
      console.error('binary frame on a JSON session - protocol mode mismatch');
      return;
    }

    let packet: { [key: string]: unknown };
    try {
      packet = JSON.parse(event.data);
    } catch {
      console.error('unparseable packet from server');
      return;
    }

    switch (packet[TYPE_PROPERTY]) {
      case PacketType.StateUpdate:
        this.onStateUpdate(packet as unknown as StateUpdate);
        break;
      case PacketType.ResponseChatMessage:
        this.handlers.onChatMessage?.(packet as unknown as ResponseChatMessage);
        break;
      case PacketType.ResponseLootDrop:
        this.handlers.onLootDrop?.(packet as unknown as ResponseLootDrop);
        break;
      case PacketType.ResponseCombatEvent:
        this.handlers.onCombatEvent?.(packet as unknown as ResponseCombatEvent);
        break;
      default:
        console.warn('unhandled packet type', packet[TYPE_PROPERTY]);
    }
  }

  private onStateUpdate(packet: StateUpdate): void {
    if (this.attempt !== 0 || this.socket) {
      this.attempt = 0;
      this.report('live', '');
    }

    this.epoch = packet.LogicEpochCounter;
    this.playerId = packet.PlayerId;

    this.answerChallengeIfIssued(packet);
    this.handlers.onStateUpdate?.(packet);
  }

  private answerChallengeIfIssued(packet: StateUpdate): void {
    const seed = packet.ActiveChallengeSeed;
    if (seed === 0 || seed === this.answeredChallengeSeed) return;

    this.answeredChallengeSeed = seed;

    // Judged against the epoch the challenge was ISSUED under, which is the
    // one on this very packet - not whatever the counter has become by the
    // time we reply.
    const hash = computeChallengeHash(seed, packet.PlayerId, packet.LogicEpochCounter);

    // Every other field must be zero or the answer is rejected as malformed
    // (ValidateAntiCheatChallengeResponse checks eighteen of them), which the
    // draft shape gives for free by omission.
    this.send({
      Command: CommandType.AntiCheatChallengeResponse,
      ChallengeId: seed,
      ChallengeVerificationHash: hash,
      LogicEpochCounter: packet.LogicEpochCounter,
    });
  }

  /** Sends a command, stamping the epoch so obligation 1 cannot be forgotten. */
  send(draft: ClientCommandDraft): void {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) return;

    this.socket.send(
      JSON.stringify({
        [TYPE_PROPERTY]: PacketType.ClientCommand,
        LogicEpochCounter: this.epoch,
        ...draft,
      }),
    );
  }

  sendChat(text: string, channelType: number, targetPlayerId = 0): void {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) return;
    const byteLength = new TextEncoder().encode(text).length;

    const draft: RequestChatMessageDraft = {
      ChannelType: channelType,
      TargetPlayerId: targetPlayerId,
      MessageLength: byteLength,
      MessageText: toBase64(text),
    };

    this.socket.send(JSON.stringify({ [TYPE_PROPERTY]: PacketType.RequestChatMessage, ...draft }));
  }

  get currentPlayerId(): number {
    return this.playerId;
  }
}

export const connection = new GameConnection();
