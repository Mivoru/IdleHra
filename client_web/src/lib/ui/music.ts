// Modul: BACKGROUND MUSIC (owner, 2026-09-28). "I added soundtracks I want to
// play in the game, everywhere - the three in a cycle for now. In Settings the
// player controls the SFX volume and, separately, the music; both quiet by
// default so they don't disturb. A player can take a song out of the cycle, or
// have just one play over and over."
//
// An <audio> element, not Web Audio: a track is ~4 MB, and a decoded buffer of
// it would be ~40 MB of PCM per track held in memory. The element streams (the
// server answers Range requests for exactly this), and the browser owns
// buffering. SFX stay on Web Audio in audio.ts - short, decoded once, mixed.
//
// The TRACK LIST IS THE SERVER'S (/audio/music): the client keeps no copy of
// which files exist. What IS kept here is the player's own choice - volume,
// mode, which tracks are out - in localStorage, as a per-device preference.
import { writable, get } from 'svelte/store';
import { HTTP_BASE } from '../net/config';
import { muted, pageActive } from './audio';
import { readPref, writePref } from '../net/prefs';
import { currentScreen } from '../stores/navigation';
import { DEFAULT_MUSIC_VOLUME, nextInPlaylist, playlistFor, type MusicMode, type MusicTrack } from './musicPlaylist';

export { DEFAULT_MUSIC_VOLUME, type MusicMode, type MusicTrack };

const VOLUME_KEY = 'folkidle.music.volume';
const MODE_KEY = 'folkidle.music.mode';
const ONE_KEY = 'folkidle.music.one';
const EXCLUDED_KEY = 'folkidle.music.excluded';

function readVolume(): number {
  const stored = readPref(VOLUME_KEY);
  const raw = Number(stored);
  return stored !== null && stored !== '' && Number.isFinite(raw) && raw >= 0 && raw <= 1 ? raw : DEFAULT_MUSIC_VOLUME;
}

