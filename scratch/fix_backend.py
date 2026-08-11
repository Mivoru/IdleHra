import re

with open(r'c:\Users\promi\skola2025\IdleHra\server\FolkIdle.Server\Network\NetworkBroadcastSystem.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace(
    'var engine = _serviceProvider.GetRequiredService<GuildContributionEngine>();',
    'var engine = new GuildContributionEngine(_serviceProvider, _playerSessionRegistry);'
)

with open(r'c:\Users\promi\skola2025\IdleHra\server\FolkIdle.Server\Network\NetworkBroadcastSystem.cs', 'w', encoding='utf-8') as f:
    f.write(content)

print("Backend fix done.")
