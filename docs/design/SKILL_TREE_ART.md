# Skill tree - art, effects and sound

Companion to `SKILL_TREE_REWORK.md`. It lists what the owner generates
(icons in Gemini), what the client draws itself (combat-feed callouts,
CSS/SVG effects), and the sounds (ElevenLabs Sound Effects).

## 1. Icons

### Format

- **Generate at 1024 × 1024**, square, PNG.
- **Full-bleed tile, no transparency.** Every icon is a complete painted
  square on a dark background. The client crops it into a rounded square
  and draws the border, rank badge and glow itself. Do **not** paint a
  frame, a border, text or a number into the image.
- **Background: near-black blue-grey (#0f1218)**, plain, with at most a
  faint vignette. The rune tiles sit on the darkened Yggdrasil painting,
  and a dark tile keeps the glow states readable.
- **One subject, centred, filling about 70%** of the square. It has to be
  readable at 48 px on a phone.
- Delivery: drop the PNGs into
  `client/Assets/Images/SpritesWeb/SkillIcons/<slug>.png` (for example
  `crushing_blow.png`). The sprite pipeline (`npm run generate:sprites`)
  converts them to WebP, and the panel shows the icon instead of the rune
  glyph. Until a file exists, the glyph stays.

### Prompt template (paste into Gemini, one icon per request)

```
Square 1:1 game skill icon, 1024x1024. Painted digital illustration in the
style of a dark Slavic folk-myth fantasy RPG: rich hand-painted textures,
carved wood, hammered iron and runes, warm firelight against cold night.
Subject: <SUBJECT>. Dominant accent colour: <COLOUR>, glowing softly.
Single centred subject filling about 70% of the frame, strong silhouette,
readable at small size. Plain near-black blue-grey background (#0f1218)
with a faint vignette. No border, no frame, no text, no letters, no
numbers, no watermark, no UI elements.
```

Column accent colours: Might **ember red (#e2674a)**, Precision **ice cyan
(#57c7e3)**, Guard **verdigris green (#5fc28a)**, Fortune **old gold
(#e8c15a)**. Bridges take both neighbours' colours ("ember red and ice
cyan").

### The list (War half, 35 icons + 4 column emblems)

| Slug | Node | `<SUBJECT>` |
|---|---|---|
| `col_might` | Might emblem | a war hammer crossed with a split boar-tusk, embers rising |
| `col_precision` | Precision emblem | a falcon's eye inside a carved rune circle |
| `col_guard` | Guard emblem | a round linden-wood shield with an iron boss and a bear rune |
| `col_fortune` | Fortune emblem | a golden fleece draped over a carved oak chest |
| `edge` | Edge | a sword edge catching a single bright spark |
| `breach` | Breach | an iron chest-plate with a glowing crack through it |
| `crushing_blow` | Crushing Blow | a stone maul smashing through a shield in a burst of shards |
| `executioner` | Executioner | a bearded axe above a guttering candle |
| `bossbreaker` | Bossbreaker | a giant's horned skull split by a lightning-forged spear |
| `heavy_hand` | Heavy Hand | an iron gauntlet clenched into a fist, knuckles glowing |
| `thunderer` | Thunderer | Perun's axe striking down with forked lightning |
| `ruin` | Ruin (keystone) | a black iron hammer with eight glowing notches on its haft |
| `keen_eye` | Keen Eye | a hawk's eye reflected in a drop of water |
| `cruel_edge` | Cruel Edge | a curved knife with a thin line of bright blood light |
| `relentless` | Relentless | three overlapping arrows in motion blur |
| `echo` | Echo | a blade striking, with a translucent ghost blade repeating it |
| `frenzy` | Frenzy | a wolf's snarling head made of swirling wind |
| `steady_hand` | Steady Hand | a hand drawing a bowstring, perfectly still, a rune on the wrist |
| `double_strike` | Double Strike | two crossed daggers striking at the same instant |
| `perfect_sequence` | Perfect Sequence (keystone) | six runes in a circle, the sixth blazing white |
| `vitality` | Vitality | a heart-shaped red apple on a living branch |
| `ironhide` | Ironhide | bear fur over riveted iron scales |
| `bloodthirst` | Bloodthirst | a horn cup overflowing with glowing red mead |
| `evasion` | Evasion | a fox mid-leap leaving an afterimage of mist |
| `shield_wall` | Shield Wall | three overlapping round shields in a line |
| `riposte` | Riposte | a shield deflecting a blow, sparks flying back toward the attacker |
| `last_stand` | Last Stand | a cracked shield still standing in the snow, one ember glowing |
| `living_fortress` | Living Fortress (keystone) | a warrior made of stone and oak roots, eyes glowing green |
| `scavenger` | Scavenger | a raven holding a shining coin in its beak |
| `golden_touch` | Golden Touch | an open palm with gold dust drifting off it |
| `trophy_hunter` | Trophy Hunter | a monster's horn mounted on a carved plaque |
| `plenty` | Plenty | an overflowing woven basket of ore, herbs and fish |
| `rarity` | Rarity | a rough gemstone splitting open to reveal a brighter one inside |
| `quick_study` | Quick Study | an open birch-bark book with runes floating up |
| `codex_scholar` | Codex Scholar | a bestiary with monster sketches, a quill and a candle |
| `golden_fleece` | Golden Fleece (keystone) | a golden ram's fleece radiating light |
| `know_your_prey` | Know Your Prey (bridge) | a hunter's map with a monster's track circled in gold and red |
| `piercing_crit` | Piercing Crit (bridge) | an arrow passing clean through an iron plate |
| `counterstroke` | Counterstroke (bridge) | a sword sliding along a shield's rim into a strike |

Today's 20 nodes reuse these where they match (Double Strike, Thunderer,
Last Stand, Golden Fleece, Bloodthirst, Relentless, Rarity, Plenty, Trophy
Hunter). Hearth-half icons wait for phase 4.

### Cleanup, only if an image comes back with a busy background

`D:\AIvidea\rembg` can cut the subject out, which the client could then
lay on the standard dark tile. The full-bleed format above makes this
normally unnecessary. Use it only for a failed generation.

## 2. Combat-feed callouts and effects

The client already draws floating damage from `ResponseCombatEventPacket`
(`stores/damage.ts`) and labels a Double Strike from `FlagDoubleStrike`.
Each new mechanic gets a **flag on the event** (never an inference from
numbers) and a callout:

| Mechanic | Callout | Colour | Effect (CSS/SVG, no images) |
|---|---|---|---|
| Crushing Blow | **CRUSH!** | ember red | shard burst, 6 triangles, 300 ms, short screen shake (2 px) |
| Ruin (8th hit) | **RUIN** | dark red, heavy | number scales 1.6× then settles, a radial shockwave ring |
| Thunderer | **THUNDER!** | white-blue | a full-height lightning bolt SVG for 180 ms, a flash at 15% opacity |
| Echo | **ECHO** | pale cyan | the number repeats as a fading ghost, 120 ms behind |
| Double Strike | **DOUBLE!** | cyan | two numbers fanning apart (exists, gains the callout) |
| Perfect Sequence | **PERFECT** | white with a gold rim | six small rune pips fill in over the hits, the sixth bursts |
| Riposte | **RIPOSTE** | verdigris green | a short arc swipe from the player side |
| Last Stand | **LAST STAND** | gold on black | the health bar flashes gold, a vignette pulses once |
| Golden Fleece | **GOLDEN FLEECE** | gold | a coin shower on the loot row |

Rules: callouts respect `prefers-reduced-motion` (text only, no shake), and
at most two callouts are alive at once. They go through the same cap as
`MAX_LIVE_DAMAGE_TEXTS`, so a fast weapon cannot flood the DOM.

## 3. Sounds

Generated with the ElevenLabs Sound Effects API (owner's key, in
`D:\AIvidea\ElevenLabsTextToSpeach&SoundEfektsAPI`). The output is
original and covered by the plan's licence. The free `1000+UR.FASTEDITOR
SFX` pack is **not** used: it has no licence terms, and the game sells
things. All clips are 0.3-1.2 s, mono, and normalised to -16 LUFS. They
play through `lib/ui/audio.ts`, under the existing SFX volume setting.

| Clip | Prompt (ElevenLabs) |
|---|---|
| `skill_crush` | heavy stone maul shattering a wooden shield, short, punchy, no reverb tail |
| `skill_ruin` | deep iron hammer impact with a low boom and a short metallic ring |
| `skill_thunder` | close lightning crack followed by a short rumble, 1 second |
| `skill_echo` | quick sword swish followed by its own faint echo |
| `skill_double` | two fast blade hits in quick succession |
| `skill_perfect` | bright crystalline chime with a sharp sword hit |
| `skill_riposte` | shield block clang then a quick counter slash |
| `skill_last_stand` | heartbeat thump with a rising war-horn note, 1.2 seconds |
| `skill_fleece` | shimmering coins pouring with a soft magical sparkle |
| `ui_rune_select` | soft wooden tap on a carved rune stone |
| `ui_rune_add` | small warm magical chime, like a rune lighting up |
| `ui_rune_confirm` | three ascending rune chimes and a low drum hit |
| `ui_rune_locked` | dull muted wooden knock |

The combat-feed sounds share one rule with the callouts: at most one skill
sound per 250 ms, so a fast weapon does not machine-gun them.
