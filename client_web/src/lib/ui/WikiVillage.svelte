<script lang="ts">
  // Modul: the village page's numbers, GENERATED rather than typed.
  //
  // Every row below is computed from the same two functions the Village screen
  // itself prices upgrades with (villageGoldCost / villageMaterialCost, which
  // tests/serverMirrors.test.ts already holds against the C#) and the tier
  // table in wikiData, which tests/wiki.test.ts holds against
  // VillageManagementEngine.TierMaterials.
  //
  // Deliberately NOT `villageCostLabel` from commands.ts: that helper names the
  // RARE ore for tiers 1, 2 and 4, where the server charges the common one. Its
  // comment says it was fixed; a second pass the same day moved the server back
  // and the label did not follow. Reusing it here would have spread one wrong
  // answer to a second screen.

  import ItemIcon from './ItemIcon.svelte';
  import { prettifyBaseId } from '../net/content';
  import {
    VILLAGE_BUILDINGS,
    VILLAGE_TIER_MATERIALS,
    villageCostRow,
    townHallCeiling,
    townHallGoldPerHour,
    STRUCTURAL_MAX_LEVEL,
    type VillageCostKind,
  } from './wikiData';

  let costKind = $state<VillageCostKind>('service');

  const LEVELS = 20;

  const rows = $derived(
    Array.from({ length: LEVELS }, (_, level) => villageCostRow(costKind, level)),
  );

  const ceilingRows = Array.from({ length: STRUCTURAL_MAX_LEVEL + 1 }, (_, level) => ({
    level,
    ceiling: townHallCeiling(level),
    gold: townHallGoldPerHour(level),
    slots: level >= 5 ? 3 : level >= 3 ? 2 : 1,
  }));

  function duration(seconds: number): string {
    if (seconds < 60) return `${seconds}s`;
    if (seconds < 3600) return `${Math.round(seconds / 60)}m`;
    return `${(seconds / 3600).toFixed(1)}h`;
  }
</script>

<h3 id="townhall">The Town Hall ceiling</h3>
<p class="dim small">
  The Town Hall is the spine of the village: nothing else may exceed
  <strong>2 + 2 per Town Hall level</strong>, so a village with no Town Hall
  cannot take anything past level 2. It caps at {STRUCTURAL_MAX_LEVEL}.
</p>

