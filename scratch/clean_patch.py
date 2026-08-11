import re

with open('client_web/src/routes/GuildOps.svelte', 'r', encoding='utf-8') as f:
    text = f.read()

# Add logic before </script>
logic_to_add = """
  // --- guild treasury ---
  const guildDepot = createQuery(() => ({
    queryKey: queryKeys.guildDepot,
    queryFn: fetchGuildDepot,
    enabled: hasGuild,
  }));

  function refreshDepotFull() {
    setTimeout(() => {
      client.invalidateQueries({ queryKey: queryKeys.guildDepot });
      client.invalidateQueries({ queryKey: queryKeys.inventory });
    }, 700);
  }

  let donateMaterial = $state<string | number>(0);
  let donateQuantity = $state(1);

  const BUFF_MATERIAL_IDS = new Set([
    'birch_log', 'golden_birch_log', 'copper_ore', 'malachite_ore',
    'willow_log', 'golden_willow_log', 'iron_ore', 'hematite_ore',
    'acacia_log', 'golden_acacia_log', 'sulfur_ore', 'obsidian_ore',
    'frostpine_log', 'golden_frostpine_log', 'silver_ore', 'cobalt_ore',
    'ebon_log', 'golden_ebon_log', 'darksteel_ore', 'astralite_ore',
  ]);

  const donateMax = $derived(
    donateMaterial === 'gold'
      ? (snap?.Gold ?? 0)
      : (depositable.find((row: any) => row.baseId === donateMaterial)?.quantity ?? 0),
  );

  async function handleDonate() {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (donateQuantity < 1) return pushLocalNotice('Quantity must be positive.', 'info');
    if (donateMaterial === 0) return;
    
    try {
        await donateToGuildDepot(donateMaterial, Math.min(donateQuantity, donateMax));
        pushLocalNotice('Material donated for Weekly Contribution Points!', 'info');
        refreshDepotFull();
    } catch (e: any) {
        pushLocalNotice(e.message || 'Failed to donate.', 'info');
    }
  }

  let expandedBuff = $state<string | null>(null);

  function toggleBuff(type: string) {
    expandedBuff = expandedBuff === type ? null : type;
  }

  const BUFF_TIERS = [
    { tier: 1, region: 'Sunlit Plains',       commonWood: 'birch_log',       rareWood: 'golden_birch_log',    commonOre: 'copper_ore',    rareOre: 'malachite_ore'  },
    { tier: 2, region: 'Whispering Woods',    commonWood: 'willow_log',      rareWood: 'golden_willow_log',   commonOre: 'iron_ore',      rareOre: 'hematite_ore'   },
    { tier: 3, region: 'Scorched Wasteland',  commonWood: 'acacia_log',      rareWood: 'golden_acacia_log',   commonOre: 'sulfur_ore',    rareOre: 'obsidian_ore'   },
    { tier: 4, region: 'Frozen Peaks',        commonWood: 'frostpine_log',   rareWood: 'golden_frostpine_log',commonOre: 'silver_ore',    rareOre: 'cobalt_ore'     },
    { tier: 5, region: 'Shadow Citadel',      commonWood: 'ebon_log',        rareWood: 'golden_ebon_log',     commonOre: 'darksteel_ore', rareOre: 'astralite_ore'  },
  ];

  const BUFF_TYPES = [
    { type: 'Exp',      label: 'Experience Boost' },
    { type: 'Gold',     label: 'Gold Gain Boost'  },
    { type: 'DropRate', label: 'Drop Rate Boost'  },
    { type: 'Damage',   label: 'Damage Boost'     },
  ];

  const BUFF_COST_PER_MAT = 25_000;

  function getDepotQty(baseId: string): number {
    const depot = guildDepot.data?.DepotByBaseId as Record<string, number> | undefined;
    return depot?.[baseId] ?? 0;
  }

  function canActivateTierPath(tierDef: typeof BUFF_TIERS[0], path: 'common' | 'rare'): boolean {
    const wood = path === 'rare' ? tierDef.rareWood : tierDef.commonWood;
    const ore  = path === 'rare' ? tierDef.rareOre  : tierDef.commonOre;
    return getDepotQty(wood) >= BUFF_COST_PER_MAT && getDepotQty(ore) >= BUFF_COST_PER_MAT;
  }

  async function handleActivateBuff(buffType: string, tier: number, path: 'common' | 'rare') {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (myRole < 1) return pushLocalNotice('Only officers and leaders can activate buffs.', 'info');
    
    try {
        await activateGuildBuff(buffType, tier, path);
        pushLocalNotice(`Buff activated! (Tier ${tier}, ${path})`, 'info');
        refreshDepotFull();
    } catch (e: any) {
        pushLocalNotice(e.message || 'Failed to activate buff.', 'info');
    }
  }
</script>"""

