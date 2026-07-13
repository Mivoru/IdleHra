import re

with open(r'c:\Users\promi\skola2025\IdleHra\IdleHraGDD\CombinedGDD.txt', 'r', encoding='utf-8') as f:
    lines = f.readlines()

monsters = []
current_monster = {}

for line in lines:
    line = line.strip()
    match = re.match(r'Level \d+(?:\s*\([^)]+\))?:?\s*(.*)', line)
    
    if match and "Drop Item" not in line and "Probability" not in line and "Base" not in line and "Minimum" not in line and "Set" not in line:
        if current_monster and "Attack Interval" in current_monster:
            monsters.append(current_monster)
        current_monster = {"Name": match.group(1).strip()}
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

if current_monster and "Attack Interval" in current_monster:
    monsters.append(current_monster)

print(f"Parsed {len(monsters)} monsters.")
for i, m in enumerate(monsters):
    print(f"[{i}] {m}")
