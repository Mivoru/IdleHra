<script lang="ts">
  import { onMount } from 'svelte';
  import { loadContent, type ContentRegistry, getArmourFamily } from '../lib/net/content';
  import { locationName, nodeLocation } from '../lib/ui/locations';
  import { professionName, craftingProfessionName, resolveSlotIndex } from '../lib/ui/slots';
  import ItemIcon from '../lib/ui/ItemIcon.svelte';
  import MonsterPortrait from '../lib/ui/MonsterPortrait.svelte';
  import Skeleton from '../lib/ui/Skeleton.svelte';
  import { queryKeys, fetchRecipes } from '../lib/net/rest';
  import { createQuery } from '@tanstack/svelte-query';

  let registry = $state<ContentRegistry | null>(null);
  let activeTab = $state('basics');
  const recipes = createQuery(() => ({ queryKey: queryKeys.recipes, queryFn: fetchRecipes }));

  onMount(async () => {
    registry = await loadContent().catch(() => null);
  });

  const tabs = [
    { id: 'basics', label: 'Basics & Progression' },
    { id: 'combat', label: 'Combat & Stats' },
    { id: 'items', label: 'Items & Tiers' },
    { id: 'map', label: 'Map & Regions' },
    { id: 'gathering', label: 'Gathering & Crafting' },
    { id: 'genetics', label: 'Genetics & Breeding' },
    { id: 'guilds', label: 'Guilds & Social' }
  ];

  const armourFamilies = $derived.by(() => {
    if (!registry) return [];
    const sets = new Set<string>();
    for (const item of registry.items.values()) {
      const fam = getArmourFamily(item.BaseId);
      if (fam) sets.add(fam);
    }
    return Array.from(sets).sort();
  });

  function getItemsByRegion(regionTier: number) {
    if (!registry) return [];
    return Array.from(registry.items.values()).filter(i => i.RegionTier === regionTier);
  }
</script>

