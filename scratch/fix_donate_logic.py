import re

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Remove donate variables
content = re.sub(r'let donateMaterial = \$state<string \| 0>\(0\);\n\s*let donateQuantity = \$state<number>\(1\);\n\s*const donateMax = \$derived\(\n\s*depositable\.find\(\(r\) => r\.baseId === donateMaterial\)\?\.quantity \|\| 0\n\s*\);', '', content)

# 2. Fix handleDonate
old_handleDonate = """  async function handleDonate() {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (donateQuantity < 1) return pushLocalNotice('Quantity must be positive.', 'info');
    
    try {
        await donateToGuildDepot(donateMaterial, Math.min(donateQuantity, donateMax));
        pushLocalNotice('Material donated for Weekly Contribution Points!', 'info');
        refreshDepotFull();
    } catch (e: any) {
        pushLocalNotice(e.message || 'Failed to donate.', 'info');
    }
  }"""

new_handleDonate = """  async function handleDonate() {
    if (!hasGuild) return pushLocalNotice('You are not in a guild.', 'info');
    if (depotQuantity < 1) return pushLocalNotice('Quantity must be positive.', 'info');
    if (depotMaterial === 0) return;
    
    try {
        await donateToGuildDepot(depotMaterial, Math.min(depotQuantity, depotMax));
        pushLocalNotice('Material donated for Weekly Contribution Points!', 'info');
        refreshDepotFull();
    } catch (e: any) {
        pushLocalNotice(e.message || 'Failed to donate.', 'info');
    }
  }"""

content = content.replace(old_handleDonate, new_handleDonate)

# 3. Fix dropdown filter
old_select = """            {#each depositable.filter(r => BUFF_MATERIAL_IDS.has(r.baseId)) as row (row.definition!.Id)}"""
new_select = """            {#each depositable as row (row.definition!.Id)}"""
content = content.replace(old_select, new_select)

# 4. Fix Donate button
old_donate_btn = """          <button disabled={depotMaterial === 0 || donateMax === 0} onclick={handleDonate}>
            Donate
          </button>"""
new_donate_btn = """          <button disabled={depotMaterial === 0 || depotMax === 0 || !isDonatableMaterial(depotMaterial.toString())} onclick={handleDonate}>
            Donate
          </button>"""
content = content.replace(old_donate_btn, new_donate_btn)

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print("Donate logic fixed.")
