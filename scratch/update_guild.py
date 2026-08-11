import re

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace("let donateMaterial = $state(0);", "let donateMaterial = $state<string | number>(0);")

if "value=\"gold\"" not in content:
    content = content.replace("<option value={0}>Choose...</option>", "<option value={0}>Choose...</option>\n              <option value=\"gold\">Gold</option>")

content = content.replace("<option value={row.definition!.Id}>", "<option value={row.baseId}>")

raid_start = content.find('<section class="panel">\n      <h2>Cross-shard war</h2>')
if raid_start != -1:
    raid_end = content.find('</section>\n\n    <section class="panel">\n      <h2>Members</h2>')
    if raid_end != -1:
        content = content[:raid_start] + "<!-- Cross-shard war hidden -->\n" + content[raid_end:]

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\lib\net\rest.ts', 'r', encoding='utf-8') as f:
    rest_content = f.read()

rest_content = rest_content.replace(
    "export function donateToGuildDepot(materialId: number, quantity: number): Promise<void> {\n  return authedPost<void>('/api/v1/guilds/depot/donate', { MaterialId: materialId, Quantity: quantity })",
    "export function donateToGuildDepot(materialId: string | number, quantity: number): Promise<void> {\n  return authedPost<void>('/api/v1/guilds/depot/donate', { itemId: String(materialId), quantity: quantity })"
)

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\lib\net\rest.ts', 'w', encoding='utf-8') as f:
    f.write(rest_content)

print("done")
