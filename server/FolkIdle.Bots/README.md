# FolkIdle playtest bots

Headless players that sign in as brand-new guests and play the game over the
same WebSocket and REST surface the web client uses. They measure how long a
new player takes to reach each milestone, and where they stall.

**A dev-box tool.** Never point it at production: it creates accounts that look
like players and it feeds the macro detector. A non-local `--server` is refused
unless `--allow-remote` is given.

## Run

```powershell
# 1. The server, with dev tools and a tick multiplier (stop any running server first)
$env:FOLKIDLE_DB_CONN='Host=localhost;Database=folkidle_dev;Username=postgres;Password=postgres'
$env:FOLKIDLE_DEV_TOOLS='1'
$env:FOLKIDLE_TICK_SPEED='10'
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj

# 2. The bots (another terminal); --speed must match FOLKIDLE_TICK_SPEED
dotnet run --project server/FolkIdle.Bots/FolkIdle.Bots.csproj -- --bots 3 --hours 24 --speed 10
# or a mix of personas
dotnet run --project server/FolkIdle.Bots/FolkIdle.Bots.csproj -- --mix optimal:2,casual:2,idle:2 --hours 72 --speed 10
```

`--hours` is GAME time. At `--speed 10`, 24 game hours take 2.4 wall hours.
Results go to `bot-runs/<timestamp>/` (or `--out DIR`):

- `milestones.csv`: one row per bot per milestone (`level_10`, `region_2_open`,
  `boss_r1_defeated`, `first_death`, `end`) with game hours, level, gold, deaths.
- `events.log`: every decision the bot made, with game time. Read it when a
  milestone never arrives.
- `summary.md`: per persona, the median game hours to each milestone and how
  many of its bots reached it, plus the level each bot ended on.

## Personas

| Persona | Attention |
|---|---|
| `optimal` | Plays without pause |
| `casual` | Checks in every 8 game hours for 20 minutes |
| `idle` | Checks in every 24 game hours for 20 minutes |

All three make the same decisions while checked in. Between check-ins a bot
stays CONNECTED and sends nothing. It does not log out, because offline
catch-up runs on the wall clock and would earn only a tenth of the time at x10.
Online progress is what offline catch-up was built to match (#120). The bot
applies the offline cap itself: once a gap passes the snapshot's
`OfflineCapSeconds`, it stops its activity until the next check-in.

## The tick multiplier

`FOLKIDLE_TICK_SPEED=N` (1-20) makes the engine run N ticks per 100 ms of wall
time. It is honoured only when dev tools are on and `DOTNET_ENVIRONMENT` is not
`Production` (`TickPacer.ResolveTickSpeed`).

Each tick is still 100 ms of game time, so no balance number changes. Combat,
gathering, XP, ageing and potion durations all count ticks. The snapshot
broadcast and the anti-cheat challenge stay at one per wall second, because
challenge answers spend a wall-clock token bucket. At x10 a per-tick broadcast
read as a packet flood and the socket was aborted with no log line.

**Not compressed:** anything on `DateTime.UtcNow`. That covers daily dungeon
tickets, the midnight refills, the world boss calendar and epoch expiries. A
run that depends on those measures them at wall speed.

## The "optimal" persona (`BotBrain`)

Each decision copies something a screen already recommends, so the bot
measures the game as it is presented:

| Decision | Source |
|---|---|
| Which monster | Combat's "best XP/h" pick (`huntingEstimate.ts` `bestPick`) over the server's `HuntingProjection`; the region boss as soon as its projection says the fight holds |
| Fishing | When no fight holds without food and the larder is empty: fish region 1's node up to 200 food, then load the larder |
| Gear | The loot row's rule (`lootCompare.ts` `isUpgrade`): base ATK + DEF times the rarity multiplier |
| Larder | Larder's one-press Load: the biggest food stack into its slot or the first empty one |
| Attributes | Meet the gear requirement of the current region and the next, then put the rest into Might and Vigour at 2:1 |
| Skill tree | The cheapest open node, lowest row first, through `/skilltree/preview` |
| Quests | Claims every claimable step |
| Chest | Auto-sell Common drops, so the backpack never fills |

It sends at most one WebSocket command every 1.2-3.0 s with random jitter,
because of `ValidateCommand`'s 100 ms rule and the macro detector.

Not yet done: crafting, cooking, the village, the forge, the dungeons, rebirth,
and fishing anywhere but region 1's node.
