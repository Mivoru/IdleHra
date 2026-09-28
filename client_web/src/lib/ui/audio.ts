// Modul: sound effects. The descendant of GameAudioDirector, SfxPoolEngine and
// AmbientAudioEngine - three Unity classes that collapse to this file, because
// the browser already owns decoding, mixing and pooling.
//
// Plain Web Audio rather than Howler: ten one-shot WAVs need decode, gain and
// play, all of which are three lines each here, and a dependency whose whole
// value is cross-browser fallbacks for formats we do not use is not worth
// 30 kB. The port plan named Howler as the default answer; this is the same
// answer arrived at more cheaply, and swapping later changes only this file.
//
// The clips are the SAME BYTES the Unity client plays - the server links them
// out of client/Assets/Resources/Audio rather than keeping a second copy, so
// the two clients cannot drift apart on what a level-up sounds like.

import { HTTP_BASE } from '../net/config';
import { writable, get } from 'svelte/store';

export const CLIPS = {
  buttonClick: 'ui_button_click.wav',
  windowOpen: 'ui_window_open.wav',
  windowClose: 'ui_window_close.wav',
  notification: 'notification.wav',
  itemSold: 'item_sold.wav',
  itemEquipped: 'item_equipped.wav',
  delveDoor: 'delve_door_open.wav',
  playerMiss: 'combat_miss.wav',

  // Modul: one hit sound for every weapon in the game is the same swing
  // whether you are holding a claymore or a wand. The three weapon clips
  // exist since 2026-09-27; the crit clip still does not, and playHit falls
  // back to the weapon's own clip for it, so dropping the WAV in later needs
  // no code change and its absence is silence-free. The generic
  // combat_player_hit clip was removed 2026-09-28 at the owner's request.
  hitMelee: 'combat_hit_melee.wav',
  hitRanged: 'combat_hit_ranged.wav',
  hitMagic: 'combat_hit_magic.wav',
  hitCrit: 'combat_hit_crit.wav',
  playerDied: 'combat_player_died.wav',
  playerDiedFemale: 'combat_player_died_female.wav',
  bossFirstClear: 'combat_boss_first_clear.wav',
  // Modul: NO PLAIN LOOT SOUND (owner, 2026-09-28): "I want the loot drop
  // sound deleted and not played at all, only the rare loot." The clip and its
  // file are gone; lootRare, pitched per tier, is the only loot sound.
  lootRare: 'loot_rare_dropped.wav',
  craftingCompleted: 'crafting_completed.wav',
  levelUp: 'level_up.wav',
  raceUnlocked: 'race_unlocked.wav',
  achievementUnlock: 'achievement_unlock.wav',
  error: 'error.wav',
} as const;

export type ClipName = keyof typeof CLIPS;

// Modul: A NEW KEY, ON PURPOSE (2026-09-28). readNumber read a MISSING value as
// 0 - Number(null) is 0, a valid volume - so every player who never touched the
// slider started with sound effects at zero, and the subscription below then
// saved that 0 back. Those saved zeros cannot be told apart from a real choice,
// so the old 'folkidle.volume' key is simply no longer read: everyone gets the
// default once, and Mute remains the way to silence the game.
const VOLUME_KEY = 'folkidle.sfxVolume';
const MUTED_KEY = 'folkidle.muted';

// Declared before the subscriptions below, which fire SYNCHRONOUSLY at module
// load - referencing a `let` declared further down is a temporal dead zone
// error, and because this module is imported by the root store it took the
// entire app down with a blank page.
let context: AudioContext | null = null;
let masterGain: GainNode | null = null;
const buffers = new Map<string, AudioBuffer>();
const pending = new Map<string, Promise<AudioBuffer | null>>();

// Clips that are not on the server. Optional per-weapon hit sounds are
// expected to be in here until somebody authors the files; see playHit.
const missing = new Set<string>();

// Modul: 0.35, not 0.6, for a player who has never set it (owner, 2026-09-28:
// "both not too loud by default, so they don't disturb"). A stored choice wins.
export const volume = writable(readNumber(VOLUME_KEY, 0.35));
export const muted = writable(localStorage.getItem(MUTED_KEY) === '1');

function readNumber(key: string, fallback: number): number {
  const stored = localStorage.getItem(key);
  if (stored === null || stored.trim() === '') return fallback;
  const raw = Number(stored);
  return Number.isFinite(raw) && raw >= 0 && raw <= 1 ? raw : fallback;
}

volume.subscribe((value) => {
  localStorage.setItem(VOLUME_KEY, String(value));
  if (masterGain) masterGain.gain.value = value;
});

muted.subscribe((value) => localStorage.setItem(MUTED_KEY, value ? '1' : '0'));

/**
 * Whether the game's window is the one the player is in (owner, 2026-09-29:
 * "when I alt-tab the music stops but the sound effects don't - I want those to
 * stop too when I'm not in the game's window").
 *
 * Hidden OR unfocused counts as away: alt-tabbing to another window blurs the
 * page without always hiding it, and a phone going to the background does both.
 * music.ts pauses on the same store, so the two can never disagree about
 * whether the player is here.
 */
