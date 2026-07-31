# Audio clips

`GameAudioDirector` resolves every sound effect from this folder by name at
startup, via `Resources.Load<AudioClip>("Audio/<name>")`.

**This folder is intentionally allowed to be empty.** A missing clip resolves to
null, `GameAudioDirector.Play` returns immediately, and nothing is logged. The
game is silent but fully functional. Dropping a file in here starts playing it
with no code change and no scene rebuild.

Unity imports any of `.wav`, `.ogg`, `.mp3` or `.aiff`; the extension is not part
of the lookup name.

| File name (no extension)   | Raised when                                              |
|----------------------------|----------------------------------------------------------|
| `ui_button_click`          | Any button built by `MainSceneBuilder.CreateButton`, plus map zones and pooled list rows |
| `ui_window_open`           | Reserved - not yet raised by any call site               |
| `combat_player_hit`        | `VisualSyncProxy.OnMonsterHit`, once per real server tick where the monster lost HP |
| `combat_monster_defeated`  | `VisualSyncProxy.OnCombatInstanceChanged`, i.e. the monster was replaced |
| `loot_dropped`             | A `ResponseLootDropPacket` below quality tier 7          |
| `loot_rare_dropped`        | A `ResponseLootDropPacket` at quality tier 7 or above    |
| `crafting_completed`       | A craft dispatched from the Crafting Tree screen         |
| `level_up`                 | `VisualPlayerLevel` increased                            |
| `race_unlocked`            | A region boss first-kill granted a new playable race     |
| `error`                    | Any rejection surfaced by `UiCommandResultToast`         |

## Volume

SFX and music volumes are owned by `GameAudioDirector` and persisted to
`PlayerPrefs` under `FolkIdle.Audio.SfxVolume` / `FolkIdle.Audio.MusicVolume`.
Both are driven by the sliders on the Settings screen. Music volume is applied
as a master scalar inside `AmbientAudioEngine`, kept separate from the crossfade
weights so changing it mid-fade cannot corrupt the transition.

## Music

`AmbientAudioEngine` crossfades up to four looping tracks (ids 1-4) registered
through `RegisterTrack`. Nothing registers a track today, so there is no music
bed; the crossfade machinery and its per-frame driver are in place for when
there is one.
