// Modul: the live-state stores, fed by the WebSocket. The descendant of
// VisualSyncProxy (1100 lines), minus everything the browser already does.
//
// One store per packet domain, as the port plan specifies. The raw snapshot
// and the smoothed one are deliberately SEPARATE stores rather than one merged
// object: mixing an interpolated health value into the same object as
// authoritative fields is how a UI ends up making decisions ("is it dead?")
// from a value that is a rendering artefact rather than a fact. Anything that
// decides reads `playerState`; anything that animates reads `visualState`.

import { writable, get } from 'svelte/store';
import { connection, fromBase64, type ConnectionStatus } from '../net/connection';
import {
  SnapshotInterpolator,
  extractInterpolated,
  type InterpolatedFields,
} from '../net/interpolation';
import { DamageFeed, type DamageEvent } from './damage';
import { CommandResultFeed, COMMAND_RESULT_SUCCESS, type CommandResultEntry } from './commandResults';
import { queryClient } from '../net/queryClient';
import { initTutorial, notifyItemLooted, notifyItemCrafted, notifyCombatWon } from './tutorial';
import { play } from '../ui/audio';
import type { StateUpdate, ResponseChatMessage, ResponseLootDrop } from '../net/protocol.generated';

// ---------------------------------------------------------------------------
// Connection
// ---------------------------------------------------------------------------

export const connectionStatus = writable<ConnectionStatus>({
  phase: 'idle',
  detail: '',
  attempt: 0,
});

// ---------------------------------------------------------------------------
// Authoritative state
// ---------------------------------------------------------------------------

export const playerState = writable<StateUpdate | null>(null);

// Modul: MAX HEALTH IS NOT ON THE WIRE. StateUpdatePacket carries PlayerHp and
// nothing to scale it against, so a health bar has no honest denominator.
//
// Derived here, once, as the highest value seen this session - rather than
// each screen inventing its own guess, which is how two screens end up
// disagreeing about the same character. Combat's bar and the Character sheet
// now read the same number.
//
// It is a floor, not a fact: a character that has not been at full health this
// session reads low, and the bar then looks fuller than it is. Acceptable
// because this value only ever scales a bar - nothing decides from it - but it
// is the reason MaxHp belongs on the wire eventually.
export const observedMaxPlayerHp = writable(1);

// ---------------------------------------------------------------------------
// Smoothed state
// ---------------------------------------------------------------------------

const interpolator = new SnapshotInterpolator();
export const visualState = writable<InterpolatedFields | null>(null);

let animationHandle = 0;

// Tutorial and audio edge detection. -1 / 0 mean "no baseline yet", so the
// first packet of a session never fires a cue for progress made while away.
let tutorialArmed = false;
let lastCraftedCount = -1;
let lastLevel = 0;
let lastMonsterHp = 0;
// -1 so the FIRST packet of a session never counts as a change - a reconnect
// would otherwise announce every race the player already had.
let lastUnlockedRaceMask = -1;
let lastHaltReason = 0;

// ---------------------------------------------------------------------------
// Floating damage
// ---------------------------------------------------------------------------

const damageFeed = new DamageFeed();
export const damageEvents = writable<DamageEvent[]>([]);

/**
 * The median of this player's last sixteen observed hits, or null before any
 * have been seen.
 *
 * Exists because the world boss asks the client for a damage number and there
 * is no stat on the wire to compute one from - the state snapshot carries no
 * attack power, only outcomes. Measuring what hits actually land for is the
 * only honest answer available, and it updates on the same path the floating
 * numbers do rather than on a timer of its own.
 */
export const typicalHit = writable<number | null>(null);

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

  commandResults.update((entries) => {
    const live = entries.filter((e) => now - e.atMs < COMMAND_RESULT_LIFETIME_MS);
    return live.length === entries.length ? entries : live;
  });

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
// Command results
// ---------------------------------------------------------------------------

const commandResultFeed = new CommandResultFeed();
export const commandResults = writable<CommandResultEntry[]>([]);

/** Long enough to read a sentence, short enough not to stack up. */
const COMMAND_RESULT_LIFETIME_MS = 6000;

export function dismissCommandResult(id: number): void {
  commandResults.update((entries) => entries.filter((e) => e.id !== id));
}

let localNoticeSequence = -1;

/**
 * Shows a message through the same toast channel the server's command results
 * use, for refusals this CLIENT made - see net/commands.ts, which declines to
 * send values the server would answer with a disconnect.
 *
 * Deliberately shares the channel: from the player's side "the server said no"
 * and "we did not ask because it would have been no" are the same event, and
 * splitting them across two notification styles would only make the UI harder
 * to read. Negative ids so they can never collide with a server result.
 */
