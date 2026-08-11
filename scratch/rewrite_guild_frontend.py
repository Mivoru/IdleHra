
# This script rewrites the Guild Treasury & Buffs section and Guild Contributions section in GuildOps.svelte

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

# ============================================================
# 1. Replace handleActivateBuff function + BUFF_TYPES constant
# ============================================================
old_activate = """  async function handleActivateBuff(buffType: string) {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    
    try {
        await activateGuildBuff(buffType);
        pushLocalNotice(buffType + ' buff activated!', 'info');
        refreshDepotFull();
    } catch (e: any) {
        pushLocalNotice(e.message || 'Failed to activate buff.', 'info');
    }
  }

  const BUFF_TYPES = [
    { type: 'Exp', label: 'Experience Boost' },
    { type: 'Gold', label: 'Gold Gain Boost' },
    { type: 'DropRate', label: 'Drop Rate Boost' },
    { type: 'Damage', label: 'Damage Boost' },
  ];"""

new_activate = """  // Buff tier definitions: [commonWood, rareWood, commonOre, rareOre] per tier
  const BUFF_TIERS = [
    { tier: 1, region: 'Sunlit Plains',       commonWood: 'birch_log',       rareWood: 'golden_birch_log',    commonOre: 'copper_ore',    rareOre: 'malachite_ore'  },
    { tier: 2, region: 'Whispering Woods',    commonWood: 'willow_log',      rareWood: 'golden_willow_log',   commonOre: 'iron_ore',      rareOre: 'hematite_ore'   },
    { tier: 3, region: 'Scorched Wasteland',  commonWood: 'acacia_log',      rareWood: 'golden_acacia_log',   commonOre: 'sulfur_ore',    rareOre: 'obsidian_ore'   },
    { tier: 4, region: 'Frozen Peaks',        commonWood: 'frostpine_log',   rareWood: 'golden_frostpine_log',commonOre: 'silver_ore',    rareOre: 'cobalt_ore'     },
    { tier: 5, region: 'Shadow Citadel',      commonWood: 'ebon_log',        rareWood: 'golden_ebon_log',     commonOre: 'darksteel_ore', rareOre: 'astralite_ore'  },
  ];

  const BUFF_TYPES = [
    { type: 'Exp',      label: 'Experience Boost', icon: '✨' },
    { type: 'Gold',     label: 'Gold Gain Boost',  icon: '🪙' },
    { type: 'DropRate', label: 'Drop Rate Boost',  icon: '🎁' },
    { type: 'Damage',   label: 'Damage Boost',     icon: '⚔️' },
  ];

  const BUFF_COST_PER_MAT = 25_000; // 25k wood + 25k ore = 50k total

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
  }"""

content = content.replace(old_activate, new_activate)

# ============================================================
# 2. Fix isDonatableMaterial to only show buff materials
# ============================================================
old_is_donatable = """  function isDonatableMaterial(baseId: string): boolean {
    const valid = ['birch', 'willow', 'acacia', 'frostpine', 'ebon', 'copper', 'iron', 'cobalt', 'silver', 'darksteel', 'malachite', 'hematite', 'sulfur', 'obsidian', 'absidian', 'astralite'];
    if (baseId.includes('magic_essence')) return true;
    if (baseId.startsWith('mat_')) return false;
    if (baseId.startsWith('eq_')) return false;
    if (baseId.includes('axe') || baseId.includes('rod')) return false;
    
    return valid.some(v => baseId.includes(v)) && (baseId.includes('log') || baseId.includes('twig') || baseId.includes('ore') || baseId.includes('bar') || baseId.includes('chunk') || baseId.includes('crystal'));
  }"""

new_is_donatable = """  // Only allow buff-related materials: logs and ores from the 5 regions
  const BUFF_MATERIAL_IDS = new Set([
    'birch_log', 'golden_birch_log', 'copper_ore', 'malachite_ore',
    'willow_log', 'golden_willow_log', 'iron_ore', 'hematite_ore',
    'acacia_log', 'golden_acacia_log', 'sulfur_ore', 'obsidian_ore',
    'frostpine_log', 'golden_frostpine_log', 'silver_ore', 'cobalt_ore',
    'ebon_log', 'golden_ebon_log', 'darksteel_ore', 'astralite_ore',
  ]);

  function isDonatableMaterial(baseId: string): boolean {
    return BUFF_MATERIAL_IDS.has(baseId);
  }"""

content = content.replace(old_is_donatable, new_is_donatable)

