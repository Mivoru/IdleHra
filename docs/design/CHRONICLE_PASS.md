# Daily login, daily missions and the Chronicle pass

Approved by the owner on 2026-11-15. This document holds the plan for three PRs, built in order:

1. **Daily login window**: built and deployed (#428, client 1.94.0).
2. **Daily missions**: built (client 1.95.0, migration `DailyMissions`).
3. **Chronicle pass**: built (client 1.96.0, migration `ChronicleSeasons`). It levels from mission XP.

## Owner decisions

| Question | Decision |
|---|---|
| Pass name | **Chronicle**. Each season is named after Celtic/Slavic folklore (see "Season names"). Czech month names were rejected. |
| Premium track | **Free only.** Real-money payments are blocked, and there are three players to sell to. A premium track can be added later. |
| Pass pet (level 50) | **+12% to one stat**, a different stat each season. That matches Black Serpent (12%) and stays under the rare monster pets (16%), so those stay worth hunting. |
| Login: missed day | **Pause, never reset.** The next claim continues from the day after the last one claimed. |

## Measurements behind the numbers

Read-only queries on production, 2026-11-15, covering the 7 days before.

| Player | Gold balance | Earned gold per hour | Old best login day |
|---|---|---|---|
| Mivoru (L116) | 469M | 84M | 30k |
| (pro)boss (L100) | 609M | 9.7M | 30k |
| Tomda (L99) | 1.28B | 10.2M | 30k |

The old login week paid at most 30,000 gold, which is 0.3% of one hour of income. Rewards are therefore paid as **minutes of the player's own earned income** (`GoldRewardScale`, which the world boss and the gold dungeon already use). That figure only counts earned sources, so a reward can never raise its own base.

The old reward was also granted only inside `/api/v1/auth/login`. Sessions now renew through a rotating refresh token, so most days never reached that handler: Tomda had last been credited on 2026-11-05 while still playing.

## 1. Daily login (built)

- Window `DailyLoginWindow.svelte` opens at the first session of a UTC day that still has a claim open. It waits until What's new, the offline summary, the guided first minute, the event intro and the big-chest prompt (`bigChestPending`) have all closed. "Later" hides it for that UTC day on that device; the claim stays open on the Progress screen.
- Server: `GET /api/v1/login-bonus/state`, `POST /api/v1/login-bonus/claim` (409 when today is already claimed). `DailyLoginRewardEngine.ClaimAsync` pays everything in one transaction under the `PlayerRecords` row lock. Gold and diamonds move in the database, so the handler sends `ReloadState` and `BillingSync` to the live session, the same way the gold dungeon does.
- The week (`DailyLoginRewardEngine.Week`). The wiki mirrors it, and `wiki.test.ts` compares the two row by row:

| Day | Gold | Diamonds | Also |
|---|---|---|---|
| 1 | 60 min | | |
| 2 | 30 min | | +1 ticket at every dungeon gate (capped at `BonusTicketCap`) |
| 3 | 120 min | 20 | |
| 4 | 30 min | | Gold loot chest (from the highest region the player has opened) |
| 5 | 120 min | | |
| 6 | 30 min | | Epic wardrobe chest |
| 7 | 210 min | 100 | Ancient loot chest |

  That is 10 hours of gold and 120 diamonds a week. The old week paid 100 diamonds.
- `LastLoginTimestamp` is now just a stamp for "signed in with a password" (guild succession reads it). It is no longer the reward's clock. `LoginRewardClaimDay` (migration `LoginWeekClaim`, additive) is the reward's clock.

## 2. Daily missions (built)

What shipped, and where it differs from the plan above it:

- **The day is drawn once and stored** in `PlayerMissionDays` (one row per player per UTC day: the mission list, a claimed mask, the bonus flag, rerolls used and `XpEarned`). The draw reads facts that move during a day (gold, chests, a world boss that dies mid-week), so a draw recomputed per read would change a mission halfway through. `MissionRegistry.Draw` is still deterministic per (player, day), so a retried first read draws the same day; the insert is `ON CONFLICT DO NOTHING`.
- **Fixed missions:** fight in every dungeon (3 distinct gates, not 3 runs), send an expedition, strike the world boss. Each is offered only when open: the dungeons once Region 2's boss is beaten (`DungeonEngine.IsOpen`), the expedition once the Breeding Grounds exist and an adult reserve character is free, and the world boss while this week's encounter is active. A fixed mission that is not open gives its slot to a rotating one.
- **The world boss is weekly now** (Monday-Sunday, `WorldBossCalendar`), not the 1st-7th and 15th-22nd this plan first said, so "in its window" means "this week's encounter is alive".
- **Rotating missions (16):** reforge 3 affixes, refine 3 affixes, fuse an item, have a child, throw a feast, reach the Deep, fight the highest boss ascension open, give to the guild, send a chat message, craft a tool, brew a potion, list on the market, spend an attribute point, spend a skill point, open a wardrobe chest, open a loot chest. Each has an eligibility rule in `MissionRegistry.All` (level 5 for the Forge, level 10 for the market, a guild, chests in hand, points to spend...).
- **Not built, with reasons:**
  - *Roll a legendary affix*: luck, not effort. It can fail every day. It is folded into "reforge affixes".
  - *Spend 12 hours' worth of gold*: no spend ledger exists. Gold leaves through about twenty sinks, and `GoldIncomeLedger` records income only. It needs a spend ledger first.
  - *Raise a child*: growth is time-gated (age phases), so it cannot be done on demand in a day. "Have a child" covers breeding.
- **Writers.** `MissionEvents.Record(playerId, action, amount, detail)` only enqueues, so it is safe on the tick. `MissionProgressWorker` (a cron, in `CronWorkerGuardTests`) coalesces once a second into `PlayerMissionCounters` with one `unnest` upsert, and prunes counters older than 3 days hourly. The day is stamped at the act, not at the drain. `MissionRegistryTests.EveryMissionActionHasAWriter` greps the server for a `Record(... MissionAction.X ...)` per action. The writers, all after the commit or the success result:

| Action | Writer |
|---|---|
| DungeonFight | `NetworkBroadcastSystem.Dungeon` live start and sweep (detail = gate) |
| ExpeditionSent | `NetworkBroadcastSystem.Expeditions` on `Started` |
| WorldBossStrike | `WorldBossEngine.AfterStrikeCommitted` (opcode 32 and the duel both pass through it) |
| AffixReforge / AffixRefine | `AffixRerollEngine.ExecuteRerollAsync` after commit (an auto-reroll counts each paid attempt) |
| ItemFused | `ForgeSplicingEngine` fusion success and stack fusion |
| ChildBorn | `BreedingEngine`, both breeding paths |
| FeastThrown | `VillageManagementEngine.ExecuteRecruitVillagerAsync` |
| DelveDeep | `/api/v1/delve/deep/descend` on `Ok` |
| BossAscension | `BossAscensionTickCoordinator` when the step is the highest open in that region |
| GuildDonation | `GuildContributionEngine` (gold, gear, depot), `GuildLogisticsDepotEngine`, `GuildLogisticsEngine` |
| ChatMessage | `HandleChatDispatchAsync` (system messages carry no sender) |
| ToolCrafted / PotionCrafted | `CraftingEngine`, live and offline job settlement |
| MarketListed | `MarketEscrowEngine.ListItemAsync` on `Listed` |
| AttributePointSpent | `AttributeTickCoordinator` |
| SkillPointSpent | `/api/v1/skilltree/commit` when `Spent > 0` |
| WardrobeChestOpened | `/api/v1/cosmetics/open` on `Ok` |
| LootChestOpened | `/api/v1/chests/open` (amount = chests opened) |

- **Routes:** `GET /api/v1/missions` (draws today on first call), `POST /api/v1/missions/claim` (claims every finished mission plus the bonus in one call, under a `FOR UPDATE` on the day row), `POST /api/v1/missions/reroll {Slot}` (two free swaps a day for unfinished ROTATING missions; a FIXED mission can be replaced, free, only when MissionRegistry.FixedMissionImpossible says it cannot be finished: the world boss is not up, or the dungeons on the launch day 2026-11-15).
- **XP** banks on `PlayerMissionDays.XpEarned`, never on `PlayerRecords` (the checkpoint rewrites that row from the live payload). `MissionEngine.XpBetweenAsync` is what the pass sums over its season.
- **Client:** `ChronicleBubble.svelte` floats bottom-left (opposite the chat dock) with a red badge = the server's `Claimable`, polled each minute. It is hidden during the guided first minute. `MissionsWindow.svelte` lists the day with Go buttons (`missions.ts` maps each action to a screen), Swap, and one Claim. In step 3 the same bubble opens the pass.
- **QuestEngine** (3 kill/craft slots, no client) still runs and checkpoints. Removing it touches `TickStatePayload` and the checkpoint, so it is a separate cleanup.

## 3. Chronicle pass (built)

- **Seasons:** 30 days, numbered from UTC day 20772 (2026-11-15, the day the missions shipped, so day-one XP counts). `ChronicleRegistry.SeasonNumberFor(dayKey)`. Names cycle through the list below.
- **XP is not stored by the pass.** It is `MissionEngine.XpBetweenAsync(seasonStart, seasonEnd)`, the sum of `PlayerMissionDays.XpEarned`. The pass and the missions can never disagree. `PlayerChronicleSeasons` (PlayerId, Season) holds only the claimed-level bitmask and the overflow chests paid.
- **Track (owner-approved 2026-11-15).** Each block of 5 levels pays 60 min of gold, +1 ticket at every gate, 15 diamonds, 90 min of gold, then a chest:

  | Level | Reward |
  |---|---|
  | 5, 15, 35 | Gold loot chest |
  | 20, 45 | Ancient loot chest |
  | 10, 30 | Legendary wardrobe chest |
  | 25 | avatar |
  | 40 | frame |
  | 50 | pet (+12%, one stat) |

  +25 diamonds at 10/20/25/30/40/45. Season totals (pinned in `ChronicleRegistryTests`): 300 diamonds, 25 h of gold income, 3 Gold + 2 Ancient + 2 Legendary-wardrobe chests, 10 ticket sets. Past 50, every 100 XP pays a Gold loot chest (at most 10 a season).
- **Routes:** `GET /api/v1/chronicle`, `POST /api/v1/chronicle/claim {Level?}` (one level, or everything ready plus overflow). One transaction under the `PlayerRecords` row lock; the XP is summed under the lock. Gold and diamonds move in the DB, so the handler sends ReloadState and BillingSync, as the login week does.
- **Keepsakes need paintings.** `PetTests.Every_pet_has_art_on_disk` and the cosmetics tests require art. A season is "defined" in `ChronicleRegistry.Defined` only once its avatar and pet exist under `client/Assets/Images/SpritesWeb/Chronicle/`. An undefined season still runs with its name, but pays a Legendary wardrobe chest at 25 and an Ancient chest at 50, and has no frame drawing. **Before season 2 (2026-12-15), generate Morana's avatar and pet** with `tools/assets/gemini.mjs` (the Veles entries in `gemini.json` are the template: Morana's rebirth avatar is the style reference for the portrait, and the Black Serpent flattened on green is the reference for the pet). Then add the season to `Defined`, draw its frame in `client_web/src/lib/ui/frames.ts`, and pick its pet stat. Veles cost about $0.54 for 2 variants each on `gemini-3-pro-image`.
- **Season 1, Veles:** avatar "Veles", frame "Veles' Horns" (the new `horns` style in `CosmeticFrame.svelte`), pet "Golden Aurochs", +12% gold. Veles is the god of cattle and wealth.
- **Client:** the bottom-left bubble (`ChronicleBubble.svelte`) shows the season level, and its red badge is pass-ready + missions-ready. It opens `ChronicleWindow.svelte` with two tabs: `ChroniclePassPanel` (the vertical road, Clash-Royale style, scrolled to the player's place, keepsakes drawn large) and `MissionsPanel`.
- The old `PlayerChroniclePasses` table and its opcodes (46, 59) stay dead. Removing them is a separate cleanup.

### Season names

Season names come from Celtic/Slavic folklore (owner, 2026-11-15). Each season's avatar, frame and pet take their theme from its name. Draft order:

1. Veles' Chronicle - the horned lord of the underworld and cattle
2. Morana's Chronicle - winter and death, drowned each spring
3. The Wild Hunt
4. Kupala Night - the fern flower at midsummer
5. Svarog's Forge
6. Mokosh's Spindle
7. Beltane Fires
8. The Cailleach's Winter
9. Perun's Oak
10. Domovoi's Hearth
11. Rusalka Week
12. Lugh's Harvest

## Verification still owed

`exercise.mjs` gained `dailyMissionsStep`: the bubble and the window list the server's day, a dev-granted loot chest is opened and its mission must move (when drawn), and a claim must bank XP. It has not run yet (overnight).


`exercise.mjs` gained a claim step for the dev fixture (it passes whether or not the fixture already claimed today) and a step for a brand-new account (day 1 after the guided minute). Neither has run yet. Per the owner, long runs happen overnight.
