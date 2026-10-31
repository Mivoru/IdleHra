# Asset prompts for manual generation (ElevenLabs image and video)

Written 2026-10-30. The API's image and video endpoints need the Pro plan
(the Creator plan answers `402 paid_plan_required`), so these assets are
made by hand in the ElevenLabs web app. Sound effects are made by the script
(`tools/assets/eleven.mjs`), so they are not in this list.

Design behind every asset: `LOOT_DUNGEON_VISUALS.md`.

## How to use this list

1. **One asset at a time.** Copy its prompt exactly. Generate **4 variants**,
   keep the best one.
2. **Model and settings** are given per asset:
   - *GPT Image 1.5* for objects that need a **transparent background**
     (chests, tickets, icons, monster sprites). Set Background =
     **Transparent**, Quality = **High**.
   - *Gemini 3 Pro Image* for paintings (backgrounds, portraits). Its style
     follows reference images well.
   - *Veo 3.1* for the few video effects.
3. **Reference images:** when an asset lists references, upload them as
   reference images. They keep the game's painted look. All paths are in
   the repository.
4. **Save** the chosen file with the given **file name** into
   `tools/assets/incoming/` (create it; it is not committed). Do not crop or
   resize - that is done in code (step 6).
5. **Matching sets** (a chest's closed/open/lid, a boss's portrait and its
   sprite): generate the first one, then pass THAT image as a reference for
   the rest of its set, so they match.
6. Tell Claude which files are in `incoming/`. Claude checks the edges on
   dark and light backgrounds, trims, exports WebP at the listed size, and
   wires them in.

### The style line

Every prompt below already ends with the game's style line. It is repeated
here so you can recognise it:

> *Painted fantasy game art, rich hand-painted textures, warm rim light,
> Slavic folklore setting, crisp clean edges, no text, no watermark, no
> border.*

### Avoid in every result

Text, letters or numbers on the object; watermarks; frames or borders; cut
off edges; a second object; a shadow baked onto the transparent background
(the game draws its own floor shadow); hands with wrong fingers on
characters.

---

## 1. The chest renderer (`ChestStage`)

The opening is animated in code from separate layers (visuals doc section 2).
Each chest needs **three images**: closed, open, and the lid alone. All
three: GPT Image 1.5, Transparent, High, **1:1, the largest size offered**.
Same camera for all three: three-quarter front view, slightly from above,
centred, the whole chest in frame with a margin.

### 1.1 Cosmetic casket (rebuild of today's chest)

References: the chest still that was `client_web/public/chest/idle.webp` (deleted 2026-10-31 with the opening clips; in git history before that) (today's chest, for the
design to keep).

| File | Prompt |
|---|---|
| `cosmetic_closed.png` | An ornate closed treasure casket, the same design as the reference: carved dark wood, gold corners and filigree, a domed lid, a small gold lock. Three-quarter front view, slightly from above, centred, whole object in frame. Isolated object on a transparent background. Painted fantasy game art, rich hand-painted textures, warm rim light, Slavic folklore setting, crisp clean edges, no text, no watermark, no border. |
| `cosmetic_open.png` | The same casket as the reference, with its lid open wide on its hinge (about 110 degrees), the inside empty and dark velvet. Same camera, same size. Isolated object on a transparent background. (style line) |
| `cosmetic_lid.png` | Only the domed lid of the casket in the reference, on its own, closed position, same camera and scale, no body under it. Isolated object on a transparent background. (style line) |

### 1.2 Loot strongboxes, four rarities

Generate Common first; then use `loot_common_closed.png` as the reference for
every other strongbox image, so the four rarities share one shape.

