import re

with open('server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Fix HandleGuildDepotDonate quantity parsing
target = '''                string itemId = payload.GetProperty("itemId").GetString() ?? "";
                int quantity = payload.GetProperty("quantity").GetInt32();'''

replacement = '''                string itemId = payload.GetProperty("itemId").GetString() ?? "";
                int quantity = 0;
                var qProp = payload.GetProperty("quantity");
                if (qProp.ValueKind == JsonValueKind.Number)
                {
                    quantity = qProp.GetInt32();
                }
                else if (qProp.ValueKind == JsonValueKind.String)
                {
                    int.TryParse(qProp.GetString(), out quantity);
                }'''

content = content.replace(target, replacement)

with open('server/FolkIdle.Server/Network/NetworkBroadcastSystem.cs', 'w', encoding='utf-8') as f:
    f.write(content)
print('Done backend')
