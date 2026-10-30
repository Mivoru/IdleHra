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

References: `client_web/public/chest/idle.webp` (today's chest, for the
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
