# Loot chests, dungeons and the chest opening - visual and audio design

Written 2026-10-30, after the owner approved the loot chests and dungeons in
`CHESTS_DUNGEONS_DIAMONDS.md`. This document covers how those things LOOK
and SOUND, how the assets are made with the ElevenLabs API, and how the
existing cosmetic chest opening is rebuilt so it is smooth and crisp.

It is a proposal. The numbers in section 4 (chest contents) are a starting
point to be measured, not final balance. The skill tree's look stays as it
is: the owner likes it, Yggdrasil backdrop included.

Contents:
1. What is wrong with today's chest opening
2. One chest renderer for both kinds of chest
3. Loot chests: look, rarities and the opening sequence
4. Loot chests: contents, odds and amounts
5. Dungeon tickets
6. The dungeon lobby
7. The three dungeons, their monsters and bosses
8. The fight playback
9. Backgrounds
10. Sound
11. Making the assets with ElevenLabs
12. Order of work

---

## 1. What is wrong with today's chest opening

The owner's complaint: "not smooth, transparent where it should not be,
angular, a bad mask". The code (`ChestOpening.svelte`, `public/chest/`)
shows four separate causes.

| Symptom | Cause in the code |
|---|---|
| The chest turns see-through in places | On Safari and iOS the clip is an MP4, which has no alpha. `.clip.blend { mix-blend-mode: screen }` fakes transparency by making BLACK transparent, and the chest's own dark wood, shadows and iron bands are near-black, so they go transparent too. |
| An angular frame around the light | `.chest-hit` carries a mask built from two LINEAR gradients (7% at the sides, 10% at the top). It fades the light rays in a rectangle, so the glow ends in a box. |
| Jagged edges | The WebM alpha was keyed out of an H.264 clip (see the chest memory: "clips keyed from H.264"). Compression noise at the edge became a ragged alpha edge. |
| Soft, not crisp | The clips are 406 x 720 and scaled up on most screens. |
| A visible jump between shake and burst | The shake clip and the open clip are two `<video>` elements switched with `opacity: 0 / 1`, with no crossfade, and the open clip's first frame is not the shake clip's last. |

Patching these one by one (an HEVC-alpha file for Safari, a radial mask,
higher-resolution clips) fixes some and keeps the others, because the root
problem is that a pre-rendered video with a keyed alpha is the wrong tool
for an object that has to sit cleanly on any background.

## 2. One chest renderer for both kinds of chest

**Proposal: rebuild the chest as layered still art animated in code, and use
video only for light, never for the chest itself.**

A chest is drawn from separate transparent images at twice display
resolution (about 1024 px tall):

| Layer | What it is |
|---|---|
| `body` | The chest without its lid. |
| `lid` | The lid alone, drawn so it can rotate on its hinge. |
| `glow` | A soft light shape the size of the opening, white, so code can tint it per rarity. |
| `seams` | Thin light lines along the lid seam, white, for the shake phase. |
| `shadow` | A soft floor shadow. |

Code animates them with the Web Animations API (transforms and opacity only,
so the browser composites on the GPU):

- **Shake:** the body and lid rotate a few degrees around the base, with an
  ease that settles; each tap shakes harder; the seam light grows brighter in
  the rarity colour.
- **Burst:** the lid rotates open on its hinge (about 110 degrees, with an
  overshoot and a settle), the glow scales up from the opening, and the light
  rays and particles start.
- **Light rays and particles** are drawn on a `<canvas>` behind and in front
  of the chest with additive blending (`globalCompositeOperation =
  'lighter'`). Rays are RADIAL and fade with distance, so there is no box edge
  anywhere. Particle count drops on low-end devices and to zero with "reduce
  motion".

What this fixes, one for one: no alpha from video (nothing to go
see-through), no rectangular mask (rays are radial), no keyed edges (the
art is keyed once, from a clean still), crisp at any size (2x art), and no
jump (one continuous animation, not two clips).

