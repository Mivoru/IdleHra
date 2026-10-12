# Chernobog: world boss identity, beatable HP, weekly rewards

Status: APPROVED by the owner 2026-10-13. Every decision below was made by the
owner in conversation on 2026-10-12/13.

## 0. Why

Measured on production, 2026-10-12:

- The boss is "Perun's Celestial Avatar" (monsters.json id 30) with no art and no story.
- It has 75,020,250 HP. Monday's strikes dealt 19,479 (two players). Last week
  had four participants. It has never fallen and, with `50M x 1.5 x active accounts`,
  it cannot.
- The payout mails `perun_avatar_reward_token`, which has no `items.json` entry -
  it grants nothing. The only real reward is 10k-250k gold by percentile, and with
  four participants first place lands in "Top 50%".
- Real strikes: Mivoru 17,280 in one strike, Tomda 2,199 (a player who clears tier-4 bosses).
  The floor is 1,000 per strike (`WorldBossEngine.MinStrikeDamage`).

The shield-wheel minigame, one strike a day and the weekly calendar stay as they are.

## 1. Identity and lore

The boss is **Chernobog**. Rename monster id 30 in content (`WorldBossIdentity`
reads the name from there). Art: `client/Assets/Images/WithWhiteBackground/Others/Chernobog.jpg`
(white background - needs keying like the other sprites).

Story, adapted from the Svarog legend:

> Svarog sealed Chernobog in a forged chest, but the bargain gave darkness half
> the world. So every Monday the black god breaks the chest's lock and walks into
> the world of men. Svarog's forge needs mortals to drive him back before Sunday.

How the mechanics map onto it:

- The five plates are the five runes on Chernobog's armour (the art shows rune
  medallions). The weak plate is the rune Svarog's hammer once cracked.
- **Enrage (at or below 25% HP) is the Black Serpent form**: the boss portrait
  switches to `Black Serpent.jpg`. Enrage itself is unchanged
  (`WorldBossStrikeRules.EnrageHpFraction`).
- Titles and lore text use Svarog's sons: Dazhbog, Stribog, Simargl, Hors.

The lore text lives on the World Boss screen and in the Wiki.

## 2. The week

All days are UTC, Monday to Sunday, as `WorldBossCalendar` already defines.

### 2.1 Registration: Monday and Tuesday

- HP is hidden ("???") during registration.
- A player who strikes on Monday or Tuesday is **registered** for the encounter.
- Registration requires the same minimum level as the ranked leaderboards
  (`MinimumRankedLevel`), so `exercise######` throwaways and fresh alts do not
  inflate HP.
- Registration damage counts: it is subtracted from HP at the reveal.

### 2.2 Reveal: Wednesday 00:00 UTC

For each registered player:

    avg_i = (Mon damage + Tue damage) / (number of those days they struck)

    TotalHp   = sum(avg_i) x N        N drawn uniformly from [5.5, 7.0], once a week, hidden
    CurrentHp = TotalHp - (all registration damage)

N and the draw live in one place (constants on a `WorldBossHpRules` class) so the
range can be retuned after the first weeks. The draw is stored on the snapshot so a
restart does not redraw.

If nobody registered, the boss has no HP to reveal: the week runs for milestones
and the leaderboard only.

### 2.3 Wednesday to Sunday

- A registered player's damage subtracts from HP and counts for them.
- An unregistered player (first strike on Wednesday or later) **does not subtract
  from HP**. Their damage counts only for their own milestones and leaderboard rank.
- After the boss falls, everyone can keep striking for milestones and rank.

### 2.4 Per-day damage has to be stored

`player_world_boss_attempts` keeps one running total. The reveal needs Monday and
Tuesday separately, and registration needs "struck on Mon/Tue". Add columns
(`RegistrationDamageMon`, `RegistrationDamageTue`, `IsRegistered`) - additive migration.

## 3. Rewards

All gold that scales with income uses one helper (section 3.5).

### 3.1 Kill reward (registered players only)

Paid at the week's payout to every registered player, if the boss fell.

The kill reward is **2 hours of income** (3.5) **plus one Legendary cosmetic chest**
(`CosmeticRegistry`, rarity Legendary). No diamonds - the milestones already reach
the weekly 60.

A player flagged for sandbagging (section 4) gets **25%** of the kill reward.

### 3.2 Weekly damage milestones (everyone)

Paid by mail **when crossed**, not at the end of the week, so a milestone feels
like a reward at the moment it is earned.

| Weekly damage | Gold | Diamonds | Cosmetic (once per account, ever) |
|---|---|---|---|
| 10,000 | 15 min of income | 5 | |
| 25,000 | 30 min | 10 | |
| 60,000 | 1 h | 15 | avatar **Svarog** |
| 150,000 | 2 h | 15 | avatar **Black Serpent** + title |
| 400,000 | 3 h | 15 | avatar **Chernobog** + title |
| 1,000,000 | | | pet **Black Serpent** |

- Gold and diamonds repeat every week. Diamonds total **60 a week**, matching the
  Delve's weekly ceiling (`DelveRegistry.MaxDiamondsPerWeek`); a test asserts the sum.
- Avatars, titles and the pet are granted once. Crossing the milestone again in a
  later week pays only its gold and diamonds.
- The upper thresholds are estimates from two real strikes. They are constants;
  after the first full week they are re-measured from the production attempt rows.

