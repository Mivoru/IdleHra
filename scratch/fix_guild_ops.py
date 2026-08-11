import re

with open('client_web/src/routes/GuildOps.svelte', 'r', encoding='utf-8') as f:
    text = f.read()

text = text.replace('contributeGuildGold,', '')
text = re.sub(r'\n  function defend\(\) \{[\s\S]*?\}', '', text)
text = re.sub(r'\n  function attackShard\(\) \{[\s\S]*?\}', '', text)
text = re.sub(r'\n  const damageDelta = \$derived\(snap\?\.CombatSimulationDamageDelta \?\? 0\);', '', text)
text = re.sub(r'\n  function takeTurn\(\) \{[\s\S]*?\}', '', text)

text = text.replace('let donateQuantity = $state(1);', '')
text = re.sub(r'const donateMax = \$derived\(\n\s*donateMaterial === \'gold\'\n\s*\? \(snap\?\.Gold \?\? 0\)\n\s*: \(depositable\.find\(\(row: any\) => row\.baseId === donateMaterial\)\?\.quantity \?\? 0\),\n\s*\);', '', text)

text = text.replace("d.definition?.Subtype === 'Log'", "d.definition?.Id")
text = text.replace("d.definition?.Subtype === 'Ore'", "d.definition?.Id")

with open('client_web/src/routes/GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(text)

print('Cleaned up GuildOps.svelte')
