# What FolkIdle needs to hear

A working list for generating the game's audio. Every row says what the file
must be called, how long it should be, and what it should sound like — enough to
paste into a generator.

Hand finished files back as ordinary `.wav` and drop them in
`client/Assets/Resources/Audio/`. Nothing else is needed: the server links that
folder out at `/audio/<name>.wav`, the client fetches by exact filename, and a
clip that appears starts playing with **no code change**. See that folder's own
`README.md` for the mechanism.

---

## The rules, before anything is generated

| | |
|---|---|
| **Format** | `.wav`, **mono, 44100 Hz, 16-bit** — matches every existing clip |
| **Filename** | Exact, lowercase, from the tables below. The extension is part of the name |
| **Loudness** | Peak around **−3 dBFS**, no clipping. All clips share one volume slider, so a clip mastered louder than the rest will be the one players complain about |
| **Head/tail** | Trim silence to near zero at the head. A UI click with 80 ms of lead-in feels broken |
| **No LFS** | Commit as ordinary git blobs. The game was **silent in production for its entire life** because these were LFS pointers the deploy box could not resolve. CI and the Dockerfile both fail now if a clip is a stub — do not add them back to LFS |

### The sound of this game

FolkIdle is **Slavic and Czech folklore**, not generic high fantasy. The races
are Vila, Vodník, Draugr, Kobold, Moosleute; the places are a village, an inn, a
forge, a town hall. Sound design should follow that:

- **Yes:** wood, bone, iron, rope, stone, water, fire. Folk instruments —
  **fujara** and **koncovka** (overtone flutes), **dudy** (Czech bagpipe),
  **cimbalom** / hammered dulcimer, frame drum, jaw harp, low male choral drone.
- **No:** orchestral brass fanfares, synth pads, chiptune, sci-fi risers,
  anything that sounds like a slot machine. The game is rustic and a bit grim.
- **Restraint.** This is an idle game people leave running. Every sound here can
  fire hundreds of times an hour. Short, soft and dry beats big and wet.

---

## 1. Clips that already exist — do not regenerate

These eleven are in the game and working. Listed so nothing is duplicated.

`ui_button_click` · `ui_window_open` · `combat_player_hit` ·
`combat_monster_defeated` · `loot_dropped` · `loot_rare_dropped` ·
`crafting_completed` · `level_up` · `race_unlocked` · `achievement_unlock` ·
`error`

If any of them sounds wrong to you, replacing the file is enough — same name,
same folder.

---

## 2. Wired but missing — **generate these first**

The code already calls all six. They are fetched, 404, and fall back to silence
or to `combat_player_hit`. Dropping the file in is the entire job.

| File | Length | How it should sound |
|---|---|---|
| `combat_hit_melee.wav` | 0.10–0.18 s | A blade or axe landing on flesh and mail. Dry thud with a short metallic edge on the front. **No** cartoon "shing" — the ring belongs to the armour, not the sword. Currently every weapon in the game uses the same generic hit |
| `combat_hit_ranged.wav` | 0.10–0.18 s | Bowstring release plus impact, close together. A taut *thum* then a wet *tk*. Woody, not whistling |
| `combat_hit_magic.wav` | 0.15–0.25 s | Folk magic, not a fireball: a struck cimbalom string with a breathy overtone-flute swell under it. Slightly detuned. Should feel *old*, not electrical |
| `combat_hit_crit.wav` | 0.20–0.30 s | The melee hit with weight added — a lower body, a bone-crack transient, and a single low drum hit under it. Plays **on top of** the normal hit, so it must sit low and not fight it |
| `combat_player_died.wav` | 0.8–1.5 s | Your character dying. A low drone falls away, a frame drum stops dead, one breath out. Bleak and quiet — **not** a fanfare of failure. The player will hear this at their worst moment |
| `combat_boss_first_clear.wav` | 2.0–3.5 s | The rarest sound in the game — five per account, ever. Male choral drone opening upward, a struck bell, a fujara note held over it. Triumphant but earthy. This one is allowed to be big |

---

## 3. Not wired yet — worth adding

These have no code calling them. Each needs about one line at the right call
site, which I can add when the files exist. Ordered by how much they'd improve
the game.

