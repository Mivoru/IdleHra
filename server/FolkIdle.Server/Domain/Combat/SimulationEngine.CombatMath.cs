using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Network;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: moved out of SimulationEngine.cs unchanged (2026-11-20 split,
    // see docs/architecture/CURRENT_IMPLEMENTATION_STATE.md).
    public partial class SimulationEngine
    {
        internal static bool OrdinaryKillPaysDiamond(double roll) => roll < OrdinaryKillDiamondChance;

        /// <summary>Whether a kill of this monster rolls the ordinary-kill diamond at all (regional bosses do not).</summary>
        internal static bool KillCanPayDiamond(int monsterId) => !ContentRegistry.IsRegionalBoss(monsterId);

        /// <summary>
        /// The share of each landed monster blow reflected back: the set's 20%
        /// plus the gear's thorns_pct (roadmap 3.6). The ONE place they sum, read
        /// by the live tick and by HuntingProjection alike.
        /// </summary>
        internal static double ThornsFractionFor(in TickStatePayload payload, in CombatStats stats)
            => (stats.SetThornsReflectionActive ? ThornsReflectionFraction : 0.0)
               + Math.Max(0, AffixFamilies.Totals(in payload).ThornsTenthsPct) / 1000.0;

        /// <summary>
        /// The combat stats the live tick fights with. ONE derivation, shared by
        /// RunCombatTick and the hunting advisor (HuntingProjection).
        /// </summary>
        /// <remarks>
        /// Modul: the Fortitude bough's armour is applied here, not inside
        /// StatsCalculator, because the skill tree lives on the payload and
        /// Calculate does not take it - the same reason its health half sits in
        /// EffectiveMaxMilliHpFor. The broadcast reads this function too, so
        /// the armour the client shows is the armour the tick mitigates with.
        /// </remarks>
        internal static CombatStats LiveCombatStats(in TickStatePayload payload)
        {
            int activeAgePhase = payload.Slot1_CharacterId != System.Guid.Empty ? payload.Slot1_AgePhase : 1;
            CombatStats stats = StatsCalculator.Calculate(payload.STR, payload.DEX, payload.CON, payload.LCK, payload.ActiveOffensivePotionId, payload.ActiveDefensivePotionId, activeAgePhase, payload.CompletedAreaFlags, ActiveRaceId(in payload), payload.HumanMasteryLevel, payload.VilaMasteryLevel, payload.DraugrMasteryLevel, AffixFamilies.Totals(in payload), payload.IsEpicMutation, TraitTotals.From(payload.TraitMask), payload.CachedSetIds);
            ApplyTreeStats(ref stats, in payload);
            // The inheritance milestones' combat half (crit chance, crit damage,
            // armour): here, so the live tick, the away projection, the dungeon
            // and the stats sheet all fight with them.
            InheritanceMilestones.ApplyCombatStats(ref stats, in payload);
            // Aptitude milestones: Endurance's Stout Frame (block; the tick's
            // 75% block cap still applies in PlayerBlockFraction).
            stats.BlockStrengthPct += AptitudeMilestones.BlockPct(payload.Aptitude_Endurance);
            // Nav's Mokosh's thread research (NavBlessingsCache).
            stats.LootLuckPct += NavBlessingsCache.LootLuckPct(payload.PlayerId);
            return stats;
        }

        /// <summary>
        /// The skill tree's share of the combat stats: armour, dodge, block,
        /// accuracy and Living Fortress. LiveCombatStats and the Character
        /// screen's roster both call this, so the armour a player reads is the
        /// armour the tick mitigates with.
        /// </summary>
        /// <remarks>
        /// Modul: ONE DERIVATION FOR BOTH (rework slice 2.5 cleanup). The roster
        /// used to apply today's Fortitude armour by itself and never learned
        /// about the War half, so after the switch it showed every character
        /// without Ironhide, Evasion, Shield Wall or Steady Hand.
        /// </remarks>
        internal static void ApplyTreeStats(ref CombatStats stats, in TickStatePayload payload)
        {
            var war = WarTreeRegistry.Resolve(in payload);
            if (war.ArmourTenthsPct > 0 && stats.FlatPhysicalArmor > 0)
                stats.FlatPhysicalArmor = (int)(stats.FlatPhysicalArmor + (long)stats.FlatPhysicalArmor * war.ArmourTenthsPct / 1000L);
            stats.DodgeChancePct += war.DodgeTenths / 10f;
            stats.BlockStrengthPct += war.BlockTenths / 10f;
            stats.AccuracyRating += war.AccuracyTenths / 10;
            // Living Fortress: the crit chance becomes block, and the cap rises.
            if (war.LivingFortress)
            {
                stats.BlockStrengthPct += CritChanceBeforeKeystones(in payload, in stats);
                stats.BlockCapBonusPct += WarEffects.LivingFortressBlockCapBonusPct;
            }
        }

        /// <summary>
        /// The combat health bar, in milli-HP. Moved out of RunCombatTick
        /// unchanged (task 78) so the hunting advisor reads the same bar.
        /// </summary>
        /// <summary>
        /// A milli-HP bar, never below zero. Modul: this used to clamp into
        /// int.MaxValue (BarAsInt), which silently capped every character at
        /// 2,147,483 HP - see TickStatePayload.PlayerHp.
        /// </summary>
        internal static long Bar(long milliHp) => Math.Max(0L, milliHp);

        /// <summary>Current health plus a heal, capped at the bar; a negative heal heals nothing.</summary>
        internal static long HealClamped(long current, long heal, long max) => Math.Min(max, current + Math.Max(0L, heal));

        internal static long EffectiveMaxMilliHpFor(in TickStatePayload payload, in CombatStats combatStats)
        {
            var lineage = LineageOf(in payload);

            // Modul: the base pool is a CURVE now, not a constant - see
            // ProgressionEngine.BaseMilliHpForLevel. A flat 100 against monster
            // attack that goes up 4.2x a region is why region 5 one-shot
            // everybody.
            long baseMilliHp = ProgressionEngine.BaseMilliHpForLevel(payload.CurrentLevel);
            long effectiveMilliHp = baseMilliHp + (baseMilliHp * lineage.HpScalePerLevelPct * payload.CurrentLevel / 100) + (combatStats.MaxHp * 1000L);
            // Modul: inheritance, applied to the whole pool for the same reason
            // the damage bonus is applied last - a flat addition would stop
            // mattering.
            effectiveMilliHp += effectiveMilliHp * InheritanceRegistry.GetBonusPct(payload.Inherit_MaxHp) / 100L;
            // The character's pet (a monster pet's +health), on the whole pool
            // like inheritance - a flat addition would stop mattering.
            effectiveMilliHp += effectiveMilliHp * payload.CachedAffixTotals.HpTenthsPct / 1000L;
            // The War half's Vitality, in the same additive-percent layer.
            effectiveMilliHp += effectiveMilliHp * (long)WarTreeRegistry.Resolve(in payload).MaxHpTenthsPct / 1000L;
            // Rebirth Trials: Frailty's cut while active, a finished trial's
            // reward after - one percent, on the whole pool.
            effectiveMilliHp += effectiveMilliHp * RebirthTrials.MaxHpPct(payload.ActiveRebirthTrial, payload.CompletedRebirthTrials) / 100L;

            // Modul: the bloodline's health - Endurance, then health traits -
            // layered the same additive-percent way as inheritance and the tree
            // above. Shared with the offline projection: see BloodlineBonuses.
            return BloodlineBonuses.ApplyMaxHp(effectiveMilliHp, payload.Aptitude_Endurance, TraitTotals.From(payload.TraitMask));
        }

        /// <summary>The live swing interval: the stat, then Relentless, Frenzy and Ruin's price.</summary>
        internal static int LiveAttackIntervalMs(in TickStatePayload payload, in CombatStats combatStats)
        {
            int playerAttackSpeedMs = CombatDamageModel.AttackIntervalMs(in combatStats);
            var warSpeed = WarTreeRegistry.Resolve(in payload);
            // A negative sum is a keystone's price (Juggernaut): a slower swing.
            if (warSpeed.AttackIntervalTenthsPct != 0)
            {
                playerAttackSpeedMs = Math.Max(200, (int)(playerAttackSpeedMs * (1f - warSpeed.AttackIntervalTenthsPct / 1000f)));
            }
            // Frenzy: a share of the crit chance, taken off the interval.
            if (warSpeed.FrenzyTenths > 0)
            {
                float cut = LiveCritChancePct(in payload, in combatStats) / 100f * (warSpeed.FrenzyTenths / 1000f);
                playerAttackSpeedMs = Math.Max(200, (int)(playerAttackSpeedMs * (1f - Math.Clamp(cut, 0f, 0.9f))));
            }
            // Momentum (skill tree v3 step B): each kill in a row cuts the
            // interval, never against a boss - a streak is a farming thing.
            int momentum = MomentumTenthsPct(in payload, in warSpeed);
            if (momentum > 0)
            {
                playerAttackSpeedMs = Math.Max(200, (int)(playerAttackSpeedMs * (1f - momentum / 1000f)));
            }
            // Ruin's price: every source of attack speed counts at
            // WarEffects.RuinSpeedSharePct. The price grows with the speed a
            // character has while the gain does not, so a fast build pays most:
            // at 75% and x1.6 a geared region-5 character LOST 3.6% of its kills
            // (owner decision 2026-10-30: x1.7 and 85%, never a loss, measured
            // +1.8% fast to +5.7% slow - Perfect Sequence's size).
            if (warSpeed.Ruin && playerAttackSpeedMs < BaseAttackIntervalMs)
            {
                playerAttackSpeedMs = BaseAttackIntervalMs - (BaseAttackIntervalMs - playerAttackSpeedMs) * WarEffects.RuinSpeedSharePct / 100;
            }
            return playerAttackSpeedMs;
        }

        /// <summary>Whether the payload's current monster is a region's boss.</summary>
        internal static bool FightingRegionBoss(in TickStatePayload payload) => IsRegionBossId(payload.CurrentMonsterId);

        /// <summary>Whether this monster id is a region's boss.</summary>
        internal static bool IsRegionBossId(int id)
        {
            if (id <= 0) return false;
            int tier = ContentRegistry.GetMonsterRegionTier(id);
            if (tier < RaceUnlockRegistry.FirstRegion || tier > RaceUnlockRegistry.LastRegion) return false;
            return RaceUnlockRegistry.GetRegionBossMonsterId(tier) == id;
        }

        /// <summary>Momentum's interval cut now, in tenths of a percent: the streak times the rank's value, nothing against a boss.</summary>
        internal static int MomentumTenthsPct(in TickStatePayload payload, in WarEffects war)
        {
            if (war.MomentumTenthsPerKill <= 0 || payload.KillStreak <= 0 || FightingRegionBoss(in payload)) return 0;
            return Math.Min((int)payload.KillStreak, WarEffects.MomentumMaxStreak) * war.MomentumTenthsPerKill;
        }

        /// <summary>The monster's armour after this many Sunder stacks.</summary>
        internal static int SunderedArmour(int armour, int stacks, in WarEffects war)
        {
            if (war.SunderTenthsPerStack <= 0 || stacks <= 0 || armour <= 0) return armour;
            int cut = Math.Min(1000, Math.Min(stacks, WarEffects.SunderMaxStacks) * war.SunderTenthsPerStack);
            return (int)((long)armour * (1000 - cut) / 1000L);
        }

        /// <summary>Clears the fight state that lives for one monster: Sunder and the bleed.</summary>
        internal static void ClearMonsterFightState(ref TickStatePayload payload)
        {
            payload.SunderStacks = 0;
            payload.BleedSecondsLeft = 0;
            payload.BleedMilliPool = 0;
        }

        /// <summary>
        /// Double Strike's chance as a probability. The live roll and the
        /// projection both ask this.
        /// </summary>
        internal static double DoubleStrikeChance(in TickStatePayload payload)
            => Math.Clamp(WarTreeRegistry.Resolve(in payload).DoubleStrikeTenths / 1000.0, 0.0, 1.0);

        /// <summary>Bloodthirst in tenths of a percent of damage dealt.</summary>
        internal static long BloodthirstTenths(in TickStatePayload payload)
            => WarTreeRegistry.Resolve(in payload).BloodthirstTenths;

        /// <summary>
        /// A Riposte landing: damage off the monster's bar, logged as the
        /// player's hit with its Proc. A kill it causes is resolved where a
        /// thorns kill is, at the end of the tick.
        /// </summary>
        private static void Riposte(ref TickStatePayload payload, in CombatStats stats, in MonsterDefinition monster)
        {
            long dealt = RiposteMilliDamage(in payload, in stats, in monster, payload.CachedEffectiveMilliAttack);
            if (dealt <= 0) return;
            payload.CurrentMonsterHp -= dealt;
            CombatEventFeed.Publish(
                payload.PlayerId,
                payload.CurrentMonsterId,
                Network.ResponseCombatEventPacket.KindPlayerHit,
                (int)Math.Min(int.MaxValue, dealt / 1000),
                (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L),
                0,
                Network.SkillProc.Riposte);
        }

        /// <summary>
        /// The codex damage multiplier a swing lands with: the cached account
        /// figure, its bonus part raised by the War half's Codex Scholar. The
        /// live hit, Riposte and the projection all multiply by this.
        /// </summary>
        internal static float LiveCodexDamageMultiplier(in TickStatePayload payload)
        {
            float codex = payload.CachedCodexDamageMultiplier;
            int scholar = WarTreeRegistry.Resolve(in payload).CodexBonusTenthsPct;
            return scholar > 0 && codex > 1f ? 1f + (codex - 1f) * (1f + scholar / 1000f) : codex;
        }

        /// <summary>Whether the payload holds Golden Fleece.</summary>
        internal static bool HasGoldenFleece(in TickStatePayload payload)
            => WarTreeRegistry.Resolve(in payload).GoldenFleece;

        /// <summary>Whether the payload holds Last Stand.</summary>
        internal static bool HasLastStand(in TickStatePayload payload)
            => WarTreeRegistry.Resolve(in payload).LastStand;

        /// <summary>
        /// Riposte's chance per MONSTER swing: a dodge, or a connecting swing
        /// the block takes a share of - block is a fraction off a hit, not a
        /// roll, so that fraction is the chance of an answer.
        /// </summary>
        internal static double RiposteChance(in CombatStats stats)
        {
            double hit = MonsterHitChance(in stats);
            return (1.0 - hit) + (hit * PlayerBlockFraction(in stats));
        }

        /// <summary>One Riposte's damage to the monster, in milli-HP: a share of the weapon blow through armour and the codex.</summary>
        internal static long RiposteMilliDamage(in TickStatePayload payload, in CombatStats stats, in MonsterDefinition monster, long rawMilliAttack)
        {
            int share = WarTreeRegistry.Resolve(in payload).RiposteTenthsPct;
            if (share <= 0) return 0L;
            long raw = rawMilliAttack * share / 1000L;
            long net = CombatDamageModel.Mitigate(raw, monster.Armor, CombatDamageModel.MonsterArmourHalvingConstant(monster.RegionTier), stats.FlatArmorPenetration);
            return (long)(net * LiveCodexDamageMultiplier(in payload));
        }

        /// <summary>
        /// Know Your Prey's bonus against this monster, in tenths of a percent:
        /// the node's value against a monster of the highest unlocked region,
        /// 0 anywhere else. The live hit, HuntingProjection and CombatGoldReward
        /// all ask here.
        /// </summary>
        internal static int KnowYourPreyTenthsPct(in TickStatePayload payload, in MonsterDefinition monster)
        {
            int bonus = WarTreeRegistry.Resolve(in payload).KnowYourPreyTenthsPct;
            if (bonus <= 0) return 0;
            return monster.RegionTier == RegionUnlockGate.HighestUnlockedRegion(payload.DefeatedRegionBossMask) ? bonus : 0;
        }

        /// <summary>
        /// Crit chance in percent, with the Precision branch, clamped - and the
        /// War keystones: Perfect Sequence caps the chance at 50 (its PerfectEvery-th swing
        /// crits on its own), Living Fortress takes it to 0 (it became block).
        /// </summary>
        internal static float LiveCritChancePct(in TickStatePayload payload, in CombatStats combatStats)
        {
            var war = WarTreeRegistry.Resolve(in payload);
            if (war.LivingFortress) return 0f;
            float chance = CritChanceBeforeKeystones(in payload, in combatStats);
            // Modul: Counterstroke, a share of dodge plus block as crit chance.
            // Added HERE, past Living Fortress, and not in
            // CritChanceBeforeKeystones: Living Fortress turns that figure into
            // block, so reading block inside it would feed block into itself.
            // combatStats is LiveCombatStats, so Evasion and Shield Wall count.
            if (war.CounterstrokeTenths > 0)
            {
                float defence = Math.Max(0f, combatStats.DodgeChancePct) + Math.Max(0f, combatStats.BlockStrengthPct);
                chance = Math.Min(MaxCritChancePct, chance + defence * war.CounterstrokeTenths / 1000f);
            }
            return war.PerfectSequence ? Math.Min(chance, WarEffects.PerfectOtherCritCapPct) : chance;
        }

        private static float CritChanceBeforeKeystones(in TickStatePayload payload, in CombatStats combatStats)
            => Math.Clamp(
                combatStats.CritChancePct
                    + WarTreeRegistry.Resolve(in payload).CritChanceTenths / 10f,
                0f,
                MaxCritChancePct);

        /// <summary>A crit's multiplier before Double Strike: the stat, Cruelty, Guile.</summary>
        internal static float LiveCritMultiplier(in TickStatePayload payload, in CombatStats combatStats)
            => StatsCalculator.ComputeCritMultiplier(combatStats)
                + (WarTreeRegistry.Resolve(in payload).CritDamageTenthsPct / 1000f)
                // Aptitude milestones: Strength's Keen Edge.
                + AptitudeMilestones.CritDamageBonus(payload.Aptitude_Strength);

        // Modul: THE MONSTER'S SWING, ONE DEFINITION FOR EVERY PATH THAT PRICES IT.
        //
        // RunCombatTick rolls these; HuntingProjection and the offline
        // projection (OfflineSimulationEngine) take their expectation. The
        // offline path used to hold its own copy of the incoming side that had
        // never learned about dodge, block, the 1,000 floor or the Dreadnought
        // cap, so an hour away priced every monster swing 10-25% dearer than
        // the live tick did (task 78, "found on the way"). The helpers below
        // are the tick's own steps, moved out unchanged.

        /// <summary>Step 1: the monster's chance to connect. Monsters carry no accuracy; the player's dodge is the only term.</summary>
        /// <remarks>
        /// Modul: THE FIRST 5.26 POINTS OF DODGE USED TO DO NOTHING. This was
        /// `clamp(100 / (100 + dodge), 5%, 95%)`: with no dodge the raw value is
        /// 100%, the clamp pulls it to 95%, and it stays pinned there until
        /// dodge reaches 5.26 - so a potion, a single affix roll (0.5-2.1) and
        /// most of Nimble (+4) changed nothing, and a Fairy's +10 was worth
        /// about 4.7. Measured in RaceUnitMeasurementTests, 2026-10-29. The
        /// curve is now shifted down by the 5% the ceiling used to take, so
        /// zero dodge still means 95% and every point after it counts (owner,
        /// 2026-10-29). BossGearBenchmark calls this too - one curve.
        /// </remarks>
        internal static float MonsterHitChance(in CombatStats combatStats)
            => Math.Clamp(100f / (100f + Math.Max(0f, combatStats.DodgeChancePct)) - MonsterHitCeilingGap, 0.05f, 0.95f);

        /// <summary>Step 2: 5% base + 0.5% per region tier.</summary>
        internal static float MonsterCritChance(int monsterRegionTier)
            => 0.05f + (monsterRegionTier * 0.005f);

        /// <summary>A monster crit's multiplier, less Vodnik's CritMitigationPct, floored at 1.0.</summary>
        internal static float MonsterCritMultiplier(in CombatStats combatStats)
            => Math.Max(1.0f, RaceBonusRegistry.MonsterCritMultiplier - (combatStats.CritMitigationPct / 100f));

        /// <summary>The share of a post-armour hit the player's block takes off, capped at 75%.</summary>
        internal static float PlayerBlockFraction(in CombatStats combatStats)
            => Math.Clamp(combatStats.BlockStrengthPct / 100f, 0f, 0.75f + combatStats.BlockCapBonusPct / 100f);

        /// <summary>
        /// Steps 3-5 of a CONNECTING monster swing: armour, then block, then the
        /// 1,000 milli-HP floor, then the Eternal Dreadnought per-hit ceiling.
        /// </summary>
        internal static long LandedMonsterMilliDamage(
            long attackPower, float monsterCritMult, in CombatStats combatStats, int monsterRegionTier, long effectiveMaxMilliHp)
        {
            long rawDamage = (long)(attackPower * 1000L * monsterCritMult);
            long armorMitigatedDamage = CombatDamageModel.Mitigate(
                rawDamage,
                combatStats.FlatPhysicalArmor,
                CombatDamageModel.PlayerArmourHalvingConstant(monsterRegionTier));
            long finalDamage = Math.Max(1000L, (long)(armorMitigatedDamage * (1f - PlayerBlockFraction(in combatStats))));
            if (combatStats.SetDamageCapActive)
            {
                long damageCeiling = (long)(effectiveMaxMilliHp * SetDamageCapMaxHpFraction);
                if (damageCeiling > 0 && finalDamage > damageCeiling)
                {
                    finalDamage = damageCeiling;
                }
            }
            return finalDamage;
        }

        /// <summary>
        /// One monster swing in expectation: the hit roll and the crit roll
        /// replaced by their probabilities, each outcome landed through
        /// LandedMonsterMilliDamage. What the hunting advisor and the offline
        /// projection both charge per swing.
        /// </summary>
        internal static double ExpectedMonsterMilliDamagePerSwing(
            in CombatStats combatStats, int monsterId, byte defeatedRegionBossMask, long effectiveMaxMilliHp)
        {
            int region = ContentRegistry.GetMonsterRegionTier(monsterId);
            long attackPower = BossFirstClearRules.AttackPowerFor(defeatedRegionBossMask, monsterId);
            float critChance = MonsterCritChance(region);
            double normal = LandedMonsterMilliDamage(attackPower, 1.0f, in combatStats, region, effectiveMaxMilliHp);
            double crit = LandedMonsterMilliDamage(attackPower, MonsterCritMultiplier(in combatStats), in combatStats, region, effectiveMaxMilliHp);
            return MonsterHitChance(in combatStats) * (((1.0 - critChance) * normal) + (critChance * crit));
        }

        /// <summary>
        /// Boss-bane Philtre (PotionRegistry): extra damage against a region's
        /// boss, in tenths of a percent, or 0. The live swing and the hunting
        /// projection both ask here; the world boss reads the slot itself.
        /// </summary>
        internal static int BossbaneTenthsPctFor(in TickStatePayload payload, in MonsterDefinition monster)
        {
            int pct = PotionRegistry.MagnitudeOf(payload.ActiveDefensivePotionId, PotionKind.Bossbane);
            if (pct <= 0 || monster.RegionTier < RaceUnlockRegistry.FirstRegion || monster.RegionTier > RaceUnlockRegistry.LastRegion) return 0;
            return RaceUnlockRegistry.GetRegionBossMonsterId(monster.RegionTier) == monster.Id ? pct * 10 : 0;
        }

        internal static int LiveKillXpMultiplierPct(in TickStatePayload payload, int localXpMultiplier)
        {
            int finalXpMultiplier = localXpMultiplier;
            if (payload.CurrentLevel < 50 && payload.CachedMentorCount > 0)
            {
                finalXpMultiplier += payload.CachedMentorCount * 5;
            }

            finalXpMultiplier += RaceMasteryResolver.GetHumanXpBonusPct(payload.HumanMasteryLevel);
            finalXpMultiplier += LegacyPerkResolver.GetXpBonusPct(payload.CachedLegacyPerks);
            finalXpMultiplier += InheritanceRegistry.GetBonusPct(payload.Inherit_XpGain);
            // The War half's Quick Study, in the same additive layer.
            finalXpMultiplier += WarTreeRegistry.Resolve(in payload).XpTenthsPct / 10;
            var hearthXp = HearthTreeRegistry.Resolve(in payload);
            finalXpMultiplier += hearthXp.XpTenthsPct / 10;
            if (hearthXp.MentorXpTenthsPct > 0 && AChildWorksInASlot(in payload))
                finalXpMultiplier += hearthXp.MentorXpTenthsPct / 10;
            finalXpMultiplier += FolkIdle.Server.Engine.GuildBonusesCache.GetBuffTier(payload.GuildId, "Exp") * 2;
            // The character's pet (PetRegistry), on the active slot's totals.
            finalXpMultiplier += payload.CachedAffixTotals.XpTenthsPct / 10;
            // Task 114: a reborn account re-levels faster below its best level.
            // Here, so the live kill, the offline window and the hunting
            // projection all pay it from one place.
            finalXpMultiplier += RebirthRules.ReLevelXpBonus(payload.CurrentLevel, payload.PeakLevel);
            // Elixir of the Hunt (PotionRegistry), the rewards slot - here for the
            // same reason as the line above: one place every path asks.
            finalXpMultiplier += PotionRegistry.MagnitudeOf(payload.ActiveOffensivePotionId, PotionKind.Hunt);
            // Rebirth Trials: a finished trial's reward in the additive layer,
            // and the Slow Hand's cut on the WHOLE multiplier - a cut in the
            // additive layer would shrink to nothing against a stack of
            // bonuses, and the rule says "40% less".
            finalXpMultiplier += RebirthTrials.XpPctReward(payload.CompletedRebirthTrials);
            // Nav's Memory of the dead research (NavBlessingsCache).
            finalXpMultiplier += NavBlessingsCache.XpPct(payload.PlayerId);
            int trialXpCut = RebirthTrials.XpPctDuring(payload.ActiveRebirthTrial);
            if (trialXpCut < 0) finalXpMultiplier = finalXpMultiplier * (100 + trialXpCut) / 100;
            return finalXpMultiplier;
        }

        internal static long EffectiveMilliAttackFor(ref TickStatePayload payload, in CombatStats combatStats, int damageScalePerLevelPct)
        {
            long effective = StatsCalculator.ComputeEffectiveMilliAttack(
                in combatStats,
                damageScalePerLevelPct,
                payload.CurrentLevel,
                InheritanceRegistry.GetBonusPct(payload.Inherit_Damage)
                    + (FolkIdle.Server.Engine.GuildBonusesCache.GetBuffTier(payload.GuildId, "Damage") * 2)
                    // Task 88: Renown, the permanent rebirth bonus - a curve
                    // under a ceiling (RebirthRules), in PowerCeilingTests.
                    + RebirthRules.DamageBonusPct(payload.RenownedRebirths)
                    // Rebirth Trials: the Trial of Iron's reward.
                    + RebirthTrials.DamagePctReward(payload.CompletedRebirthTrials));

            // Modul: Prestige "combat speed" perk (LegacyPerkResolver) -
            // applied as a flat percent boost to effective damage output per
            // attack rather than to the attack-interval tick cadence itself
            // (AttackIntervalMs governs the shared per-monster pacing loop and
            // is not a per-player value), which is a materially equivalent DPS
            // increase without touching that shared cadence math.
            int legacyCombatSpeedBonusPct = LegacyPerkResolver.GetCombatSpeedBonusPct(payload.CachedLegacyPerks);
            if (legacyCombatSpeedBonusPct > 0)
            {
                effective += (effective * legacyCombatSpeedBonusPct) / 100;
            }

            // Modul: the bloodline - the Strength aptitude, then attack traits -
            // on the pre-armour figure. Shared with the offline projection, which
            // used to skip Strength entirely: see BloodlineBonuses.
            effective = BloodlineBonuses.ApplyAttack(effective, payload.Aptitude_Strength, TraitTotals.From(payload.TraitMask));

            // The War half's Edge and Heavy Hand: their own multiplier, so they
            // do not dilute into the inheritance/guild/Renown group above.
            int warDamage = WarTreeRegistry.Resolve(in payload).DamageTenthsPct;
            // A negative sum is a keystone's price (Midas, Sage, Bastion), floored at a tenth.
            if (warDamage != 0) effective = Math.Max(effective / 10, effective + effective * warDamage / 1000L);

            return effective;
        }
    }
}
