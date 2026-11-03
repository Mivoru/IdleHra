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
$env:FOLKIDLE_TICK_SPEED='100'
$env:FOLKIDLE_BOSS_MINIGAME='wheel'   # as production
dotnet run --project server/FolkIdle.Server/FolkIdle.Server.csproj

# 2. The bots (another terminal); --speed must match FOLKIDLE_TICK_SPEED
dotnet run --project server/FolkIdle.Bots/FolkIdle.Bots.csproj -- --mix optimal:2,casual:2,idle:2 --hours 72 --speed 100
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
| `optimal` | Plays without pause; reborn when Renown rises and no level for 6 game hours ("eager") |
| `patient` | Plays without pause; reborn only after a game day with no progress (level, best dungeon stage, Ascension steps) and with gold to keep every worn combat piece - after three days without progress it goes anyway |
| `patientbare` | Like `patient` in timing, but keeps only what `optimal` keeps (the weapon, when it costs at most half the gold) - rebirth experiment 0.6.2, variant B |
| `eagerkit` | Like `optimal` in timing, but keeps every worn combat piece it can afford at that moment - variant C |
| `casual` | Checks in every 8 game hours for 20 minutes |
| `idle` | Checks in every 24 game hours for 20 minutes |

All of them make the same decisions while checked in (only the rebirth rule differs: `Persona.PatientRebirth` is the timing, `Persona.KeepWholeKit` the gear kept). `--seed N` shifts every bot's dice, so a repeated mix is an independent sample. Between check-ins a bot
is LOGGED OUT: the server's own offline catch-up and offline cap decide what
the time away earned, and `events.log` records each login's offline hours,
XP and gold.

## An unattended run

`run-overnight.ps1` does it all: a fresh `folkidle_bots`, the server at a
multiplier (shield wheel on, as production), the bots until a wall-clock end
time, then stops the server. Build first; it starts both projects with
`--no-build`.

```powershell
dotnet build server/FolkIdle.Bots/FolkIdle.Bots.csproj
.\server\FolkIdle.Botsun-overnight.ps1 -StartAt 00:00 -EndAt 08:55   # the rebirth experiment, x200
```

## The tick multiplier

`FOLKIDLE_TICK_SPEED=N` (1-200) makes the engine run N ticks per 100 ms of wall
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

## What the bot does (`BotBrain`, one partial file per system)

The same decisions for every persona; only the attention differs.

| System | Decision | File |
|---|---|---|
| Fight | Combat's "best XP/h" pick over the server's `HuntingProjection`; the region boss once its projection holds; one scouting fight in the next location to open its gathering; a monster that killed it twice is left until the next level | `BotBrain.Combat.cs` |
| Fishing | A trip to the best reachable river when the larder is empty, AND as soon as a better river is reachable than any fish it holds (bot mistake 4.1.1). Target: three game hours of the current fight's food use plus rations, 150-3,000 | `BotBrain.Combat.cs` |
| Larder | The best food tier first; a better chest food replaces the worst slot; slots hold 9,999 | `BotBrain.Combat.cs` |
| Attributes | Up to the open region's gear requirement first, then a third of the points at a time wherever the what-if projection scores best | `BotBrain.Build.cs` |
| Skill tree | War: the open node with the best projected score gain per point, through `/skilltree/preview`; cheapest-lowest-row when nothing gains. Hearth: cheapest first | `BotBrain.Build.cs` |
| Gear | The three best unworn candidates per slot projected against the worn set; wears the best gain over 1%. Tools by region tier, then rarity | `BotBrain.Build.cs` |
| Village | The most urgent affordable building (Forge and Town Hall, then Lumberjack/Mine/Workshop, then Warehouse; Inn and Breeding Grounds from level 40); short of a gatherable material, a gathering trip | `BotBrain.Economy.cs` |
| Crafting | The next better rod, axe, pickaxe; potions (XP elixir, then armour, attack speed, bossbane) from herbs it gathers | `BotBrain.Economy.cs` |
| Potion shelves | XP elixir on the rewards shelf, the best combat potion on the combat shelf, auto-drink on | `BotBrain.Economy.cs` |
| Forge | Fuses stacks of three identical spares of the current region up to the Forge level; with spare gold, an auto reroll of the weapon's affixes | `BotBrain.Economy.cs` |
| Workshop, Great Works | A commission for the weakest slot; logs and ore beyond 1,500 into the monument | `BotBrain.Economy.cs` |
| Claims | Quest steps, achievements, mail, loot chests | `BotBrain.Economy.cs` |
| Chest | Sells Common on the way in until the Forge; then only gear two regions back; bulk-sells old regions | `BotBrain.Economy.cs` |
| World boss | Today's strike: the Auto strike with the shield wheel on, opcode 32 with it off | `BotBrain.Events.cs` |
| Dungeons | Every ticket: pushes the next stage (telegraphs answered at human speed, one in twelve missed), sweeps the best stage after a loss | `BotBrain.Events.cs` |
| Delve | A run when the fee is under a fifth of the spare gold; the door with the best odds; banks under 65% | `BotBrain.Events.cs` |
| Boss Ascension | The next step when the plain boss holds and dies inside a third of the time limit | `BotBrain.Events.cs` |
| Roster | Benched members into open playable slots; characters 2 and 3 woodcut and mine | `BotBrain.Lineage.cs` |
| Breeding | Main character x the best villager of the other sex and same race, when the preview says eligible | `BotBrain.Lineage.cs` |
| Inheritance | Diamonds into the cheapest wanted bonus (XP, damage, health, loot luck, gold, gathering) | `BotBrain.Lineage.cs` |
| Rebirth | When it raises Renown and the run has stalled 6 game hours, or at level 100 | `BotBrain.Lineage.cs` |

**Decisions by the server's numbers.** Attributes, skill nodes and gear are
chosen with `POST /api/v1/dev/projection/whatif` (dev tools only): the hunting
projection on a copy of the live payload with the change applied. The bot
keeps no copy of the combat formula.

**Pacing.** At most one WebSocket command per pass, 0.4-1.2 s apart with
random jitter (`ClientInputThrottler`, `ValidateCommand`'s 100 ms rule, the
macro detector's 0.05 CV floor). Every chore timer counts GAME minutes, so the
bot's attention per game day is the same at x20 and x100.

## Output

Besides `milestones.csv`, `events.log` and `summary.md`, a run writes
`timeline.csv` (one row per bot per game hour: level, XP, region, gold,
diamonds, activity, larder, deaths, Forge, Town Hall, rebirths). `summary.md`
ends with the speed the server really ran at, from `/metrics`.

## Not done

Market and guilds (a single bot has nobody to trade with), the seasonal event
shop and boss, playing the shield wheel (the bot takes the Auto strike), the
Deep below Delve floor 8, and breeding between two of the player's own
characters.