export const pageActive = writable(true);

function readPageActive(): boolean {
  if (typeof document === 'undefined') return true;
  return !document.hidden && (typeof document.hasFocus !== 'function' || document.hasFocus());
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
  const update = () => pageActive.set(readPageActive());
  document.addEventListener('visibilitychange', update);
  window.addEventListener('blur', update);
  window.addEventListener('focus', update);
}

// Effects are silenced by suspending the whole context, and play() refuses to
// START one while away - a suspended context queues what is started on it, so
// a fight left running in the background would otherwise come back as a burst
// of every hit that landed meanwhile.
pageActive.subscribe((active) => {
  if (!context) return;
  if (active) void context.resume();
  else void context.suspend();
});

/**
 * Browsers refuse to start an AudioContext before a user gesture, so this is
 * called from the first click rather than at load. Calling it early does not
 * fail loudly - it produces a context stuck in "suspended" that silently plays
 * nothing, which is the kind of quiet failure worth avoiding by construction.
 */
export function unlockAudio(): void {
  if (context) {
    if (context.state === 'suspended' && get(pageActive)) void context.resume();
    return;
  }

  context = new AudioContext();
  masterGain = context.createGain();
  masterGain.gain.value = get(volume);
  masterGain.connect(context.destination);
}

async function loadClip(file: string): Promise<AudioBuffer | null> {
  if (buffers.has(file)) return buffers.get(file)!;
  // Modul: A MISS IS REMEMBERED, and it was not.
  //
  // `pending` is cleared in the finally below, so a clip that 404s was
  // refetched by the NEXT caller - and once optional per-weapon hit clips
  // existed, that meant one failed request per swing, for as long as the
  // player fought. Sound is decoration; a request every 1.5 seconds forever
  // is not.
  if (missing.has(file)) return null;
  if (pending.has(file)) return pending.get(file)!;
  if (!context) return null;

  const task = (async () => {
    try {
      const response = await fetch(`${HTTP_BASE}/audio/${file}`);
      if (!response.ok) {
        missing.add(file);
        return null;
      }
      const decoded = await context!.decodeAudioData(await response.arrayBuffer());
      buffers.set(file, decoded);
      return decoded;
    } catch {
      // A missing or undecodable clip must never break the screen that asked
      // for it - sound is decoration here, not information.
      missing.add(file);
      return null;
    } finally {
      pending.delete(file);
    }
  })();

  pending.set(file, task);
  return task;
}

/**
 * The hit sound for a weapon family.
 *
 * A FALLBACK RATHER THAN A GAP. combat_hit_crit.wav is not in the repository
 * yet - it needs authoring, which is not something code can do - and a missing
 * clip resolves to silence in loadClip. A silent crit would be a worse combat
 * feel than an ordinary hit, so a crit plays the weapon's own clip until the
 * crit file exists.
 */
export function playHit(weaponKind: number, isCrit: boolean): void {
  const weapon: ClipName = weaponKind === 1 ? 'hitRanged' : weaponKind === 2 ? 'hitMagic' : 'hitMelee';
  if (isCrit) playWithFallback('hitCrit', weapon);
  else play(weapon);
}

/**
 * Plays `name`, or `fallback` when that clip is not present.
 *
 * Resolves the buffer BEFORE deciding, so the fallback is chosen on what the
 * server actually has rather than on a hardcoded list this file would have to
 * keep in step with a directory.
 */
export function playWithFallback(name: ClipName, fallback: ClipName): void {
  if (get(muted) || !get(pageActive) || !context || !masterGain) return;

  void loadClip(CLIPS[name]).then((buffer) => {
    if (buffer) {
      playBuffer(buffer);
      return;
    }
    play(fallback);
  });
}

function playBuffer(buffer: AudioBuffer): void {
  if (!context || !masterGain || get(muted) || !get(pageActive)) return;
  const source = context.createBufferSource();
  source.buffer = buffer;
  source.connect(masterGain);
  source.start();
}

/**
 * `rate` is playbackRate - it moves pitch and length together, which is what
 * lets one rare-loot clip rise a step per rarity tier (lootFeel.ts) without a
 * second recording.
 */
export function play(name: ClipName, rate = 1): void {
  if (get(muted) || !get(pageActive) || !context || !masterGain) return;

  void loadClip(CLIPS[name]).then((buffer) => {
    if (!buffer || !context || !masterGain || get(muted) || !get(pageActive)) return;
    const source = context.createBufferSource();
    source.buffer = buffer;
    source.playbackRate.value = rate;
    source.connect(masterGain);
    source.start();
    // No pooling: an AudioBufferSourceNode is single-use by design and the
    // browser collects it after it ends. This is exactly what SfxPoolEngine
    // existed to hand-roll around Unity's allocation behaviour.
  });
}

/** Warms the cache so the first real cue is not silent while it downloads. */
export async function preloadAll(): Promise<void> {
  if (!context) return;
  await Promise.all(Object.values(CLIPS).map(loadClip));
}
