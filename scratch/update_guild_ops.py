import re

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

if 'import Money from' not in content:
    content = content.replace("import Skeleton from '../lib/ui/Skeleton.svelte';", "import Skeleton from '../lib/ui/Skeleton.svelte';\n  import Money from '../lib/ui/Money.svelte';")

if 'function isDonatableMaterial' not in content:
    material_filter_func = """
  function isDonatableMaterial(baseId: string): boolean {
    const valid = ['birch', 'willow', 'acacia', 'frostpine', 'ebon', 'copper', 'iron', 'cobalt', 'silver', 'darksteel', 'malachite', 'hematite', 'sulfur', 'obsidian', 'absidian', 'astralite'];
    if (baseId.includes('magic_essence')) return true;
    if (baseId.startsWith('mat_')) return false;
    if (baseId.startsWith('eq_')) return false;
    if (baseId.includes('axe') || baseId.includes('rod')) return false;
    
    return valid.some(v => baseId.includes(v)) && (baseId.includes('log') || baseId.includes('twig') || baseId.includes('ore') || baseId.includes('bar') || baseId.includes('chunk') || baseId.includes('crystal'));
  }
"""
    content = content.replace("function handleActivateBuff(buffType: string) {", material_filter_func + "\n  function handleActivateBuff(buffType: string) {")

old_filter = "depositable.filter(r => !r.definition!.FlatAttackPower && !r.definition!.FlatDefenseRating && r.definition!.BaseValueGold < 100 && r.baseId !== 'gold' && !r.baseId.includes('slime') && !r.baseId.includes('ear') && !r.baseId.includes('wing'))"
new_filter = "depositable.filter(r => isDonatableMaterial(r.baseId))"
content = content.replace(old_filter, new_filter)

old_gold_html = """<div class="good-text" style="margin-bottom: 1rem; font-size: 1.2rem;">
            Gold: {guildDepot.data.GuildGold.toLocaleString()}
          </div>"""
new_gold_html = """<div style="margin-bottom: 1rem; font-size: 1.2rem;">
            <Money amount={guildDepot.data.GuildGold} icon />
          </div>"""
content = content.replace(old_gold_html, new_gold_html)

# Splitting panels securely
content = content.replace(
"""          <h3>Weekly Leaderboard</h3>
          <p class="dim tiny">Top 3 contributors receive a cut of the guild's gold at the end of the week.</p>""", 
"""        {/if}
      {/if}
    </section>

    <section class="panel">
      <h2>Guild Contributions</h2>
      {#if !hasGuild}
        <p class="dim">Join a guild to use the contributions.</p>
      {:else}
        {#if guildDepot.isPending}
          <Skeleton />
        {:else if guildDepot.data}
          <h3>Weekly Leaderboard</h3>
          <p class="dim tiny">Top 3 contributors receive a cut of the guild's gold at the end of the week.</p>""")

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print("success!")
