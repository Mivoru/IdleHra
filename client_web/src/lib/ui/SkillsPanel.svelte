<script lang="ts">
  // Modul: THE SKILL TREE. Five passive branches, bought with the points an
  // account earns one per level.
  //
  // This panel used to drive four ACTIVE skills - buttons with cooldowns and a
  // mana bar. They were removed after being measured: mana refilled faster than
  // the cooldowns cleared, so at a 1.5 second swing nearly every hit could be
  // buffed, which came to +90% damage and +136% with the status synergy. All of
  // it available only to a player willing to click every three seconds, in a
  // game whose premise is that you do not have to.
  //
  // What a branch is worth is stated on its card, because a passive bonus the
  // player cannot see is indistinguishable from one that does not work - a
  // mistake this project has made more than once.
  import { playerState, pushLocalNotice } from '../stores/game';
  import {
    SKILL_TREE_BRANCHES,
    SKILL_TREE_MAX_LEVEL,
    skillTreeUpgradeCost,
    purchaseSkillTreeLevel,
  } from '../net/commands';
  import Bar from './Bar.svelte';

  const snap = $derived($playerState);
  const points = $derived(snap?.AvailableSkillPoints ?? 0);

  // The wire carries one byte per branch, indexed by the same ids the server's
  // SkillTreeRegistry uses, so the two cannot drift on ordering.
  function levelOf(branchId: number): number {
    if (!snap) return 0;
    switch (branchId) {
      case 0: return snap.SkillTree_LootRarity;
      case 1: return snap.SkillTree_WorldBossDamage;
      case 2: return snap.SkillTree_CritChance;
      case 3: return snap.SkillTree_CritDamage;
      case 4: return snap.SkillTree_XpGain;
      default: return 0;
    }
  }

  const rows = $derived(
    SKILL_TREE_BRANCHES.map((branch) => {
      const level = levelOf(branch.id);
      const cost = skillTreeUpgradeCost(level);
      const total = level * branch.perLevel;
      return {
        ...branch,
        level,
        cost,
        capped: level >= SKILL_TREE_MAX_LEVEL,
        affordable: cost > 0 && points >= cost,
        // Crit chance is percentage POINTS; the others are percentages of
        // their own quantity. Writing "+8%" for both would be wrong for one.
        label: branch.unit === 'points' ? `+${total.toFixed(1)} pts` : `+${total.toFixed(1)}%`,
      };
    }),
  );

  const spent = $derived(
    rows.reduce((sum, row) => {
      let total = 0;
      for (let i = 0; i < row.level; i++) total += skillTreeUpgradeCost(i);
      return sum + total;
    }, 0),
  );

  function buy(branchId: number, level: number) {
    const outcome = purchaseSkillTreeLevel(branchId, level, points);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }
</script>

<section class="panel">
  <header>
    <h3>Skill tree</h3>
    <span class="dim tiny">{points} point{points === 1 ? '' : 's'} unspent</span>
  </header>

  <p class="dim small">
    One point per level. Five branches, twenty levels each, and the price rises
    every fifth level - so a season buys two branches deep or five shallow.
    These reset when the season does.
  </p>

  {#if !snap}
    <p class="dim">Waiting for your state to arrive...</p>
  {:else}
    <ul class="branches">
      {#each rows as row (row.id)}
        <li class:capped={row.capped}>
          <div class="head">
            <span class="name">{row.name}</span>
            <span class="value">
              {#if row.level > 0}{row.label}{:else}<span class="dim">not taken</span>{/if}
            </span>
          </div>

          <p class="blurb dim tiny">{row.blurb}</p>

          <Bar
            value={row.level}
            max={SKILL_TREE_MAX_LEVEL}
            color="var(--accent)"
            label={`${row.level} / ${SKILL_TREE_MAX_LEVEL}`}
          />

          <div class="buy">
            {#if row.capped}
              <span class="dim tiny">At maximum.</span>
            {:else}
              <button
                disabled={!row.affordable}
                title={row.affordable ? '' : `Needs ${row.cost} point${row.cost === 1 ? '' : 's'}`}
                onclick={() => buy(row.id, row.level)}
              >
                +{row.perLevel}{row.unit === 'points' ? ' pts' : '%'} for {row.cost}
                point{row.cost === 1 ? '' : 's'}
              </button>
            {/if}
          </div>
        </li>
      {/each}
    </ul>

    {#if spent > 0}
      <p class="dim tiny footer">{spent} points spent this season.</p>
    {/if}
  {/if}
</section>

<style>
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
  header h3 { margin: 0; }
  header .dim { margin-left: auto; }

  .small { font-size: 0.9rem; max-width: 46rem; }
  .tiny  { font-size: 0.8rem; }
  .dim   { opacity: 0.75; }

  .branches {
    list-style: none;
    margin: 1rem 0 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
    gap: 0.9rem;
  }

  .branches li {
    display: grid;
    gap: 0.45rem;
    padding: 0.85rem 0.95rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: rgba(127, 127, 127, 0.04);
  }

  /* A maxed branch stays fully legible - it is an achievement, not a disabled
     control, and dimming it would read as "broken". */
  .branches li.capped { border-color: var(--accent); }

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
    font-size: 0.85rem;
    padding: 0.4rem 0.6rem;
  }

  .footer { margin: 1rem 0 0; }
</style>
