import re

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

# ============================================================
# 1. Remove all emoji icons
# ============================================================
emojis = ['✨', '🏆', '🥇', '🥈', '🥉', '🪙', '🎁', '⚔️', '⚔', '🛡', '⚡', '🔥', '💎', '►', '▼', '▶']
for e in emojis:
    content = content.replace(e, '')

# ============================================================
# 2. Fix giveGold() to use REST API (donateToGuildDepot) instead of WebSocket
# ============================================================
old_give_gold = """  function giveGold() {
    const outcome = contributeGuildGold(treasuryGold, hasGuild);
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    refresh();
  }"""

new_give_gold = """  async function giveGold() {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (treasuryGold < 1) return;
    try {
      await donateToGuildDepot('gold', treasuryGold);
      pushLocalNotice('Gold contributed to treasury!', 'info');
      setTimeout(() => {
        client.invalidateQueries({ queryKey: queryKeys.guildDepot });
        client.invalidateQueries({ queryKey: queryKeys.inventory });
      }, 700);
    } catch (e: any) {
      pushLocalNotice(e.message || 'Failed to contribute gold.', 'info');
    }
  }"""

content = content.replace(old_give_gold, new_give_gold)

# ============================================================
# 3. Add expandedBuff state for collapsible buffs
# ============================================================
old_buff_state = """  const BUFF_TIERS = ["""
new_buff_state = """  let expandedBuff = $state<string | null>(null);

  function toggleBuff(type: string) {
    expandedBuff = expandedBuff === type ? null : type;
  }

  const BUFF_TIERS = ["""

content = content.replace(old_buff_state, new_buff_state)

# ============================================================
# 4. Fix buff icons in BUFF_TYPES (remove emojis from labels)
# ============================================================
old_buff_types = """  const BUFF_TYPES = [
    { type: 'Exp',      label: 'Experience Boost', icon: '' },
    { type: 'Gold',     label: 'Gold Gain Boost',  icon: '' },
    { type: 'DropRate', label: 'Drop Rate Boost',  icon: '' },
    { type: 'Damage',   label: 'Damage Boost',     icon: '' },
  ];"""

new_buff_types = """  const BUFF_TYPES = [
    { type: 'Exp',      label: 'Experience Boost' },
    { type: 'Gold',     label: 'Gold Gain Boost'  },
    { type: 'DropRate', label: 'Drop Rate Boost'  },
    { type: 'Damage',   label: 'Damage Boost'     },
  ];"""

content = content.replace(old_buff_types, new_buff_types)

# ============================================================
# 5. Rewrite Treasury & Buffs panel HTML (collapsible, no icons)
# ============================================================
# Find the treasury panel start and end
treasury_start = content.find('    <section class="panel">\n      <h2>Guild Treasury & Buffs</h2>')
# Find the end - next </section> at the same indent
treasury_end = content.find('\n    </section>', treasury_start) + len('\n    </section>')

new_treasury = """    <section class="panel">
      <h2>Guild Treasury & Buffs</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to use the treasury.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <div style="margin-bottom: 0.75rem; font-size: 1.1rem;">
            <Money amount={guildDepot.data.GuildGold ?? 0} icon />
          </div>

          {#if (guildDepot.data.ActiveBuffs ?? []).filter(b => b.ExpiresAtEpoch * 1000 > Date.now()).length > 0}
            <div class="active-buffs-bar">
              {#each (guildDepot.data.ActiveBuffs ?? []).filter(b => b.ExpiresAtEpoch * 1000 > Date.now()) as ab}
                {@const buffInfo = BUFF_TYPES.find(b => b.type === ab.BuffType)}
                <span class="active-buff-chip">
                  {buffInfo?.label ?? ab.BuffType} T{ab.Tier} — do {new Date(ab.ExpiresAtEpoch * 1000).toLocaleTimeString()}
                </span>
              {/each}
            </div>
          {/if}

          {#each BUFF_TYPES as buff}
            {@const active = (guildDepot.data.ActiveBuffs ?? []).find(b => b.BuffType === buff.type && b.ExpiresAtEpoch * 1000 > Date.now())}
            {@const isOpen = expandedBuff === buff.type}
            <div class="buff-block">
              <button class="buff-header" onclick={() => toggleBuff(buff.type)}>
                <span class="buff-title">{isOpen ? '▼' : '▶'} {buff.label}</span>
                {#if active}
                  <span class="good-text tiny">Aktivni T{active.Tier} do {new Date(active.ExpiresAtEpoch * 1000).toLocaleString()}</span>
                {:else}
                  <span class="dim tiny">Neaktivni</span>
                {/if}
              </button>

              {#if isOpen}
                {#each BUFF_TIERS as td}
                  <div class="buff-tier-row">
                    <span class="tier-label">T{td.tier}<br><span class="dim tiny">{td.region}</span></span>

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
                      >Aktivovat (1h)</button>
                    </div>

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
                      >Aktivovat (9h)</button>
                    </div>
                  </div>
                {/each}
              {/if}
            </div>
          {/each}
        {/if}
      {/if}
    </section>"""

content = content[:treasury_start] + new_treasury + content[treasury_end:]