| File | Prompt |
|---|---|
| `loot_common_closed.png` | A heavy square treasure strongbox, closed: oak planks, black iron bands, rivets, a plain iron ring on the lock plate. Sturdier and squarer than an ornate casket. Three-quarter front view, slightly from above, centred, whole object in frame. Isolated object on a transparent background. (style line) |
| `loot_common_open.png` | The strongbox from the reference with its lid thrown open on its hinge (about 110 degrees), the inside empty and dark. Same camera, same size. Isolated on a transparent background. (style line) |
| `loot_common_lid.png` | Only the lid of the strongbox in the reference, closed position, same camera and scale, no box under it. Isolated on a transparent background. (style line) |
| `loot_rare_closed.png` | The same strongbox shape as the reference, rare version: dark oak, polished silver bands, a carved six-petal rosette on the lock plate, faint pale-blue glow in the seams. Same camera. Isolated on a transparent background. (style line) |
| `loot_rare_open.png` / `loot_rare_lid.png` | As the Common open and lid prompts, with "rare version: dark oak, polished silver bands, carved six-petal rosette" added. |
| `loot_epic_closed.png` | The same strongbox shape, epic version: stained ash wood, gilded bands, an amethyst set in the lock, a thunder-mark (a six-spoked wheel inside a circle) engraved on the plate, faint violet glow in the seams. Same camera. Isolated on a transparent background. (style line) |
| `loot_epic_open.png` / `loot_epic_lid.png` | As above, epic version. |
| `loot_legendary_closed.png` | The same strongbox shape, legendary version: black bog oak, heavy gold bands, small rubies along the lid, a radiant sun disc with straight rays on the lock plate, faint gold-red glow in the seams. Same camera. Isolated on a transparent background. (style line) |
| `loot_legendary_open.png` / `loot_legendary_lid.png` | As above, legendary version. |

**Do not use** sun wheels with bent or hooked arms on any chest: some read as
extremist symbols out of context.

### 1.3 Chest stage background

Gemini 3 Pro Image. References: `client_web/public/chest/bg-pc.jpg`.

| File | Ratio | Prompt |
|---|---|---|
| `bg_vault_pc.png` | 16:9 | An empty stone vault room, worn flagstone floor, a single soft shaft of light from a high window falling on the centre of the floor, dark walls fading to shadow at the edges, calm and empty, room for an object in the middle. (style line) |
| `bg_vault_mobile.png` | 9:16 | The same vault as the reference, tall portrait framing, the shaft of light in the lower middle. (style line) |

---

## 2. Dungeon tickets

GPT Image 1.5, Transparent, High, 1:1. Icons are shown small, so the shape
must read at a glance.

| File | Prompt |
|---|---|
| `ticket_scholar.png` | A rolled parchment scroll tied with a cord and sealed with a large blue wax seal stamped with an open eye. Simple bold silhouette, readable at small size. Centred, isolated on a transparent background. (style line) |
| `ticket_dragon.png` | A thick worn gold token coin with a three-headed dragon in relief on its face, chipped edges. Simple bold silhouette, readable at small size. Centred, isolated on a transparent background. (style line) |
| `ticket_smith.png` | An old iron key on a short leather tag, the tag burned with a hammer and anvil mark. Simple bold silhouette, readable at small size. Centred, isolated on a transparent background. (style line) |

---

## 3. Dungeon backgrounds

Gemini 3 Pro Image. References for every background: `client_web/public/chest/bg-pc.jpg`
and the Yggdrasil backdrop `client/Assets/Images/SpritesWeb/Backgrounds/yggdrasil.webp`. Make the PC version (16:9)
first, then the mobile version (9:16) with the PC one as an extra reference.

| File | Prompt |
|---|---|
| `bg_lobby_pc.png` | A dark stone hall with three tall arched gates side by side: the left gate glowing cold green, the middle warm orange, the right misty violet. Torches on the walls, worn floor, no people. Wide, calm, the gates are the focus. (style line) |
| `bg_koschei_pc.png` | Inside a tall tower of black stone and bone: shelves of old books, hanging iron cages, cold green candlelight, dust in the air. The floor in the lower third is open for a fight. No people. (style line) |
| `bg_zmey_pc.png` | A mountain cave full of gold heaps, broken mine carts and lanterns, warm orange light, the tail of a huge dragon disappearing into the darkness at the back. The floor in the lower third is open for a fight. No people. (style line) |
| `bg_babayaga_pc.png` | A dark birch forest at night, a fence of bones topped with glowing skulls, a hut on huge chicken legs in the misty distance, violet moonlight. The ground in the lower third is open for a fight. No people. (style line) |
| `bg_*_mobile.png` | For each of the four above: "The same scene as the reference, tall portrait framing, the open floor in the lower third." |

