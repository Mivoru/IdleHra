<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { queryKeys, fetchStatistics } from '../lib/net/rest';
  import { BUILDINGS, upgradeBuilding, villageCostLabel, villageUpgradeDurationSeconds, formatDuration, villageUpgradeBlockedReason, TOWN_HALL_BUILDING_ID } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import type { StateUpdate } from '../lib/net/protocol.generated';
  import VillageFolk from '../lib/ui/VillageFolk.svelte';


  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));

  const snap = $derived($playerState);

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

  function upgrade(buildingId: number) {
    const outcome = upgradeBuilding(buildingId);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
  }

  // Modul: evict() is gone with the button that called it - see the Villagers
  // panel. evictVillager still exists in commands.ts and still has a live
  // server handler; what it does not have is a target, because the table it
  // names has no rows and never did.

  // Modul: skills moved to the Character screen - they are combat abilities
  // that spend mana and have cooldowns, and they lived here between the
  // building queue and the mentor slots. See lib/ui/SkillsPanel.svelte.

  // Modul: the gathering-tool and mentor-slot state went with their panels.
  // What is left on this screen is the village itself.
</script>

{#if !snap}
  <p class="dim pad">Waiting for state...</p>
{:else}
  <div class="grid">
    <!-- Modul: the people, before the buildings. The village's reason to exist
         is the blood it brings in; the buildings are how it gets better at
         it. -->
    <VillageFolk />
    <section class="panel">
      <div class="head">
        <h2>Village</h2>
        <span class="dim tiny">
          {snap.CurrentPopulationCount}/{snap.CachedMaxPopulationCapacity} population
        </span>
      </div>

      <dl class="stocks">
        <div><dt>Wood</dt><dd>{Number(snap.CachedWoodStock).toLocaleString()}</dd></div>
        <div><dt>Stone</dt><dd>{Number(snap.CachedStoneStock).toLocaleString()}</dd></div>
        <div><dt>Iron ore</dt><dd>{Number(snap.CachedIronOreStock).toLocaleString()}</dd></div>
      </dl>

      {#if pendingId !== 0}
        <!-- Modul: A BUILD TIMER HAS TO LOOK LIKE ONE.
             This was a line of text and a raw second count, which was almost
             readable while every upgrade took thirty seconds and is not now
             that the top one takes three and a half hours - "12667s left" is
             not a time anybody reads. A stopwatch, a real duration and a bar
             that visibly fills are what say "this is running, come back". -->
        <div class="pending" role="status">
          <p class="pending-line">
            <span class="stopwatch" aria-hidden="true">
              <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2">
                <circle cx="12" cy="13" r="8" />
                <!-- The crown and the bow, which do not move. -->
                <path d="M9 2h6M12 2v3" stroke-linecap="round" />
                <!-- The hands, and only these turn. -->
                <path class="hand" d="M12 9v4l2.5 2.5" stroke-linecap="round" />
              </svg>
            </span>
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

      <ul class="buildings">
        {#each BUILDINGS as building}
          {@const level = levelOf(snap, building.stateField)}
          <!-- Modul: THE BUTTON REFUSES BEFORE THE SERVER DOES.
               A capped building's Upgrade click used to travel, get rolled back
               with MaxTierReached or TownHallCeilingReached, and show the player
               nothing at all - an enabled button that did nothing and said
               nothing. The ceilings are mirrored in commands.ts so the reason
               can be stated here instead of discovered by pressing. The server
               still enforces both.

               `{@const}` has to be an immediate child of the `{#each}`, which
               is why it sits here rather than beside the button it feeds. -->
          {@const blocked = villageUpgradeBlockedReason(building.id, level, townHallLevel)}
          <li class:upgrading={building.id === pendingId}>
            <span class="name">
              <!-- Modul: the stopwatch marks WHICH building is busy. Greying
                   every button said "something is happening"; it did not say
                   what, and the row that was actually being worked on looked
                   exactly like the eleven that were not. -->
              {#if building.id === pendingId}
                <span class="stopwatch" aria-label="Upgrade in progress">
                  <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2">
                    <circle cx="12" cy="13" r="8" />
                    <path d="M9 2h6M12 2v3" stroke-linecap="round" />
                    <path class="hand" d="M12 9v4l2.5 2.5" stroke-linecap="round" />
                  </svg>
                </span>
              {/if}
              {building.name}
              <!-- Modul: WHAT IT DOES AND WHAT IT COSTS.
                   The village listed a name, a level and an Upgrade button, so
                   raising anything was a gamble with an invisible price against
                   an unexplained benefit - and the most valuable one, the
                   Forge, gates fusion rarity without ever saying so. -->
              <span class="what dim tiny">{building.what}</span>
            </span>
            <span class="lvl">{level}</span>
            <span class="cost dim tiny">{villageCostLabel(building.costKind, level)}</span>
            <button
              class="tiny-btn"
              disabled={pendingId !== 0 || blocked !== null}
              title={blocked !== null
                ? blocked
                : pendingId !== 0
                  ? 'Another upgrade is already in progress'
                  : `Next level costs ${villageCostLabel(building.costKind, level)}`}
              onclick={() => upgrade(building.id)}
            >
              {building.id === pendingId ? formatDuration(pendingRemaining) : blocked !== null ? 'Maxed' : 'Upgrade'}
            </button>
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
        The <strong>Inn</strong> is what feeds the gene pool above: arrivals,
        capacity and how high a newcomer's aptitudes can roll all come off its
        level. Buildings survive the season; the people in the village do not.
      </p>
    </section>

    <!-- Modul: your villagers ARE your characters. This panel used to read a
         table nothing in the server ever writes, so it said "No villagers yet"
         to a player the Character screen was telling they had two. Same
         question, two tables, two honest answers.

         The Evict button is gone with it. It sent a slot index at that dead
         table, so it never did anything - and now that the roster is real,
         wiring it up would mean deleting a character, which is a different and
         permanent thing that deserves its own decision rather than inheriting
         a button that happened to be here. -->
    <section class="panel">
      <!-- Modul: RENAMED FROM "Villagers" - it was the same word as the gene
           pool panel directly above it, on the same screen, for a different
           concept. These are the old identity-less production slots; the people
           you marry are NEWCOMERS and ELDERS. One word, one meaning; see
           docs/breeding_model.md section 0. -->
      <h2>Work slots</h2>
      <p class="dim tiny">
        Production slots, not people. Nobody here can be married into your line.
      </p>
      {#if (statistics.data?.Villagers ?? []).length === 0}
        <p class="dim">No work slots in use.</p>
      {:else}
        <ul class="villagers">
          {#each statistics.data?.Villagers ?? [] as villager (villager.SlotIndex)}
            <li>
              <span class="name">Slot {villager.SlotIndex + 1}</span>
              <span class="dim tiny">{villager.IsActive ? 'working' : 'idle'}</span>
            </li>
          {/each}
        </ul>
      {/if}
    </section>


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
  }

  .pending-line {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    margin: 0;
    font-size: 0.85rem;
  }

  .stopwatch {
    display: inline-flex;
    align-items: center;
    color: var(--accent);
    flex: none;
  }

  /* A stopwatch that does not move is a picture of a stopwatch.

     Modul: THE HANDS TURN, NOT THE WATCH. This used to animate the whole
     <svg>, so the case, the crown and the bow rotated with the hands and the
     thing spun like a dropped coin. The hands and the crown were also a single
     <path>, so there was nothing to animate separately until they were split.

     transform-box: view-box makes transform-origin resolve against the
     viewBox's own coordinates rather than the path's bounding box - so the
     hands turn about the DIAL centre (12, 13) instead of about the middle of
     their own stroke, which is what makes it read as a clock. */
  .stopwatch .hand {
    animation: tick 2s steps(8, end) infinite;
    transform-box: view-box;
    transform-origin: 12px 13px;
  }

  @keyframes tick {
    to {
      transform: rotate(360deg);
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .stopwatch .hand {
      animation: none;
    }
  }

  .progress {
    height: 6px;
    border-radius: 3px;
    background: var(--bg-sunken, rgba(0, 0, 0, 0.25));
    overflow: hidden;
  }

  .progress-fill {
    height: 100%;
    background: var(--accent);
    /* Matches the 1s ticker, so the bar creeps rather than stepping. */
    transition: width 1s linear;
  }

  li.upgrading {
    border-left: 2px solid var(--accent);
    padding-left: 0.4rem;
  }
  .what {
    display: block;
    max-width: 34ch;
    line-height: 1.25;
  }

  /* Modul: WRAPS AT THE PLUS SIGNS. This was `white-space: nowrap`, and the
     `auto` grid track holding it could therefore never be narrower than
     "2 690g + 100 Willow Log + 100 Hematite Ore". The row forced itself 151px
     wider than the panel, so the price of an upgrade was cut off mid-word
     ("100 Willow L") and painted over the panel beside it. A cost that reads
     as a smaller number than it is, is worse than a cost on two lines. */
  .cost {
    overflow-wrap: break-word;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(19rem, 1fr));
    gap: 1rem;
    padding: 1rem;
    align-items: start;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .head {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    gap: 1rem;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
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

  .pending {
    padding: 0.45rem 0.6rem;
    background: rgba(74, 163, 223, 0.12);
    border-left: 3px solid var(--accent);
    border-radius: 4px;
    font-size: 0.82rem;
    margin: 0 0 0.6rem;
  }

  .stocks {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    margin: 0 0 0.7rem;
  }

  .stocks div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .buildings,
  .villagers,
  .slots {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.3rem;
  }

  .buildings li,
  .villagers li,
  .slots li {
    display: grid;
    grid-template-columns: 1fr auto auto;
    gap: 0.5rem;
    align-items: center;
    font-size: 0.85rem;
    border-bottom: 1px solid var(--border);
    padding-bottom: 0.28rem;
  }

  .slots li {
    grid-template-columns: auto 1fr auto;
  }

  /* Modul: THE COST TAKES ITS OWN LINE RATHER THAN SQUEEZING THE NAME.
     As a grid of `1fr auto auto` the cost column could never be narrower than
     its own content, so "2 690g + 100 Willow Log + 100 Hematite Ore" pushed
     the row 151px past the panel - where it was cut off mid-word and painted
     over the panel beside it. Letting the cost merely WRAP fixed the clipping
     and replaced it with a second defect: the auto track still claimed most of
     the row, and the description beside it came out one word, sometimes one
     syllable, per line.

     Flex with a basis instead of fixed tracks. Name and cost sit side by side
     while both fit and the cost drops to its own line when they do not, at
     whatever width the panel happens to be - the panel's width comes from the
     grid it sits in, not from the viewport, so a breakpoint would be guessing
     at the wrong number. min-width: 0 is load-bearing: a flex item defaults to
     min-content and would refuse to shrink, which is the trap the grid had. */
  .buildings li {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem 0.5rem;
  }

  .buildings li .name {
    flex: 1 1 11rem;
  }

  .buildings li .lvl {
    flex: none;
  }

  .buildings li .cost {
    flex: 1 1 12rem;
    min-width: 0;
  }

  .buildings li button {
    flex: none;
  }

  /* Modul: EVERY UPGRADE BUTTON THE SAME SIZE.
     Each `li` is its own grid, so its `1fr` column is sized by that row's own
     content - and the cost text differs per building ("100 logs + 100 ore"
     against "980g + 225 logs + 225 ore"). The button wraps onto the second
     row into that column, so it inherited a different width on every line and
     the list read as nine buttons of nine sizes.

     Pinned to a fixed width and left-aligned instead: the control is the same
     control on every row, so it should be the same shape.

     `min()` rather than a flat 11rem: a fixed width is also a floor on the
     column's min-content, so in a panel squeezed to one grid track the button
     alone kept the row wider than the panel. It gives way before the row
     does, and only then. */
  .buildings li button {
    justify-self: start;
    width: min(11rem, 100%);
  }

  .slots select {
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.25rem 0.4rem;
    font-size: 0.8rem;
    min-width: 0;
  }

  .tier {
    color: var(--accent);
  }

  .tools {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 0.7rem;
  }

  .tool {
    display: grid;
    place-items: center;
    width: 3.4rem;
    height: 3.4rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg);
  }

  .tool img {
    width: 100%;
    height: 100%;
    object-fit: contain;
  }

  /* Modul: NOT `white-space: nowrap` - this span WRAPS A PARAGRAPH.
     `.name` holds the building name AND the `.what` block that explains what
     upgrading it does. nowrap is inherited, so that whole sentence was laid
     out on one line and `overflow: hidden` then cut it off: "Raises the level
     ceiling every other building i". 512px of the explanation was invisible on
     every row, and the explanation is the only reason the row is there.
     A second, shorter use of `.name` is the "Slot 3" label in Work slots,
     which never needed truncating either. */
  .name {
    min-width: 0;
    overflow-wrap: break-word;
  }

  .lvl {
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    min-width: 1.5rem;
    text-align: right;
  }

  .mana {
    display: grid;
    gap: 0.15rem;
    margin-bottom: 0.7rem;
  }

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