Permanent titles: 150k **Serpent Bane**, 400k **Chainer of the Dark**.

### 3.3 Weekly leaderboard (end of week)

Ranked by weekly damage, everyone (registered or not), same rows as the board
(`WorldBossBoard.RankedAsync`).

- **Temporary titles for the top 3**, each different, held until the next payout
  and then moved to the next week's top 3:
  1st **Svarog's Hammer**, 2nd **Dazhbog's Light**, 3rd **Simargl's Wing**.
- Gold by rank: 1st 3 h of income, 2nd 2 h, 3rd 1 h.
- **No diamonds**: the milestones already pay the weekly 60.
- The percentile brackets and `perun_avatar_reward_token` are removed.

The title system needs a way to revoke a title (today titles are only granted).

### 3.4 The pet: Black Serpent

- `pet_black_serpent`, art `Black Serpent.jpg`, new `PetSource.WorldBoss`.
- **+12% damage, +12% critical damage.** Same 24% total as the Phoenix, the
  strongest single bonuses in the game, stronger than the Samhain pets.
- No world boss damage bonus: a pet that raises the damage that earns it would compound.
- Granted once; never a duplicate (the 1M milestone pays nothing more after that).
- `PowerCeilingTests` gets the new lever.

### 3.5 Gold scaled to income

    hourly income = sum(GoldIncomeDaily over the last 7 days, earned sources) / 168
    reward        = max(20,000, hourly income x hours)

Earned sources: Combat, CombatAway, TownHall, AutoSalvage, ChestSale.
Excluded: Market, LoginReward, Mail, GuildPayout, Delve, Starter, Other, QuestLine -
trades and rewards, so a reward never raises its own base.

Example: at 8M gold an hour, the five milestones pay ~54M a week. Across a
168-hour idle week that is about 4% on top of income. A new player gets the 20,000 floor.

### 3.6 Other small gold rewards

Inventory of fixed gold rewards today:

| Where | Today | Proposal |
|---|---|---|
| Daily login | 25,500 gold a week (three rotating matrices) | escalate by week, see below |
| Samhain seasonal boss tiers | 25k-1M fixed | switch to income-scaled (3.5) for future events; leave the running Samhain event alone |
| Quest line | 1/4 of the region's Delve fee | already scales with region - leave |
| Guild weekly payout | from the treasury | leave |
| Delve consolation | from the entry fee | leave |
| Achievements | diamonds, not gold | leave |

**Daily login escalation:** week 1 as today, week 2 higher, week 3 higher again,
then week 3 stays forever.

Decided: **x2 in week 2, x3 from week 3**, counted by **completed 7-day login cycles
over the account's lifetime** (not reset by a broken streak, so a returning beginner is
not sent back to week 1). 25,500 -> 51,000 -> 76,500 gold a week.

## 4. Sandbagging: detection and penalty

A registered player could play badly on Monday/Tuesday to keep HP low, then play
well from Wednesday. A deliberate loser already misses milestones; the detector adds
a penalty.

**Signal: the minigame skill score `s`** (best 4 of 5 spears, Seam = 1), not damage.
Damage rises during the week for honest players, because the crowd finds the weak
plate by breaking the others, and gear improves. `s` does not depend on the weak plate.

A correct parry read always lands its counter spear as a Seam
(`ShieldWheelScorer`, counter branch). So an honest player who knows the game scores
on Monday too; a sandbagger has to throw away free counters.

Flagged when **all three** hold:

1. Mean `s` over Monday/Tuesday played strikes is near zero (threshold e.g. <= 0.1).
2. Mean `s` from Wednesday is high (e.g. >= 0.6) over at least 3 played strikes.
3. The player showed the skill in an **earlier** week (some week with mean `s` >= 0.6).
   This protects a new player who is still learning on Monday.

Auto-strikes (no minigame) are ignored by the detector.

Penalty: 25% of the kill reward; a row in `account_penalties` (so it can be found and
reversed); and one World chat message from **Chernobog**, chosen at random:

- "{name} struck me like a sleepy moth on Monday. The forge thanks you for your... restraint."
- "Ha! {name} hid their strength until Wednesday. Svarog saw. So did I."
- "{name} tried to make me smaller. I only grow fonder of mortals like you."

Thresholds are constants and get tuned on real data; a test pins that an honest
learning curve (low Monday, high Wednesday, no earlier skilled week) is never flagged.

## 5. Known traps this touches

- **Full mailbox loses rewards** (`PayEncounterRewardsAsync` skips at 50 items). Milestones
  add mail. Grants should go through `PendingGrantOutbox` or bypass the cap for earned,
  non-repeatable rewards - decide in the plan.
- **Calendar-gated feature**: the dev window tool (`POST /api/v1/dev/worldboss/window`)
  needs a way to jump registration -> reveal, or `exercise.mjs` can only test one phase.
- **Index/order on the wire**: no new ordered list crosses the wire; avatars, titles and
  pets go by slug.
- **New wire fields** (registered flag, revealed HP, phase) must be hydrated at login
  (`StateUpdatePacketFieldCoverageTests`) - or travel on the REST board, which is preferred.
- The chat must support a non-player sender ("Chernobog") - to verify.

## 6. Out of scope

Breeding (round 115, parked); Guild Wars; changes to the shield wheel itself.