---

## 4. Bosses

Two images per boss: a **portrait** (the intro card and the lobby) and a
**sprite** (the fighter on the stage, transparent).

References for all bosses: `client/Assets/Images/Sprites/Locations/05/Monsters/Malakor.png`
(the game's own boss style).

| File | Model | Ratio | Prompt |
|---|---|---|---|
| `boss_koschei_portrait.png` | Gemini 3 Pro | 3:4 | Koschei the Deathless: a tall gaunt skeletal sorcerer-king in a rusted iron crown and a tattered black cloak, a long needle glinting at his chest, cold green eyes. Behind him, small in the background, a duck, a hare and an egg in an old carved panel. Dark tower interior, green candlelight. Half-length portrait. (style line) |
| `boss_koschei_sprite.png` | GPT Image 1.5, Transparent | 1:1 | The character from the reference, full body, standing ready to cast, facing left, three-quarter view. Isolated on a transparent background. (style line) |
| `boss_zmey_portrait.png` | Gemini 3 Pro | 3:4 | Zmey Gorynych: a huge three-headed green dragon coiled on a heap of gold, each head with its own face - one furious, one sly, one sleepy - glowing orange cave light. Portrait of the three heads and chest. (style line) |
| `boss_zmey_sprite.png` | GPT Image 1.5, Transparent | 1:1 | The dragon from the reference, full body, three heads raised, facing left, three-quarter view. Isolated on a transparent background. (style line) |
| `boss_babayaga_portrait.png` | Gemini 3 Pro | 3:4 | Baba Yaga: a wild old witch with iron teeth and long grey hair, flying in a wooden mortar and steering with a pestle, a broom tied behind her, violet moonlight, birch forest behind. Half-length portrait. (style line) |
| `boss_babayaga_sprite.png` | GPT Image 1.5, Transparent | 1:1 | The witch from the reference, full body in her mortar, facing left, three-quarter view. Isolated on a transparent background. (style line) |

---

## 5. Wave monsters (three per dungeon)

GPT Image 1.5, Transparent, High, 1:1. References: one existing monster
sprite, for example `client/Assets/Images/Sprites/Locations/01/Monsters/Alpha Wolf.png`.
All face **left**, full body, three-quarter view.

| File | Prompt |
|---|---|
| `mob_bone_scribe.png` | A skeleton scribe in a torn grey robe, holding a quill like a dagger and an old book, faint green glow in its eye sockets. Full body, facing left. Isolated on a transparent background. (style line) |
| `mob_carrion_crow.png` | A large ragged carrion crow with green-glowing eyes, wings spread. Full body, facing left. Isolated on a transparent background. (style line) |
| `mob_wisp.png` | A will-o'-the-wisp (bludička): a small floating pale-green flame with a faint face, trailing light. Facing left. Isolated on a transparent background. (style line) |
| `mob_permonik.png` | A permoník, a small bearded mine spirit from Czech folklore, with a miner's lamp and a pickaxe, a greedy grin, gold dust on his clothes. Full body, facing left. Isolated on a transparent background. (style line) |
| `mob_gold_snake.png` | A cave snake with gold scales, coiled to strike. Full body, facing left. Isolated on a transparent background. (style line) |
| `mob_cave_bat.png` | A large cave bat with torn wings and orange eyes. Facing left. Isolated on a transparent background. (style line) |
| `mob_iron_wolf.png` | A gaunt black wolf with iron teeth, snarling. Full body, facing left. Isolated on a transparent background. (style line) |
| `mob_bayun.png` | The cat Bayun: a huge dark storyteller cat with half-closed glowing violet eyes, sitting proud. Full body, facing left. Isolated on a transparent background. (style line) |
| `mob_skull_lantern.png` | A walking skull-lantern: a glowing skull on a pole from a bone fence, on spindly bone legs. Full body, facing left. Isolated on a transparent background. (style line) |

---

## 6. Boss ability icons (twelve)

GPT Image 1.5, Transparent, High, 1:1. Each is a round emblem that reads at
48 px: one bold shape, the boss's colour (Koschei green, Zmey orange, Baba
Yaga violet), no text.