# ============================================================
# 3. Replace Guild Treasury & Buffs panel HTML
# ============================================================
old_treasury_panel = """    <section class="panel">
      <h2>Guild Treasury & Buffs</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to use the treasury.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <div style="margin-bottom: 1rem; font-size: 1.2rem;">
            <Money amount={guildDepot.data.GuildGold ?? 0} icon />
          </div>
          <div style="display: flex; gap: 1rem; flex-wrap: wrap; margin-bottom: 1rem;">
            {#each BUFF_TYPES as buff}
              {@const active = (guildDepot.data.ActiveBuffs ?? []).find(b => b.BuffType === buff.type)}
              <div style="border: 1px solid var(--border); padding: 0.5rem; border-radius: 4px; flex: 1; min-width: 140px;">
                <div style="font-weight: bold; margin-bottom: 0.2rem;">{buff.label}</div>
                {#if active}
                  <div class="good-text tiny">Active until: {new Date(active.ExpiresAtEpoch * 1000).toLocaleString()}</div>
                {:else}
                  <div class="dim tiny" style="margin-bottom: 0.5rem;">Inactive</div>
                  <button class="tiny-btn" disabled={myRole < 1} onclick={() => handleActivateBuff(buff.type)}>Activate (50k)</button>
                {/if}
              </div>
            {/each}
          </div>

        {/if}
      {/if}
    </section>"""

new_treasury_panel = """    <section class="panel">
      <h2>Guild Treasury & Buffs</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to use the treasury.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <div style="margin-bottom: 0.5rem; font-size: 1.2rem;">
            <Money amount={guildDepot.data.GuildGold ?? 0} icon />
          </div>

          <!-- Active buffs summary -->
          {#if (guildDepot.data.ActiveBuffs ?? []).filter(b => b.ExpiresAtEpoch * 1000 > Date.now()).length > 0}
            <div class="active-buffs-bar">
              {#each (guildDepot.data.ActiveBuffs ?? []).filter(b => b.ExpiresAtEpoch * 1000 > Date.now()) as ab}
                {@const buffInfo = BUFF_TYPES.find(b => b.type === ab.BuffType)}
                <span class="active-buff-chip">
                  {buffInfo?.icon ?? '⚡'} {buffInfo?.label ?? ab.BuffType} T{ab.Tier}
                  <span class="dim tiny">→ {new Date(ab.ExpiresAtEpoch * 1000).toLocaleTimeString()}</span>
                </span>
              {/each}
            </div>
          {/if}

          {#each BUFF_TYPES as buff}
            {@const active = (guildDepot.data.ActiveBuffs ?? []).find(b => b.BuffType === buff.type && b.ExpiresAtEpoch * 1000 > Date.now())}
            <div class="buff-block">
              <div class="buff-header">
                <span class="buff-title">{buff.icon} {buff.label}</span>
                {#if active}
                  <span class="good-text tiny">Active T{active.Tier} until {new Date(active.ExpiresAtEpoch * 1000).toLocaleString()}</span>
                {:else}
                  <span class="dim tiny">Inactive</span>
                {/if}
              </div>

              {#each BUFF_TIERS as td}
                <div class="buff-tier-row">
                  <span class="tier-label">T{td.tier} <span class="dim tiny">{td.region}</span></span>

                  <!-- Common path -->
                  <div class="buff-path">
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
                      disabled={myRole < 1 || !canActivateTierPath(td, 'common')}
                      onclick={() => handleActivateBuff(buff.type, td.tier, 'common')}
                      title="1 hour"
                    >Activate (1h)</button>
                  </div>

                  <!-- Rare path -->
                  <div class="buff-path rare">
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
                      disabled={myRole < 1 || !canActivateTierPath(td, 'rare')}
                      onclick={() => handleActivateBuff(buff.type, td.tier, 'rare')}
                      title="9 hours"
                    >Activate (9h)</button>
                  </div>
                </div>
              {/each}
            </div>
          {/each}
        {/if}
      {/if}
    </section>"""

content = content.replace(old_treasury_panel, new_treasury_panel)

# ============================================================
# 4. Replace Guild Contributions -> Guild Contributors
#    Remove Gold from donate dropdown
#    Add prize info
# ============================================================
old_contributions = """    <section class="panel">
      <h2>Guild Contributions</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to use the contributions.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <h3>Weekly Leaderboard</h3>
          <p class="dim tiny">Top 3 contributors receive a cut of the guild's gold at the end of the week.</p>
          {#if (guildDepot.data.Leaderboard ?? []).length === 0}
            <p class="dim small">No contributions this week.</p>
          {:else}
            <ul class="members" style="margin-bottom: 1rem;">
              {#each (guildDepot.data.Leaderboard ?? []) as member, i}
                <li>
                  <span class="who">
                    #{i + 1} {member.Name}
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
              <option value="gold">Gold</option>
              {#each depositable.filter(r => isDonatableMaterial(r.baseId)) as row}
                <option value={row.baseId}>
                  {prettifyBaseId(row.baseId)} (x{row.quantity})
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
            Donated materials go to the treasury for buffs. Higher rarity materials grant more contribution points!
          </p>
        {/if}
      {/if}
    </section>"""

