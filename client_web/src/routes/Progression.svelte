<script module lang="ts">
  // The tab survives leaving Progress and coming back within a session.
  let lastTab: 'goals' | 'collection' | 'stats' | 'daily' = 'goals';
</script>

<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { playerState } from '../lib/stores/game';
  import {
    queryKeys,
    fetchLoginBonus,
    fetchRaceMastery,
    fetchStatistics,
    fetchRecords,
  } from '../lib/net/rest';
  import Bar from '../lib/ui/Bar.svelte';
  import CollectionLog from '../lib/ui/CollectionLog.svelte';
  import PlayerInsights from '../lib/ui/PlayerInsights.svelte';
  import Money from '../lib/ui/Money.svelte';
  import RaceIcon from '../lib/ui/RaceIcon.svelte';
  import { RACE_NAMES, ALL_RACE_IDS, isRaceUnlocked } from '../lib/ui/races';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import BookOfDeeds from '../lib/ui/BookOfDeeds.svelte';
  import { bossTimes, formatTenths } from '../lib/stores/records';
  import { prettifyBaseId } from '../lib/net/content';
  import { rarityColor, rarityName } from '../lib/ui/rarity';

  // Modul: ONE SCREEN, FOUR TABS (tasks 56, 57). Progress was four panels in
  // a grid: the Book, an Achievements list with claim buttons, the daily login
  // and races, and twelve flat statistics. The Achievements list is gone into
  // the Book's Lifetime chapter - where every tier pays itself, because a
  // claim there paid three of the four a second time - and the collection log
  // and the "how you play" statistics are new, so the screen is split by the
  // question a player comes with: what to do next, what have I found, how am
  // I doing, and today's login.
  const TABS = [
    { key: 'goals', label: 'Goals' },
    { key: 'collection', label: 'Collection' },
    { key: 'stats', label: 'Statistics' },
    { key: 'daily', label: 'Daily & races' },
  ] as const;
  let tab = $state<(typeof TABS)[number]['key']>(lastTab);
  $effect(() => {
    lastTab = tab;
  });

  const loginBonus = createQuery(() => ({ queryKey: queryKeys.loginBonus, queryFn: fetchLoginBonus }));
  const raceMastery = createQuery(() => ({ queryKey: queryKeys.raceMastery, queryFn: fetchRaceMastery }));
  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));
  // Task 51. The hit and boss times read the live stream (hydrated at login),
  // the best drop and the Deep read the durable row.
  const records = createQuery(() => ({ queryKey: queryKeys.records, queryFn: fetchRecords }));

  const snap = $derived($playerState);

  // Race names and the unlock bitmask both live in lib/ui/races.ts - this
  // screen used to carry its own copy and it had already gone stale at five
  // entries, so anyone who unlocked Moosleute saw "Race 6".
  const unlockedMask = $derived(snap?.UnlockedRaceBitmask ?? 0);

  function duration(seconds: number): string {
    const hours = Math.floor(seconds / 3600);
    if (hours < 1) return `${Math.floor(seconds / 60)}m`;
    return `${hours}h`;
  }
</script>