<div class="wrap">
  <div class="wiki-layout">
    <aside class="panel sidebar">
      <h2>Wiki</h2>
      <nav class="wiki-nav">
        {#each tabs as tab}
          <button 
            class="tab-btn" 
            class:active={activeTab === tab.id}
            onclick={() => activeTab = tab.id}
          >
            {tab.label}
          </button>
        {/each}
      </nav>
    </aside>

    <main class="panel content">
      {#if !registry}
        <Skeleton rows={10} />
      {:else}
        {#if activeTab === 'basics'}
          <div class="head">
            <h2>Basics & Progression</h2>
            <span class="dim tiny">The core loop</span>
          </div>
          <p>Welcome to FolkIdle! Your goal is to build a thriving village, gather resources, craft powerful gear, and conquer the dangerous regions of the world.</p>
          
          <h3>Town Hall</h3>
          <p class="dim small">The <strong>Town Hall</strong> is the heart of your village. Upgrading it unlocks new features and allows you to manage more characters simultaneously.</p>
          <ul class="styled-list">
            <li><strong>Level 3:</strong> Unlocks your second character slot.</li>
            <li><strong>Level 5:</strong> Unlocks your third character slot.</li>
          </ul>

          <h3>Offline Progression</h3>
          <p class="dim small">FolkIdle respects your time. When you log off, your characters continue their assigned tasks (gathering, fighting, or crafting) and you will receive a detailed summary of your loot and experience upon your return.</p>

        {:else if activeTab === 'combat'}
          <div class="head">
            <h2>Combat & Stats</h2>
            <span class="dim tiny">Fighting and surviving</span>
          </div>
          <p>Combat in FolkIdle is automatic, but your preparation determines victory or defeat.</p>

          <h3>Core Attributes</h3>
          <dl class="stats-list">
            <div><dt>STR (Strength)</dt><dd>Increases Attack Power and Block Strength.</dd></div>
            <div><dt>DEX (Dexterity)</dt><dd>Increases Accuracy and Dodge Rating.</dd></div>
            <div><dt>CON (Constitution)</dt><dd>Increases Maximum Health and Armor.</dd></div>
            <div><dt>LCK (Luck)</dt><dd>Improves Loot Rarity and Drop Rates.</dd></div>
          </dl>

          <h3>Mechanics</h3>
          <ul class="styled-list">
            <li><strong>Lifesteal:</strong> Heals you for a percentage of the damage dealt. Capped at <strong>1% of your maximum health per hit</strong> to balance fast attack speeds.</li>
            <li><strong>Auto-Eat:</strong> The Larder automatically consumes food when your health drops below your set threshold.</li>
          </ul>

          <h3>Armour Set Bonuses</h3>
          <p class="dim small">Wearing multiple pieces of the same armour family grants powerful bonuses at 2, 3, and 5 pieces.</p>
          <div class="badge-list">
            {#each armourFamilies as fam}
              <span class="fam-badge">{fam}</span>
            {/each}
          </div>

        {:else if activeTab === 'items'}
          <div class="head">
            <h2>Items & Tiers</h2>
            <span class="dim tiny">Loot and rarity</span>
          </div>
          <p>Equipment drops from monsters and can be crafted. Every item has a Rarity Tier that determines its power.</p>

          <h3>Rarity Tiers</h3>
          <div class="tier-grid">
            <span class="t-badge t1">T1 Normal</span>
            <span class="t-badge t2">T2 Common</span>
            <span class="t-badge t3">T3 Uncommon</span>
            <span class="t-badge t4">T4 Rare</span>
            <span class="t-badge t5">T5 Ultra Rare</span>
            <span class="t-badge t6">T6 Epic</span>
            <span class="t-badge t7">T7 Legendary</span>
            <span class="t-badge t8">T8 Mythic</span>
            <span class="t-badge t9">T9 Relic</span>
            <span class="t-badge t10">T10 Ancient</span>
            <span class="t-badge t11">T11 Divine</span>
            <span class="t-badge t12">T12 Demonic</span>
            <span class="t-badge t13">T13 Godly</span>
            <span class="t-badge t14">T14 Transcendent</span>
          </div>

          <h3>Drop Rates</h3>
          <p class="dim small">Monsters have a base <strong>15% chance</strong> to drop equipment. Your <strong>Luck (LCK)</strong> stat heavily influences the Rarity Tier of the dropped item. For example, getting a Legendary (T7) drop requires overcoming low probabilities (~0.5% at +16% luck).</p>

        {:else if activeTab === 'map'}
          <div class="head">
            <h2>Map & Regions</h2>
            <span class="dim tiny">Exploration</span>
          </div>
          <p>The world is divided into distinct regions. You must defeat the Boss of a region to unlock the next one.</p>
          
          {#each registry.regions as region, idx}
            <h3>{locationName(idx + 1)}</h3>
            <div class="monster-grid">
              {#each region as monster}
                <div class="monster-card">
                  <MonsterPortrait id={monster.Id} />
                  <div class="monster-info">
                    <strong>{monster.Name}</strong>
                    <span class="dim tiny">HP: {monster.MaxHp.toLocaleString()} | DMG: {monster.AttackPower}</span>
                  </div>
                </div>
              {/each}
            </div>
          {/each}

        {:else if activeTab === 'gathering'}
          <div class="head">
            <h2>Gathering & Crafting</h2>
            <span class="dim tiny">Professions</span>
          </div>
          <p>Not everything is solved with a sword. Gathering resources is essential for village upgrades.</p>

          <h3>Professions</h3>
          <ul class="styled-list">
            <li><strong>Woodcutting:</strong> Yields logs for building and bows.</li>
            <li><strong>Mining:</strong> Yields ores for armor and weapons.</li>
            <li><strong>Fishing:</strong> Yields fish, the primary source of food for healing.</li>
            <li><strong>Herbalism:</strong> Yields herbs for alchemy and boosts.</li>
          </ul>

          <h3>Gathering Nodes</h3>
          <div class="node-grid">
            {#each registry.gatheringNodes as node}
              <div class="node-card">
                <strong>{professionName(node.ProfessionType)}</strong>
                <span class="dim small">{locationName(nodeLocation(node.ActivityId))}</span>
                <span class="dim tiny">Tick: {node.BaseTickThreshold}</span>
              </div>
            {/each}
          </div>

        {:else if activeTab === 'genetics'}
          <div class="head">
            <h2>Genetics & Breeding</h2>
            <span class="dim tiny">Bloodlines</span>
          </div>
          <p>Create the ultimate lineage by passing down traits from generation to generation.</p>
          
          <ul class="styled-list">
            <li><strong>Breeding:</strong> Combine two characters to create an heir. The heir inherits a mix of stats, potential, and visual traits from its parents.</li>
            <li><strong>Ancestors:</strong> Retired characters become Ancestors.</li>
            <li><strong>Inheritance:</strong> Select perks from your Ancestors to permanently buff your current active lineage.</li>
          </ul>

        {:else if activeTab === 'guilds'}
          <div class="head">
            <h2>Guilds & Social</h2>
            <span class="dim tiny">Multiplayer</span>
          </div>
          <p>FolkIdle is better with friends. Join a guild to access shared benefits and compete on the leaderboards.</p>
          
          <h3>Roles</h3>
          <ul class="styled-list">
            <li><strong>Leader:</strong> Can invite, kick, promote, and demote members.</li>
            <li><strong>Officer:</strong> Can invite and kick regular members.</li>
            <li><strong>Member:</strong> Contributes to the guild's overall progress.</li>
          </ul>
        {/if}
      {/if}
    </main>
  </div>
</div>

<style>
  .wiki-layout {
    display: flex;
    gap: 1.5rem;
    align-items: flex-start;
  }

  @media (max-width: 800px) {
    .wiki-layout {
      flex-direction: column;
    }
  }

  .sidebar {
    flex: 0 0 240px;
    position: sticky;
    top: 1rem;
    padding: 1.5rem 1rem;
  }
  
  .sidebar h2 {
    margin: 0 0 1rem 0.5rem;
    font-size: 1.2rem;
  }

  .wiki-nav {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .tab-btn {
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-dim);
    text-align: left;
    padding: 0.75rem 1rem;
    border-radius: var(--radius, 8px);
    cursor: pointer;
    font-size: 0.95rem;
    transition: background 0.2s, color 0.2s;
  }

  .tab-btn:hover {
    background: rgba(255, 255, 255, 0.05);
    color: var(--text-bright);
  }

  .tab-btn.active {
    background: var(--bg-button);
    border: 1px solid var(--border);
    color: var(--text-bright);
    font-weight: 500;
  }

  .content {
    flex: 1;
    min-width: 0;
    padding: 2rem;
  }

  .content h3 {
    margin: 2rem 0 1rem 0;
    color: var(--text-bright);
    font-size: 1.15rem;
  }

  .styled-list {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    padding-left: 1rem;
    color: var(--text-dim);
  }
  
  .styled-list strong {
    color: var(--text-bright);
  }

  .stats-list {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 1rem;
    background: var(--bg-deep);
    padding: 1.5rem;
    border-radius: var(--radius, 8px);
    border: 1px solid var(--border);
  }

  .stats-list dt {
    font-weight: bold;
    color: var(--text-bright);
    margin-bottom: 0.25rem;
  }

  .stats-list dd {
    margin: 0;
    color: var(--text-dim);
    font-size: 0.85rem;
  }

  .tier-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(130px, 1fr));
    gap: 0.5rem;
    margin-top: 1rem;
  }

  .t-badge {
    display: inline-block;
    padding: 0.5rem;
    text-align: center;
    border-radius: var(--radius, 4px);
    font-size: 0.8rem;
    background: var(--bg-deep);
    border: 1px solid var(--border);
    color: var(--text-dim);
    font-family: monospace;
  }
  
  .t-badge.t1, .t-badge.t2, .t-badge.t3 { opacity: 0.7; }
  .t-badge.t4, .t-badge.t5, .t-badge.t6 { opacity: 0.85; }
  .t-badge.t7, .t-badge.t8, .t-badge.t9 { opacity: 1; color: var(--text-bright); }
  .t-badge.t10, .t-badge.t11, .t-badge.t12 { opacity: 1; color: var(--text-bright); border-color: rgba(255, 255, 255, 0.3); }
  .t-badge.t13, .t-badge.t14 { opacity: 1; color: var(--good); border-color: var(--good); }

  .monster-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
    gap: 1rem;
  }

  .monster-card {
    display: flex;
    align-items: center;
    gap: 1rem;
    background: var(--bg-deep);
    padding: 0.75rem;
    border-radius: var(--radius, 8px);
    border: 1px solid var(--border);
  }

  .monster-info {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
  }

  .node-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
    gap: 1rem;
  }

  .node-card {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    background: var(--bg-deep);
    padding: 1rem;
    border-radius: var(--radius, 8px);
    border: 1px solid var(--border);
  }
  
  .badge-list {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }
  
  .fam-badge {
    padding: 0.25rem 0.75rem;
    border-radius: 999px;
    background: var(--bg-button);
    border: 1px solid var(--border);
    font-size: 0.85rem;
    text-transform: capitalize;
  }
</style>
