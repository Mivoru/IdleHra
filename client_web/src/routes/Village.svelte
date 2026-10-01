<script lang="ts">
  import { formatNumber } from '../lib/ui/format';
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { queryKeys, fetchVillageQuote, type VillageQuoteLine } from '../lib/net/rest';
  import { prettifyBaseId } from '../lib/net/content';
  import {
    BUILDINGS,
    upgradeBuilding,
    villageUpgradeDurationSeconds,
    formatDuration,
    villageUpgradeBlockedReason,
    maxBuildingLevelCeiling,
    MAX_STRUCTURAL_BUILDING_LEVEL,
    TOWN_HALL_BUILDING_ID,
    CRAFTING_WORKSHOP_BUILDING_ID,
  } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import type { StateUpdate } from '../lib/net/protocol.generated';
  import VillageFolk from '../lib/ui/VillageFolk.svelte';
  import GreatWorks from '../lib/ui/GreatWorks.svelte';
  import Stopwatch from '../lib/ui/Stopwatch.svelte';
  import QueryError from '../lib/ui/QueryError.svelte';
  import DisabledReason from '../lib/ui/DisabledReason.svelte';



  const snap = $derived($playerState);

  // Modul: THE PRICE COMES FROM THE SERVER (VillageManagementEngine
  // .QuoteUpgrade), with how much of each line the player holds. The key
  // carries every building's level and the pending slot, so a finished or
  // started upgrade asks again by itself; the interval catches what gathering
  // adds while the screen is open.
  const levelSignature = $derived(
    snap ? `${BUILDINGS.map((b) => levelOf(snap, b.stateField)).join('.')}:${snap.PendingUpgradeBuildingId}` : '',
  );
  const quote = createQuery(() => ({
    queryKey: [...queryKeys.villageQuote, levelSignature] as const,
    queryFn: fetchVillageQuote,
    enabled: levelSignature !== '',
    refetchInterval: 20_000,
  }));

  function linesFor(buildingId: number): VillageQuoteLine[] | null {
    return quote.data?.Buildings.find((b) => b.BuildingId === buildingId)?.Lines ?? null;
  }

  /** Gold is read live, like the header; the quote's figure is the durable one. */
  function heldOf(line: VillageQuoteLine): number {
    return line.ItemId === 'gold' && snap ? Number(snap.Gold) : line.Held;
  }

  function lineName(line: VillageQuoteLine): string {
    return line.ItemId === 'gold' ? 'gold' : prettifyBaseId(line.ItemId);
  }

  function shortfall(lines: VillageQuoteLine[] | null): string | null {
    if (!lines) return null;
    const missing = lines
      .filter((line) => heldOf(line) < line.Quantity)
      .map((line) => `${formatNumber(line.Quantity - heldOf(line))} ${lineName(line)}`);
    return missing.length > 0 ? `You need ${missing.join(', ')} more.` : null;
  }

  function levelOf(state: StateUpdate, field: string): number {
    const value = (state as unknown as Record<string, unknown>)[field];
    return typeof value === 'number' ? value : 0;
  }

  // Modul: PendingUpgradeBuildingId == 0 means no upgrade is in flight. Only
  // one can run at a time, so every other button is disabled while one is -
  // otherwise the player queues a second and it silently does nothing.
  const pendingId = $derived(snap ? snap.PendingUpgradeBuildingId : 0);
  const pendingUntil = $derived(snap ? Number(snap.PendingUpgradeCompletesAtEpoch) : 0);

  let nowSeconds = $state(Math.floor(connection.serverNowMs() / 1000));
  $effect(() => {
    // Server-corrected clock, never Date.now() - cooldowns and windows on this
    // wire are epoch-based and a browser clock can be arbitrarily wrong.
    const timer = setInterval(() => {
      nowSeconds = Math.floor(connection.serverNowMs() / 1000);
    }, 1000);
    return () => clearInterval(timer);
  });

  const pendingRemaining = $derived(Math.max(0, pendingUntil - nowSeconds));

  // Modul: THE BAR NEEDS A START, AND THE WIRE ONLY CARRIES THE END.
  //
  // The duration is a pure function of the level the building is being
  // upgraded FROM - and while an upgrade is in flight the snapshot still
  // reports that level, because CurrentLevel only advances when the server
  // resolves it. So the start is `completesAt - duration(levelNow)`, with no
  // second timestamp on a packet that has no room for one. The formula is
  // mirrored in commands.ts and guarded by serverMirrors.test.ts.
  const townHallLevel = $derived.by(() => {
    const hall = BUILDINGS.find((b) => b.id === TOWN_HALL_BUILDING_ID);
    return hall && snap ? levelOf(snap, hall.stateField) : 0;
  });

  const pendingBuilding = $derived(BUILDINGS.find((b) => b.id === pendingId) ?? null);
  const pendingTotal = $derived(
    pendingBuilding && snap ? villageUpgradeDurationSeconds(levelOf(snap, pendingBuilding.stateField)) : 0,
  );
  const pendingProgress = $derived(
    pendingTotal > 0 ? Math.max(0, Math.min(1, (pendingTotal - pendingRemaining) / pendingTotal)) : 0,
  );

  // Modul: AFFORDABLE FIRST (task 103). The list was in a fixed order with a
  // lone level number, a half-width button floating left on every row, and
  // "Maxed" as a disabled button - so the one upgrade a player could actually
  // make looked exactly like the seven they could not. Now the row being built
  // leads, then what can be afforded (the only FILLED Upgrade button), then
  // what is short (with what is missing), then what is capped (plain text, no
  // control). Within a group the order is BUILDINGS' own, so Town Hall still
  // leads its group.
  type RowState = 'building' | 'ready' | 'short' | 'loading' | 'capped';
  const STATE_RANK: Record<RowState, number> = { building: 0, ready: 1, short: 2, loading: 2, capped: 3 };

  function isStructural(buildingId: number): boolean {
    return buildingId === TOWN_HALL_BUILDING_ID || buildingId === CRAFTING_WORKSHOP_BUILDING_ID;
  }

  /** The level this building can reach right now - the "/ 5" of "Lv 4 / 5". */
  function ceilingOf(buildingId: number): number {
    return isStructural(buildingId) ? MAX_STRUCTURAL_BUILDING_LEVEL : maxBuildingLevelCeiling(townHallLevel);
  }

  /** "Maxed" only when nothing can raise it further; otherwise it is the Town Hall that caps it. */
  function cappedLabel(buildingId: number): string {
    if (isStructural(buildingId)) return 'Maxed';
    return townHallLevel >= MAX_STRUCTURAL_BUILDING_LEVEL ? 'Maxed' : 'Town Hall cap';
  }

  const rows = $derived.by(() => {
    if (!snap) return [];
    const list = BUILDINGS.map((building, order) => {
      const level = levelOf(snap, building.stateField);
      const blocked = villageUpgradeBlockedReason(building.id, level, townHallLevel);
      const lines = linesFor(building.id);
      const missing = shortfall(lines);
      const state: RowState =
        building.id === pendingId
          ? 'building'
          : blocked !== null
            ? 'capped'
            : lines === null
              ? 'loading'
              : missing !== null
                ? 'short'
                : 'ready';
      return { building, order, level, ceiling: ceilingOf(building.id), blocked, lines, missing, state };
    });
    return list.sort((a, b) => STATE_RANK[a.state] - STATE_RANK[b.state] || a.order - b.order);
  });

  function upgrade(buildingId: number) {
    const outcome = upgradeBuilding(buildingId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
  }

  // Modul: evict() is gone with the button that called it, and the panel it
  // sat in is gone too (task 77, in the markup). evictVillager still exists in
  // commands.ts and still has a live server handler; what it does not have is a
  // target, because the table it names has no rows and never did.

  // Modul: skills moved to the Character screen - they are combat abilities
  // that spend mana and have cooldowns, and they lived here between the
  // building queue and the mentor slots. See lib/ui/SkillsPanel.svelte.

  // Modul: the gathering-tool and mentor-slot state went with their panels.
  // What is left on this screen is the village itself.
</script>

{#if !snap}
  <p class="dim pad">Waiting for the first state snapshot...</p>
{:else}
  <div class="grid">
    <!-- Modul: THE BUILDINGS FIRST (task 103). The gene pool led this page,
         so at 390px the first building started about 1,050px down, after
         ~260px of prose - and a new player met marriage and bloodlines before
         the one thing they could act on. The people follow the buildings now;
         the Inn row says it is what feeds them. -->
    <section class="panel village">
      <div class="head">
        <h2>Village</h2>
        <!-- Modul: A COUNT, NOT A CAP. This read "Household 184 / 35 housing":
             every character the account owns against 10 + 5 x Inn. Nothing on
             the server enforces that capacity - no handler reads it - so the
             fraction promised a limit that does not exist, and turning it into
             a real one would have locked every player already past it. The
             real ceilings are the gene pool's places (below) and the Hall of
             Ancestors' cap at a rebirth. -->
        <span class="household" data-testid="village-household" title="Characters in your line">
          Household {snap.CurrentPopulationCount}
        </span>
      </div>

      <!-- Modul: no stock row. It showed Wood / Stone / Iron ore, legacy
           stocks no upgrade spends, above prices in logs and ores that were
           not in it - so a player could never check a price against the row.
           Each cost line below says what is held of it instead. -->

      {#if pendingId !== 0}
        <!-- Modul: A BUILD TIMER HAS TO LOOK LIKE ONE.
             This was a line of text and a raw second count, which was almost
             readable while every upgrade took thirty seconds and is not now
             that the top one takes three and a half hours - "12667s left" is
             not a time anybody reads. A stopwatch, a real duration and a bar
             that visibly fills are what say "this is running, come back". -->
        <div class="pending" role="status">
          <p class="pending-line">
            <Stopwatch size={14} />
            Upgrading <strong>{pendingBuilding?.name ?? `building ${pendingId}`}</strong>
            &middot; {pendingRemaining > 0 ? `${formatDuration(pendingRemaining)} left` : 'finishing...'}
          </p>
          <div
            class="progress"
            role="progressbar"
            aria-valuemin="0"
            aria-valuemax="100"
            aria-valuenow={Math.round(pendingProgress * 100)}
          >
            <div class="progress-fill" style="width: {pendingProgress * 100}%"></div>
          </div>
        </div>
      {/if}

      {#if quote.isError && quote.data === undefined}
        <!-- Without the quote every cost reads "..." and nothing says why. -->
        <QueryError query={quote} what="the upgrade costs" />
      {/if}

      <ul class="buildings">
        {#each rows as row (row.building.id)}
          <!-- Modul: THE BUTTON REFUSES BEFORE THE SERVER DOES.
               A capped building's Upgrade click used to travel, get rolled back
               with MaxTierReached or TownHallCeilingReached, and show the player
               nothing at all. The ceilings are mirrored in commands.ts so the
               reason can be stated here instead of discovered by pressing. A
               capped row has no button at all now - "Maxed" is a fact, not a
               control. The server still enforces both. -->
          <li
            class:upgrading={row.state === 'building'}
            data-testid="village-building"
            data-building-id={row.building.id}
            data-state={row.state}
          >
            <div class="top">
              <span class="name">
                <!-- Modul: the stopwatch marks WHICH building is busy. -->
                {#if row.state === 'building'}
                  <Stopwatch size={12} label="Upgrade in progress" />
                {/if}
                <strong>{row.building.name}</strong>
                <span class="lvl" data-testid="village-building-level">Lv {row.level} / {row.ceiling}</span>
              </span>
              {#if row.state === 'building'}
                <span class="status">{pendingRemaining > 0 ? formatDuration(pendingRemaining) : 'finishing...'}</span>
              {:else if row.state === 'capped'}
                <span class="status dim">{cappedLabel(row.building.id)}</span>
              {:else}
                <button
                  type="button"
                  class="upgrade"
                  class:primary={row.state === 'ready' && pendingId === 0}
                  disabled={pendingId !== 0 || row.state !== 'ready'}
                  title={pendingId !== 0
                    ? 'Another upgrade is already in progress'
                    : (row.missing ?? 'Upgrade to the next level')}
                  onclick={() => upgrade(row.building.id)}
                >
                  Upgrade
                </button>
              {/if}
            </div>
            <!-- Modul: WHAT IT DOES AND WHAT IT COSTS. The village listed a
                 name, a level and an Upgrade button, so raising anything was a
                 gamble with an invisible price against an unexplained benefit -
                 and the most valuable one, the Forge, gates fusion rarity
                 without ever saying so. -->
            <span class="what dim tiny">{row.building.what}</span>
            <span class="cost tiny">
              {#if row.blocked !== null}
                <span class="dim">{row.blocked}</span>
              {:else if row.lines}
                {#each row.lines as line (line.ItemId)}
                  <span class="line" class:short={heldOf(line) < line.Quantity}>
                    {formatNumber(Math.min(heldOf(line), line.Quantity))}/{formatNumber(line.Quantity)}
                    {lineName(line)}
                  </span>
                {/each}
              {:else}
                <span class="dim">...</span>
              {/if}
              <!-- Modul: the shortfall in words, on the row - a phone never
                   shows a disabled button's title. The same goes for the
                   one-upgrade-at-a-time rule. -->
              {#if row.blocked === null && row.state !== 'building'}
                <DisabledReason
                  text={row.missing ??
                    (pendingId !== 0 ? 'Another upgrade is already in progress.' : null)}
                />
              {/if}
            </span>
          </li>
        {/each}
      </ul>
      <!-- Town Hall gates every other building's ceiling, which is why it is
           listed first rather than in id order. -->
      <p class="dim tiny">Town Hall level caps every other building.</p>
      <!-- Modul: THE INTERLOCK, said where it bites.
           The village is rebuilt from nothing every season and the reason to
           bother is that THIS season's Inn decides what blood you can marry
           into THIS season's line - which was written down in the server and
           nowhere a player could read it. See docs/breeding_model.md. -->
      <p class="dim tiny">
        The <strong>Inn</strong> is what feeds the gene pool below: arrivals,
        capacity and how high a newcomer's aptitudes can roll all come off its
        level. Buildings survive the season; the people in the village do not.
      </p>
    </section>

    <VillageFolk />

    <!-- Task 84: the long material sink. Buildings survive a rebirth and so
         do these; they are the destination for the stacks a rebirth deletes. -->
    <GreatWorks />

    <!-- Modul: THE "WORK SLOTS" PANEL IS GONE (task 77), and it was not dead -
         it was mislabelled. It started as "Villagers" over VillageResidents, a
         table nothing in the server ever writes; it was repointed at the
         character roster, renamed "Work slots" and captioned "production slots,
         not people". The rows were people - the same characters the Character
         screen and the breeding pickers show - and "working" meant only that a
         character had any activity at all, combat included. On the dev fixture
         it was a 168-row un-windowed list saying nothing true. The roster lives
         on the Character screen; VillageFolk above already draws the village's
         people. -->


    <!-- Modul: TWO PANELS REMOVED HERE - "Gathering tool" and "Mentor slots".
         The tool panel was a survival from when tools were a stackable
         material with one shared tier and an "Upgrade tool" button. They are
         ordinary equipment now: crafted, carried, rolled for affixes, and
         raised in rarity at the Forge like anything else, one per profession
         rather than one tier for all three. A second, parallel upgrade path
         for them was a way to be wrong in two places at once.

         Mentor slots went with the Mentorship feature - see
         BossFirstClearRules' neighbours in Domain/Combat for the pattern:
         removed features get their commands IGNORED server-side, so an old
         tab pressing an old button does nothing rather than being kicked. -->

  </div>
{/if}

<style>
  /* Modul: the build timer's own furniture. See the markup for why a line of
     text and a raw second count stopped being enough. */
  .pending {
    display: grid;
    gap: 0.35rem;
    margin: 0 0 0.6rem;
    padding: 0.45rem 0.6rem;
    background: rgba(74, 163, 223, 0.12);
    border-left: 3px solid var(--accent);
    border-radius: 4px;
    font-size: 0.82rem;
  }

  .pending-line {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin: 0;
    font-size: 0.85rem;
  }

  /* Modul: the stopwatch's styles and its history (the watch that spun whole,
     task 28) live in lib/ui/Stopwatch.svelte - one component, not two copies. */

  .progress {
    height: 6px;
    border-radius: 3px;
    background: var(--bg-sunken);
    overflow: hidden;
  }

  .progress-fill {
    height: 100%;
    background: var(--accent);
    /* Matches the 1s ticker, so the bar creeps rather than stepping. */
    transition: width 1s linear;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(19rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  /* Modul: the bottom gap clears the corner bracket app.css paints 4px in
     from each corner (18px tall), so the closing note never sits on it. On a
     phone app.css forces every panel to 0.7rem, so the last child carries the
     gap there. */
  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem 1rem 1.5rem;
  }

  @media (max-width: 40rem) {
    .panel > :last-child {
      margin-bottom: 0.75rem;
    }
  }

  .head {
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: baseline;
    gap: 0.3rem 1rem;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  .household {
    font-size: 0.8rem;
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
  }

  .dim {
    color: var(--text-dim);
  }
  .tiny {
    font-size: 0.72rem;
  }
  .pad {
    padding: 1rem;
  }

  .panel > p {
    margin: 0.4rem 0 0;
  }

  .buildings {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.35rem;
  }

  /* Modul: A ROW IS A NAME LINE AND A DETAIL LINE, not a grid of tracks.
     As `1fr auto auto` the cost column could never be narrower than its own
     content, so "2 690g + 100 Willow Log + 100 Hematite Ore" pushed the row
     151px past the panel - cut off mid-word and painted over the panel beside
     it. The name, its level pill and the action share the top line (the
     action pinned right, the same shape on every row); what it does and what
     it costs wrap underneath at whatever width the panel happens to be. */
  .buildings li {
    display: grid;
    gap: 0.15rem;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.35rem;
    min-width: 0;
  }

  li.upgrading {
    border-left: 2px solid var(--accent);
    padding-left: 0.4rem;
  }

  .top {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 0;
  }

  /* Modul: NOT `white-space: nowrap` on anything that holds prose - nowrap is
     inherited, and it once cut "Raises the level ceiling every other building
     i" off on every row. */
  .name {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.2rem 0.45rem;
    overflow-wrap: break-word;
  }

  .lvl {
    font-size: 0.7rem;
    padding: 0 0.35rem;
    border: 1px solid var(--border);
    border-radius: 999px;
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .status {
    flex: none;
    font-size: 0.78rem;
    font-variant-numeric: tabular-nums;
  }

  .upgrade {
    flex: none;
    flex-shrink: 0;
    min-width: 6.5rem;
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
  }

  /* The one upgrade that can actually be made, filled. Everything else is an
     outline at most. */
  .upgrade.primary {
    background-color: var(--brass);
    background-image: none;
    border-color: var(--brass-lit);
    color: var(--bg);
    font-weight: 700;
  }

  .what {
    display: block;
    max-width: 52ch;
    line-height: 1.25;
  }

  .cost {
    overflow-wrap: break-word;
    display: flex;
    flex-wrap: wrap;
    gap: 0.1rem 0.6rem;
    min-width: 0;
  }

  .cost .line {
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
  }

  /* The words carry it too (the shortfall sentence beside it), so this is
     never colour-only. */
  .cost .line.short {
    color: var(--danger);
  }
</style>
