import re

with open('client_web/src/routes/GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Remove non-buff materials from donate select
donate_select_re = r"\{#each depositable\.filter\(d => !BUFF_MATERIAL_IDS\.has\(d\.baseId\)[^\}]+\} *\n *<option value=\{row\.baseId\}> *\n *\{prettifyBaseId\(row\.baseId\)\} \(x\{row\.quantity\}\) *\n *</option> *\n *\{/each\}"
content = re.sub(donate_select_re, "", content)

# 2. Redesign the buff layout
old_buffs = '''          {#each BUFF_TYPES as buff}
            {@const active = (guildDepot.data.ActiveBuffs ?? []).find(b => b.BuffType === buff.type && b.ExpiresAtEpoch * 1000 > Date.now())}
            {@const isOpen = expandedBuff === buff.type}
            <div class="buff-block">
              <button class="buff-header" onclick={() => toggleBuff(buff.type)}>
                <span class="buff-title">{isOpen ? ' ' : '?'} {buff.label}</span>
                {#if active}
                  <span class="good-text tiny">Active T{active.Tier} until {new Date(active.ExpiresAtEpoch * 1000).toLocaleString()}</span>
                {:else}
                  <span class="dim tiny">Inactive</span>
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
                      >Activate (1h)</button>
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
                      >Activate (9h)</button>
                    </div>
                  </div>
                {/each}
              {/if}
            </div>
          {/each}'''

new_buffs = '''          {#each BUFF_TYPES as buff}
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
                  <span style="font-size: 0.8rem;">{isOpen ? '▲' : '▼'}</span>
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
          {/each}'''
content = content.replace(old_buffs, new_buffs)

# 3. Add CSS for new layout
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
  .buff-header {
    justify-content: space-between;
  }
'''
if '.buff-tier-row-col' not in content:
    content = content.replace('</style>', css_to_add + '\n</style>')

with open('client_web/src/routes/GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print('UI done')