**The cosmetic chest moves to the same renderer.** One component,
`ChestStage`, takes an art set (`cosmetic` or `loot`) and a rarity. The
existing taps, the server timing (the burst waits for the server's answer)
and the reveal card stay; only the drawing changes. The old clips are then
deleted.

**Measured acceptance:** `check:perf` stays inside its budget with the
canvas running, the chest has no transparent pixel inside its outline on
Chrome, Firefox and iOS Safari (a screenshot check against a magenta
background), and the shake-to-burst hand-over shows no frame where the chest
jumps.

## 3. Loot chests: look, rarities and the opening sequence

### 3.1 Look

A loot chest is a **strongbox**, not the cosmetic chest's ornate casket, so
the two never get confused: squarer, heavier, iron-banded, with a lock plate.
Four rarities, the same four names as cosmetic chests:

| Rarity | Material | Colour of its light | Mark on the lock plate |
|---|---|---|---|
| Common | Oak planks, black iron bands | Warm white | A plain iron ring |
| Rare | Dark oak, silver bands | Pale blue | A carved six-petal rosette |
| Epic | Stained ash, gilded bands, an amethyst set in the lock | Violet | A thunder mark (six-petalled wheel in a circle) |
| Legendary | Black bog oak, gold bands, small rubies along the lid | Gold to red | A sun disc with rays |

All marks are Slavic folk ornaments that fit the game's setting. Sun signs
with bent arms are avoided on purpose: some of them read as extremist symbols
outside their folk context.

### 3.2 The opening sequence

1. **Arrive.** The chest drops onto the stage with a dust puff and a thud.
   Its rarity shows in the seam light even before it opens, so a Legendary is
   exciting from the first second.
2. **Tap, tap, tap.** Three taps, as today. Each shakes it harder, the seam
   light brightens, the third holds a short beat (the server answer arrives
   during it).
3. **Burst.** The lid flies open, light pours out in the rarity colour, a
   short swell of sound.
4. **Rewards fly out.** Each reward leaves the chest as a card in an arc and
   lands in a row. A card shows the item art, its count (gold and materials
   count up from zero) and, for equipment, its rarity frame. The rarest reward
   comes LAST, with a half-second pause, a flash in its own colour and its own
   sound, so the best thing in the chest gets the moment.
5. **Collect.** "Collect all" sends the cards into the bag icon at the top of
   the screen, and the counters there tick up.

**Opening several (x5, x10):** the chests stand in a row and burst one after
another, quickly; the rewards then pour into one summary grid, the rarest
first, with "Collect all". The batch flow already exists for cosmetic chests.

**Skip:** a tap during steps 3-4 jumps to the summary. Opening fifty chests
must not take fifty animations.

## 4. Loot chests: contents, odds and amounts

**Every amount scales with the player**, so a chest is worth the same share
of progress at level 20 and at level 100:
- gold in **minutes of the player's own income** (the same measure the gold
  sinks use);
- materials of the **player's current region tier**, in minutes of that
  region's gathering;
- the "highest tier" slot is the highest region tier the player has
  unlocked;
- equipment from the player's **highest unlocked region**, with a rarity
  floor per chest rarity.

### 4.1 Rolls per chest

| Chest | Guaranteed | Extra rolls |
|---|---|---|
| Common | 1 material roll | 1 |
| Rare | 1 material roll | 2 |
| Epic | 1 material roll | 3 |
| Legendary | 1 material roll + 1 equipment piece (floor Epic-equivalent) | 3 |

### 4.2 What each extra roll gives (chance per roll)

| Reward | Common | Rare | Epic | Legendary |
|---|---|---|---|---|
| Materials, current tier | 55% | 40% | 30% | 20% |
| Materials, highest tier | 10% | 20% | 25% | 25% |
| Gold | 30% | 28% | 25% | 22% |
| Equipment, highest region | 5% | 10% | 15% | 25% |
| Diamonds | 0% | 1% | 3% | 5% |
| Dungeon ticket (once dungeons exist) | 0% | 1% | 2% | 3% |

### 4.3 Amounts

| Reward | Common | Rare | Epic | Legendary |
|---|---|---|---|---|
| Materials | 15 min | 30 min | 60 min | 120 min |
| Gold | 10 min | 20 min | 40 min | 90 min |
| Equipment rarity floor | Common | Rare | Epic | Legendary |
| Diamonds | - | 1-2 | 2-4 | 4-8 |
| Tickets | - | 1 | 1 | 1-2 |

