<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import {
    queryKeys,
    fetchAchievements,
    fetchAchievementsState,
    fetchLoginBonus,
    fetchRaceMastery,
    fetchStatistics,
    type AchievementEntry,
  } from '../lib/net/rest';
  import { claimAchievement } from '../lib/net/commands';
  import { connection } from '../lib/net/connection';
  import Bar from '../lib/ui/Bar.svelte';
  import Money from '../lib/ui/Money.svelte';
  import RaceIcon from '../lib/ui/RaceIcon.svelte';
  import { RACE_NAMES, ALL_RACE_IDS, isRaceUnlocked } from '../lib/ui/races';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import BookOfDeeds from '../lib/ui/BookOfDeeds.svelte';

  const client = useQueryClient();
  const achievements = createQuery(() => ({ queryKey: queryKeys.achievements, queryFn: fetchAchievements }));

  // Modul: /achievements/state and /achievements/snapshot are DIFFERENT
  // endpoints answering different questions. The snapshot above says how far
  // along each achievement is; this says how many rewards have actually been
  // taken, across the account's whole lifetime rather than the current set.
  // Neither is derivable from the other.
  const achievementsState = createQuery(() => ({
    queryKey: [...queryKeys.achievements, 'state'] as const,
    queryFn: fetchAchievementsState,
  }));
  const loginBonus = createQuery(() => ({ queryKey: queryKeys.loginBonus, queryFn: fetchLoginBonus }));
  const raceMastery = createQuery(() => ({ queryKey: queryKeys.raceMastery, queryFn: fetchRaceMastery }));
  const statistics = createQuery(() => ({ queryKey: queryKeys.statistics, queryFn: fetchStatistics }));

  const snap = $derived($playerState);
  // Quarantine blocks every claim server-side, so the reason is stated rather
  // than leaving buttons that silently do nothing.
  const quarantined = $derived(snap ? snap.Quarantine_Active !== 0 : false);

  // Race names and the unlock bitmask both live in lib/ui/races.ts - this
  // screen used to carry its own copy and it had already gone stale at five
  // entries, so anyone who unlocked Moosleute saw "Race 6".
  const unlockedMask = $derived(snap?.UnlockedRaceBitmask ?? 0);

  // The three races whose mastery level rides on the hot path rather than
  // waiting for the REST snapshot - they feed StatsCalculator directly.
  const liveMastery = $derived(
    snap
      ? [
          { raceId: 1, level: snap.HumanMasteryLevel },
          { raceId: 2, level: snap.VilaMasteryLevel },
          { raceId: 3, level: snap.DraugrMasteryLevel },
        ]
      : [],
  );

  function claim(entry: AchievementEntry) {
    const outcome = claimAchievement(entry.AchievementId, quarantined);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    setTimeout(() => client.invalidateQueries({ queryKey: queryKeys.achievements }), 800);
  }

  const claimable = $derived(
    (achievements.data ?? []).filter((a) => !a.IsClaimed && a.CompletedTier > 0),
  );

  function duration(seconds: number): string {
    const hours = Math.floor(seconds / 3600);
    if (hours < 1) return `${Math.floor(seconds / 60)}m`;
    return `${hours}h`;
  }
</script>

<div class="grid">
  <!-- Modul: the first chapter leads, because it is the onboarding. A new
       player opening Progress should meet six things they can do today, not a
       Treasury tier asking for 100,000 gold they have never seen. -->
  <BookOfDeeds />

  <section class="panel">
    <div class="head">
      <h2>Achievements</h2>
      <span class="dim tiny">
        {claimable.length} ready to claim
        {#if achievementsState.data}
          &middot; {achievementsState.data.TotalAchievementsClaimedCount} claimed for good
        {/if}
      </span>
    </div>

    {#if quarantined}
      <p class="warn">Your account is restricted, so rewards cannot be claimed.</p>
    {/if}

    {#if achievements.isPending}
      <Skeleton />
    {:else if achievements.isError}
      <p class="err">{achievements.error?.message}</p>
    {:else if (achievements.data ?? []).length === 0}
      <p class="dim">No achievements tracked yet.</p>
    {:else}
      <ul class="rows">
        {#each achievements.data ?? [] as entry (entry.AchievementId)}
          {@const ready = !entry.IsClaimed && entry.CompletedTier > 0}
          <li class:ready>
            <div class="line">
              <strong>Achievement #{entry.AchievementId}</strong>
              {#if entry.IsClaimed}
                <span class="dim tiny">claimed</span>
              {:else if ready}
                <!-- Modul: the button deliberately does NOT show a number.
                     NextTierReward is the reward for the tier NOT yet reached,
                     while claiming pays out the tiers already earned - a
                     Treasury claim at tier 2 paid 60 while the field read 250.
                     The client cannot compute the real total (the reward
                     tables are server-side), so promising a figure here would
                     be guessing at the player's payout. -->
                <button class="tiny-btn" disabled={quarantined} onclick={() => claim(entry)}>
                  Claim tier {entry.CompletedTier}
                </button>
              {/if}
            </div>
            <Bar
              value={entry.CurrentProgress}
              max={Math.max(1, entry.NextTierTarget)}
              color={ready ? 'var(--good)' : 'var(--accent)'}
              label={`${entry.CurrentProgress.toLocaleString()} / ${entry.NextTierTarget.toLocaleString()}`}
            />
            <span class="dim tiny">
              tier {entry.CompletedTier}
              {#if !entry.IsClaimed}&middot; next tier pays {entry.NextTierReward}{/if}
            </span>
          </li>
        {/each}
      </ul>
    {/if}
  </section>

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

    {#if liveMastery.some((m) => m.level > 0)}
      <p class="dim tiny">
        <!-- These three come off the hot path rather than the REST snapshot,
             so they can disagree with the bars above for a moment after a
             level-up. Naming the source is cheaper than an unexplained
             mismatch. -->
        Live from the state feed:
        {#each liveMastery as entry, index}{index > 0 ? ', ' : ''}{RACE_NAMES[entry.raceId]}
          {entry.level}{/each}.
      </p>
    {/if}
  </section>

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
      {/if}
    {:else}
      <Skeleton />
    {/if}
  </section>
</div>

<style>
  .progress {
    text-align: right;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
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
  .err {
    color: var(--danger);
  }

  .warn {
    padding: 0.5rem 0.65rem;
    background: rgba(224, 85, 63, 0.12);
    border-left: 3px solid var(--danger);
    border-radius: 4px;
    font-size: 0.82rem;
    margin: 0 0 0.7rem;
  }

  .rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    gap: 0.5rem;
    max-height: 28rem;
    overflow-y: auto;
  }

  .rows li {
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.45rem 0.55rem;
    opacity: 0.75;
  }

  .rows li.ready {
    opacity: 1;
    border-color: var(--good);
  }

  .line {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.85rem;
    margin-bottom: 0.25rem;
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

  .tiny-btn {
    font-size: 0.72rem;
    padding: 0.2rem 0.45rem;
  }
</style>
