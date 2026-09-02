<script lang="ts">
  // Modul: the guild buff tiers, from the same table the server charges
  // against - GuildContributionEngine.BuffTierMaterials, mirrored in wikiData
  // and guarded by tests/wiki.test.ts.

  import ItemIcon from './ItemIcon.svelte';
  import { prettifyBaseId } from '../net/content';
  import {
    GUILD_BUFF_TIERS,
    GUILD_BUFF_TYPES,
    GUILD_BUFF_COST_PER_MATERIAL,
  } from './wikiData';

  let path = $state<'common' | 'rare'>('common');

  const rows = $derived(
    GUILD_BUFF_TIERS.map((tier) => ({
      tier: tier.tier,
      region: tier.region,
      wood: path === 'rare' ? tier.rareWood : tier.commonWood,
      ore: path === 'rare' ? tier.rareOre : tier.commonOre,
      hours: path === 'rare' ? 9 : 1,
      pct: tier.tier * 2,
    })),
  );
</script>

<div class="buffs">
  <div class="picker" role="group" aria-label="Buff path">
    <button type="button" class:active={path === 'common'} onclick={() => (path = 'common')}>
      Common path — 1 hour
    </button>
    <button type="button" class:active={path === 'rare'} onclick={() => (path = 'rare')}>
      Rare path — 9 hours
    </button>
  </div>

  <p class="dim small">
    An officer or the leader spends
    <strong>{GUILD_BUFF_COST_PER_MATERIAL.toLocaleString()}</strong> of the wood
    <em>and</em> {GUILD_BUFF_COST_PER_MATERIAL.toLocaleString()} of the ore out
    of the guild depot — {(GUILD_BUFF_COST_PER_MATERIAL * 2).toLocaleString()} materials
    a buff. The two paths cost the same; the rare one lasts nine times as long,
    which is the whole reason to hoard rare drops in the depot rather than sell
    them. Ordinary members can donate but cannot activate.
  </p>

  <div class="scroll">
    <table>
      <thead>
        <tr>
          <th>Tier</th>
          <th>Region</th>
          <th>Wood</th>
          <th>Ore</th>
          <th class="num">Strength</th>
          <th class="num">Lasts</th>
        </tr>
      </thead>
      <tbody>
        {#each rows as row (row.tier)}
          <tr>
            <td>T{row.tier}</td>
            <td class="dim">{row.region}</td>
            <td>
              <span class="mat">
                <ItemIcon baseItemId={row.wood} name={prettifyBaseId(row.wood)} size="sm" />
                {prettifyBaseId(row.wood)}
              </span>
            </td>
            <td>
              <span class="mat">
                <ItemIcon baseItemId={row.ore} name={prettifyBaseId(row.ore)} size="sm" />
                {prettifyBaseId(row.ore)}
              </span>
            </td>
            <td class="num">+{row.pct}%</td>
            <td class="num">{row.hours}h</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>

  <h4>The four buffs</h4>
  <ul>
    {#each GUILD_BUFF_TYPES as buff (buff.type)}
      <li><strong>{buff.label}</strong> — {buff.what}</li>
    {/each}
  </ul>
  <p class="dim tiny">
    Only one buff of each type runs at a time, and activating again replaces or
    extends the one already up. A tier 5 rare buff is +10% for nine hours to
    every member online — worth more than any single player can produce, which
    is what a guild is for.
  </p>
</div>

<style>
  .buffs {
    display: flex;
    flex-direction: column;
    gap: 0.6rem;
    min-width: 0;
  }

  .picker {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
  }

  .picker button {
    background: transparent;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    color: var(--text-dim);
    padding: 0.35rem 0.7rem;
    font-size: 0.8rem;
    cursor: pointer;
  }

  .picker button.active {
    color: var(--text);
    border-color: var(--accent);
  }

  .scroll {
    overflow-x: auto;
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    background: rgba(0, 0, 0, 0.12);
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.85rem;
    min-width: 28rem;
  }

  th {
    text-align: left;
    padding: 0.5rem 0.6rem;
    border-bottom: 1px solid var(--border);
    color: var(--text-dim);
    font-weight: 600;
    white-space: nowrap;
  }

  td {
    padding: 0.4rem 0.6rem;
    border-bottom: 1px solid rgba(128, 128, 128, 0.12);
  }

  tbody tr:last-child td {
    border-bottom: none;
  }

  .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .mat {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
  }

  h4 {
    margin: 0.6rem 0 0.2rem;
    font-size: 0.95rem;
  }

  ul {
    margin: 0;
    padding-left: 1.1rem;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    color: var(--text-dim);
    font-size: 0.85rem;
  }

  ul strong {
    color: var(--text);
  }

  p {
    margin: 0;
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
  .tiny {
    font-size: 0.72rem;
  }
</style>
