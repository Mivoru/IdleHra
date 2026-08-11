with open(r'c:\Users\promi\skola2025\IdleHra\walkthrough.md', 'r', encoding='utf-8') as f:
    content = f.read()

content += """
## UI Clean Up and Fixes
- **Unified Treasury Panel**: Combined "Depot", "Guild Treasury & Buffs", and "Guild Contributions" into a single cohesive "Guild Treasury & Contributions" panel as requested.
- **Hidden Raid completely**: The "Raid" section is now completely hidden.
- **Gold Donation Logic**: Gold now has its own dedicated input and "Donate Gold" button in the Treasury panel.
- **Material Filter Fix**: The dropdown list of materials was empty because the numerical ItemId string (e.g. `"267"`) wasn't translating properly into words. Added a lookup using `registry.definitions.find` to fix it, so materials like Birch logs show up again.
- **Deployed**: The new UI changes are currently being deployed to production.
"""

with open(r'c:\Users\promi\skola2025\IdleHra\walkthrough.md', 'w', encoding='utf-8') as f:
    f.write(content)

print("done")
