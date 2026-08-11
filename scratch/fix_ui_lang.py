import re

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Weekly Prizes
content = content.replace('<strong>50 % pokladny guildy</strong>', '<strong>50% of the guild treasury</strong>')
content = content.replace('1. misto', '1st place')
content = content.replace('2. misto', '2nd place')
content = content.replace('3. misto', '3rd place')
content = content.replace('25 % pokladny', '25% of treasury')
content = content.replace('15 % pokladny', '15% of treasury')
content = content.replace('10 % pokladny', '10% of treasury')

# 2. Donate Materials section
content = content.replace('Donujte materialy do depotu guildy. Vzacnejsi materialy daji vice bodu.', 'Donate logs and ores to the guild depot. Rarer materials grant more contribution points!')
content = content.replace('<option value={0}>Vyberte...</option>', '<option value={0}>Choose...</option>')

content = content.replace('Do depotu\n          </button>', 'To depot\n          </button>')
content = content.replace('Do retezce\n          </button>', 'To chain\n          </button>')
content = content.replace('Donovat\n          </button>', 'Donate\n          </button>')

content = content.replace('<strong>Do depotu</strong> plni pozadavky logistiky.', '<strong>To depot</strong> fills the requirements above.')
content = content.replace('<strong>Do retezce</strong> krmí vyrobní bar.', '<strong>To chain</strong> feeds the logistics production bar instead.')
content = content.replace('<strong>Donovat</strong> pridava materialy do pokladny pro buffy a contribution pointy.', '<strong>Donate</strong> adds materials to the treasury for buffs and contribution points.')

# 3. Active Buffs chips
content = content.replace('— do {new Date(ab.ExpiresAtEpoch * 1000).toLocaleTimeString()}', '— until {new Date(ab.ExpiresAtEpoch * 1000).toLocaleTimeString()}')

# 4. Buff headers
content = content.replace('Aktivni T{active.Tier} do', 'Active T{active.Tier} until')
content = content.replace('<span class="dim tiny">Neaktivni</span>', '<span class="dim tiny">Inactive</span>')

# 5. Buff buttons
content = content.replace('Aktivovat (1h)', 'Activate (1h)')
content = content.replace('Aktivovat (9h)', 'Activate (9h)')

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print("UI language fix done.")