| File | Length | How it should sound |
|---|---|---|
| `gather_woodcutting.wav` | 0.25–0.4 s | An axe biting into a trunk. Dry, woody, a little echo of the swing. **Loops every few seconds while chopping — must be soft** |
| `gather_mining.wav` | 0.25–0.4 s | Pick on stone, with grit falling after. Higher and sharper than the axe so the two professions are distinguishable with eyes closed |
| `gather_fishing.wav` | 0.3–0.5 s | A line casting and a small splash. Water, rope, a wooden rod flex |
| `gather_complete.wav` | 0.3–0.5 s | A gathering node finishing. Something is set down — a basket on a table, a small satisfied wooden knock |
| `village_upgrade_done.wav` | 1.0–1.5 s | A building finished: hammer on timber, then a beam settling into place. Heavy, constructive, warm |
| `breeding_child_born.wav` | 1.0–2.0 s | A birth in the bloodline. Warm, human, a little wondrous — a soft choral swell and a single high flute note. **Not** a baby crying. This is the emotional centre of the long game and currently makes no sound at all |
| `market_sold.wav` | 0.2–0.35 s | Coins onto wood. Small, dry, three or four coins — not a jackpot cascade |
| `item_equip.wav` | 0.15–0.25 s | Leather and buckle, a piece of kit settling on. Cloth and metal, quiet |
| `delve_descend.wav` | 1.0–2.0 s | Going down into the Delve: a heavy door, air changing, a low drone opening beneath. Should feel like leaving the surface |
| `season_rollover.wav` | 2.5–4.0 s | The ninety-day season turning over. A long bell, wind, a choral drone resolving. Ceremonial and final — everything a player built has just been taken and their bloodline survived |

---

## 4. Music — the biggest gap

There is currently **no music of any kind**. This is the single largest
difference in perceived quality between FolkIdle and anything comparable on a
store.

> **Music needs code before files are useful.** `client_web/src/lib/ui/audio.ts`
> is 199 lines of one-shot sound effects: one gain bus, no looping, no
> crossfade, and it decodes whole files into memory — which is fine for a 0.04 s
> click and wrong for a three-minute track on a phone. Music needs a second
> audio bus, its own volume slider, seamless looping, and streaming playback. I
> can build that so tracks drop in the same way clips do; say the word.

Tracks should **loop seamlessly** — the end must run into the start with no gap
or click. Generate a little longer than feels necessary; a two-minute loop heard
for an hour becomes a two-minute loop you hate.

| File | Length | How it should sound |
|---|---|---|
| `music_menu.wav` | 1.5–2.5 min | The login and hub theme. Solo fujara over a quiet drone, a frame drum entering late. Sparse, inviting, a little melancholy. The first thing anyone hears — it sets whether this reads as a folk tale or a spreadsheet |
| `music_village.wav` | 2–3 min | The village, inn, market, crafting. Warm and domestic: cimbalom, plucked strings, a lazy triple metre. Somewhere people live |
| `music_combat.wav` | 2–3 min | Under ordinary fighting. Frame drum and jaw harp, driving but **low-key** — this plays for hours. Tension without urgency; no rising climaxes that never resolve |
| `music_boss.wav` | 1.5–2.5 min | The five region bosses. Everything the combat bed refuses: dudy drone, full frame drums, a male choir low and close. This is the only time the game is allowed to be loud |
| `music_delve.wav` | 2–3 min | Underground. Near-silence with intent — a drone, dripping water, an occasional struck string. Mostly space |
| `music_ambient_wild.wav` | 2–3 min | Gathering out in the regions. Wind, distant birds, a flute figure that returns every 30–40 s. Barely music |

### If you only make one

`music_menu.wav`. It is the first thirty seconds of the game for every player,
and right now those thirty seconds are silent while a blank page loads.

---

## How to prompt a generator

Give the tool the palette, the length, the mood and the restraint — in that
order. For example, for `combat_hit_melee.wav`:

> Short dry sound effect, 150 milliseconds, mono. An iron axe blade striking
> leather armour and flesh. A dull woody thud with a brief metallic edge at the
> attack. Close-miked, no reverb, no music, no cartoon whoosh. Rustic medieval,
> not fantasy.

And for `music_menu.wav`:

> Seamless looping instrumental, 2 minutes. Slovak fujara overtone flute over a
> low sustained drone, with a soft frame drum entering after 40 seconds.
> Slavic folk, sparse and melancholy, no percussion build, no orchestral
> instruments, no modern production. Quiet and patient.

Then trim, normalise to about −3 dBFS peak, convert to mono 44.1 kHz 16-bit WAV,
and name it exactly as the table says.