| File | Prompt (each followed by: "Round emblem icon, one bold shape, readable at small size, isolated on a transparent background." + style line) |
|---|---|
| `ab_deathless.png` | A green soul flame rising from a crown |
| `ab_bone_ward.png` | A ring of bones forming a shield, green glow |
| `ab_soul_drain.png` | A green spiral of light pulled into a skeletal hand |
| `ab_needle.png` | A long needle inside a cracked egg, green glow |
| `ab_three_heads.png` | Three dragon heads in silhouette, orange |
| `ab_fire_breath.png` | A cone of dragon fire, orange |
| `ab_regrowth.png` | A dragon head regrowing from a smoking stump, orange |
| `ab_gold_hide.png` | Gold dragon scales overlapping like armour |
| `ab_mortar_flight.png` | A wooden mortar and pestle in flight with motion streaks, violet |
| `ab_hex.png` | A violet hex sigil over a sword blade |
| `ab_bone_fence.png` | A bone fence post topped with a glowing skull, violet |
| `ab_broom_sweep.png` | A twig broom sweeping in an arc, violet |

---

## 7. Video effects (few, optional)

Veo 3.1 (or the fast variant). **Black background, light only**: the game
draws these with additive blending, so black becomes see-through on its own.
Each 2-3 s, 1:1 or 16:9, no camera motion, no objects.

| File | Prompt |
|---|---|
| `fx_burst_white.mp4` | A burst of warm white light and sparkles exploding outward from a point in the lower centre, on a pure black background, light only, no objects, static camera, 2 seconds. |
| `fx_burst_blue.mp4` | The same burst in pale blue. |
| `fx_burst_violet.mp4` | The same burst in violet with a few lightning-like threads. |
| `fx_burst_gold.mp4` | The same burst in gold fading to red, larger, with slow embers. |
| `fx_soul_flame.mp4` | A spiralling green soul flame rising upward, on a pure black background, light only, static camera, 2 seconds. |
| `fx_fire_breath.mp4` | A cone of dragon fire sweeping from right to left, on a pure black background, light only, static camera, 2 seconds. |

---

## 7a. Videos by hand in the Gemini app (2026-10-30)

