// The background music's pure rules - which tracks play, in what order -
// kept apart from music.ts so they are tested without a browser.

export interface MusicTrack {
  File: string;
  Title: string;
}

export type MusicMode = 'cycle' | 'one';

/** Quiet by default (owner): music sits under the game, it does not lead it. */
export const DEFAULT_MUSIC_VOLUME = 0.15;

/**
 * The tracks to play, in order. Pure, so the rules are tested without a
 * browser: "one" plays the chosen track (or the first, if the choice no longer
 * exists); "cycle" plays every track not taken out, in the server's order.
 */
export function playlistFor(tracks: readonly MusicTrack[], mode: MusicMode, one: string | null, excluded: readonly string[]): MusicTrack[] {
  if (tracks.length === 0) return [];
  if (mode === 'one') {
    const chosen = tracks.find((t) => t.File === one) ?? tracks[0];
    return [chosen];
  }
  return tracks.filter((t) => !excluded.includes(t.File));
}

/** The track after `current` in the playlist, wrapping; the first if `current` is not in it. */
export function nextInPlaylist(playlist: readonly MusicTrack[], current: string | null): MusicTrack | null {
  if (playlist.length === 0) return null;
  const at = playlist.findIndex((t) => t.File === current);
  return playlist[(at + 1) % playlist.length];
}

