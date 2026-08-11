import re

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'r', encoding='utf-8') as f:
    content = f.read()

# Note: The previous python script already replaced `row.definition!.Id` with `row.baseId` but let's be careful.
content = content.replace(
    "const donateMax = $derived(\n    depositable.find((row: any) => row.baseId === donateMaterial)?.quantity ?? 0,\n  );",
    "const donateMax = $derived(\n    donateMaterial === 'gold' ? (connection.player?.Gold ?? 0) : (depositable.find((row: any) => row.baseId === donateMaterial)?.quantity ?? 0),\n  );"
)

with open(r'c:\Users\promi\skola2025\IdleHra\client_web\src\routes\GuildOps.svelte', 'w', encoding='utf-8') as f:
    f.write(content)

print("done")