function readExcluded(): string[] {
  try {
    const parsed = JSON.parse(readPref(EXCLUDED_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

export const musicTracks = writable<MusicTrack[]>([]);
export const musicVolume = writable<number>(readVolume());
export const musicMode = writable<MusicMode>(readPref(MODE_KEY) === 'one' ? 'one' : 'cycle');
export const musicOneTrack = writable<string | null>(readPref(ONE_KEY));
export const musicExcluded = writable<string[]>(readExcluded());
export const nowPlaying = writable<MusicTrack | null>(null);

musicVolume.subscribe((v) => writePref(VOLUME_KEY, String(v)));
musicMode.subscribe((m) => writePref(MODE_KEY, m));
musicOneTrack.subscribe((f) => writePref(ONE_KEY, f ?? ''));
musicExcluded.subscribe((list) => writePref(EXCLUDED_KEY, JSON.stringify(list)));

let element: HTMLAudioElement | null = null;
let started = false;
let manifest: Promise<void> | null = null;

// Modul: A SCREEN CAN HAVE ITS OWN THEME (owner, 2026-11-19: Nav music). While
// that screen is open its loop replaces the cycle; leaving resumes the cycle's
// track where it was. The file sits in the music folder as screen_<key>.mp3,
// which the server serves but leaves out of /audio/music, so it never joins
// the everywhere cycle or the Settings list.
const SCREEN_THEMES: Readonly<Record<string, MusicTrack>> = {
  nav: { File: 'screen_nav.mp3', Title: 'Below the Roots (Nav)' },
};
let theme: MusicTrack | null = null;
let resume: { track: MusicTrack; at: number } | null = null;
let fadeRaf = 0;

function themeFor(screen: string): MusicTrack | null {
  return SCREEN_THEMES[screen] ?? null;
}

/** Eases the element's volume to `to` over `ms`, then runs `then`. */
function fade(to: number, ms: number, then?: () => void): void {
  if (!element) return;
  cancelAnimationFrame(fadeRaf);
  const el = element;
  const from = el.volume;
  const start = performance.now();
  const step = (now: number) => {
    const k = Math.min(1, (now - start) / ms);
    el.volume = Math.max(0, Math.min(1, from + (to - from) * k));
    if (k < 1) fadeRaf = requestAnimationFrame(step);
    else then?.();
  };
  fadeRaf = requestAnimationFrame(step);
}

function effectiveVolume(): number {
  return get(muted) ? 0 : get(musicVolume);
}

function loadManifest(): Promise<void> {
  if (!manifest) {
    manifest = fetch(`${HTTP_BASE}/audio/music`)
      .then((r) => (r.ok ? r.json() : { Tracks: [] }))
      .then((body: { Tracks?: MusicTrack[] }) => musicTracks.set(body.Tracks ?? []))
      .catch(() => {
        manifest = null; // try again next time
      });
  }
  return manifest;
}

function currentPlaylist(): MusicTrack[] {
  return playlistFor(get(musicTracks), get(musicMode), get(musicOneTrack), get(musicExcluded));
}

function playTrack(track: MusicTrack | null): void {
  if (!element) return;
  nowPlaying.set(track);
  if (!track) {
    element.pause();
    element.removeAttribute('src');
    return;
  }
  element.src = `${HTTP_BASE}/audio/music/${encodeURIComponent(track.File)}`;
  element.volume = effectiveVolume();
  // "One" repeats itself natively; the cycle advances on `ended`. A screen
  // theme always loops.
  element.loop = theme !== null || get(musicMode) === 'one';
  void element.play().catch(() => {
    // Autoplay refused (no gesture yet): the next gesture calls startMusic.
    started = false;
  });
}

/**
 * Starts the music. Idempotent, and safe to call on every user gesture: a
 * browser refuses to play sound before one, so App.svelte calls this from the
 * same gesture listener that unlocks the sound effects.
 */
export async function startMusic(): Promise<void> {
  if (started || typeof Audio === 'undefined') return;
  started = true;
  await loadManifest();
  if (!element) {
    element = new Audio();
    element.preload = 'auto';
    element.addEventListener('ended', () => playTrack(nextInPlaylist(currentPlaylist(), get(nowPlaying)?.File ?? null)));
  }
  if (effectiveVolume() === 0 && get(musicVolume) === 0) {
    // Turned all the way down: do not stream 4 MB nobody will hear.
    started = false;
    return;
  }
  theme = themeFor(get(currentScreen));
  playTrack(theme ?? nextInPlaylist(currentPlaylist(), null));
}

// Entering a themed screen swaps to its loop; leaving puts the cycle back.
currentScreen.subscribe((screen) => {
  const next = themeFor(screen);
  if (next?.File === theme?.File) return;
  if (!element || !started) {
    theme = next;
    return;
  }
  const el = element;
  fade(0, 350, () => {
    if (next) {
      if (!theme) {
        const playing = get(nowPlaying);
        resume = playing ? { track: playing, at: el.currentTime } : null;
      }
      theme = next;
      playTrack(next);
    } else {
      theme = null;
      const back = resume;
      resume = null;
      if (back && currentPlaylist().some((t) => t.File === back.track.File)) {
        playTrack(back.track);
        // Seeking before the new source's metadata is in is ignored.
        el.addEventListener('loadedmetadata', () => (el.currentTime = back.at), { once: true });
      } else {
        playTrack(nextInPlaylist(currentPlaylist(), null));
      }
    }
    el.volume = 0;
    fade(effectiveVolume(), 900);
  });
});

/** Signing out stops the music; the next session starts it again. */
export function stopMusic(): void {
  started = false;
  if (element) {
    element.pause();
    element.removeAttribute('src');
  }
  nowPlaying.set(null);
}

/** Jump to a track now (the Settings list's play button). */
export function playNow(file: string): void {
  const track = get(musicTracks).find((t) => t.File === file) ?? null;
  if (!track) return;
  // A track picked by hand wins over the screen's theme until the next screen.
  theme = null;
  resume = null;
  started = true;
  if (!element) void startMusic().then(() => playTrack(track));
  else playTrack(track);
}

// Volume and mute apply at once, to whatever is playing.
musicVolume.subscribe(() => {
  if (element) element.volume = effectiveVolume();
  // Turning the music up from zero starts it - but only once a gesture has
  // already created the element; before that, the gesture itself will.
  if (element && get(musicVolume) > 0 && !started) void startMusic();
});
muted.subscribe(() => {
  if (element) element.volume = effectiveVolume();
});

// A changed choice takes effect now: a track taken out of the cycle, or a
// switch between modes, re-picks what should be playing.
function reconcile(): void {
  if (!element || !started) return;
  const playlist = currentPlaylist();
  const current = get(nowPlaying)?.File ?? null;
  if (theme) return; // the screen's loop keeps playing; the cycle resumes on leaving
  element.loop = get(musicMode) === 'one';
  if (!playlist.some((t) => t.File === current)) playTrack(playlist[0] ?? null);
}
musicMode.subscribe(reconcile);
musicOneTrack.subscribe(reconcile);
musicExcluded.subscribe(reconcile);

// Away from the game's window - alt-tabbed, minimised, a phone in the
// background - the music pauses, on the same signal that silences the effects
// (audio.ts pageActive), and picks up where it was on return.
pageActive.subscribe((active) => {
  if (!element || !started || !get(nowPlaying)) return;
  if (active) void element.play().catch(() => {});
  else element.pause();
});
