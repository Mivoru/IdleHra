<script lang="ts">
  // Modul: what a season leaves behind, and the only real thing diamonds buy.
  //
  // The season wipes levels, gear, gold and materials every three months. Three
  // things survive it: the village you built, the race mastery you learned, and
  // these - the bonuses you chose. That makes a rollover a step rather than a
  // loss, and gives the premium currency a sink it did not have (nine producers
  // against one chronicle pass).
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import {
    INHERITANCE_STATS,
    INHERITANCE_MAX_LEVEL,
    INHERITANCE_PCT_PER_LEVEL,
    inheritanceUpgradeCost,
    purchaseInheritanceLevel,
  } from '../lib/net/commands';
  import Money from '../lib/ui/Money.svelte';
  import Bar from '../lib/ui/Bar.svelte';

  const snap = $derived($playerState);
  const diamonds = $derived(snap?.PremiumCurrencyBalance ?? 0);

  // The wire carries one byte per stat. Indexed by the same ids the server's
  // InheritanceRegistry uses, so the two cannot drift on ordering.
  function levelOf(statId: number): number {
    if (!snap) return 0;
    switch (statId) {
      case 0: return snap.Inherit_Damage;
      case 1: return snap.Inherit_MaxHp;
      case 2: return snap.Inherit_XpGain;
      case 3: return snap.Inherit_GoldGain;
      case 4: return snap.Inherit_GatheringYield;
      case 5: return snap.Inherit_LootLuck;
      default: return 0;
    }
  }

  const rows = $derived(
    INHERITANCE_STATS.map((stat) => {
      const level = levelOf(stat.id);
      const cost = inheritanceUpgradeCost(level);
      return {
        ...stat,
        level,
        cost,
        capped: level >= INHERITANCE_MAX_LEVEL,
        bonus: level * INHERITANCE_PCT_PER_LEVEL,
        affordable: cost > 0 && diamonds >= cost,
      };
    }),
  );

  const totalSpent = $derived(
    rows.reduce((sum, r) => {
      let spent = 0;
      for (let i = 0; i < r.level; i++) spent += inheritanceUpgradeCost(i);
      return sum + spent;
    }, 0),
  );

  function buy(statId: number, level: number) {
    const outcome = purchaseInheritanceLevel(statId, level, diamonds);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }
</script>

<div class="wrap">
  <section class="panel">
    <header>
      <h2>Inheritance</h2>
      <span class="dim tiny">
        <Money amount={diamonds} kind="diamond" /> available
      </span>
    </header>

    <p class="dim small">
      These are permanent. A season resets your level, your gear, your gold and
      your materials - it does not touch these, your village, or the race
      mastery you have earned. What you buy here is what makes the next season
      faster than this one.
    </p>

    {#if !snap}
      <p class="dim">Waiting for your state to arrive...</p>
    {:else}
      <ul class="stats">
        {#each rows as row (row.id)}
          <li class:capped={row.capped}>
            <div class="head">
              <span class="name">{row.name}</span>
              <span class="value">
                {#if row.bonus > 0}+{row.bonus}%{:else}<span class="dim">not bought</span>{/if}
              </span>
            </div>

            <p class="blurb dim tiny">{row.blurb}</p>

            <Bar
              value={row.level}
              max={INHERITANCE_MAX_LEVEL}
              color="var(--diamond, #7dd3fc)"
              label={`${row.level} / ${INHERITANCE_MAX_LEVEL}`}
            />

            <div class="buy">
              {#if row.capped}
                <span class="dim tiny">At maximum.</span>
              {:else}
                <button
                  disabled={!row.affordable}
                  title={row.affordable ? '' : `Needs ${row.cost.toLocaleString()} diamonds`}
                  onclick={() => buy(row.id, row.level)}
                >
                  Buy +{INHERITANCE_PCT_PER_LEVEL}% for
                  <Money amount={row.cost} kind="diamond" />
                </button>
              {/if}
            </div>
          </li>
        {/each}
      </ul>

      {#if totalSpent > 0}
        <p class="dim tiny footer">
          <Money amount={totalSpent} kind="diamond" /> invested so far. None of it
          resets.
        </p>
      {/if}
    {/if}
  </section>
</div>

<style>
  .wrap { display: grid; gap: 1rem; }

  .panel {
    background: var(--panel, rgba(127, 127, 127, 0.05));
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 1rem 1.15rem 1.25rem;
  }

  header {
    display: flex;
    align-items: baseline;
    gap: 0.75rem;
    flex-wrap: wrap;
    margin-bottom: 0.4rem;
  }
  header h2 { margin: 0; }
  header .dim { margin-left: auto; }

  .small { font-size: 0.9rem; max-width: 46rem; }
  .tiny  { font-size: 0.8rem; }
  .dim   { opacity: 0.75; }

  .stats {
    list-style: none;
    margin: 1.1rem 0 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr));
    gap: 0.9rem;
  }

  .stats li {
    display: grid;
    gap: 0.45rem;
    padding: 0.85rem 0.95rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: rgba(127, 127, 127, 0.04);
  }

  /* A maxed stat stays fully legible - it is an achievement, not a disabled
     control, and dimming it would read as "broken". */
  .stats li.capped { border-color: var(--diamond, #7dd3fc); }

  .head {
    display: flex;
    align-items: baseline;
    gap: 0.6rem;
  }
  .name { font-weight: 650; }
  .value {
    margin-left: auto;
    font-variant-numeric: tabular-nums;
    font-weight: 650;
  }

  .blurb { margin: 0; }

  .buy { margin-top: 0.15rem; }
  .buy button {
    width: 100%;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 0.35rem;
    font-size: 0.85rem;
    padding: 0.4rem 0.6rem;
  }

  .footer { margin: 1rem 0 0; }
</style>
