# FolkIdle playtest bots

Headless players that sign in as brand-new guests and play the game over the
same WebSocket and REST surface the web client uses. They measure how long a
new player takes to reach each milestone, and where they stall.

**A dev-box tool.** Never point it at production: it creates accounts that look
like players and it feeds the macro detector. A non-local `--server` is refused
unless `--allow-remote` is given.

## Run

```powershell
# 0. A throwaway database (a fast server writes timestamps from the future)
.\server\FolkIdle.Bots\new-bot-db.ps1

# 1. The server on it, with dev tools and a tick multiplier (stop any running server first)
$env:FOLKIDLE_DB_CONN='Host=localhost;Database=folkidle_bots;Username=postgres;Password=postgres'
$env:FOLKIDLE_DEV_TOOLS='1'
$env:FOLKIDLE_TICK_SPEED='20'
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj

# 2. The bots (another terminal); --speed must match FOLKIDLE_TICK_SPEED
dotnet run --project server/FolkIdle.Bots/FolkIdle.Bots.csproj -- --mix optimal:2,casual:2,idle:2 --hours 72 --speed 20
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
is LOGGED OUT: the server's own offline catch-up and offline cap decide what
the time away earned, and `events.log` records each login's offline hours,
XP and gold.

## The tick multiplier

`FOLKIDLE_TICK_SPEED=N` (1-20) makes the engine run N ticks per 100 ms of wall
time. It is honoured only when dev tools are on and `DOTNET_ENVIRONMENT` is not
`Production` (`TickPacer.ResolveTickSpeed`).

Each tick is still 100 ms of game time, so no balance number changes. Combat,
gathering, XP, ageing and potion durations all count ticks. The snapshot
broadcast and the anti-cheat challenge stay at one per wall second, because
challenge answers spend a wall-clock token bucket. At x10 a per-tick broadcast
read as a packet flood and the socket was aborted with no log line.

**The game clock compresses too.** `GameClock` (server root) runs at the
same multiplier from the moment the server starts. That covers offline
catch-up, daily keys and midnight refills (dungeon tickets, the world boss
day, login bonus), potion and buff expiries, village upgrades, seasonal
events, mail and market ages, and per-day ledgers. At speed 1 it is
`DateTime.UtcNow`, so production is untouched.

These stay on the wall clock: token lifetimes, rate limits and anti-cheat
timing, billing, telemetry, chat, retry back-offs, push and e-mail sends, and
the live dungeon run's swipe windows. The CLIENT also counts down against the
browser's clock, so countdowns on screen are wrong on a fast server. Bots do
not read them.

**Use a throwaway database.** The game clock runs ahead of the wall clock and
writes future timestamps. A server restart re-anchors the clock to the wall,
and a "future" logout then refuses that login (`ValidateLoginTime`). Recreate
`folkidle_bots` with `new-bot-db.ps1` before each run and keep the server up
for the whole run.

## The "optimal" persona (`BotBrain`)

Each decision copies something a screen already recommends, so the bot
measures the game as it is presented:

| Decision | Source |
|---|---|
| Which monster | Combat's "best XP/h" pick (`huntingEstimate.ts` `bestPick`) over the server's `HuntingProjection`; the region boss as soon as its projection says the fight holds |
| Fishing | When no fight holds without food and the larder is empty: fish at the highest node the character has reached (`HighestLocationReached`) up to 200 food, then load the larder |
| Gear | The loot row's rule (`lootCompare.ts` `isUpgrade`): base ATK + DEF times the rarity multiplier |
| Larder | Larder's one-press Load: the biggest food stack into its slot or the first empty one |
| Attributes | Meet the gear requirement of the current region and the next, then put the rest into Might and Vigour at 2:1 |
| Skill tree | The cheapest open node, lowest row first, through `/skilltree/preview` |
| Quests | Claims every claimable step |
| Chest | Auto-sell Common drops, so the backpack never fills |

It sends at most one WebSocket command every 1.2-3.0 s with random jitter,
because of `ValidateCommand`'s 100 ms rule and the macro detector.

Not yet done: crafting, the village, the forge, the dungeons, rebirth.