<div class="scroll">
  <table>
    <thead>
      <tr>
        <th>Town Hall</th>
        <th class="num">Other buildings may reach</th>
        <th class="num">Character slots</th>
        <th class="num">Passive gold</th>
      </tr>
    </thead>
    <tbody>
      {#each ceilingRows as row}
        <tr>
          <td>Level {row.level}</td>
          <td class="num">{row.ceiling}</td>
          <td class="num">{row.slots}</td>
          <td class="num">{row.gold.toLocaleString()}/h</td>
        </tr>
      {/each}
    </tbody>
  </table>
</div>

<p class="dim tiny">
  Extra character slots hang off the Town Hall rather than off your level,
  because reaching level 30 was a pure function of leaving combat running - it
  rewarded no decision. A Town Hall can only be raised by gathering, which is
  what the extra characters exist to do more of.
</p>

<h3 id="buildings">What each building actually does</h3>
<div class="cards">
  {#each VILLAGE_BUILDINGS as building (building.id)}
    <div class="card">
      <div class="card-head">
        <strong>{building.name}</strong>
        <span class="pill {building.costKind}">{building.costKind}</span>
        {#if building.cap}<span class="pill cap">caps at {building.cap}</span>{/if}
      </div>
      <p class="dim small">{building.effect}</p>
    </div>
  {/each}
</div>

<h3 id="materials">Tier materials</h3>
<p class="dim small">
  Every upgrade is paid in logs and ore, and <em>which</em> log and ore depends
  on the level the building is leaving - one pair per region, common and rare.
  This is the same pairing the gathering loot tables and the guild's buff tiers
  use, so nothing in the game asks for a material nothing produces.
</p>

<div class="scroll">
  <table>
    <thead>
      <tr>
        <th>Building levels</th>
        <th>Log</th>
        <th>Ore</th>
        <th>Rare log</th>
        <th>Rare ore</th>
      </tr>
    </thead>
    <tbody>
      {#each VILLAGE_TIER_MATERIALS as tier}
        <tr>
          <td>{tier.levels}</td>
          <td><span class="mat"><ItemIcon baseItemId={tier.log} name={prettifyBaseId(tier.log)} size="sm" />{prettifyBaseId(tier.log)}</span></td>
          <td><span class="mat"><ItemIcon baseItemId={tier.ore} name={prettifyBaseId(tier.ore)} size="sm" />{prettifyBaseId(tier.ore)}</span></td>
          <td><span class="mat"><ItemIcon baseItemId={tier.rareLog} name={prettifyBaseId(tier.rareLog)} size="sm" />{prettifyBaseId(tier.rareLog)}</span></td>
          <td><span class="mat"><ItemIcon baseItemId={tier.rareOre} name={prettifyBaseId(tier.rareOre)} size="sm" />{prettifyBaseId(tier.rareOre)}</span></td>
        </tr>
      {/each}
    </tbody>
  </table>
</div>

<p class="dim tiny">
  The ores are drawn as ingots. That is deliberate - there is no smelting step
  in this game, so the nugget and the bar are the same item.
</p>

<h3 id="costs">Upgrade costs, level by level</h3>
<div class="picker" role="group" aria-label="Building kind">
  {#each [['service', 'Service (Forge, Inn, Breeding)'], ['production', 'Production (Lumberjack, Mine, Warehouse)'], ['structural', 'Structural (Town Hall, Workshop)']] as [kind, label]}
    <button
      type="button"
      class:active={costKind === kind}
      onclick={() => (costKind = kind as VillageCostKind)}
    >
      {label}
    </button>
  {/each}
</div>

<p class="dim small">
  {#if costKind === 'structural'}
    Structural buildings pay no gold - they are the ones the whole village is
    gated behind, and doubling their price would deepen the wall. The Crafting
    Workshop additionally wants a tenth of the material cost in rare logs.
  {:else}
    Both gold and materials, every level. Gold is the thing players end up with
    too much of; the materials are the thing they have to go and get.
  {/if}
  The material price <strong>resets every five levels</strong>, when the tier
  moves up to a harder log and ore - it is not a bargain, it is a new material.
</p>

<div class="scroll">
  <table>
    <thead>
      <tr>
        <th>Level</th>
        {#if costKind !== 'structural'}<th class="num">Gold</th>{/if}
        <th class="num">Logs</th>
        <th class="num">Ore</th>
        {#if costKind === 'structural'}<th class="num">Rare logs</th>{/if}
        <th class="num">Build time</th>
      </tr>
    </thead>
    <tbody>
      {#each rows as row, level}
        <tr class:tier-start={level % 5 === 0}>
          <td>{row.level}</td>
          {#if costKind !== 'structural'}<td class="num">{row.gold.toLocaleString()}</td>{/if}
          <td class="num">{row.materials.toLocaleString()} <span class="dim tiny">{prettifyBaseId(row.log)}</span></td>
          <td class="num">{row.materials.toLocaleString()} <span class="dim tiny">{prettifyBaseId(row.ore)}</span></td>
          {#if costKind === 'structural'}
            <td class="num">{row.rareLog.toLocaleString()} <span class="dim tiny">{prettifyBaseId(row.rareLogId)}</span></td>
          {/if}
          <td class="num">{duration(row.seconds)}</td>
        </tr>
      {/each}
    </tbody>
  </table>
</div>

<p class="dim tiny">
  Worked example: taking a fresh Town Hall to level 3 - the second character
  slot - costs 100 + 150 + 225 = 475 Birch Logs and the same again in Copper
  Ore, no gold at all, and ninety seconds of building time - each of those
  three levels is under the thirty-second floor, so each takes exactly it. The
  same three levels of the Forge additionally cost 500 + 700 + 980 = 2,180 gold.
</p>

<style>
  h3 {
    margin: 2rem 0 0.75rem;
    color: var(--text);
    font-size: 1.1rem;
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
    min-width: 24rem;
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
    vertical-align: middle;
  }

  tbody tr:last-child td {
    border-bottom: none;
  }

  .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }

  .tier-start td {
    border-top: 1px solid var(--brass, var(--border));
  }

  .mat {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    white-space: nowrap;
  }

  .cards {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr));
    gap: 0.75rem;
  }

  .card {
    border: 1px solid var(--border);
    border-radius: var(--radius, 8px);
    padding: 0.75rem;
    background: rgba(0, 0, 0, 0.12);
    min-width: 0;
  }

  .card-head {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.35rem;
    margin-bottom: 0.4rem;
  }

  .pill {
    font-size: 0.65rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    border: 1px solid var(--border);
    border-radius: 999px;
    padding: 0.05rem 0.45rem;
    color: var(--text-dim);
  }

  .pill.structural {
    border-color: var(--brass, var(--border));
    color: var(--accent);
  }

  .picker {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin-bottom: 0.75rem;
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

  p {
    margin: 0.6rem 0;
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
