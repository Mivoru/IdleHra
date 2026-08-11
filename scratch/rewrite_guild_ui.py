import re

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    lines = f.readlines()

content = "".join(lines)

# 1. Hide Raid Panel
content = content.replace('<section class="panel">\n      <h2>Raid</h2>', '<!-- Raid hidden -->\n      <section class="panel" style="display:none;">\n      <h2>Raid</h2>')

# 2. Fix isDonatableMaterial
old_isDonatable = """  function isDonatableMaterial(baseId: string): boolean {
    const valid = ['birch', 'willow', 'acacia', 'frostpine', 'ebon', 'copper', 'iron', 'cobalt', 'silver', 'darksteel', 'malachite', 'hematite', 'sulfur', 'obsidian', 'absidian', 'astralite'];
    if (baseId.includes('magic_essence')) return true;
    if (baseId.startsWith('mat_')) return false;
    if (baseId.startsWith('eq_')) return false;
    if (baseId.includes('axe') || baseId.includes('rod')) return false;
    
    return valid.some(v => baseId.includes(v)) && (baseId.includes('log') || baseId.includes('twig') || baseId.includes('ore') || baseId.includes('bar') || baseId.includes('chunk') || baseId.includes('crystal'));
  }"""

new_isDonatable = """  function getMaterialName(itemId: string) {
    if (!registry) return itemId;
    const def = registry.definitions.find((d: any) => d.Id.toString() === itemId || d.BaseId === itemId);
    return def ? def.BaseId : itemId;
  }

  function isDonatableMaterial(itemId: string): boolean {
    const baseId = getMaterialName(itemId);
    const valid = ['birch', 'willow', 'acacia', 'frostpine', 'ebon', 'copper', 'iron', 'cobalt', 'silver', 'darksteel', 'malachite', 'hematite', 'sulfur', 'obsidian', 'absidian', 'astralite'];
    if (baseId.includes('magic_essence')) return true;
    if (baseId.startsWith('mat_')) return false;
    if (baseId.startsWith('eq_')) return false;
    if (baseId.includes('axe') || baseId.includes('rod')) return false;
    
    return valid.some(v => baseId.includes(v)) && (baseId.includes('log') || baseId.includes('twig') || baseId.includes('ore') || baseId.includes('bar') || baseId.includes('chunk') || baseId.includes('crystal'));
  }"""

content = content.replace(old_isDonatable, new_isDonatable)

# 3. Create unified Treasury panel
depot_start = content.find('<section class="panel">\n      <h2>Depot</h2>')
members_start = content.find('<!-- Cross-shard war hidden -->\n    <section class="panel">\n      <h2>Members</h2>')

if members_start == -1:
    members_start = content.find('<section class="panel">\n      <h2>Members</h2>')

unified_panel = """    <section class="panel">
      <h2>Guild Treasury & Contributions</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to use the treasury.</p>
      {:else}
        {#if guildDepot.isPending}
          <p class="dim small">Loading treasury data...</p>
        {:else if guildDepot.data}
          <div style="margin-bottom: 1rem; font-size: 1.2rem;">
            <Money amount={guildDepot.data.GuildGold ?? 0} icon />
          </div>

          <h3>Active Buffs</h3>
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

          <h3>Contribute Gold</h3>
          <div class="row">
            <input type="number" min="1" max={snap?.Gold ?? 0} bind:value={donateGoldQuantity} />
            <button disabled={donateGoldQuantity <= 0 || donateGoldQuantity > (snap?.Gold ?? 0)} onclick={handleDonateGold}>
              Donate Gold
            </button>
          </div>
          <p class="dim tiny">
            Donated gold goes directly to the treasury to fund guild buffs.
          </p>

          <h3>Contribute Materials</h3>
          <label>
            Material
            <select bind:value={donateMaterial}>
              <option value={0}>Choose...</option>
              {#each depositable.filter(r => isDonatableMaterial(r.baseId)) as row}
                <option value={row.baseId}>
                  {getLabel(row.baseId)} (x{row.quantity})
                </option>
              {/each}
            </select>
          </label>
          <div class="row">
            <input type="number" min="1" max={donateMax || 1} bind:value={donateQuantity} />
            <button disabled={donateMaterial === 0 || donateMax === 0} onclick={handleDonate}>
              Donate Material
            </button>
          </div>
          <p class="dim tiny">
            Higher rarity materials grant more contribution points!
          </p>

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
        {/if}
      {/if}
    </section>

"""

# Add state variable for gold quantity
content = content.replace('let donateQuantity = $state(1);', 'let donateQuantity = $state(1);\n  let donateGoldQuantity = $state(1);')

# Add handleDonateGold function
donate_gold_fn = """  async function handleDonateGold() {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (donateGoldQuantity <= 0) return;
    
    try {
      await donateToGuildDepot('gold', donateGoldQuantity);
      pushLocalNotice('Gold donated successfully!', 'info');
      donateGoldQuantity = 1;
      client.invalidateQueries({ queryKey: queryKeys.inventory });
      client.invalidateQueries({ queryKey: queryKeys.guildDepot });
    } catch (err: any) {
      pushLocalNotice(err.message || 'Donation failed.', 'error');
    }
  }

  async function handleDonate() {"""

content = content.replace('async function handleDonate() {', donate_gold_fn)

# Fix donateMax calculation to avoid checking gold anymore since gold is handled separately
content = content.replace(
    "const donateMax = $derived(\n    donateMaterial === 'gold' ? (connection.player?.Gold ?? 0) : (depositable.find((row: any) => row.baseId === donateMaterial)?.quantity ?? 0),\n  );",
    "const donateMax = $derived(\n    depositable.find((row: any) => row.baseId === donateMaterial)?.quantity ?? 0,\n  );"
)

# Replace the sections
content = content[:depot_start] + unified_panel + content[members_start:]

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print("done")