**Diamonds from chests share one weekly ceiling** (proposed 30 a week), the
same rule as every other free tap: a roll that would pass it pays gold
instead. The expected diamonds of a Legendary chest are about 3 x 5% x 6 =
0.9, so the ceiling only matters for someone opening dozens.

**Measure before shipping:** gold and materials per week from chests at the
expected drop rates (section 1.3 of the proposal), against today's weekly
income and the sinks. If chests add more than about 10% to a week's gold,
the minutes come down.

## 5. Dungeon tickets

Three kinds, one per dungeon. Each is an icon (inventory, lobby, chest
reward card) and a larger card for the "ticket used" moment.

| Ticket | Look | Colour |
|---|---|---|
| **Scholar's Seal** (XP dungeon) | A rolled parchment with a blue wax seal stamped with an open eye | Blue |
| **Dragon Coin** (Gold dungeon) | A thick gold token with a three-headed dragon in relief, worn at the edges | Gold |
| **Smith's Key** (Equipment dungeon) | An iron key on a leather tag burned with a hammer and anvil | Iron grey and red |

Using a ticket plays a short animation on the lobby card: the seal cracks,
the coin flips into a slot, the key turns in a lock.

## 6. The dungeon lobby

One screen, reached from the main menu.

- **Three gate cards**, side by side on a wide screen and stacked on a
  phone: each with its dungeon's painting, name, what it pays, and its
  ticket count (`2 / 4`, with the time to the next free ticket).
- **Difficulty:** a stepper (Step 1 to Step N, tied to the region ladder),
  showing the recommended power and the reward at that step.
- **Champion:** the character who goes in (the fielded fighter by default),
  with their power next to the recommendation, green when it is enough.
- **Enter:** spends the ticket and starts the run. The server decides the
  outcome at once; the playback is the telling of it.
- **Last runs:** the last five results, so a player sees whether a step is
  too hard before spending another ticket.

## 7. The three dungeons, their monsters and bosses

All three come from Slavic folklore, like the rest of the game, and none
reuses a boss already in it (Chernobog, Malakor, the Black Serpent, the
Cailleach).

### 7.1 Koschei's Tower (XP dungeon)
- **Setting:** a tall tower of black stone and bone, full of shelves of old
  books and hanging cages; cold green light.
- **Waves:** bone scribes (skeletons in robes with quills), carrion crows, and
  will-o'-the-wisps (bludičky), small floating lights that drain.
- **Boss: Koschei the Deathless.** A tall, gaunt sorcerer-king in a rusted
  crown and a black cloak, with a needle glinting at his chest. In his intro
  card a duck, a hare and an egg are shown in a small vignette behind him,
  the old tale of where his death is hidden.

### 7.2 Zmey's Hoard (Gold dungeon)
- **Setting:** a mountain cave of gold heaps, broken carts and lantern light;
  warm orange.
- **Waves:** permoníci (the mine spirits of Czech folklore: small bearded
  miners with lamps, here gone greedy), gold-scaled cave snakes, and bats.
- **Boss: Zmey Gorynych.** A three-headed green dragon coiled on the hoard.
  Each head has its own expression (angry, sly, sleepy), so the intro card is
  memorable rather than generic.

### 7.3 Baba Yaga's Hut (Equipment dungeon)
- **Setting:** a dark birch forest, a fence of bones topped with glowing
  skulls, and the hut on chicken legs turning slowly; misty violet.
- **Waves:** iron-toothed wolves, the cat Bayun (a huge storyteller cat whose
  purr puts heroes to sleep), and walking skull-lanterns from the fence.
- **Boss: Baba Yaga.** An old witch flying in her mortar and steering with the
  pestle, sweeping her tracks with a broom. Her reward is the equipment and a
  loot chest.

## 8. The fight playback

The run is simulated on the server (owner decision D1) and comes back as
the same combat event feed the combat screen already reads
(`ResponseCombatEventPacket`, or a dungeon variant of it). The client tells
the story:

