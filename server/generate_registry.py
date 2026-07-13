import re

with open(r'c:\Users\promi\skola2025\IdleHra\IdleHraGDD\CombinedGDD.txt', 'r', encoding='utf-8') as f:
    lines = f.readlines()

monsters = []
current_monster = {}
item_names = []

def get_item_id(name):
    if name not in item_names:
        item_names.append(name)
    return item_names.index(name)

for line in lines:
    line = line.strip()
    match = re.match(r'Level \d+(?:\s*\([^)]+\))?:?\s*(.*)', line)
    
    if match and "Drop Item" not in line and "Probability" not in line and "Base" not in line and "Minimum" not in line and "Set" not in line:
        if current_monster and "Attack Interval" in current_monster:
            monsters.append(current_monster)
        current_monster = {"Name": match.group(1).strip(), "Drops": []}
        continue
    
    if current_monster:
        if "Maximum Health Pool:" in line:
            val = re.search(r'([\d,]+)', line)
            if val: current_monster["MaxHp"] = int(val.group(1).replace(',', ''))
        elif "Base Attack Damage:" in line:
            val = re.search(r'([\d,]+)', line)
            if val: current_monster["AttackPower"] = int(val.group(1).replace(',', ''))
        elif "Gold Drop Reward:" in line:
            val = re.search(r'([\d,]+)', line)
            if val: current_monster["Gold"] = int(val.group(1).replace(',', ''))
        elif "Experience Drop Reward:" in line:
            val = re.search(r'([\d,]+)', line)
            if val: current_monster["XP"] = int(val.group(1).replace(',', ''))
        elif "Attack Interval:" in line:
            val = re.search(r'([\d.]+)', line)
            if val: current_monster["Attack Interval"] = int(float(val.group(1)) * 1000)
        elif "Drop Item" in line and "Probability" in line:
            # Drop Item 01: Copper Ore (Crafting Material) | 60.00% Probability
            # Level 10 (Boss): Moss-Grown Forest Troll
            # Drop Item 02: Premium Diamond Cluster (Guaranteed Currency Payout) | 100.00% Probability
            drop_match = re.search(r'Drop Item \d+:\s*(.*?)\s*\|\s*([\d.]+)%\s*Probability', line)
            if drop_match:
                item_name = drop_match.group(1).strip()
                prob = float(drop_match.group(2))
                weight = int(prob * 100)
                current_monster["Drops"].append({"ItemId": get_item_id(item_name), "Weight": weight})

if current_monster and "Attack Interval" in current_monster:
    monsters.append(current_monster)

# Generate C# Code
cs_code = """using System;
using System.Runtime.InteropServices;

namespace FolkIdle.Server.Engine
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ItemDefinition
    {
        public int Id;
        public int BaseValueGold;
        public int FlatAttackPower;
        public int FlatDefenseRating;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MonsterDefinition
    {
        public int Id;
        public int MaxHp;
        public int AttackPower;
        public int BaseGoldReward;
        public int BaseXpReward;
        public int AttackIntervalMs;
        public int LootTableId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LootTableEntry
    {
        public int ItemId;
        public int Weight;
    }

    public static class ContentRegistry
    {
"""

cs_code += "        private static readonly string[] _monsterNames = new string[]\n        {\n"
for i, m in enumerate(monsters):
    name_clean = m['Name'].replace('"', '\\"')
    cs_code += f'            "{name_clean}",\n'
cs_code += "        };\n\n"

cs_code += "        private static readonly string[] _monsterEnemyIds = new string[]\n        {\n"
for i, m in enumerate(monsters):
    enemy_id = m['Name'].lower().replace(' ', '_').replace('(', '').replace(')', '').replace('-', '_').replace('\'', '')
    cs_code += f'            "{enemy_id}",\n'
cs_code += "        };\n\n"

cs_code += "        private static readonly string[] _itemBaseIds = new string[]\n        {\n"
for i, name in enumerate(item_names):
    item_id = name.lower().replace(' ', '_').replace('(', '').replace(')', '').replace('-', '_').replace('\'', '')
    cs_code += f'            "{item_id}",\n'
cs_code += "        };\n\n"

cs_code += "        private static readonly MonsterDefinition[] _monsters = new MonsterDefinition[]\n        {\n"
for i, m in enumerate(monsters):
    # Id matches index
    # Note: Using offset + 1 if necessary? GDD uses Level 1 to 10 for regions. We just use array index + 1 as Id, or just index.
    cs_code += f"            new MonsterDefinition {{ Id = {i + 1}, MaxHp = {m['MaxHp']}, AttackPower = {m.get('AttackPower', 0)}, BaseGoldReward = {m.get('Gold', 0)}, BaseXpReward = {m.get('XP', 0)}, AttackIntervalMs = {m.get('Attack Interval', 2000)}, LootTableId = {i + 1} }},\n"
cs_code += "        };\n\n"

# Flat loot table and segment mapping
cs_code += "        private static readonly LootTableEntry[] _lootEntries = new LootTableEntry[]\n        {\n"
loot_segments = []
current_index = 0
for i, m in enumerate(monsters):
    start = current_index
    count = len(m["Drops"])
    loot_segments.append((start, count))
    for d in m["Drops"]:
        cs_code += f"            new LootTableEntry {{ ItemId = {d['ItemId'] + 1}, Weight = {d['Weight']} }},\n"
    current_index += count
cs_code += "        };\n\n"

cs_code += "        private static readonly (int Start, int Count)[] _lootSegments = new (int Start, int Count)[]\n        {\n"
for seg in loot_segments:
    cs_code += f"            ({seg[0]}, {seg[1]}),\n"
cs_code += "        };\n\n"

cs_code += """        public static ReadOnlySpan<MonsterDefinition> Monsters => _monsters;
        public static string GetMonsterName(int id) => _monsterNames[id - 1];
        public static string GetMonsterEnemyId(int id) => _monsterEnemyIds[id - 1];
        public static string GetItemBaseId(int itemId) => _itemBaseIds[itemId - 1];

        public static ReadOnlySpan<LootTableEntry> GetLootTable(int lootTableId)
        {
            var segment = _lootSegments[lootTableId - 1];
            return new ReadOnlySpan<LootTableEntry>(_lootEntries, segment.Start, segment.Count);
        }
    }
}
"""

with open(r'c:\Users\promi\skola2025\IdleHra\server\FolkIdle.Server\Engine\ContentRegistry.cs', 'w', encoding='utf-8') as f:
    f.write(cs_code)

print("Generated ContentRegistry.cs successfully.")