# ============================================================
# 6. Fix Depot panel: filter dropdown to buff materials only + add Donate button
# ============================================================
old_depot_select = """        <h3>Deposit</h3>

        <label>
          Material
          <select bind:value={depotMaterial}>
            <option value={0}>Choose...</option>
              <option value="gold">Gold</option>
            {#each depositable as row (row.definition!.Id)}
              <option value={row.baseId}>
                {prettifyBaseId(row.baseId)} (x{row.quantity})
              </option>
            {/each}
          </select>
        </label>

        <div class="row">
          <input type="number" min="1" max={depotMax || 1} bind:value={depotQuantity} />
          <button disabled={depotMaterial === 0 || depotMax === 0} onclick={deposit}>
            To depot
          </button>
          <button disabled={depotMaterial === 0 || depotMax === 0} onclick={contributeStock}>
            To chain
          </button>
        </div>

        <!-- These two buttons look interchangeable and are not. Saying so is
             cheaper than a player wondering why one number moved and not the
             other. -->
        <p class="dim tiny">
          <strong>To depot</strong> fills the requirements above.
          <strong>To chain</strong> feeds the logistics production bar instead.
          They are separate systems that happen to take the same materials.
        </p>"""

new_depot_select = """        <h3>Donate Materials</h3>
        <p class="dim tiny">Donujte materialy do depotu guildy. Vzacnejsi materialy daji vice bodu.</p>
        <label>
          Material
          <select bind:value={depotMaterial}>
            <option value={0}>Vyberte...</option>
            {#each depositable.filter(r => BUFF_MATERIAL_IDS.has(r.baseId)) as row (row.definition!.Id)}
              <option value={row.baseId}>
                {prettifyBaseId(row.baseId)} (x{row.quantity})
              </option>
            {/each}
          </select>
        </label>

        <div class="row">
          <input type="number" min="1" max={depotMax || 1} bind:value={depotQuantity} />
          <button disabled={depotMaterial === 0 || depotMax === 0} onclick={deposit}>
            Do depotu
          </button>
          <button disabled={depotMaterial === 0 || depotMax === 0} onclick={contributeStock}>
            Do retezce
          </button>
          <button disabled={depotMaterial === 0 || donateMax === 0} onclick={handleDonate}>
            Donovat
          </button>
        </div>

        <p class="dim tiny">
          <strong>Do depotu</strong> plni pozadavky logistiky.
          <strong>Do retezce</strong> krmí vyrobní bar.
          <strong>Donovat</strong> pridava materialy do pokladny pro buffy a contribution pointy.
        </p>"""

content = content.replace(old_depot_select, new_depot_select)

# ============================================================
# 7. Remove Donate Materials from Guild Contributors
# ============================================================
old_donate_section = """          <h3>Donate Materials</h3>
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
          </div>"""

# Remove the whole donate materials section
content = content.replace(old_donate_section, '')

# ============================================================
# 8. Remove emojis from prize-info and leaderboard in Guild Contributors
# ============================================================
content = content.replace('Weekly Prizes', 'Weekly Prizes')
content = content.replace('<strong>50% of the guild treasury</strong>', '<strong>50 % pokladny guildy</strong>')

# Fix the "prize-list" to not use emoji
content = content.replace(
    '<li><span class="gold-text"> 1st place</span> — 25% of treasury</li>',
    '<li><span class="gold-text">1. misto</span> — 25 % pokladny</li>'
)
content = content.replace(
    '<li><span class="silver-text"> 2nd place</span> — 15% of treasury</li>',
    '<li><span class="silver-text">2. misto</span> — 15 % pokladny</li>'
)
content = content.replace(
    '<li><span class="bronze-text"> 3rd place</span> — 10% of treasury</li>',
    '<li><span class="bronze-text">3. misto</span> — 10 % pokladny</li>'
)

# Fix CSS for buff-header as a button
old_css_header = """  .buff-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.4rem 0.6rem;
    background: color-mix(in srgb, var(--accent) 10%, transparent);
    border-bottom: 1px solid var(--border);
  }"""

new_css_header = """  .buff-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.4rem 0.6rem;
    background: color-mix(in srgb, var(--accent) 10%, transparent);
    border-bottom: 1px solid var(--border);
    width: 100%;
    text-align: left;
    border: none;
    border-radius: 0;
    cursor: pointer;
    color: inherit;
    font: inherit;
  }
  .buff-header:hover {
    background: color-mix(in srgb, var(--accent) 18%, transparent);
  }"""

content = content.replace(old_css_header, new_css_header)

# Fix buff-tier-row CSS to handle text overflow better
old_tier_row_css = """  .buff-tier-row {
    display: grid;
    grid-template-columns: 5rem 1fr 1fr;
    gap: 0.3rem;
    padding: 0.35rem 0.6rem;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 50%, transparent);
    align-items: center;
    font-size: 0.78rem;
  }"""

new_tier_row_css = """  .buff-tier-row {
    display: grid;
    grid-template-columns: 4rem 1fr 1fr;
    gap: 0.25rem;
    padding: 0.35rem 0.5rem;
    border-bottom: 1px solid color-mix(in srgb, var(--border) 50%, transparent);
    align-items: start;
    font-size: 0.75rem;
  }"""

content = content.replace(old_tier_row_css, new_tier_row_css)

# Fix tiny-btn to not clip text
old_tiny_btn = '  .rare-btn {'
new_tiny_btn = """  .tiny-btn {
    white-space: nowrap;
    font-size: 0.72rem;
  }

  .rare-btn {"""

content = content.replace(old_tiny_btn, new_tiny_btn)

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print("done")
