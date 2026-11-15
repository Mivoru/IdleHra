# Daily login, daily missions and the Chronicle pass

Approved by the owner on 2026-11-15. This document holds the plan for three PRs, built in order:

1. **Daily login window**: built in the PR that adds this file.
2. **Daily missions**: next.
3. **Chronicle pass**: last. It levels from mission XP.

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

## 2. Daily missions (next)

- **Six missions a day.** Three are fixed:
  - fight in each dungeon
  - send an expedition
  - strike the world boss (only inside its window, the 1st-7th and 15th-22nd; outside it, another rotating mission takes the slot)

  The other three rotate, drawn from the owner's list: reforge or roll a legendary affix, refine an affix, fuse an item, make a child, raise a child, throw a feast, reach the Deep in the Delve, fight the highest regional boss ascension available, donate to the guild, send a chat message, craft a tool, craft a potion, list an item on the market, allocate an attribute point, allocate a skill point, open a wardrobe chest, open a loot chest, spend 12 hours' worth of gold.
- **Filtered by what the player has unlocked.** A player with no guild never gets "donate", a player without Breeding Grounds never gets "make a child", and so on. An impossible mission is the new-player trap the dev fixture can never show.
- One free reroll of one rotating mission a day.
- Generation is deterministic per (player, UTC day, slot), the way `QuestEngine` already does it, so a retried request can never reshuffle the day.
- **One hook.** `MissionEvents.Record(playerId, MissionAction, amount)` is called from the success branch of each engine. A test asserts that every `MissionAction` has at least one writer. A read side with no writer is this codebase's recurring "computed but never consumed" defect, inverted.
- The old `QuestEngine` (three kill/craft slots, no client) is replaced, not extended.

## 3. Chronicle pass (last)

- **30-day seasons, 50 levels, 100 pass XP a level, 5,000 XP in total.**
- **Mission XP:** 30 XP per mission, plus 20 XP for finishing all six. A full day is 200 XP, so **finishing all missions on 25 of the 30 days reaches level 50 exactly.** A player who finishes 4 of 6 every day ends near level 36.
- XP past level 50 fills a bonus chest every 100 XP (the Clash Royale overflow).
- **Rewards:**
  - gold as minutes of income
  - about 300 diamonds a season (for scale: the Delve pays at most 60 a week)
  - Gold and Ancient loot chests, legendary wardrobe chests, dungeon tickets
  - a season avatar at level 25, a season frame at level 40
  - the season pet at level 50 (+12%, one stat)
- **UI:** a bubble on the side of the main screen with a red badge counting the rewards ready to claim. The pass screen is a vertical track, laid out like Clash Royale's.
- **Server-authoritative.** The old pass (`PlayerChroniclePasses`, hidden since 2026-09-28) took its XP from the client and granted item ids that do not exist. Its table can be reused; its claim path cannot.

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

`exercise.mjs` gained a claim step for the dev fixture (it passes whether or not the fixture already claimed today) and a step for a brand-new account (day 1 after the guided minute). Neither has run yet. Per the owner, long runs happen overnight.
