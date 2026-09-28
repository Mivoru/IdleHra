import { describe, it, expect } from 'vitest';
import { playlistFor, nextInPlaylist, DEFAULT_MUSIC_VOLUME, type MusicTrack } from '../src/lib/ui/musicPlaylist';

const tracks: MusicTrack[] = [
  { File: 'a.mp3', Title: 'A' },
  { File: 'b.mp3', Title: 'B' },
  { File: 'c.mp3', Title: 'C' },
];

describe('background music (owner, 2026-09-28)', () => {
  it('cycles every track in order, wrapping', () => {
    const list = playlistFor(tracks, 'cycle', null, []);
    expect(list.map((t) => t.File)).toEqual(['a.mp3', 'b.mp3', 'c.mp3']);
    expect(nextInPlaylist(list, null)?.File).toBe('a.mp3');
    expect(nextInPlaylist(list, 'a.mp3')?.File).toBe('b.mp3');
    expect(nextInPlaylist(list, 'c.mp3')?.File).toBe('a.mp3');
  });

  it('leaves out a track taken out of the cycle', () => {
    const list = playlistFor(tracks, 'cycle', null, ['b.mp3']);
    expect(list.map((t) => t.File)).toEqual(['a.mp3', 'c.mp3']);
    // A track just removed is not in the list, so the next is the first.
    expect(nextInPlaylist(list, 'b.mp3')?.File).toBe('a.mp3');
  });

  it('repeats one track, falling back to the first if the choice is gone', () => {
    expect(playlistFor(tracks, 'one', 'c.mp3', []).map((t) => t.File)).toEqual(['c.mp3']);
    expect(playlistFor(tracks, 'one', 'gone.mp3', []).map((t) => t.File)).toEqual(['a.mp3']);
    // Taking a track out of the cycle does not stop it being the one repeated.
    expect(playlistFor(tracks, 'one', 'c.mp3', ['c.mp3']).map((t) => t.File)).toEqual(['c.mp3']);
  });

  it('plays nothing when every track is taken out, and starts quiet', () => {
    expect(playlistFor(tracks, 'cycle', null, ['a.mp3', 'b.mp3', 'c.mp3'])).toEqual([]);
    expect(nextInPlaylist([], null)).toBeNull();
    expect(DEFAULT_MUSIC_VOLUME).toBeLessThanOrEqual(0.2);
  });
});
