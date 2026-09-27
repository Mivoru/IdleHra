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

**What is still MISSING, and how to make it:** see
`docs/AUDIO_REQUIREMENTS.md` - every clip the code already calls and does not
get, the ones worth adding, the music the game has none of, and a sound-design
brief for each so they can be generated and dropped straight in here.

## What exists, and when it plays

The clips marked **2026-09-27** came from the owner's own set
(`D:\FolkIdleSounds`, MP3). They were converted to this folder's format
(44.1 kHz mono 16-bit WAV, which the server route, `ops/validate_audio.py` and
the Dockerfile check all assume), trimmed of leading and trailing silence
(`combat_hit_magic` was 8 s of which 0.9 s was sound), and peak-matched to the
existing mix: quiet for what repeats (a click at -15 dBFS, a miss at -14),
loud for what is rare (the first-clear fanfare at -4).

| File | Raised when |
|---|---|
| `ui_button_click.wav` | Any nav button - **2026-09-27** |
| `ui_window_open.wav` | A chrono boost starts; Settings' sound check - **2026-09-27** |
| `ui_window_close.wav` | The offline, victory or death card is closed - **2026-09-27** |
| `notification.wav` | The unclaimed-mail count ROSE between two polls - **2026-09-27** |
| `combat_player_hit.wav` | Fallback for the crit hit, and the world boss strike |
| `combat_hit_melee.wav` | A landed hit, melee weapon - **2026-09-27** |
| `combat_hit_ranged.wav` | A landed hit, ranged weapon - **2026-09-27** |
| `combat_hit_magic.wav` | A landed hit, magic weapon - **2026-09-27** |
| `combat_miss.wav` | The player's swing missed - **2026-09-27** |
| `combat_monster_defeated.wav` | A monster's health reached zero on an authoritative snapshot |
| `combat_player_died.wav` | The fighting character (slot 1) died, a man - **2026-09-27** |
| `combat_player_died_female.wav` | The same, a woman; sex read from the Hall (`deathSound.ts`) - **2026-09-27** |
| `combat_boss_first_clear.wav` | A boss beaten for the first time - **2026-09-27** |
| `loot_dropped.wav` | A drop below quality tier 10; a mail claim |
| `loot_rare_dropped.wav` | A drop at quality tier 10 or above (see `rarity.ts`, `shouldGlow`) |
| `item_sold.wav` | A chest sale to the vendor - **2026-09-27** |
| `item_equipped.wav` | An equip sent from the Chest or the Character screen - **2026-09-27** |
| `delve_door_open.wav` | A Delve door chosen - **2026-09-27** |
| `crafting_completed.wav` | A craft finished |
| `level_up.wav` | The account level rose - **2026-09-27** |
| `race_unlocked.wav` | A region boss first-kill granted a playable race |
| `achievement_unlock.wav` | An achievement tier crossed |
| `error.wav` | Any rejection surfaced as a toast; the last fallback for a death - **2026-09-27** |

## What does NOT exist yet, and what plays instead

| File | Plays instead today | Wanted because |
|---|---|---|
| `combat_hit_crit.wav` | `combat_player_hit` | A crit is a stat players buy and could not hear |

Adding it needs no code change, and `ops/validate_audio.py` deliberately does
not require it. Every other clip above IS required there, and `exercise.mjs`
fails on any audio 404 other than this one.

## Volume and mute

Owned by `client_web/src/lib/ui/audio.ts` and persisted to `localStorage` under
`folkidle.volume` and `folkidle.muted`, driven by the Settings screen. Volume is
a single master `GainNode`; there is no separate music bus, because there is no
music.

## Music

There is none, and no machinery for it either. The Unity `AmbientAudioEngine`
that used to crossfade four tracks went with that client. If music is ever
wanted, it is a new thing rather than a folder to fill.