<div class="progress-tabs" role="tablist" aria-label="Progress">
  {#each TABS as t (t.key)}
    <button role="tab" class:active={tab === t.key} aria-selected={tab === t.key} data-progress-tab={t.key} onclick={() => (tab = t.key)}>
      {t.label}
    </button>
  {/each}
</div>

<div class="grid">
  {#if tab === 'goals'}
    <!-- Modul: the first chapter leads, because it is the onboarding. A new
         player opening Progress should meet six things they can do today, not a
         Treasury tier asking for 100,000 gold they have never seen. -->
    <BookOfDeeds />
  {:else if tab === 'collection'}
    <CollectionLog />
  {:else if tab === 'stats'}
    <PlayerInsights />
  <section class="panel">
    <h2>Statistics</h2>
    {#if statistics.data}
      {@const st = statistics.data}
      <dl class="stats">
        <div><dt>Level</dt><dd>{st.Level}</dd></div>
        <!-- Modul: ONE gold figure per screen.
             This read st.Gold, which is CommodityRecords - the durable balance,
             refreshed when this query runs. The header beside it reads the live
             state feed. The two are the same number at rest and different
             numbers whenever the session has earned since the last checkpoint,
             so the screen showed 27,287g and 2,091,564g at once and gave a
             player no way to know which was theirs.
             The live feed wins: it is what every other screen shows and it is
             what the player just earned. It falls back to the persisted figure
             only before the first packet arrives. -->
        <div><dt>Gold</dt><dd><Money amount={snap ? snap.Gold : st.Gold} /></dd></div>
        <div><dt>Diamonds</dt><dd><Money amount={snap ? snap.PremiumCurrencyBalance : st.PremiumDiamonds} kind="diamond" /></dd></div>
        <div><dt>Login streak</dt><dd>{st.LoginStreakDays}</dd></div>
        <div><dt>Kills</dt><dd>{st.TotalKills.toLocaleString()}</dd></div>
        <div><dt>Bosses</dt><dd>{st.BossesSlain.toLocaleString()}</dd></div>
        <div><dt>Crafted</dt><dd>{st.TotalItemsCrafted.toLocaleString()}</dd></div>
        <div><dt>Deaths</dt><dd>{st.TotalDeaths.toLocaleString()}</dd></div>
        <div><dt>Regions done</dt><dd>{st.RegionsCompletedCount}</dd></div>
        <div><dt>Achievements</dt><dd>{st.AchievementsClaimedCount}</dd></div>
        <div><dt>Characters</dt><dd>{st.CharacterCount}</dd></div>
        <div><dt>Played</dt><dd>{duration(st.TotalPlayTimeSeconds)}</dd></div>
      </dl>
      {#if st.GuildName}
        <p class="dim tiny">Guild: {st.GuildName}</p>
      {/if}
    {:else}
      <Skeleton />
    {/if}

    <h3>Records</h3>
    <dl class="stats" data-records>
      <div><dt>Highest hit</dt><dd>{snap && snap.BestHit > 0 ? snap.BestHit.toLocaleString() : '-'}</dd></div>
      <div>
        <dt>Best drop</dt>
        <dd>
          {#if records.data && records.data.BestDropTier > 0}
            <span style="color: {rarityColor(records.data.BestDropTier)}">{rarityName(records.data.BestDropTier)}</span>
            {records.data.BestDropBaseId ? prettifyBaseId(records.data.BestDropBaseId) : ''}
          {:else}-{/if}
        </dd>
      </div>
      <div><dt>Deepest Delve floor</dt><dd>{records.data?.DelveDeepestFloor || '-'}</dd></div>
      {#if snap}
        {#each bossTimes(snap) as tenths, i}
          <div><dt>Region {i + 1} boss, fastest</dt><dd>{formatTenths(tenths)}</dd></div>
        {/each}
      {/if}
    </dl>
  </section>

  {:else}
  <section class="panel">
    <h2>Daily login</h2>
    {#if loginBonus.data}
      <!-- Modul: the week used to be seven identical tiles with today's
           outlined. A player on day four saw days one to three looking exactly
           like days five to seven and reasonably concluded their earlier
           rewards were still waiting to be opened. Nothing is opened here -
           signing in credits the day by itself - so each tile says which of
           the three things it is. -->
      <p class="dim small">
        Day {loginBonus.data.CurrentStreakDay} of 7.
        {loginBonus.data.CreditedToday
          ? "Today is credited - rewards arrive on sign-in, there is nothing to claim."
          : 'Today is not credited yet.'}
      </p>
      <ol class="week">
        {#each loginBonus.data.WeeklyGoldSchedule as gold, index}
          {@const day = index + 1}
          {@const isToday = day === loginBonus.data.CurrentStreakDay}
          {@const collected = day < loginBonus.data.CurrentStreakDay
            || (isToday && loginBonus.data.CreditedToday)}
          <li class:current={isToday} class:collected class:upcoming={!collected && !isToday}>
            <span class="dim tiny">Day {day}</span>
            <strong><Money amount={gold} /></strong>
            <span class="daystate tiny">
              {collected ? 'collected' : isToday ? 'today' : 'upcoming'}
            </span>
          </li>
        {/each}
      </ol>
      {#if loginBonus.data.Day7DiamondBonus > 0}
        <p class="dim tiny">
          Day 7 also grants <Money amount={loginBonus.data.Day7DiamondBonus} kind="diamond" />.
        </p>
      {/if}
    {:else}
      <Skeleton />
    {/if}

    <h3>Races unlocked</h3>
    <ul class="races">
      {#each ALL_RACE_IDS as raceId}
        {@const unlocked = isRaceUnlocked(unlockedMask, raceId)}
        <li class:locked={!unlocked}>
          <RaceIcon {raceId} />
          <span class="race-name">{RACE_NAMES[raceId]}</span>
          <!-- The word, not only the colour - a locked race has to read as
               locked without relying on the palette. -->
          <span class="race-state">{unlocked ? 'unlocked' : 'locked'}</span>
        </li>
      {/each}
    </ul>

    <h3>Race mastery</h3>
    {#if (raceMastery.data ?? []).length === 0}
      <p class="dim tiny">No race mastery yet.</p>
    {:else}
      {#each raceMastery.data ?? [] as race (race.RaceId)}
        <div class="mastery">
          <span class="dim tiny">{RACE_NAMES[race.RaceId] ?? `Race ${race.RaceId}`} &middot; level {race.Level}</span>
          <Bar
            value={race.Experience}
            max={Math.max(1, race.NextLevelExperience)}
            color="var(--rarity-6)"
            label={`${race.Experience.toLocaleString()} / ${race.NextLevelExperience.toLocaleString()}`}
          />
        </div>
      {/each}
    {/if}

  </section>

  {/if}

  <!-- Modul: THE CHRONICLE PASS IS NOT SHOWN (2026-09-28). Its only claim UI
       was a number box (0-49) beside text admitting the client cannot know
       which milestones are taken, and the free track mints
       `chronicle_free_{n}`, an item id items.json does not contain. The server
       side is untouched; what the pass should become is an owner decision -
       docs/superpowers/plans/2026-09-28-design-audit-phases.md, O5. -->
</div>

<style>
  .progress-tabs {
    display: flex;
    gap: 0.5rem;
    padding: 1rem 1rem 0;
    flex-wrap: wrap;
  }

  .progress-tabs button {
    min-height: 44px;
    flex-shrink: 0;
    padding: 0.4rem 0.9rem;
    border-radius: var(--radius);
    border: 1px solid var(--border);
    background: var(--bg-panel);
    color: inherit;
    font: inherit;
    cursor: pointer;
  }

  .progress-tabs button.active {
    border-color: var(--accent);
    color: var(--accent);
    font-weight: 700;
  }


  .week li.collected {
    opacity: 0.55;
  }

  .week li.upcoming {
    opacity: 0.8;
  }

  .daystate {
    display: block;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    opacity: 0.7;
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr));
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


  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.8rem;
    margin: 0 0 0.7rem;
  }
  .tiny {
    font-size: 0.72rem;
  }






  /* Modul: the week WRAPS. Seven fixed columns cannot be narrower than their
     content ("10 000g" plus a state word), so in a panel sized by the page's
     `minmax(20rem, 1fr)` grid the last tiles overflowed the panel and drew on
     top of whatever sat to the right - Statistics, in the reported case, whose
     numbers then read as a single garbled line of golds.
     auto-fit lets the row break instead. A wrapped week is still a week; a
     week painted over the neighbouring panel is not readable at all. */
  .week {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(4.25rem, 1fr));
    gap: 0.25rem;
    text-align: center;
  }

  .week li {
    border: 1px solid var(--border);
    border-radius: 4px;
    padding: 0.3rem 0.15rem;
    font-size: 0.7rem;
  }

  .week li.current {
    border-color: var(--good);
    background: rgba(123, 201, 111, 0.1);
  }

  .week strong {
    display: block;
    font-size: 0.72rem;
  }

  .mastery {
    display: grid;
    gap: 0.15rem;
    margin-bottom: 0.45rem;
  }

  .races {
    list-style: none;
    margin: 0 0 0.5rem;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
  }

  /* Centred rather than baseline-aligned now that each pill leads with an
     image - baseline puts the picture's bottom edge on the text baseline and
     the whole row sits crooked. */
  .races li {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.2rem 0.5rem 0.2rem 0.25rem;
    border-radius: var(--radius);
    border: 1px solid var(--rarity-6);
    color: var(--rarity-6);
    font-size: 0.76rem;
  }

  /* A locked race is greyed as well as dimmed, so the pills read as two
     distinct states at a glance rather than as one state at two opacities. */
  .races li.locked :global(img) {
    filter: grayscale(1);
    opacity: 0.6;
  }

  .races li.locked {
    border-color: var(--border);
    color: var(--text-dim);
    opacity: 0.7;
  }

  .race-state {
    font-size: 0.65rem;
    opacity: 0.8;
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    margin: 0;
  }

  .stats div {
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
    font-size: 0.9rem;
  }

</style>