text = text.replace("</script>", logic_to_add)

# Add UI
ui_to_add = """
    <section class="panel">
      <h2>Guild Treasury & Buffs</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to use the treasury.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <div style="display: flex; flex-direction: column; gap: 0.5rem; margin-bottom: 1rem;">
            {#each BUFF_TYPES as buff}
              {@const active = (guildDepot.data.ActiveBuffs ?? []).find(b => b.BuffType === buff.type && b.ExpiresAtEpoch * 1000 > Date.now())}
              {@const isOpen = expandedBuff === buff.type}
              <div class="buff-block">
                <button class="buff-header" onclick={() => toggleBuff(buff.type)}>
                  <span class="buff-title">{buff.label}</span>
                  <span style="display: flex; gap: 0.5rem; align-items: center;">
                    {#if active}
                      <span class="good-text tiny">Active T{active.Tier} until {new Date(active.ExpiresAtEpoch * 1000).toLocaleString()}</span>
                    {:else}
                      <span class="dim tiny">Inactive</span>
                    {/if}
                    <span style="font-size: 0.8rem;">{isOpen ? ' ' : ' '}</span>
                  </span>
                </button>

                {#if isOpen}
                  {#each BUFF_TIERS as td}
                    <div class="buff-tier-row-col">
                      <div style="font-weight: 600; font-size: 0.9rem; padding: 0.2rem 0;">Tier {td.tier}</div>

                      <div class="buff-paths-container">
                        <div class="buff-path-card">
                          <div class="mat-req">
                            <span class="mat-name">{prettifyBaseId(td.commonWood)}</span>
                            <span class="mat-stock" class:mat-ok={getDepotQty(td.commonWood) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.commonWood) < BUFF_COST_PER_MAT}>
                              {getDepotQty(td.commonWood).toLocaleString()} / {BUFF_COST_PER_MAT.toLocaleString()}
                            </span>
                          </div>
                          <div class="mat-req">
                            <span class="mat-name">{prettifyBaseId(td.commonOre)}</span>
                            <span class="mat-stock" class:mat-ok={getDepotQty(td.commonOre) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.commonOre) < BUFF_COST_PER_MAT}>
                              {getDepotQty(td.commonOre).toLocaleString()} / {BUFF_COST_PER_MAT.toLocaleString()}
                            </span>
                          </div>
                          <button
                            class="tiny-btn"
                            style="margin-top: 0.3rem;"
                            disabled={myRole < 1 || !canActivateTierPath(td, 'common')}
                            onclick={() => handleActivateBuff(buff.type, td.tier, 'common')}
                          >Activate (1h)</button>
                        </div>

                        <div class="buff-path-card rare">
                          <div class="mat-req">
                            <span class="mat-name rare-mat">{prettifyBaseId(td.rareWood)}</span>
                            <span class="mat-stock" class:mat-ok={getDepotQty(td.rareWood) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.rareWood) < BUFF_COST_PER_MAT}>
                              {getDepotQty(td.rareWood).toLocaleString()} / {BUFF_COST_PER_MAT.toLocaleString()}
                            </span>
                          </div>
                          <div class="mat-req">
                            <span class="mat-name rare-mat">{prettifyBaseId(td.rareOre)}</span>
                            <span class="mat-stock" class:mat-ok={getDepotQty(td.rareOre) >= BUFF_COST_PER_MAT} class:mat-low={getDepotQty(td.rareOre) < BUFF_COST_PER_MAT}>
                              {getDepotQty(td.rareOre).toLocaleString()} / {BUFF_COST_PER_MAT.toLocaleString()}
                            </span>
                          </div>
                          <button
                            class="tiny-btn rare-btn"
                            style="margin-top: 0.3rem;"
                            disabled={myRole < 1 || !canActivateTierPath(td, 'rare')}
                            onclick={() => handleActivateBuff(buff.type, td.tier, 'rare')}
                          >Activate (9h)</button>
                        </div>
                      </div>
                    </div>
                  {/each}
                {/if}
              </div>
            {/each}
          </div>

          <h3>Weekly Leaderboard</h3>
          <p class="dim tiny">Top 3 contributors receive a cut of the guild's gold at the end of the week.</p>
          {#if guildDepot.data.Leaderboard.length === 0}
            <p class="dim small">No contributions this week.</p>
          {:else}
            <ul class="members" style="margin-bottom: 1rem;">
              {#each guildDepot.data.Leaderboard as member, i}
                <li>
                  <span class="who">
                    #{i + 1} {nameById.get(member.PlayerId) ?? member.PlayerId}
                    {#if member.PlayerId === connection.currentPlayerId}<span class="dim tiny">you</span>{/if}
                  </span>
                  <span class="dim">{member.WeeklyContributionPoints.toLocaleString()} pts</span>
                </li>
              {/each}
            </ul>
          {/if}

          <h3>Donate Materials</h3>
          <label>
            Material
            <select bind:value={donateMaterial}>
              <option value={0}>Choose...</option>
              <option value={'gold'}>Gold ({snap?.Gold.toLocaleString() ?? 0})</option>
              {#each depositable.filter(d => BUFF_MATERIAL_IDS.has(d.baseId)) as row}
                <option value={row.baseId}>
                  {row.baseId} (x{row.quantity})
                </option>
              {/each}
            </select>
          </label>
          <div class="row">
            <input type="number" min="1" max={donateMax || 1} bind:value={donateQuantity} />
            <button disabled={donateMaterial === 0 || donateMax === 0} onclick={handleDonate}>
              Donate
            </button>
          </div>
          <p class="dim tiny">
            Donated materials go to the treasury for buffs. Gold goes directly to Guild Treasury.
          </p>
        {/if}
      {/if}
    </section>

    <section class="panel">
      <h2>Cross-shard war</h2>"""

