import re

with open('client_web/src/lib/net/rest.ts', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace(
    "export interface GuildDepotResponse {\n  Balances: Record<number, number>;",
    "export interface GuildDepotResponse {\n  Balances: Record<number, number>;\n  DepotByBaseId?: Record<string, number>;"
)

content = content.replace(
    "export interface GuildActiveBuffInfo {\n  BuffType: string;",
    "export interface GuildActiveBuffInfo {\n  BuffType: string;\n  Tier: number;"
)

with open('client_web/src/lib/net/rest.ts', 'w', encoding='utf-8') as f:
    f.write(content)

with open('client_web/src/routes/GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace("contributeGuildGold,", "")
content = re.sub(r'function defend\(\)\s*\{[^}]+\}', '', content)
content = re.sub(r'function attackShard\(\)\s*\{[^}]+\}', '', content)
content = content.replace("const damageDelta = $derived(snap?.CombatSimulationDamageDelta ?? 0);", "")
content = re.sub(r'function takeTurn\(\)\s*\{[^}]+\}', '', content)
content = content.replace("let donateQuantity = $state(1);", "")
content = re.sub(r'const donateMax = \$derived\([^;]+;', '', content)
content = content.replace(")\n  );", "")

with open('client_web/src/routes/GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print('Fixed TS errors')
