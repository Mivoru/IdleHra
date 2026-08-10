<script lang="ts">
  import { rarityName, rarityColor } from './rarity';

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
  <div class="calculator">
    <label>
      <strong>Your LCK (Luck) Stat:</strong>
      <input type="number" bind:value={playerLuck} min="0" max="1000" />
    </label>
    <p class="dim small">
      Monsters have a base <strong>15% chance</strong> to drop an equipment piece on kill (Bosses roll twice).
      Your Luck stat multiplies the weights of higher rarities, reducing the proportion of Normal items.
    </p>
  </div>

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

<style>
  .drop-chances {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    background: var(--bg-deep);
    padding: 1.5rem;
    border-radius: var(--radius, 8px);
    border: 1px solid var(--border);
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

  .chance-table {
    width: 100%;
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
</style>