- **The stage:** the dungeon's background, the champion on the left, the
  current enemy on the right (the combat scene from the 2026-10 UI overhaul,
  reused).
- **Progress:** a bar across the top with five wave pips and a skull for the
  boss; the current pip glows.
- **Each blow** plays from the feed: the champion's lunge, damage numbers (the
  existing damage-number layer), crits bigger and gold, misses as "miss", the
  enemy's reply. Health bars move with honest maximums, as on the combat
  screen.
- **Between waves:** a one-second banner ("Wave 3"), and the next enemy
  slides in.
- **Boss intro:** the screen dims, the boss's portrait and name slide in with
  a title line ("Koschei the Deathless - his death is not here"), a roar, then
  the fight.
- **Boss abilities** (proposal section 2.6a): each has an icon on the boss's
  health bar that lights when it is about to fire, a name banner when it does,
  and its own effect and sound. Koschei's Deathless: he falls, a green soul
  flame spirals back into him, he rises with a dry laugh. Bone Ward: a ring of
  bones closes around him and cracks as it absorbs. Zmey's Three Heads: three
  segments on his bar; a head that falls drops with a crash and its stump
  smokes, and Regrowth shows as a timer over the stump. Fire Breath: a cone of
  flame and an orange burn tick on the champion's bar. Baba Yaga's Mortar
  Flight: she blurs aside and "dodged" floats up. Hex: a violet sigil hangs
  over the champion's weapon and its next hits show smaller numbers. Bone
  Fence: a skull-lantern rises and takes her place on the stage. Broom Sweep:
  the larder icon is swept off the screen and greys out for its seconds.
- **Speed:** 1x, 2x and Skip. Skip goes straight to the result; the outcome
  was decided before the first frame.
- **Result:** "Victory" or "Defeated at wave 4". Rewards fly out as in the
  chest sequence (section 3.2), and the equipment dungeon opens its loot chest
  on the spot.

## 9. Backgrounds

| Screen | Background |
|---|---|
| Dungeon lobby | A stone hall with three arched gates, each lit in its dungeon's colour (green, orange, violet) |
| Koschei's Tower | Inside the tower: shelves, cages, green candlelight |
| Zmey's Hoard | The cave of gold, lanterns, the dragon's tail visible at the back |
| Baba Yaga's Hut | The birch forest at night, the bone fence, the hut in the distance |
| Loot chest stage | A stone vault floor with a single shaft of light from above, so any chest colour reads against it |

Each comes in a PC (wide) and a mobile (tall) crop, as the chest and
loading backgrounds already do. The art matches the existing painted style:
the Yggdrasil backdrop and the chest backgrounds are given to the image model
as style references (section 11).

## 10. Sound

Every sound is a short clip in the client's audio registry, muted by the
existing sound setting. Proposed list:

| Event | Sound |
|---|---|
| Chest lands | A heavy wooden thud with a little dust |
| Chest shake (x3) | A rattle of iron and wood, each a step higher |
| Lid bursts, Common / Rare | A wooden creak and a bright shimmer |
| Lid bursts, Epic | The same with a low choir swell |
| Lid bursts, Legendary | A deep boom, a choir and a high bell |
| Reward card flies | A soft whoosh |
| Gold counts up | A short cascade of coins |
| Materials | Wood and stone clinks |
| Equipment card | A metal ring, brighter for higher rarities |
| Diamonds | A crystal chime |
| Rarest reward reveal | A short rising sting |
| Collect all | Coins and items pouring into a bag |
| Ticket used | The seal cracking / the coin dropping / the key turning |
| Dungeon gate opens | A low stone rumble |
| Wave starts | A short war horn |
| Boss intros | Koschei: a dry whispering laugh; Zmey: three overlapping roars; Baba Yaga: a cackle and the knock of the pestle |
| Victory | A short fanfare on horns and drums |
| Defeat | A low drum and a fading horn |
| Achievement banner (proposal section 5.5) | A warm two-note chime; Legendary: a longer bell |

Optional ambient loops (Eleven Music): one per dungeon, 60-90 seconds,
quiet, looping cleanly.

