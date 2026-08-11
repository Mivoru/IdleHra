import re

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Rewrite the depositable dropdown logic to specifically show BUFF_MATERIAL_IDS (always) + any other Logs/Ores in inventory

old_depot_html = """        <label>
          Material
          <select bind:value={depotMaterial}>
            <option value={0}>Choose...</option>
            {#each depositable as row (row.definition!.Id)}
              <option value={row.baseId}>
                {prettifyBaseId(row.baseId)} (x{row.quantity})
              </option>
            {/each}
          </select>
        </label>"""

new_depot_html = """        <label>
          Material
          <select bind:value={depotMaterial}>
            <option value={0}>Choose...</option>
            {#each Array.from(BUFF_MATERIAL_IDS) as baseId}
              {@const invItem = depositable.find(d => d.baseId === baseId)}
              <option value={baseId}>
                {prettifyBaseId(baseId)} (x{invItem?.quantity ?? 0})
              </option>
            {/each}
            {#each depositable.filter(d => !BUFF_MATERIAL_IDS.has(d.baseId) && (d.definition?.Subtype === 'Log' || d.definition?.Subtype === 'Ore')) as row}
              <option value={row.baseId}>
                {prettifyBaseId(row.baseId)} (x{row.quantity})
              </option>
            {/each}
          </select>
        </label>"""

content = content.replace(old_depot_html, new_depot_html)

# 2. Fix the depotMax to allow 0 if not found, since now we can select items we don't own
old_depotMax = """  const depotMax = $derived(
    depositable.find((r) => r.baseId === depotMaterial)?.quantity || 0
  );"""

new_depotMax = """  const depotMax = $derived(
    depositable.find((r) => r.baseId === depotMaterial)?.quantity || 0
  );""" # It already defaults to 0, which is perfect

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print("Dropdown logic fixed.")