new_contributions = """    <section class="panel">
      <h2>Guild Contributors</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to contribute.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <div class="prize-info">
            <h3>🏆 Weekly Prizes</h3>
            <p class="dim tiny">Every week, <strong>50% of the guild treasury</strong> is distributed to the top 3 material contributors:</p>
            <ul class="prize-list">
              <li><span class="gold-text">🥇 1st place</span> — 25% of treasury</li>
              <li><span class="silver-text">🥈 2nd place</span> — 15% of treasury</li>
              <li><span class="bronze-text">🥉 3rd place</span> — 10% of treasury</li>
            </ul>
            <p class="dim tiny">Only material contributions count toward the leaderboard, not gold donations.</p>
          </div>

          <h3>Weekly Leaderboard</h3>
          {#if (guildDepot.data.Leaderboard ?? []).filter(m => m.WeeklyContributionPoints > 0).length === 0}
            <p class="dim small">No material contributions this week yet.</p>
          {:else}
            <ul class="members" style="margin-bottom: 1rem;">
              {#each (guildDepot.data.Leaderboard ?? []).filter(m => m.WeeklyContributionPoints > 0) as member, i}
                <li>
                  <span class="who">
                    {#if i === 0}🥇{:else if i === 1}🥈{:else if i === 2}🥉{:else}#{i + 1}{/if}
                    {member.Name}
                    {#if member.PlayerId === connection.currentPlayerId}<span class="dim tiny">you</span>{/if}
                  </span>
                  <span class="dim">{member.WeeklyContributionPoints.toLocaleString()} pts</span>
                </li>
              {/each}
            </ul>
          {/if}

          <h3>Donate Materials</h3>
          <p class="dim tiny">Donate logs and ores to the guild depot. Rarer materials grant more contribution points!</p>
          <label>
            Material
            <select bind:value={donateMaterial}>
              <option value={0}>Choose...</option>
              {#each depositable.filter(r => isDonatableMaterial(r.baseId)) as row}
                <option value={row.baseId}>
                  {prettifyBaseId(row.baseId)} (x{row.quantity})
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
        {/if}
      {/if}
    </section>"""

content = content.replace(old_contributions, new_contributions)

# ============================================================
# 5. Add CSS for new buff UI
# ============================================================
old_css_end = """  .members {"""

new_css_buff = """  .buff-block {
    border: 1px solid var(--border);
    border-radius: 4px;
    margin-bottom: 0.75rem;
    overflow: hidden;
  }

  .buff-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.4rem 0.6rem;
    background: color-mix(in srgb, var(--accent) 10%, transparent);
    border-bottom: 1px solid var(--border);
  }

  .buff-title {
    font-weight: bold;
    font-size: 0.9rem;
  }

  .buff-tier-row {
    display: grid;
    grid-template-columns: 5rem 1fr 1fr;
    gap: 0.3rem;
    padding: 0.35rem 0.6rem;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 50%, transparent);
    align-items: center;
    font-size: 0.78rem;
  }

  .buff-tier-row:last-child {
    border-bottom: none;
  }

  .tier-label {
    font-weight: 600;
  }

  .buff-path {
    display: flex;
    flex-direction: column;
    gap: 0.15rem;
    padding: 0.25rem 0.4rem;
    border-radius: 3px;
    background: color-mix(in srgb, var(--bg-panel) 50%, transparent);
  }

  .buff-path.rare {
    background: color-mix(in srgb, var(--accent) 8%, transparent);
  }

  .mat-req {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.25rem;
  }

  .mat-name {
    color: var(--text-dim);
    font-size: 0.72rem;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 7rem;
  }

  .mat-name.rare-mat {
    color: var(--accent);
  }

  .mat-stock {
    font-size: 0.7rem;
    white-space: nowrap;
    font-variant-numeric: tabular-nums;
  }

  .mat-ok { color: var(--good); }
  .mat-low { color: var(--danger); }

  .rare-btn {
    background: color-mix(in srgb, var(--accent) 20%, transparent);
    border-color: var(--accent);
    color: var(--accent);
  }

  .active-buffs-bar {
    display: flex;
    flex-wrap: wrap;
    gap: 0.4rem;
    margin-bottom: 0.75rem;
  }

  .active-buff-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    background: color-mix(in srgb, var(--good) 15%, transparent);
    border: 1px solid var(--good);
    border-radius: 12px;
    padding: 0.15rem 0.5rem;
    font-size: 0.75rem;
    color: var(--good);
  }

  .prize-info {
    background: color-mix(in srgb, var(--accent) 8%, transparent);
    border: 1px solid color-mix(in srgb, var(--accent) 30%, transparent);
    border-radius: 4px;
    padding: 0.6rem 0.8rem;
    margin-bottom: 0.75rem;
  }

  .prize-list {
    list-style: none;
    margin: 0.4rem 0;
    padding: 0;
    font-size: 0.82rem;
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
  }

  .gold-text   { color: #f0c040; }
  .silver-text { color: #c0c0c0; }
  .bronze-text { color: #cd7f32; }

  .members {"""

content = content.replace(old_css_end, new_css_buff)

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print("done")