text = text.replace("""    <section class="panel">
      <h2>Cross-shard war</h2>""", ui_to_add)

css_to_add = '''
  .buff-tier-row-col {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    padding: 0.5rem 0;
    border-bottom: 1px dashed var(--line, rgba(255,255,255,0.07));
  }
  .buff-tier-row-col:last-child {
    border-bottom: none;
  }
  .buff-paths-container {
    display: flex;
    gap: 0.5rem;
  }
  .buff-path-card {
    flex: 1;
    background: color-mix(in srgb, var(--accent) 5%, transparent);
    border: 1px solid var(--border);
    border-radius: 4px;
    padding: 0.5rem;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }
  .buff-path-card.rare {
    background: color-mix(in srgb, #9370DB 5%, transparent);
    border-color: color-mix(in srgb, #9370DB 30%, transparent);
  }
  .buff-block {
    border: 1px solid var(--border);
    border-radius: 4px;
    background: rgba(0,0,0,0.1);
  }
  .buff-header {
    width: 100%;
    text-align: left;
    padding: 0.75rem;
    display: flex;
    justify-content: space-between;
    align-items: center;
    background: none;
    border: none;
    cursor: pointer;
    border-bottom: 1px solid transparent;
  }
  .buff-title {
    font-weight: 600;
  }
  .mat-req {
    display: flex;
    justify-content: space-between;
    font-size: 0.8rem;
  }
  .mat-stock.mat-low {
    color: var(--danger);
  }
  .mat-stock.mat-ok {
    color: var(--good);
  }
'''

if '.buff-tier-row-col' not in text:
    text = text.replace('</style>', css_to_add + '\n</style>')

with open('client_web/src/routes/GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(text)

print('Done applying clean patch')