## 11. Making the assets with ElevenLabs

The account is on the Creator plan (the key's location is in the session
memory, never in the repo). ElevenLabs covers all three kinds of asset:
**images** and **video** through its "flows" API, and **sound effects** and
**music**.

**Measured 2026-10-30:** sound effects work on Creator
(`sfx_achievement_chime` came back in 3 s). The flows API (images and video)
answers `402 paid_plan_required: "This endpoint requires a Pro plan or
above"`. So images and video need either the Pro plan or another tool;
the script supports them already and works the day the plan allows it.
`gpt-image-1` and `gpt-image-1.5` accept `background: "transparent"`, which
makes the chroma-key step below unnecessary for items and chests.

### 11.1 A script, not hand work

A small script, `tools/assets/eleven.mjs`, driven by a manifest
`tools/assets/manifest.json`. Each entry names an asset id, its kind, the
prompt, the model, the size and where the result goes. The script:
1. reads the key from the path in an environment variable (never from the
   repo), and refuses to run without it;
2. submits the job, polls it (images at most every 2 s, video every 10 s, as
   the docs ask), and downloads the result at once, because the signed URL
   expires after about an hour;
3. post-processes (below) and writes the files where the client serves them;
4. records each asset's prompt, model, seed or generation id and date in
   `tools/assets/generated.json`, so any asset can be regenerated or
   explained later.

Every asset is reviewed by eye before it is committed: the script writes to
a staging folder, and a person moves what is good.

### 11.2 Images

- **Model:** a strong image model from the flows list (for example
  `gpt-image-2` or `gemini-3-pro-image`). One is picked after a short test of
  both on the same three prompts.
- **Style references:** the existing painted art (Yggdrasil, the chest
  backgrounds, a monster portrait) passed as reference images, so new art
  looks like the game, not like a stock illustration.
- **Transparency:** the flows API takes no transparency field (unknown fields
  are rejected). Objects are generated on a flat, pure chroma-green
  background, then keyed with ImageMagick (colour key with a small fuzz,
  green spill removed from the edges, a one-pixel feather), and checked on
  magenta and black for halos.
- **Layered chests:** the closed chest is generated first; the open chest, the
  lid alone and the body alone are generated WITH the closed chest as a
  reference, so the layers match. Where they still do not line up, the lid is
  cut from the open-chest image by hand.
- **Output:** WebP (and AVIF where it is smaller) at 2x display size, through
  the existing sprite pipeline (`npm run generate:sprites`), so the art
  coverage check sees the new files.

### 11.3 Video

Video is used only for light effects (a burst of light, a sparkle trail),
which are naturally black-backed and are drawn with additive blending, so
there is no alpha to fake. A model like `veo-3.1-generate-001` makes the
effect on black; the script trims it and encodes WebM (VP9) and MP4 at the
display size. The chests, monsters and items are never video.

### 11.4 Sound

- **Sound effects API** for every clip in section 10. Each prompt is short
  and concrete ("heavy oak chest dropped on stone floor, dust, close"), and
  each is generated three or four times and the best is kept.
- **Post-processing:** trim the silence, normalise loudness to one level so no
  clip is louder than the others (about -16 LUFS), and encode OGG (Opus) with
  an MP3 fallback for Safari.
- **Music** (optional) with Eleven Music for the dungeon loops; checked for a
  clean loop point.

### 11.5 Cost

Credits per image, video second and sound are not known yet. Before the full
batch, the script runs one asset of each kind and records what it cost; the
manifest then gets a total, and the batch runs only if it fits the plan.

## 12. Order of work

1. **The asset script** (11.1) and one test asset of each kind, with costs.
2. **`ChestStage`** (section 2) with the COSMETIC chest first: it fixes what
   players already see, and proves the renderer before the loot chest needs
   it. New layered art for the cosmetic casket.
3. **Loot chests** (sections 3-4): the strongbox art in four rarities, the
   server side from the proposal, the opening and the reward cards, the
   sounds.
4. **Tickets, the lobby, the three dungeons, the playback** (sections 5-9),
   with the dungeon phase, after Hearth and the affix rework.
