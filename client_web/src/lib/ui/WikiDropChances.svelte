<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { rarityName, rarityColor } from './rarity';
  import { queryKeys, fetchLootOdds } from '../net/rest';

  // Modul: THE ODDS LINE (task 26). "No Ancient in five days - is something
  // broken?" is a question about a number the player had no way to see. The
  // server quotes the luck and elevation its loot worker last rolled with and
  // what they come to, so this line is the roll's own arithmetic, not a copy.
  const odds = createQuery(() => ({ queryKey: queryKeys.lootOdds, queryFn: fetchLootOdds }));

  function oneIn(share: number): string {
    return share > 0 ? Math.round(1 / share).toLocaleString() : '—';
  }

  // Base weights matching FolkIdle.Server/Engine/CombatLootEngine.cs
  const EXPLICIT_WEIGHTS = [
    0.0,    // unused
    0.0,    // Normal - remainder, computed in RollTier
    50.0,   // Common
    25.0,   // Uncommon
    12.5,   // Rare
    5.0,    // Ultra Rare
    2.5,    // Epic
    1.0,    // Legendary
    0.5,    // Mythic
    0.1,    // Relic
    0.05,   // Ancient
    0.01,   // Divine
    0.005,  // Demonic
    0.001,  // Godly
    0.0001  // Transcendent
  ];

  const NORMAL_BASE_WEIGHT = 100.0;

  let playerLuck = $state(0);
  const equipmentDropChance = 0.15; // 15%

  const chances = $derived.by(() => {
    let luckFactor = 1.0 + (playerLuck / 100.0);
    let effectiveWeights = new Array(15).fill(0);
    effectiveWeights[1] = NORMAL_BASE_WEIGHT;
    let totalWeight = NORMAL_BASE_WEIGHT;

    for (let tier = 2; tier <= 14; tier++) {
      let weight = EXPLICIT_WEIGHTS[tier] * luckFactor;
      effectiveWeights[tier] = weight;
      totalWeight += weight;
    }

    let results = [];
    for (let tier = 1; tier <= 14; tier++) {
      let relativeChance = effectiveWeights[tier] / totalWeight;
      let absoluteChance = relativeChance * equipmentDropChance;
      results.push({
        tier,
        name: rarityName(tier),
        color: rarityColor(tier),
        relativePct: (relativeChance * 100).toFixed(4),
        absolutePct: (absoluteChance * 100).toFixed(4)
      });
    }
    return results;
  });
</script>

<div class="drop-chances">
  <p class="odds-line" data-testid="loot-odds-line">
    {#if odds.isPending}
      Working out your odds…
    {:else if odds.isError || !odds.data}
      Your odds could not be loaded.
    {:else if !odds.data.Known}
      Your odds appear here after your next kill.
    {:else}
      <strong>Your odds:</strong> about 1 drop in {oneIn(odds.data.LegendaryPlusPerDrop)} is
      Legendary or better, and 1 in {oneIn(odds.data.AncientPlusPerDrop)} is Ancient or better
      ({odds.data.LootLuckPct.toFixed(1)}% loot luck, {odds.data.RarityElevationPct.toFixed(1)}%
      rarity elevation{odds.data.HasGoldenFleece ? ', Golden Fleece' : ''}). Gear drops on
      {Math.round(odds.data.EquipmentDropChance * 100)}% of kills, so Ancient+ is roughly one kill in
      {oneIn(odds.data.AncientPlusPerDrop * odds.data.EquipmentDropChance)}. Long gaps are normal:
      these are averages, not a schedule.
    {/if}
  </p>

  <div class="calculator">
    <label>
      <strong>Loot luck (%):</strong>
      <input type="number" bind:value={playerLuck} min="0" max="1000" />
    </label>
    <p class="dim small">
      Monsters have a base <strong>15% chance</strong> to drop an equipment piece on kill (Bosses roll twice).
      Loot luck multiplies the weights of every rarity above Normal by the same factor, so it
      shrinks Normal's share and can never much more than double the top. This table is the
      roll alone - rarity elevation and Golden Fleece lift a drop afterwards, and the line
      above includes them.
    </p>
  </div>

  <div class="scroll">
  <table class="chance-table">
    <thead>
      <tr>
        <th>Rarity Tier</th>
        <th class="num">Chance (if equipment drops)</th>
        <th class="num">Absolute Chance (per kill)</th>
      </tr>
    </thead>
    <tbody>
      {#each chances as c}
        <tr>
          <td>
            <span class="badge" style="border-color: {c.color}; color: {c.color}">
              T{c.tier} {c.name}
            </span>
          </td>
          <td class="num">{c.relativePct}%</td>
          <td class="num">{c.absolutePct}%</td>
        </tr>
      {/each}
    </tbody>
  </table>
  </div>
</div>

<style>
  .drop-chances {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    background: rgba(0, 0, 0, 0.12);
    padding: 1.5rem;
    border-radius: var(--radius, 8px);
    border: 1px solid var(--border);
  }

  .odds-line {
    margin: 0;
    font-size: 0.9rem;
  }

  .calculator {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .calculator input {
    width: 100px;
    margin-left: 0.5rem;
  }

  /* Modul: the table is three dense numeric columns and cannot be made to
     wrap, so it scrolls inside its own box rather than pushing the panel
     sideways at a narrow CONTAINER width. */
  .scroll {
    overflow-x: auto;
  }

  .chance-table {
    width: 100%;
    min-width: 24rem;
    border-collapse: collapse;
    font-size: 0.9rem;
  }

  .chance-table th {
    text-align: left;
    padding: 0.5rem;
    border-bottom: 2px solid var(--border);
    color: var(--text-dim);
  }

  .chance-table td {
    padding: 0.5rem;
    border-bottom: 1px solid rgba(255,255,255,0.05);
  }

  .chance-table .num {
    text-align: right;
    font-variant-numeric: tabular-nums;
  }

  .badge {
    display: inline-block;
    padding: 0.15rem 0.4rem;
    border: 1px solid;
    border-radius: 4px;
    font-weight: 600;
    font-size: 0.8rem;
    background: rgba(0,0,0,0.2);
  }
  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.85rem;
  }
</style>
