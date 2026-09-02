# Audio clips

**These files are served to the WEB client.** The server links this folder out
at `/audio/<file>` (see `FolkIdle.Server.csproj`) and `client_web`'s
`lib/ui/audio.ts` fetches, decodes and plays them through plain Web Audio.
Keeping one folder rather than a second copy is why the two clients could never
disagree about what a level-up sounds like — and now that the Unity client is
retired, this is simply where the game's sound lives.

**These files are ORDINARY GIT BLOBS, and must stay that way.** They were
tracked in Git LFS until 2026-09-02; git-lfs is not installed on the Oracle
deploy box, so its checkout held 130-byte pointer stubs, the server served
those as `audio/wav`, `decodeAudioData` rejected them and the fallback below
swallowed it. **The game was silent in production for its entire life and
nothing logged a word.** The last rule in `.gitattributes` un-sets the LFS
filter for this directory; `ops/validate_audio.py` (run by CI) and a check in
`server/Dockerfile` both fail if a clip ever comes back as a pointer stub. See
`ops/oracle/README.md`.

The extension **is** part of the name for the web client, unlike Unity's
`Resources.Load`. Everything below is `.wav`.

**A missing clip is survivable by design.** `loadClip` returns null, the caller
plays nothing (or a fallback, see below), and nothing is logged beyond the
browser's own 404. Dropping a file in starts playing it with no code change.
The miss is remembered per session, so an absent clip is fetched once rather
than on every swing.

## What exists, and when it plays

| File | Raised when |
|---|---|
| `ui_button_click.wav` | Any nav button |
| `ui_window_open.wav` | A chrono boost starts |
| `combat_player_hit.wav` | Every landed hit — and the fallback for all four hit clips below |
| `combat_monster_defeated.wav` | A monster's health reached zero on an authoritative snapshot |
| `loot_dropped.wav` | A drop below quality tier 10 |
| `loot_rare_dropped.wav` | A drop at quality tier 10 or above (see `rarity.ts`, `shouldGlow`) |
| `crafting_completed.wav` | A craft finished |
| `level_up.wav` | The account level rose — and the fallback for the first-clear fanfare |
| `race_unlocked.wav` | A region boss first-kill granted a playable race |
| `achievement_unlock.wav` | An achievement tier crossed |
| `error.wav` | Any rejection surfaced as a toast — and the fallback for a death |

## What does NOT exist yet, and what plays instead

These six are referenced by the client and are **not in this folder**. Each
falls back to an existing clip, so the game is never silent where it should not
be — but a fallback is a stand-in, not the intended sound. Adding any of these
files needs no code change.

| File | Plays instead today | Wanted because |
|---|---|---|
| `combat_hit_melee.wav` | `combat_player_hit` | A claymore and a wand made the identical noise |
| `combat_hit_ranged.wav` | `combat_player_hit` | " |
| `combat_hit_magic.wav` | `combat_player_hit` | " |
| `combat_hit_crit.wav` | `combat_player_hit` | A crit is a stat players buy and could not hear |
| `combat_player_died.wav` | `error` | Dying shares a sound with a mistyped form |
| `combat_boss_first_clear.wav` | `level_up` | The hardest fight in the game sounds like an ordinary level |

The hit clips are chosen by weapon family in `audio.ts`'s `playHit`, which is
also where the fallback chain lives.

## Volume and mute

Owned by `client_web/src/lib/ui/audio.ts` and persisted to `localStorage` under
`folkidle.volume` and `folkidle.muted`, driven by the Settings screen. Volume is
a single master `GainNode`; there is no separate music bus, because there is no
music.

## Music

There is none, and no machinery for it either. The Unity `AmbientAudioEngine`
that used to crossfade four tracks went with that client. If music is ever
wanted, it is a new thing rather than a folder to fill.
