# Gold: what it is for at the top (2026-11-13)

Owner question 5 in `GAME_REVIEW_2026_11_13.md`. Measured read-only on
production; nothing here is built - it is an economy decision.

## Measured

Weekly gold minted, whole server (gold_income_daily, ISO weeks): 2.3B
(10-05), 3.3B, 3.8B, 7.5B, 14.8B (11-02), 10.7B (11-09, part week). Growth is
offline kills (2.2B -> 8.6B), auto-salvage (1.4B -> 3.5B) and the new loot
chests and Gold dungeon (1-2B each).

Last 7 days, per account:

| | Earned | Spent | Banked now |
|---|---|---|---|
| Mivoru | 13.8B | 5.6B | 2.8B |
| Tomda | 1.8B | 0.38B | 0.25B |
| (pro)boss | 1.3B | 0.06B | 0.78B |

Sinks over the last 14 days: guild 5.1B, fusion 3.5B, the Deep 3.2B, rerolls
2.8B, villager recruits 2.2B, the Delve 0.04B, breeding 0.

Read: two of the three accounts spend under a quarter of what they earn,
because once their gear is set, nothing they want costs gold. That is
not inflation hurting anyone yet; it is gold losing meaning as a goal. Cutting
income would only make Mivoru's grind slower and change nothing for the
other two.

## Options

1. **A Treasury of lasting purchases, priced in gold, escalating.** Things the
   endgame accounts want and cannot buy today: a 4th and 5th expedition slot,
   a Hall carry slot (today 250 diamonds - a gold path beside it), a Vault
   price reduction. Each step costs more (for example x2.5 a step from 250M),
   so it scales from Tomda's income to Mivoru's.
   - Pros: gives gold a goal again, ties into expeditions and breeding.
   - Cons: a Hall slot for gold weakens the diamond sink unless priced high.
2. **Scale existing sinks with income**, like the Delve's stake (a share of
   the player's own hourly gold): recruits, breeding, the Vault.
   - Pros: no new system. Cons: makes things dearer, not more desirable.
3. **Trim faucets** (offline kill gold, auto-salvage).
   - Cons: punishes the one account that uses its gold; no new goal.

## Recommendation

Option 1, starting with the expedition slots (they have no diamond price to
undercut) at 250M / 625M, then decide on a gold-priced Hall slot after a week
of data. Needs the owner's yes before anything is built.

## Decided and built (2026-11-13, PR #385, client 1.76.0)

The owner approved the expedition slots, priced by income instead of flat:
the 1st bought slot costs 24 hours of the player's own gold income, the 2nd
72 hours (`ExpeditionRules.BoughtSlotPriceHours`, `NextSlotPrice`, rounded to
1,000), at most two (`MaxBoughtSlots`). Income is `LootChestEngine.
GoldPerMinuteAsync`, the same measure the Delve stake uses, so the price
follows Tomda's income as well as Mivoru's. Spending lands in the ledger as
`GoldSpendCategory.Expedition`.

Correction from the owner: Tomda and (pro)boss do not spend on the Vault's
equipment transfer after a rebirth either, so option 2's Vault line would
not reach them. A gold-priced upgrade to an expedition (better chest odds),
priced by income the same way, is the next candidate if gold still piles up.