export function pushLocalNotice(message: string, tone: 'info' | 'error' = 'error'): void {
  const entry: CommandResultEntry = {
    id: localNoticeSequence--,
    // Reuses the server's success code for an informational notice so the
    // toast is not styled as a failure - "Guild created" in red reads as
    // something having gone wrong.
    code: tone === 'info' ? COMMAND_RESULT_SUCCESS : -1,
    tick: 0,
    message,
    atMs: performance.timeOrigin + performance.now(),
  };
  commandResults.update((entries) => [...entries, entry]);

  // A refusal makes a noise; an informational notice does not. The refusals
  // are the ones a player might otherwise miss - they usually follow a click
  // that visibly did nothing, which is exactly when a toast in the corner is
  // easiest to look past.
  if (tone === 'error') play('error');
}

// ---------------------------------------------------------------------------
// Offline summary
// ---------------------------------------------------------------------------

export interface OfflineCharacterEarnings {
  slot: number;
  raceId: number;
  gold: number;
  xp: number;
  drops: number;
}

export interface OfflineSummary {
  elapsedSeconds: number;
  goldEarned: number;
  xpEarned: number;
  materialDropsGranted: number;
  /** True when the catch-up granted nothing at all - see the note below. */
  earnedNothing: boolean;
  /** One row per character that exists, so an idle worker is visible as a
   *  row of zeroes rather than by its absence. */
  perCharacter: OfflineCharacterEarnings[];
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
  // A different account has a different maximum; carrying the old one over
  // would scale the new player's bar against a stranger's health.
  observedMaxPlayerHp.set(1);
  tutorialArmed = false;
  lastCraftedCount = -1;
  lastLevel = 0;
  lastMonsterHp = 0;
  lastUnlockedRaceMask = -1;
  lastHaltReason = 0;
  commandResultFeed.reset();
  commandResults.set([]);
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
        // The ring buffer survives a reconnect, so the watermark must reprime
        // or every old rejection pops again on reconnect - and again on the
        // next one.
        commandResultFeed.reset();
      }
      if (status.phase === 'idle') stopPump();
    },

    onStateUpdate: (packet: StateUpdate) => {
      const arrivedAtMs = performance.timeOrigin + performance.now();
      playerState.set(packet);
      observedMaxPlayerHp.update((seen) => Math.max(seen, packet.PlayerHp));

      interpolator.push(
        extractInterpolated(packet as unknown as Record<string, unknown>),
        packet.CurrentMonsterId,
        arrivedAtMs,
      );

      // Fed from the AUTHORITATIVE packet, never the interpolated value - the
      // smoothed number passes through every intermediate value on its way,
      // which would turn one hit into a blizzard of fictional tiny ones.
      // A monster's health reaching zero on a snapshot that still names it is
      // the only "you won" signal available - see damage.ts for why this wire
      // carries no combat events at all.
      if (lastMonsterHp > 0 && packet.CurrentMonsterHp <= 0 && packet.CurrentMonsterId > 0) {
        notifyCombatWon();
        play('monsterDefeated');
      }
      lastMonsterHp = packet.CurrentMonsterHp;

      const hit = damageFeed.push({
        monsterId: packet.CurrentMonsterId,
        monsterHp: packet.CurrentMonsterHp,
        atMs: arrivedAtMs,
      });
      if (hit !== null) {
        damageEvents.set(damageFeed.current);
        typicalHit.set(damageFeed.typicalHit);
      }

      // Turns every silently-rejected command into an explanation. Without
      // this the player presses a button, nothing happens, and nothing
      // anywhere says why - which is the exact state the server's result ring
      // buffer was added to end.
      const results = commandResultFeed.accept(
        packet as unknown as Record<string, unknown>,
        arrivedAtMs,
      );
      if (results.length > 0) {
        commandResults.update((entries) => [...entries, ...results]);
        // One cue per batch, not per result: the ring buffer can deliver four
        // at once and four overlapping error tones is a noise, not a signal.
        if (results.some((r) => r.code !== COMMAND_RESULT_SUCCESS)) play('error');

        // Modul: A COMMAND RESULT IS THE SERVER SAYING "THAT IS DONE", so it is
        // the moment every screen's data is stale.
        //
        // Nine screens each guessed at this with a setTimeout - 400ms here,
        // 700 there, 900 in Breeding - which is a guess about how long a
        // Serializable transaction plus a state reload takes. Too short and
        // the refetch reads the OLD rows and the screen looks unchanged; too
        // long and it feels broken. Reported as "I have to press F5 to see the
        // gold update".
        //
        // Invalidated globally rather than per screen because the results ring
        // does not say WHICH command it is answering - and a command a player
        // just issued can change gold, inventory, equipment and the village at
        // once. TanStack only refetches what is actually mounted and observed,
        // so the cost of the broad brush is small and the cost of missing one
        // is a screen that lies.
        queryClient.invalidateQueries();
      }

      // Modul: the tutorial arms from IsFreshAccount - the server's own signal
      // that this account's first character has never aged - which is the same
      // thing UiTutorialController keys off. Armed once, on the first packet.
      if (!tutorialArmed) {
        tutorialArmed = true;
        initTutorial(packet.IsFreshAccount !== 0);
      }

      // Modul: TotalItemsCraftedCount RISING is how a finished craft is
      // detected - there is no craft-completed event on this wire, and the
      // Unity tutorial controller reads the same counter for the same reason.
      // It sat at a hardcoded zero until 2026-08-01, which made that tutorial
      // step impossible to complete.
      if (lastCraftedCount >= 0 && packet.TotalItemsCraftedCount > lastCraftedCount) {
        notifyItemCrafted();
        play('craftingCompleted');
      }
      lastCraftedCount = packet.TotalItemsCraftedCount;

      if (lastLevel > 0 && packet.CurrentLevel > lastLevel) play('levelUp');
      lastLevel = packet.CurrentLevel;

      // Modul: the last two unused clips, wired to the events they were
      // recorded for rather than to a button somewhere.
      //
      // Sound lives HERE, on the packet, not scattered through seventeen
      // screens. A cue tied to a click fires when the player asks for
      // something; a cue tied to the state fires when it actually happened,
      // which is the difference between "I pressed craft" and "the craft
      // finished". It also means a screen cannot forget to make a noise.
      if (lastUnlockedRaceMask >= 0 && packet.UnlockedRaceBitmask !== lastUnlockedRaceMask) {
        // Only a NEW bit is a celebration; the mask can also arrive for the
        // first time on a reconnect, which is not.
        const gained = packet.UnlockedRaceBitmask & ~lastUnlockedRaceMask;
        if (gained !== 0) play('raceUnlocked');
      }
      lastUnlockedRaceMask = packet.UnlockedRaceBitmask;

      // A halt is the one state change a player most needs to notice, because
      // it is silent by nature - the character simply stops earning. Only on
      // the EDGE, or a stopped character would buzz every 1.6 seconds forever.
      if (lastHaltReason === 0 && packet.ActivityHaltReason !== 0) play('error');
      lastHaltReason = packet.ActivityHaltReason;

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
          const EMPTY = '00000000-0000-0000-0000-000000000000';
          const perCharacter: OfflineCharacterEarnings[] = [];
          if (packet.Slot1_CharacterId !== EMPTY) {
            perCharacter.push({
              slot: 1,
              raceId: packet.Slot1_RaceId,
              gold: packet.OfflineSlot1Gold,
              xp: packet.OfflineSlot1Xp,
              drops: packet.OfflineSlot1Drops,
            });
          }
          if (packet.Slot2_CharacterId !== EMPTY) {
            perCharacter.push({
              slot: 2,
              raceId: packet.Slot2_RaceId,
              gold: packet.OfflineSlot2Gold,
              xp: packet.OfflineSlot2Xp,
              drops: packet.OfflineSlot2Drops,
            });
          }
          if (packet.Slot3_CharacterId !== EMPTY) {
            perCharacter.push({
              slot: 3,
              raceId: packet.Slot3_RaceId,
              gold: packet.OfflineSlot3Gold,
              xp: packet.OfflineSlot3Xp,
              drops: packet.OfflineSlot3Drops,
            });
          }

          offlineSummary.set({
            elapsedSeconds: packet.OfflineElapsedSeconds,
            goldEarned: packet.OfflineGoldEarned,
            xpEarned: packet.OfflineXpEarned,
            materialDropsGranted: packet.OfflineMaterialDropsGranted,
            earnedNothing,
            perCharacter,
          });
        }
      }

      startPump();
    },

    onLootDrop: (packet: ResponseLootDrop) => {
      notifyItemLooted();
      play(packet.QualityTier >= 10 ? 'lootRare' : 'lootDropped');

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
  commandResultFeed.reset();
  commandResults.set([]);
  offlineSummary.set(null);
  playerState.set(null);
  visualState.set(null);
}

/** Current authoritative snapshot without subscribing. */
export function snapshot(): StateUpdate | null {
  return get(playerState);
}