The chest effects (flash, rays, sparks) are now drawn in code by `ChestStage`,
so only **three** videos are still worth making. Make them **by hand** in the
Gemini app (the owner's Pro plan includes Veo); driving the consumer app with
automation is against its terms, and the API would charge per second.

**Settings for all three:** 16:9, the shortest length offered (the game cuts
2-3 s out of it), no sound needed. Generate two or three tries each and keep
the cleanest. Save as the given file name into `tools/assets/incoming/`.

**What makes a clip usable:** pure black background with nothing in it, light
only, the camera does not move, no objects, no text, no watermark in the
frame. The game draws these with additive blending (`screen`), so black turns
see-through by itself; anything grey or textured in the background shows up
as a box.

| File | Prompt |
|---|---|
| `fx_legendary_burst.mp4` | A single burst of warm golden light exploding outward from one point in the lower centre of the frame, with soft straight rays and slow glowing embers drifting up and fading out. Pure black background, nothing else in the frame, light only, completely static camera, no objects, no text. Duration about 3 seconds. |
| `fx_dungeon_gate.mp4` | A tall arch-shaped opening of cold blue-white light that grows from a thin vertical line in the centre to a full glowing doorway, with faint mist curling at its edges, then holds steady. Pure black background, nothing else in the frame, light only, completely static camera, no stone, no objects, no text. Duration about 3 seconds. |
| `fx_boss_aura.mp4` | A slow pulsing aura of dark red and violet light rising from the bottom edge of the frame like heat haze, with a few sparks drifting upward, looping smoothly. Pure black background, nothing else in the frame, light only, completely static camera, no figure, no objects, no text. Duration about 4 seconds, the end matching the start. |

| `fx_vault_dust.mp4` (optional) | Slow drifting dust motes glowing softly in a single shaft of pale warm light falling from the top centre, gentle and calm, looping smoothly. Pure black background, nothing else in the frame, light only, completely static camera, no objects, no text. Duration about 6 seconds, the end matching the start. |

The first three are made and live (`client_web/public/fx`). The fourth would
loop over the vault background while a chest opens.

**If a try fails:** a grey or blue-tinted background, a camera push-in, or a
visible object (a chest, a door frame) means the clip is not usable; ask again
with "the background must stay perfectly black for the whole clip" added.

## 8. Counts

| Group | Images | Videos |
|---|---|---|
| Chests (cosmetic 3 + loot 12) | 15 | - |
| Chest stage backgrounds | 2 | - |
| Tickets | 3 | - |
| Dungeon backgrounds (4 x PC + mobile) | 8 | - |
| Bosses (portrait + sprite) | 6 | - |
| Wave monsters | 9 | - |
| Ability icons | 12 | - |
| Video effects | - | 6 |
| **Total** | **55** | **6** |

**Order:** the cosmetic casket (section 1.1) and the vault background (1.3)
first - that fixes the opening players already see. Then the loot chests,
the tickets, and the dungeon art when the dungeon phase starts.

## 9. Hearth skill icons, herbs and potions (2026-10-30)

Made through the Gemini API (`tools/assets/gemini.mjs`, entries in
`tools/assets/gemini.json`), so nothing here has to be made by hand. Listed so
the prompts can be read and changed in one place.

### 9.1 Hearth skill icons (32 nodes + 4 columns)

Style: the War icons (`client/Assets/Images/SpritesWeb/SkillIcons/bloodthirst.jpg`
and `col_fortune.jpg`) are passed as references - a single painted object on
the same dark navy background with a soft vignette, square, no text, no frame.
Every prompt is that style line plus the object below. Keystones add
"keystone, radiant".

| File | Object |
|---|---|
| `hearth_sharp_axe` | A freshly honed woodcutter axe with a gleaming edge, a wood chip flying. |
| `hearth_hard_pick` | A sturdy iron pickaxe striking sparks off a rock. |
| `hearth_seam_sense` | A glowing seam of ore running through split timber and stone. |
| `hearth_steady_swing` | An axe and a pickaxe crossed, motion arcs behind them. |
| `hearth_clearcut` | A neat stack of logs beside a pile of ore chunks. |
| `hearth_prospector` | A miner's lamp shining on a glittering gemstone in rock. |
| `hearth_double_haul` | A wooden cart overflowing with logs and ore, two of everything. |
| `hearth_deep_vein` | A deep glowing vein of rare crystal ore opening in a cave wall, keystone, radiant. |
| `hearth_patient_line` | A fishing rod with a still float on calm water at dawn. |
| `hearth_green_thumb` | A hand-sized bundle of fresh herbs with a green glow. |
| `hearth_good_waters` | A clear stream full of fish and riverside herbs. |
| `hearth_still_water` | A mirror-calm pond with a single ripple. |
| `hearth_keen_angler` | A sharp fish hook glinting, a rare golden fish nearby. |
| `hearth_netmaker` | A finely knotted fishing net with a shining scale caught in it. |
| `hearth_full_net` | A fishing net bursting with fish and herbs. |
| `hearth_moonlit_catch` | A fish leaping under a full moon, silver light, keystone, radiant. |
| `hearth_quick_hands` | A craftsman's hands blurred with speed over a workbench. |
| `hearth_thrifty` | A coin purse and spare materials saved on a workbench. |
| `hearth_ledger` | An open ledger book with a quill and a few coins. |
| `hearth_batch_work` | Several identical tools laid out in a row on a bench. |
| `hearth_fine_work` | A delicate gold filigree being worked with tiny tools. |
| `hearth_salvager` | A broken sword being melted into gold coins in a small crucible. |
| `hearth_patron` | A stone monument under construction with a banner. |
| `hearth_masterwork` | A perfect glowing weapon on an anvil with three runes floating above it, keystone, radiant. |
| `hearth_studious` | An open book with a candle and floating glowing letters. |
| `hearth_homebody` | A cosy cottage hearth with a crackling fire. |
| `hearth_long_absence` | An hourglass beside a travelling pack and a lantern. |
| `hearth_matchmaker` | Two intertwined wedding rings with a ribbon. |
| `hearth_strong_blood` | A drop of red blood glowing with an upward arrow of light. |
| `hearth_nursery` | A carved wooden cradle with a soft blanket. |
| `hearth_mentor` | An old hand guiding a young hand holding a small hammer. |
| `hearth_bloodline_memory` | A glowing family tree with roots and branches of light, keystone, radiant. |
| `hearth_col_wood_stone` | A tree stump and a boulder side by side, column emblem. |
| `hearth_col_water_herb` | A fish and a herb sprig circling each other over water, column emblem. |
| `hearth_col_workshop` | An anvil with a hammer, column emblem. |
| `hearth_col_kin` | A hearth with a family of three silhouettes, column emblem. |

### 9.2 Herbs (one normal and one rare per region; design in HERBS_POTIONS_ASCENSION.md)

Painted on a flat key colour (magenta, blue for the pink marsh mallow) and keyed
out by `keyout.py`. Each prompt ends with "Game item icon, one bold readable
shape, centred with a margin, three-quarter view."

| File | Prompt |
|---|---|
| `herb_yarrow` | Yarrow: a sprig of white flat-topped yarrow flowers with feathery leaves. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_golden_chamomile` | Golden Chamomile, rare: chamomile flowers with glowing golden petals. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_marsh_mallow` | Marsh Mallow: pale pink marsh mallow flowers on a stem with soft leaves. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_wisp_moss` | Will-o-wisp Moss, rare: a clump of moss with tiny floating pale-green lights. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_thyme` | Thyme: a bundle of small-leaved thyme sprigs tied with twine. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_dragons_blood_sage` | Dragon's Blood Sage, rare: deep red sage leaves with ember-like glowing veins. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_sea_kale` | Sea Kale: blue-green curly sea kale leaves with sea spray. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_frost_lichen` | Frost Lichen, rare: icy blue lichen with frost crystals. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_mugwort` | Mugwort: silvery-green mugwort leaves on a stalk. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |
| `herb_fern_flower` | Fern Flower, rare: the mythical Kupala night fern flower, a radiant red-gold bloom glowing among fern fronds. Game item icon, one bold readable shape, centred with a margin, three-quarter view. |

### 9.3 Potions (buffs, not heals)

| File | Prompt |
|---|---|
| `potion_hunt` | Elixir of the Hunt: a round flask of deep red potion with a wolf-tooth charm on the cord. Game item icon, one bold readable shape, centred with a margin, three-quarter view, no text on the bottle. |
| `potion_fortune` | Draught of Fortune: a slim bottle of shimmering gold potion with a four-leaf clover pressed on the label-free glass. Game item icon, one bold readable shape, centred with a margin, three-quarter view, no text on the bottle. |
| `potion_goldmoss` | Goldmoss Tonic: a squat jar of amber potion with gold flecks swirling. Game item icon, one bold readable shape, centred with a margin, three-quarter view, no text on the bottle. |
| `potion_ironbark` | Ironbark Brew: a heavy dark-glass bottle of grey-brown potion bound with iron bands. Game item icon, one bold readable shape, centred with a margin, three-quarter view, no text on the bottle. |
| `potion_swiftroot` | Swiftroot Tea: a narrow vial of bright teal potion with a small feather tied to it. Game item icon, one bold readable shape, centred with a margin, three-quarter view, no text on the bottle. |
| `potion_gatherer` | Gatherer's Draught: a corked bottle of fresh green potion with a tiny leaf inside. Game item icon, one bold readable shape, centred with a margin, three-quarter view, no text on the bottle. |
| `potion_kupala` | Kupala Night: a star-shaped bottle of deep blue potion glowing with tiny red-gold sparks. Game item icon, one bold readable shape, centred with a margin, three-quarter view, no text on the bottle. |
| `potion_bossbane` | Boss-bane Philtre: a sharp crystal flask of violet potion with a small skull stopper. Game item icon, one bold readable shape, centred with a margin, three-quarter view, no text on the bottle. |

