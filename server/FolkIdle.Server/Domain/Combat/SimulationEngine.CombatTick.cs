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
        /// <summary>
        /// A death that no Death Ward caught: the character is back at a full
        /// bar, the fight and the activity are over, and the death is recorded.
        /// Shared by RunCombatTick and the offline projection, so a character
        /// that would have died while watched stops while away as well.
        /// </summary>
        internal static void ApplyCombatDeath(ref TickStatePayload payload, int deathMonsterId, long effectiveMaxHp)
        {
            // Read before the respawn clears CurrentMonsterId, which the check needs.
            bool seasonalFight = TryGetSeasonalBoss(in payload, out _);
            // A death ends the streak and the monster's fight state.
            payload.KillStreak = 0;
            ClearMonsterFightState(ref payload);
            long seasonalReturn = payload.SeasonalBossReturnActivityId;

            payload.PlayerHp = effectiveMaxHp;
            payload.CurrentMonsterId = 0;
            payload.CurrentMonsterHp = 0;
            payload.CombatTargetTickAccumulator = 0;
            payload.ActiveActivityId = 0;
            // Task 87: dying ends an Ascension attempt, as it ends the fight.
            DisarmAscension(ref payload);
            // Modul: halt reasons. A full-HP character sitting idle
            // looked exactly like one that had never been deployed.
            payload.ActivityHaltReason = Network.ActivityHaltReason.Died;

            // Modul: WHO killed you, not just that something did.
            //
            // Passed in because the respawn above wipes it - CurrentMonsterId
            // is cleared, so by the time any broadcast runs the killer is
            // gone. A death card that cannot name the monster is a shrug.
            payload.LastDeathMonsterId = deathMonsterId;
            payload.LastDeathTick++;
            // Modul: lifetime statistics. The only place in the server
            // where a player death is recognised, so the only place
            // this can be counted. Intercepted lethal damage (the Death
            // Ward branch) is not a death and is not counted.
            payload.LifetimeDeaths++;
            payload.IsDirty = true;

            // Modul: task 85, rule 2 - HERE, not in RunCombatTick, because this
            // is the one death both the live tick and the offline projection
            // reach. The death above is still a death (counted, carded, the
            // Ascension disarmed); the rule only decides where the character
            // goes next instead of idle.
            //
            // A lost seasonal boss fight is the exception: it was one attempt
            // from a window, so the character goes back to its old work rather
            // than one step down from the Cailleach's yardstick boss. Still a
            // death above - counted, and the client's fight window reads it.
            if (seasonalFight)
            {
                ReturnFromSeasonalBoss(ref payload, seasonalReturn);
                return;
            }
            AutomationRules.TryStepDownAfterDeath(ref payload, deathMonsterId);
        }

        /// <summary>
        /// What a kill opens, whoever made it: the location's gathering, the next
        /// region's door (a region boss), and the boss's first-clear mark.
        /// Returns whether this kill was the first clear. Shared by RunCombatTick
        /// and the offline projection.
        /// </summary>
        internal static bool ApplyKillProgression(ref TickStatePayload payload, int monsterId)
        {
            // Modul: reaching a location unlocks its gathering. One kill is
            // the whole requirement - if you can fight here, you can work
            // here. Raised live as well as at hydration so the node list
            // opens up the moment the kill lands, not on next login.
            int killedLocation = ContentRegistry.GetCanonicalLocation(monsterId);
            if (killedLocation > payload.HighestLocationReached)
            {
                payload.HighestLocationReached = killedLocation;
            }

            // Modul: region progression. Felling a region's boss opens the
            // next region - to enter, and to wear its gear. Only ever raised,
            // never recomputed from the codex here: the codex write for this
            // kill has not landed yet. Hydration reconciles from the codex.
            int clearedBossRegion = RaceUnlockRegistry.GetRegionForBossMonsterId(monsterId);
            if (clearedBossRegion > 0
                && clearedBossRegion < RaceUnlockRegistry.LastRegion
                && payload.HighestUnlockedRegion < clearedBossRegion + 1)
            {
                payload.HighestUnlockedRegion = clearedBossRegion + 1;
            }

            // Modul: THE REGION-2 CATCH-22 (owner decision 2026-11-14, option
            // A). Gathering used to open only on the first kill IN a location,
            // but a region's fights need that region's fish: on region-1 fish
            // every region-2 monster projected a death, so no playtest bot ever
            // got through, even at level 30. The boss is the gate now - when it
            // falls, the next region's gathering opens with its door. Same rule
            // at hydration (StateCheckpointManager), so it holds after a login.
            payload.HighestLocationReached = GatheringReach.Raise(
                payload.HighestLocationReached, payload.HighestUnlockedRegion);

            // Modul: and the boss stops being a first clear. Deliberately NOT
            // behind the `< LastRegion` guard above: clearing region 5's boss
            // opens no sixth region, so a mask folded into that condition
            // would leave the last boss permanently at first-clear stats.
            bool wasFirstClear = BossFirstClearRules.IsFirstClearPending(payload.DefeatedRegionBossMask, monsterId);
            payload.DefeatedRegionBossMask = BossFirstClearRules.MarkDefeated(payload.DefeatedRegionBossMask, monsterId);
            return wasFirstClear;
        }

        /// <summary>
        /// One combat tick of rations (FoodRegistry.RationIntervalTicks): count
        /// towards the next ration, and at the interval eat one eligible fish or
        /// go hungry. A hungry character retries every tick, so stocking the
        /// larder ends the hunger at once rather than at the next interval.
        /// HuntingProjection.Advance mirrors this for the offline fight.
        /// </summary>
        internal static void TickRation(ref TickStatePayload payload)
        {
            // No monster standing (between spawns): nothing to count, and
            // nothing to forgive either - hunger is the larder's, not the gap's.
            if (payload.CurrentMonsterId <= 0) return;

            int region = ContentRegistry.GetMonsterRegionTier(payload.CurrentMonsterId);
            int interval = FoodRegistry.RationIntervalTicks(region);
            if (interval <= 0)
            {
                // Region 1 eats no rations, so nobody starves there.
                payload.RationTicksSinceMeal = 0;
                payload.Hungry = false;
                return;
            }

            if (!payload.Hungry && ++payload.RationTicksSinceMeal < interval)
            {
                return;
            }

            int slot = FoodRegistry.PickRationSlot(
                payload.Food1_ItemId, payload.Food1_Count,
                payload.Food2_ItemId, payload.Food2_Count,
                payload.Food3_ItemId, payload.Food3_Count,
                region,
                out int cost);
            if (slot == 0)
            {
                if (!payload.Hungry)
                {
                    payload.Hungry = true;
                    payload.ActivityHaltReason = Network.ActivityHaltReason.OutOfFood;
                }
                return;
            }

            if (slot == 1) payload.Food1_Count -= cost;
            else if (slot == 2) payload.Food2_Count -= cost;
            else payload.Food3_Count -= cost;
            payload.RationTicksSinceMeal = 0;
            if (payload.Hungry)
            {
                payload.Hungry = false;
                if (payload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood)
                {
                    payload.ActivityHaltReason = Network.ActivityHaltReason.None;
                }
            }
        }

        /// <summary>
        /// Thunderer, the Giantslayer crown: a boss fight opens with a free
        /// blow at five times the weapon.
        ///
        /// REUSES PendingSkillDamageMultiplier, which the four removed active
        /// skills left behind - the field was still on the payload and still
        /// consumed on the next hit, with nothing left in the game to set it.
        /// So the crown needed a mechanism that already existed and worked,
        /// and this is it. Expressed as a multiplier on the next swing rather
        /// than as an out-of-band hit, so it goes through the same to-hit,
        /// armour, lifesteal and kill-check path everything else does.
        /// </summary>
        /// <summary>
        /// Task 87: the modifiers in force for the fight the register is in RIGHT
        /// NOW - or none. An armed step counts only while the register holds the
        /// character that armed it and is fighting that region's boss, so a second
        /// slot at the same boss, a gathering trip and a respawn all read as an
        /// ordinary fight.
        /// </summary>
        private static bool TryGetAscensionModifiers(in TickStatePayload payload, out AscensionModifiers modifiers)
        {
            modifiers = default;
            if (payload.AscensionStep == 0) return false;
            if (payload.Slot1_CharacterId != payload.AscensionCharacterId) return false;
            if (payload.CurrentMonsterId != RaceUnlockRegistry.GetRegionBossMonsterId(payload.AscensionRegion)) return false;
            modifiers = BossAscensionRegistry.ModifiersFor(payload.AscensionRegion, payload.AscensionStep);
            return true;
        }

        /// <summary>A spawn's health, raised by an armed Ascension step's health modifier.</summary>
        /// <summary>
        /// A milli-HP blow as the whole hit points the feed and the records carry.
        /// </summary>
        /// <remarks>
        /// Modul: THE PLAYER'S BLOW WAS AN INT OF MILLI-HP (fixed 2026-11-19). It
        /// topped out at 2,147,483 whole hit points, and two of the three endgame
        /// accounts on production had a BestHit of exactly that figure - the swing
        /// had reached the ceiling. On the Ampere box a float-to-int cast
        /// saturates, so extra damage did nothing past it; worse, the war
        /// multipliers added after it (`netDamage += netDamage * pct`) wrapped the
        /// int NEGATIVE, and `CurrentMonsterHp -= netDamage` then HEALED the
        /// monster by up to two billion milli-HP. The blow is a long now; only
        /// the wire's whole-hit-point int is clamped, here.
        /// </remarks>
        internal static int WholeHitPoints(long milliDamage)
        {
            long whole = milliDamage / 1000L;
            return whole > int.MaxValue ? int.MaxValue : whole < 0 ? 0 : (int)whole;
        }

        private static long SpawnHpFor(in TickStatePayload payload)
        {
            long hp = BossFirstClearRules.MaxHpFor(FightBossMask(in payload), payload.CurrentMonsterId, BossFirstClearRules.ReliefFor(in payload)) * 1000L;
            if (TryGetSeasonalBoss(in payload, out var tier)) return BossAscensionRules.ScaleBossHp(hp, tier.Modifiers);
            return TryGetAscensionModifiers(in payload, out var m) ? BossAscensionRules.ScaleBossHp(hp, in m) : hp;
        }

        /// <summary>Disarms an attempt (activity changed, character died). Silent: the wire's AscensionStep reads 0.</summary>
        internal static void DisarmAscension(ref TickStatePayload payload)
        {
            payload.AscensionStep = 0;
            payload.AscensionRegion = 0;
            payload.AscensionCharacterId = System.Guid.Empty;
            // The seasonal boss is armed the same way and ends the same way: a
            // change of activity or a death.
            payload.SeasonalBossTier = 0;
            payload.SeasonalBossCharacterId = System.Guid.Empty;
            payload.SeasonalBossReturnActivityId = 0;
        }

        /// <summary>
        /// A seasonal boss fight is over (won or lost): slot 1 goes back to the
        /// activity it left for it, or idles when that was nothing or that kind of
        /// work has since been taken by another slot. Disarms either way.
        /// </summary>
        internal static void ReturnFromSeasonalBoss(ref TickStatePayload payload, long returnTo)
        {
            if (returnTo > 0 && !CharacterSlotEngine.IsKindOfWorkTakenByParkedSlot(in payload, returnTo))
            {
                ApplyActivityChangeToPayload(ref payload, returnTo);
                return;
            }
            DisarmAscension(ref payload);
            payload.ActiveActivityId = 0;
            payload.CurrentMonsterId = 0;
            payload.CurrentMonsterHp = 0;
            payload.CombatTargetTickAccumulator = 0;
            payload.IsDirty = true;
        }

        /// <summary>
        /// The seasonal boss tier the register is fighting RIGHT NOW, or none -
        /// the same three conditions as an Ascension step: armed, by the
        /// character in the register, against that tier's boss.
        /// </summary>
        internal static bool TryGetSeasonalBoss(in TickStatePayload payload, out SeasonalBossTier tier)
        {
            tier = null!;
            if (payload.SeasonalBossTier == 0) return false;
            if (payload.Slot1_CharacterId != payload.SeasonalBossCharacterId) return false;
            var def = SeasonalBossRegistry.Find(payload.SeasonalBossTier);
            if (def == null || payload.CurrentMonsterId != SeasonalBossRegistry.BossMonsterIdFor(def.Tier)) return false;
            tier = def;
            return true;
        }

        /// <summary>The defeated mask the FIGHT reads: a seasonal tier puts its boss back at the wall.</summary>
        private static byte FightBossMask(in TickStatePayload payload)
            => TryGetSeasonalBoss(in payload, out var tier)
                ? SeasonalBossRegistry.WallMaskFor(payload.DefeatedRegionBossMask, tier.Tier)
                : payload.DefeatedRegionBossMask;

        private static void ArmThundererIfBoss(ref TickStatePayload payload)
        {
            float war = WarTreeRegistry.Resolve(in payload).ThundererTenthsPct / 1000f;
            if (war <= 0f) return;
            if (RaceUnlockRegistry.GetRegionForBossMonsterId(payload.CurrentMonsterId) <= 0) return;

            payload.PendingSkillDamageMultiplier = war;
        }

        private static void EnqueueDrop(System.Collections.Generic.List<CombatLootDropRequest>? sink, CombatLootDropRequest request)
        {
            if (sink != null) sink.Add(request);
            else CombatLootEngine.DropRequestQueue.Enqueue(request);
        }

        /// <summary>
        /// Monster combat resolution for one tick - spawn, the player's swing,
        /// the monster's reply, auto-eat, death, and kill rewards. Extracted
        /// verbatim from ProcessSubTick, where it is what runs when neither
        /// activity branch above it claimed the tick.
        ///
        /// Modul: THE EARLY `return` IN THE DEATH BRANCH IS LOAD-BEARING. It
        /// is the only thing that keeps "died this tick" and "killed something
        /// this tick" mutually exclusive within one call - the kill-reward
        /// block below is reached only because a death returned before it. It
        /// sits inside the `!TryInterceptLethalDamage` block on purpose, so a
        /// Death Ward intercept is NOT a death and can still land a kill in
        /// the same tick. Do not split this into always-both-called
        /// ResolveDeath/ResolveKillReward methods; the exclusivity would have
        /// to be reproduced explicitly and there is nothing today that would
        /// catch it if it were reproduced wrongly. Returning from THIS method
        /// is the same as returning from ProcessSubTick only because this call
        /// is the last statement there - keep it last.
        ///
        /// Callable ONLY from ProcessSubTick. The slot register's
        /// swap/try/finally discipline lives one level up in
        /// ProcessAllSlotSubTicks; a caller that bypasses it leaves the wrong
        /// character's gear, HP and activity "active" for every subsequent
        /// read after any throw in here.
        /// </summary>
        internal static void RunCombatTick(
            ref TickStatePayload payload,
            int localXpMultiplier,
            int localDropMultiplier,
            System.Collections.Concurrent.ConcurrentQueue<GuildWarPointEvent> guildWarPointQueue,
            System.Collections.Concurrent.ConcurrentDictionary<long, LiveSessionContext> liveSessionContexts,
            Random? rng = null)
        {
            // Modul: phase 2b.1 (roadmap 5). Every roll in the tick goes through
            // this one generator. Live passes nothing and gets Random.Shared, as
            // before; a test or the offline simulator passes a seeded Random so
            // an hour can be replayed swing for swing (SeededLiveHourTests).
            rng ??= Random.Shared;

            // Modul: task 85, rule 1. Auto-eat raised OutOfFood on an earlier
            // tick (the larder ran dry mid-fight); if this character has the
            // rule, it leaves the fight now and fishes. Answered at the top of
            // the NEXT tick rather than inside the eat branch, because that
            // branch sits above the death and kill blocks - switching activity
            // there would let the kill block pay a phantom kill against the
            // cleared monster. The offline fight stops at the same tick
            // (HuntingProjection.FightState.StopWhenStarved) and calls the same
            // function.
            if (payload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood
                && AutomationRules.TryGoFishing(ref payload))
            {
                return;
            }

            int fallbackId = payload.ActiveActivityId > ContentRegistry.Monsters.Length ? 1 : (int)payload.ActiveActivityId;

            var lineage = LineageOf(in payload);
            int activeRaceId = ActiveRaceId(in payload);

            // Modul: the stats and the health bar are computed by helpers now
            // (task 78) - the hunting advisor projects a fight from the same
            // payload and must not hold a second copy of either.
            CombatStats combatStats;
            if (t_hasCarriedStats)
            {
                combatStats = t_carriedStats;
                t_hasCarriedStats = false;
            }
            else
            {
                combatStats = LiveCombatStats(in payload);
            }
            long effectiveMilliHp = EffectiveMaxMilliHpFor(in payload, in combatStats);
            // Modul: AN INT BAR (2026-10-11). PlayerHp is int milli-HP, so the
            // bar is clamped into int range here instead of truncated, and every
            // heal below adds in long and caps at the bar (HealClamped). Auto-eat
            // used to do `PlayerHp += heal` in int: a high-tier fish heals MORE
            // than a full bar, so once a bar passed ~1.4 billion milli-HP the
            // sum wrapped negative and EATING KILLED the character. Found
            // calibrating Malakor on the owner's level-106 character, which sat
            // just under that line - any health bonus (inheritance, a +8% pet)
            // made it measurably WEAKER. HealthOverflowTests pins it.
            long effectiveMaxHp = Bar(effectiveMilliHp);

            // Modul: and the client is told what the bar's maximum IS. Every
            // term above this line feeds it, and all of it used to be discarded
            // at the end of the tick - see TickStatePayload.CachedEffectiveMaxHp.
            payload.CachedEffectiveMaxHp = effectiveMilliHp;

            // A rested character starts the fight with a full bar (see
            // TickStatePayload.RestedHpPending).
            if (payload.RestedHpPending)
            {
                payload.PlayerHp = effectiveMaxHp;
                payload.RestedHpPending = false;
            }

            // Modul: the same, for the attack side. Computed unconditionally
            // rather than inside the swing branch, because the world boss
            // resolves outside the swing and must not re-derive it.
            payload.CachedEffectiveMilliAttack =
                EffectiveMilliAttackFor(ref payload, in combatStats, lineage.DamageScalePerLevelPct);

            // Modul: Deferred Part 5 Implementation, Part 2. Active food
            // buff: flat HP regeneration while in combat - 2 percent of
            // effective max HP per second (effectiveMaxHp / 500 per 10Hz
            // tick, minimum 1 milli-HP). Pure integer arithmetic on
            // unmanaged payload fields - zero allocation.
            if (payload.ActiveFoodBuffId > 0 && payload.PlayerHp > 0 && payload.PlayerHp < effectiveMaxHp)
            {
                long regenPerTick = effectiveMaxHp / ConsumableEngine.FoodRegenDivisor;
                if (regenPerTick < 1) regenPerTick = 1;
                payload.PlayerHp = HealClamped(payload.PlayerHp, regenPerTick, effectiveMaxHp);
                if (payload.PlayerHp > effectiveMaxHp) payload.PlayerHp = effectiveMaxHp;
            }

            if (payload.PlayerHp <= 0)
            {
                // Modul: Last Stand, the Cruelty crown - once an hour, the blow
                // that would kill you leaves you at one health instead.
                //
                // ONCE AN HOUR, tracked on the payload against the same tick
                // clock everything else here uses. Without the cooldown it
                // would be flat immortality, because this branch is reached
                // every time the pool empties and death here already only costs
                // a reset to full - so the crown has to buy something narrower:
                // surviving AT one health, which is a real state a player can
                // then heal out of.
                if (HasLastStand(in payload) && payload.LastStandCooldownRemaining <= 0)
                {
                    payload.PlayerHp = 1;
                    payload.LastStandCooldownRemaining = LastStandCooldownTicks;
                }
                else
                {
                    payload.PlayerHp = effectiveMaxHp;
                }
            }

            if (payload.CurrentMonsterId <= 0)
            {
                payload.CurrentMonsterId = fallbackId;
                ArmThundererIfBoss(ref payload);
                payload.CurrentMonsterHp = SpawnHpFor(in payload);
                payload.CombatTargetTickAccumulator = 0;
                payload.AteThisFight = false;
                ClearMonsterFightState(ref payload);
            }

            payload.CombatTargetTickAccumulator++;

            // The bleed: once a second of this fight's clock, an even share of
            // what is left in the pool. Before the swings, so a bleed that kills
            // is a kill like any other (the check further down sees it). A
            // warded boss takes nothing from it, as from a swing.
            if (payload.BleedSecondsLeft > 0 && payload.CurrentMonsterHp > 0 && payload.CombatTargetTickAccumulator % 10 == 0)
            {
                long share = payload.BleedMilliPool / payload.BleedSecondsLeft;
                payload.BleedMilliPool -= share;
                payload.BleedSecondsLeft--;
                bool bleedWarded = TryGetAscensionModifiers(in payload, out var bleedMods) && bleedMods.Ward
                    && payload.CombatTargetTickAccumulator < payload.AscensionWardUntilTenths;
                if (!bleedWarded && share > 0)
                {
                    payload.CurrentMonsterHp -= share;
                    // Whole hit points like every other line; a share under
                    // one point moves the bar and says nothing.
                    if (share >= 1000L)
                    {
                        CombatEventFeed.Publish(
                            payload.PlayerId,
                            payload.CurrentMonsterId,
                            Network.ResponseCombatEventPacket.KindPlayerHit,
                            (int)Math.Min(int.MaxValue, share / 1000L),
                            (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L),
                            Network.ResponseCombatEventPacket.FlagBleed);
                    }
                }
            }

            var activeMonster = ContentRegistry.Monsters[payload.CurrentMonsterId - 1];

            // Player attacks monster
            // Modul: the second copy of the interval formula, and the one that
            // decides what actually happens. See CombatDamageModel - attack
            // speed is a PERCENT like every other *Pct on CombatStats, and this
            // read it as a fraction, so every character past about DEX 20 sat
            // on the 200 ms floor at seven and a half times the intended swing
            // rate.
            int playerAttackSpeedMs = LiveAttackIntervalMs(in payload, in combatStats);

            // Modul: Relentless, the Precision bough - you swing faster, and
            // everything else in this loop scales off how often you hit.
            //
            // Applied to the resolved interval rather than to CombatStats,
            // because the tree is an account bonus and CombatStats describes
            // the gear. Floored at 200 ms, the same floor AttackIntervalMs
            // itself enforces - a swing rate the tick cannot deliver would
            // just silently round away, and the pacing model does not know
            // about sub-tick swings.
            // (Applied inside LiveAttackIntervalMs, shared with the hunting advisor.)

            // Modul: attack cadence fix, 2026-08-02.
            //
            // Was `(accumulator * 100) % intervalMs == 0`, which only fires when
            // elapsed time lands EXACTLY on the interval - so any interval that
            // is not a divisor of a multiple of 100 fired at the least common
            // multiple instead. Attack speed bonuses therefore made the player
            // attack SLOWER: +5% meant 1425ms intended but 5700ms actual (4x
            // slower), +10% was 2x, +33% was 25x. Only bonuses landing on a
            // multiple of 100 behaved. attack_speed_pct is a rerollable affix,
            // so players were paying gold and diamonds to get worse.
            //
            // Now fires when an interval BOUNDARY was crossed during this tick,
            // which is exact for any interval and needs no extra payload state.
            if (HasCrossedInterval(payload.CombatTargetTickAccumulator, playerAttackSpeedMs))
            {
                ResolvePlayerSwing(ref payload, in combatStats, in activeMonster, effectiveMaxHp, rng);
            }

            // Monster attacks player
            // Same boundary-crossing test as the player above - monster
            // AttackIntervalMs values are content-authored and equally free to
            // be non-multiples of the 100ms tick.
            if (payload.CurrentMonsterHp > 0 && HasCrossedInterval(payload.CombatTargetTickAccumulator, activeMonster.AttackIntervalMs))
            {
                ResolveMonsterSwing(ref payload, in combatStats, in activeMonster, effectiveMaxHp, rng);
            }

            // Step 5 (Auto-Eat)
            //
            // Modul: A BITE HAS A COOLDOWN. This used to fire on every tick, so
            // a stocked larder healed ten times a second - up to two hundred
            // percent of the health bar per second at the low end. Nothing
            // could kill a player who owned fish, so a boss was a check on
            // inventory rather than on equipment, and every attempt to make
            // gear the gate failed against it.
            //
            // At one bite every AutoEatCooldownTicks, healing has a ceiling and
            // the question becomes whether the player survives BETWEEN bites -
            // which is what armour and health are for.
            // Modul: Last Stand's hour, counted down on the same path auto-eat
            // uses - one place where per-tick cooldowns tick.
            if (payload.LastStandCooldownRemaining > 0)
            {
                payload.LastStandCooldownRemaining--;
            }

            if (payload.AutoEatCooldownTicks > 0)
            {
                payload.AutoEatCooldownTicks--;
            }
            else if (payload.PlayerHp > 0 && payload.PlayerHp <= (payload.AutoEatThreshold / 100.0f) * effectiveMaxHp)
            {
                int bestFoodIndex = 0;
                long highestHeal = 0;

                // Modul: larder. These were all a hardcoded 50000 milli-HP,
                // which made the "highest-healing food" selection below a tie
                // on every comparison - so it always drained slot 1 first
                // regardless of what the player had loaded, and a tier-10
                // Astral Ambrosia Roast (82000 HP per the GDD) restored the
                // same 50 HP as a tier-1 minnow. FoodRegistry lookups on the
                // cooked id block are integer arithmetic on a static array -
                // no allocation on this per-tick path.
                // Modul: a share of the bar, not a fixed number of points. See
                // FoodRegistry on why an authored heal against a growing wound
                // made food nearly free for three regions and unaffordable in
                // the fifth. effectiveMaxHp is already milli-HP here, which is
                // the unit the registry expects.
                // The Max health 5 milestone's bigger bite, read by the away
                // projection through the same function.
                long heal1 = InheritanceMilestones.FoodHealMilliHp(in payload, payload.Food1_ItemId, (long)effectiveMaxHp);
                long heal2 = InheritanceMilestones.FoodHealMilliHp(in payload, payload.Food2_ItemId, (long)effectiveMaxHp);
                long heal3 = InheritanceMilestones.FoodHealMilliHp(in payload, payload.Food3_ItemId, (long)effectiveMaxHp);

                if (payload.Food1_Count > 0 && heal1 > highestHeal) { bestFoodIndex = 1; highestHeal = heal1; }
                if (payload.Food2_Count > 0 && heal2 > highestHeal) { bestFoodIndex = 2; highestHeal = heal2; }
                if (payload.Food3_Count > 0 && heal3 > highestHeal) { bestFoodIndex = 3; highestHeal = heal3; }

                if (bestFoodIndex == 1) { payload.Food1_Count--; payload.PlayerHp = HealClamped(payload.PlayerHp, highestHeal, effectiveMaxHp); }
                else if (bestFoodIndex == 2) { payload.Food2_Count--; payload.PlayerHp = HealClamped(payload.PlayerHp, highestHeal, effectiveMaxHp); }
                else if (bestFoodIndex == 3) { payload.Food3_Count--; payload.PlayerHp = HealClamped(payload.PlayerHp, highestHeal, effectiveMaxHp); }

                if (bestFoodIndex > 0)
                {
                    payload.AutoEatCooldownTicks = AutoEatCooldownTicks;
                    payload.AteThisFight = true;

                    // Not while hungry: the warning is the ration's to clear.
                    if (payload.ActivityHaltReason == Network.ActivityHaltReason.OutOfFood && !payload.Hungry)
                    {
                        payload.ActivityHaltReason = Network.ActivityHaltReason.None;
                    }
                }
                else
                {
                    if (liveSessionContexts.TryGetValue(payload.PlayerId, out var telemetrySessionContext))
                    {
                        telemetrySessionContext.UpdateAccountId(payload.AccountId);
                        telemetrySessionContext.WriteTelemetryEvent(
                            TelemetryStreamingEngine.PackTelemetryMetric(
                                TelemetryStreamingEngine.KpiAutoEatDepletedHaltHash,
                                payload.ActiveActivityId));
                    }

                    // Modul: an empty larder no longer ENDS the activity.
                    //
                    // This used to stop combat outright the first time health
                    // crossed the auto-eat threshold with nothing to eat -
                    // which meant that at a 50% threshold a character with a
                    // full health bar and no food stopped at half health,
                    // having never been in danger. Food was therefore not a
                    // sustain system but a licence to play at all, and the
                    // only way to notice was that everything went quiet.
                    //
                    // Now the character simply does not heal. It keeps
                    // fighting and, if it loses, dies and respawns - which the
                    // death branch below already handles and which the player
                    // can see happening. The halt reason is still reported so
                    // "you are out of food" remains visible; it is now a
                    // warning rather than a full stop.
                    payload.ActivityHaltReason = Network.ActivityHaltReason.OutOfFood;
                }

                if (payload.PlayerHp > effectiveMaxHp) payload.PlayerHp = effectiveMaxHp;
            }

            TickRation(ref payload);

            if (payload.PlayerHp <= 0)
            {
                // Read before anything below can clear it.
                int deathMonsterId = payload.CurrentMonsterId;

                // Modul: Deferred Part 5 Implementation, Part 2. Death
                // Ward Elixir - intercepts the lethal blow BEFORE the
                // respawn reset: the player revives in place at 20 percent
                // max HP, keeps their activity, and the ward consumes
                // itself. Pure int compare against a cached item id - zero
                // allocation on this combat path.
                if (!ConsumableEngine.TryInterceptLethalDamage(ref payload, effectiveMaxHp))
                {
                    ApplyCombatDeath(ref payload, deathMonsterId, effectiveMaxHp);
                    return;
                }
            }

            if (payload.CurrentMonsterHp <= 0 && payload.ActiveActivityId > 0)
            {
                // Modul: Chilled/Vulnerable are scoped to the currently
                // fought monster - clear on kill/respawn so a status never
                // leaks onto the next monster.
                payload.TargetStatusEffectBitmask = 0;

                // Shared with the hunting advisor - see LiveKillXpMultiplierPct.
                int finalXpMultiplier = LiveKillXpMultiplierPct(in payload, localXpMultiplier);
                // Modul: a HUNGRY character (an unpaid ration, FoodRegistry) is
                // paid half. Here rather than in LiveKillXpMultiplierPct, which
                // the hunting advisor shares and which prices a fed character.
                if (payload.Hungry) finalXpMultiplier = finalXpMultiplier * FoodRegistry.HungryRewardPct / 100;

                // Modul: SCHOLAR PAYS PER KILL (ScholarRate, owner decision
                // 2026-09-30): with the crown's chance this kill is paid as two -
                // XP, gold, loot, codex, diamonds and the Golden Fleece count -
                // which is what the offline projection draws for its window's
                // kills. The fight itself is untouched.
                int paidKills = (int)ScholarRate.RewardUnits(rng, 1, ScholarRate.BonusPermille(in payload));


                // Modul: and the same removal on the live-kill path. Two copies
                // of one bonus, which is why it is worth saying twice that the
                // payload fields behind it are fossils.

                // Modul: THE DEATH IS THE ONLY MOMENT A FAST FIGHT HAS.
                //
                // A geared character kills an early monster inside a single
                // snapshot, so the health bar never animates and the monster is
                // simply gone and replaced. This line is what tells the client
                // a kill happened at all, as opposed to the target silently
                // changing - which is what it looked like before.
                //
                // Amount carries the xp this kill is about to pay, read from
                // the content reward rather than recomputed: the multiplied
                // figure is not known until ProcessMonsterDeath has run, and a
                // second copy of that arithmetic is exactly the drift this
                // codebase keeps paying for.
                // The base after LowRegionXp's cut: the figure this kill pays.
                int killBaseXp = LowRegionXp.BaseXp(in payload, in activeMonster);
                CombatEventFeed.Publish(
                    payload.PlayerId,
                    activeMonster.Id,
                    Network.ResponseCombatEventPacket.KindKill,
                    killBaseXp,
                    0);

                int seasonalCombatXp = activeMonster.BaseXpReward * finalXpMultiplier / 100;
                // Task 55: the level the fight was WON at - before this kill's
                // own XP can lift it past a challenge's cap.
                int levelAtKill = payload.CurrentLevel;
                long victoryXpBefore = payload.CurrentXp;
                ProgressionEngine.ProcessMonsterDeath(ref payload, killBaseXp * paidKills, finalXpMultiplier, ActiveGlobalEventId, activeRaceId);
                // Modul: what this ONE kill paid, for the victory card. Read as
                // a delta rather than recomputed - ProcessMonsterDeath applies
                // the event multiplier, the race passive and the level-up
                // curve, and a second copy of that arithmetic would drift.
                long victoryXpEarned = System.Math.Max(0L, payload.CurrentXp - victoryXpBefore);

                // Which region this boss guards (0 for a regular).
                int clearedBossRegion = RaceUnlockRegistry.GetRegionForBossMonsterId(activeMonster.Id);

                // Task 55: the boss challenges, judged off the tick from what this
                // fight was. CombatTargetTickAccumulator is zeroed at the spawn
                // and counts ticks, so it is the fight's length in tenths.
                var simulating = Simulating;
                if (clearedBossRegion > 0)
                {
                    if (simulating != null)
                    {
                        simulating.BossKills++;
                        simulating.BossKillNotes.Add((clearedBossRegion, activeMonster.Id, levelAtKill, payload.AteThisFight,
                            (int)Math.Min(int.MaxValue, (long)payload.CombatTargetTickAccumulator)));
                    }
                    else CosmeticGrantEngine.NoteBossKill(payload.PlayerId, clearedBossRegion, activeMonster.Id,
                        levelAtKill, payload.AteThisFight, (int)Math.Min(int.MaxValue, (long)payload.CombatTargetTickAccumulator));
                }

                // Task 87: an armed Boss Ascension step is CLEARED by this kill only
                // if it landed inside the step's time limit; a slower kill is
                // answered (AscensionTooSlow) and the attempt stays armed for the
                // boss's respawn. The tick only notes the clear - the titles and
                // frames are paid off it, in one transaction, by CosmeticGrantEngine.
                if (clearedBossRegion > 0 && TryGetAscensionModifiers(in payload, out var clearedMods))
                {
                    long fightTenths = payload.CombatTargetTickAccumulator;
                    bool inTime = clearedMods.TimeLimitPctOfSwift <= 0
                        || fightTenths <= BossAscensionRegistry.TimeLimitSecondsFor(clearedBossRegion, payload.AscensionStep) * 10L;
                    if (inTime)
                    {
                        int clearedStep = payload.AscensionStep;
                        payload.BossAscensionPacked = BossAscensionRegistry.WithHighestStep(payload.BossAscensionPacked, clearedBossRegion, clearedStep);
                        if (simulating != null)
                        {
                            simulating.AscensionClears++;
                            simulating.AscensionNotes.Add((clearedBossRegion, clearedStep, activeMonster.Id));
                        }
                        else BossAscensionEngine.NoteClear(payload.PlayerId, clearedBossRegion, clearedStep, activeMonster.Id);
                        // No result here: "cleared" is announced by the worker AFTER
                        // the reward commits, so the ladder the client refetches on
                        // that result already shows the step.
                        DisarmAscension(ref payload);
                    }
                    else
                    {
                        payload.AscensionPendingResult = 2;
                    }
                }

                // The seasonal boss: a FIRST clear of a tier pays (the currency
                // here, on the tick; diamonds, gold, a title and a pet off it, by
                // SeasonalBossEngine, which announces the clear once saved). A
                // repeat win is an ordinary kill - it already rolled the event
                // currency above like any monster - and stays armed for the
                // respawn, attempts being unlimited.
                //
                // Modul: EITHER WIN ENDS THE FIGHT (owner, 2026-10-10). A repeat
                // win used to stay armed and farm her for ever, and a first clear
                // left slot 1 farming the yardstick boss - "why am I fighting the
                // Alpha Wolf". The fight is one attempt from a window now; the
                // character goes back to its old work below, after the respawn.
                long seasonalReturn = -1;
                if (clearedBossRegion > 0 && TryGetSeasonalBoss(in payload, out var wonTier))
                {
                    seasonalReturn = payload.SeasonalBossReturnActivityId;
                    if (!SeasonalBossRegistry.IsCleared(payload.SeasonalBossClearedMask, wonTier.Tier))
                    {
                        long nowSeconds = GameClock.UtcNowOffset.ToUnixTimeSeconds();
                        var (seasonalEvent, _) = Domain.Progression.SeasonalEventRegistry.Current(nowSeconds);
                        if (seasonalEvent != null)
                        {
                            payload.SeasonalBossClearedMask = SeasonalBossRegistry.WithCleared(payload.SeasonalBossClearedMask, wonTier.Tier);
                            Domain.Progression.SeasonalEventEarning.Grant(ref payload, wonTier.Currency, nowSeconds);
                            var clearNote = new SeasonalBossClearNote(payload.PlayerId, seasonalEvent.Id, wonTier.Tier);
                            if (simulating != null) { simulating.SeasonalBossClears++; simulating.SeasonalBossNotes.Add(clearNote); }
                            else SeasonalBossEngine.Clears.Enqueue(clearNote);
                            DisarmAscension(ref payload);
                        }
                    }
                }

                // Task 51: every boss kill, first clear or farm, against the
                // fastest so far. CombatTargetTickAccumulator is zeroed at the
                // spawn and counts ticks, so it is this fight's length in tenths.
                if (clearedBossRegion > 0)
                {
                    Domain.Progression.PersonalRecords.ObserveBossKill(
                        ref payload, clearedBossRegion, (int)Math.Min(int.MaxValue, (long)payload.CombatTargetTickAccumulator));
                }
                // Modul: the location's gathering, the next region's door and
                // the first-clear mark - in ApplyKillProgression, which the
                // offline projection calls for its kills too. The door is the
                // reward for a boss, and one that opens on next login does not
                // read as a reward at all.
                bool wasFirstClearForThisPlayer = ApplyKillProgression(ref payload, activeMonster.Id);

                // Modul: FIRST BLOOD ON A REGION BOSS GOES TO THE WHOLE WORLD.
                //
                // Asked for directly: "when someone is the first player to beat
                // a boss, congratulate them in chat". Two claims, and only the
                // weaker one can be made from inside a tick: this player's own
                // first clear is on the payload, and whether they are the first
                // in the WORLD needs a durable, contended check that a 10Hz
                // loop has no business doing - so the announcement says what is
                // true rather than what would be nicer to say.
                //
                // BossFirstClearAnnouncer keeps the world-first claim honest by
                // holding it in Redis, where every pod can see the same answer.
                if (wasFirstClearForThisPlayer && clearedBossRegion > 0)
                {
                    if (simulating == null) BossFirstClearAnnouncer.Announce(payload.PlayerId, activeMonster.Id);

                    // Funnel step 6: this player's first clear of region 1's
                    // boss - the door to everything past the tutorial region.
                    if (simulating == null && clearedBossRegion == RaceUnlockRegistry.FirstRegion)
                    {
                        FunnelRecorder.Record(payload.PlayerId, FunnelStep.Region1Boss);
                    }

                    // Modul: and the card that says what just happened.
                    //
                    // The duration comes from CombatTargetTickAccumulator,
                    // which is zeroed when a monster spawns and incremented
                    // once per tick - so it already IS the length of this
                    // fight, in tenths. Nothing else in the payload knows when
                    // the fight started.
                    //
                    // Gold and xp are filled in further down, after the reward
                    // arithmetic that has not run yet at this point; this only
                    // opens the record.
                    payload.LastVictoryMonsterId = activeMonster.Id;
                    payload.LastVictoryDurationSeconds = (int)(payload.CombatTargetTickAccumulator * TickIntervalSeconds);
                    payload.LastVictoryGold = 0L;
                    payload.LastVictoryXp = victoryXpEarned;
                    payload.LastVictoryTick++;

                    payload.IsDirty = true;
                }

                AddSeasonalXp(ref payload, seasonalCombatXp);

                if (liveSessionContexts.TryGetValue(payload.PlayerId, out var sessionCtx))
                {
                    sessionCtx.ThreadSafeAddMonsterKill();
                }

                QuestEngine.IncrementProgress(ref payload, QuestEngine.QuestTypeKillMonsters, 1);

                // Modul: Golden Fleece, the Fortune crown - every hundredth
                // kill drops an item two rarity tiers above its due.
                //
                // Counted even when the crown is not taken, so that TAKING it
                // does not hand the player a hundred-kill head start they did
                // not earn, and so untaking it (a respec) does not reset a
                // counter they were watching.
                payload.KillsSinceFleece += paidKills;
                int fleeceProcs = 0;
                while (payload.KillsSinceFleece >= GoldenFleeceKillInterval)
                {
                    payload.KillsSinceFleece -= GoldenFleeceKillInterval;
                    fleeceProcs++;
                }
                int fleeceKills = HasGoldenFleece(in payload) ? Math.Min(fleeceProcs, paidKills) : 0;
                int plainKills = paidKills - fleeceKills;

                // Modul: the one gold formula, shared with the offline
                // projection - see CombatGoldReward.
                long goldReward = CombatGoldReward.PerKill(in payload, in activeMonster, combatStats.GoldAcquisitionMultiplierPct) * paidKills;
                if (payload.Hungry) goldReward = goldReward * FoodRegistry.HungryRewardPct / 100;

                if (goldReward > 0)
                {
                    payload.AddGold(goldReward);
                    payload.RedisPendingGoldDelta += goldReward;
                    GoldLedger.TallyIncome(ref payload, GoldIncomeSource.Combat, goldReward);
                    payload.RequiresRedisFlush = true;
                    payload.IsDirty = true;
                }

                // Modul: the victory card's gold, written only when THIS kill
                // is the one it was opened for. Guarded on the monster id
                // rather than on a bool, so a later ordinary kill of the same
                // boss cannot overwrite the first clear's figures.
                if (payload.LastVictoryMonsterId == activeMonster.Id && payload.LastVictoryGold == 0L)
                {
                    payload.LastVictoryGold = goldReward;
                }

                // Codex Integration (Sprint 38)
                int codexRaceId = 0;
                if (payload.Slot1_CharacterId != System.Guid.Empty)
                {
                    codexRaceId = (int)(payload.Slot1_GeneticVector & 0xFF);
                }

                var killEvent = new KillEvent
                {
                    PlayerId = payload.PlayerId,
                    MonsterId = payload.CurrentMonsterId,
                    RaceId = codexRaceId,
                    GainedXp = (long)seasonalCombatXp * paidKills,
                    Kills = paidKills,
                };
                if (simulating != null) simulating.CodexKills.Add(killEvent);
                else CodexEngine.KillEventQueue.Enqueue(killEvent);

                // Modul: counted HERE as well as sixty lines below, because the
                // live server insisted on something the source says is
                // impossible - codex kills climbing while the loot enqueue in
                // this same straight-line block never fired. One of those two
                // observations is wrong, and only the running process can say
                // which.
                if (simulating == null) CombatLootEngine.NoteCodexKill();

                // Modul: one shared definition. This site and CombatLootEngine
                // both used `% 6 == 0`, copied between them on the reasoning
                // that it kept "regional boss" consistent - which it did, at
                // the wrong monsters. See ContentRegistry.IsRegionalBoss.
                bool isRegionalBoss = ContentRegistry.IsRegionalBoss(activeMonster.Id);

                if (payload.ActiveGuildWarId > 0)
                {
                    int wp = isRegionalBoss ? 500 : 10;
                    guildWarPointQueue.Enqueue(new GuildWarPointEvent
                    {
                        MatchId = payload.ActiveGuildWarId,
                        GuildId = payload.GuildId,
                        Front = 0,
                        Points = wp
                    });
                }

                // Modul 03: a flat Premium Diamond drop (OrdinaryKillDiamondChance)
                // from standard/elite monsters. PremiumCurrency is updated directly in-memory here
                // (no DB access needed on the hot path) and persisted on the
                // next checkpoint flush like gold.
                //
                // Modul: REGIONAL BOSSES PAY NO DIAMONDS (owner, 2026-09-28).
                // They paid a guaranteed 10 on EVERY kill, and a beaten boss
                // stays at its normal stats - so a level-97 character parked on
                // region 1's boss killed it every 2.3 s for about 10,900
                // diamonds an hour, against the Delve's calibrated 60 a week
                // (measured with BossGearBenchmark). The live account had
                // already taken 1,160 from 116 kills of that one boss.
                // BossDiamondTests keeps it gone.
                if (KillCanPayDiamond(activeMonster.Id))
                {
                    for (int paid = 0; paid < paidKills; paid++)
                    {
                        if (!OrdinaryKillPaysDiamond(rng.NextDouble())) continue;
                        payload.SetPremiumCurrency(payload.PremiumCurrency + 1);
                        payload.PendingDiamondDelta += 1;
                        payload.IsDirty = true;
                    }
                }

                // Seasonal event currency: a chance per paid kill, under the
                // daily cap. Bosses count like any kill - the cap is the bound.
                // Modul: not in a simulated away window (phase 2b.4). The window
                // runs at login, so "now" here is the login for every kill of it,
                // and the event's offline rate and its start inside the window
                // would both be lost; OfflineSimulationEngine pays the window's
                // paid kills through OfflineEventCurrency instead, as before.
                if (simulating == null)
                    Domain.Progression.SeasonalEventEarning.Roll(
                        ref payload, Domain.Progression.SeasonalEventEarning.Source.Kill, paidKills,
                        GameClock.UtcNowOffset.ToUnixTimeSeconds(), rng);

                // A monster pet: a chance on every kill, no event needed.
                Domain.Progression.PetRegistry.RollMonsterPets(payload.PlayerId, paidKills, rng);

                // Modul 03/10/11/12: equipment drop roll request. ProcessSubTick
                // is static, so this enqueues onto CombatLootEngine's static
                // queue (mirroring CodexEngine.KillEventQueue) rather than
                // calling an instance method directly - CombatLootEngine's own
                // background poll loop performs the actual DB insert.
                // Modul: the luck sum used to be written out here, and a second,
                // shorter copy of it lived in OfflineSimulationEngine. The two
                // drifted, which is why offline drops were measurably worse -
                // see CombatLootDropRequest.Build, which is now the only place
                // either path composes one.
                //
                // Plain and Golden Fleece kills go as separate requests, the
                // split OfflineSimulationEngine.ProjectCombatLoot makes too - a
                // Scholar kill paid as two can carry one fleece proc and one
                // plain kill.
                var dropSink = simulating != null ? simulating.DropRequests : null;
                if (plainKills > 0)
                {
                    EnqueueDrop(dropSink, CombatLootDropRequest.Build(
                        in payload,
                        in combatStats,
                        payload.CurrentMonsterId,
                        kills: plainKills,
                        bonusRarityTiers: 0,
                        skipMaterialRoll: false));
                }
                if (fleeceKills > 0)
                {
                    EnqueueDrop(dropSink, CombatLootDropRequest.Build(
                        in payload,
                        in combatStats,
                        payload.CurrentMonsterId,
                        kills: fleeceKills,
                        bonusRarityTiers: GoldenFleeceBonusTiers,
                        skipMaterialRoll: false));
                }

                // Modul: THE OTHER HALF OF THE LOOT TELEMETRY.
                //
                // CombatLootEngine reports what it DRAINS. Without a matching
                // count of what the tick ENQUEUES, a silent loot path still has
                // two possible causes and no way to separate them - which is
                // exactly where a day went. These two numbers must track each
                // other; a gap is the bug, and which side the gap is on says
                // which half to look at.
                if (simulating == null) CombatLootEngine.NoteKillEnqueued();
                else { simulating.Kills++; simulating.PaidKills += paidKills; }

                // Modul: A DEAD MATERIAL ROLL WAS DELETED HERE, 2026-09-30.
                //
                // This block rolled the monster's loot table
                // `GlobalDropMultiplier x CachedCodexYieldMultiplier` times a
                // kill - and granted NOTHING: all it did was decrement
                // InventorySpaceRemaining, a backpack counter the census resets
                // to capacity every pass since the backpack was retired. It
                // read exactly like the real material roll, and the offline
                // projection copied it, so offline paid materials x codex
                // yield x drop multiplier that no live kill ever paid.
                //
                // A kill's materials are rolled in ONE place:
                // CombatLootEngine's kill loop (35%, TryPickMaterialDrop). They
                // read neither the codex yield nor the drop multiplier, and the
                // owner confirmed that is the rule (2026-09-30).

                // Momentum: a kill adds to the streak; a boss neither adds nor breaks it.
                if (!FightingRegionBoss(in payload) && payload.KillStreak < WarEffects.MomentumMaxStreak
                    && WarTreeRegistry.Resolve(in payload).MomentumTenthsPerKill > 0)
                    payload.KillStreak++;

                payload.CurrentMonsterId = fallbackId;
                payload.CurrentMonsterHp = SpawnHpFor(in payload);
                payload.CombatTargetTickAccumulator = 0;
                payload.AteThisFight = false;
                ClearMonsterFightState(ref payload);

                if (seasonalReturn >= 0) ReturnFromSeasonalBoss(ref payload, seasonalReturn);
            }
        }

        /// <summary>
        /// The player's swing, resolved once (phase 2b.1, roadmap 5). Moved out of
        /// RunCombatTick verbatim, so the live tick and the per-swing simulator run
        /// the same code: hit, crit, Double Strike, Crushing Blow, Echo, the damage and its side effects. The caller decides WHEN a swing happens (the
        /// interval test); this decides what it does. Every roll uses
        /// <paramref name="rng"/>, which is what lets a seeded hour replay.
        /// </summary>
        /// <remarks>Modul: the roadmap sketched a separate CombatState struct; the
        /// payload already IS that state (health, accumulators, War counters,
        /// LastHitWasCrit, eat cooldown, food), so it is passed by ref instead of
        /// being copied in and out of a second struct that could drift from it.</remarks>
        internal static void ResolvePlayerSwing(ref TickStatePayload payload, in CombatStats combatStats, in MonsterDefinition activeMonster, long effectiveMaxHp, Random rng)
        {
            // Step 1 (Hit Determination). AccuracyRating (DEX-derived,
            // see StatsCalculator) and the monster's content-authored
            // DodgeRating replace the previous fixed 100/100 placeholder
            // pair - a 0-DEX/0-DodgeRating pairing reproduces the exact
            // old fixed-midpoint hit chance, so this is a pure extension,
            // not a rebalance of existing content.
            float attackerAccuracy = 100f + combatStats.AccuracyRating;
            float defenderDodge = 100f + activeMonster.DodgeRating;
            float hitChance = Math.Clamp(attackerAccuracy / defenderDodge, 0.05f, 0.95f);

            var warSwing = WarTreeRegistry.Resolve(in payload);
            if (warSwing.PerfectSequence) payload.WarSwingCount++;
            // Every PerfectEvery-th swing is Perfect Sequence's; it crits if it lands.
            bool perfect = warSwing.PerfectSequence && !warSwing.LivingFortress
                && payload.WarSwingCount % WarEffects.PerfectEvery == 0;

            if (rng.NextDouble() <= hitChance)
            {
                // Step 2 (Crit Check)
                float critMult = 1.0f;
                // Modul: skill tree. Precision adds percentage POINTS of
                // crit chance and Cruelty adds to the multiplier - both are
                // applied here rather than inside StatsCalculator because
                // the tree is a player-account bonus, not a property of the
                // gear the stats are computed from.
                // Modul: CRIT CHANCE HAD NO CEILING, found by
                // PowerCeilingTests on its first run, 2026-09-06.
                //
                // It sums from DEX, Vila mastery, five affix rolls, the
                // bred crit locus and this tree branch, and nothing clamped
                // the total - so `NextDouble() <= chance/100` was a
                // guaranteed crit forever past 100. Five crit_chance rolls
                // on one region-5 Legendary weapon already exceed it on
                // their own, which turned crit from variance into a flat
                // multiplier and made every further crit-chance roll a dead
                // affix nobody was told about.
                //
                // Clamped rather than rebalanced: 100% is the honest
                // ceiling for a probability, it takes nothing away from
                // anyone (you cannot crit more often than always), and it
                // makes the excess visible to the ledger instead of
                // silently wasted.
                float treeCritChance = LiveCritChancePct(in payload, in combatStats);

                // Modul: and the client is told, so a crit can look like
                // one. Cleared on every swing rather than only set, or a
                // single crit would paint every later hit yellow.
                payload.LastHitWasCrit = 0;
                // Modul: told apart on the feed (FlagDoubleStrike) so the
                // client can say DOUBLE CRIT. A local, not a payload field:
                // nothing outside this swing's own event needs it.
                bool doubleStrikeLanded = false;

                if (perfect || rng.NextDouble() <= (treeCritChance / 100.0f))
                {
                    payload.LastHitWasCrit = 1;
                    // Modul: Guile, the Precision bough, stacks with Cruelty
                    // rather than replacing it - see LiveCritMultiplier.
                    critMult = LiveCritMultiplier(in payload, in combatStats);
                    if (perfect) critMult += WarEffects.PerfectCritBonus;

                    // Modul: Double Strike, the Precision crown. Expressed
                    // as extra multiplier rather than a second swing,
                    // because a second swing here would need its own
                    // to-hit, its own lifesteal and its own kill check -
                    // three places to get out of step for an effect the
                    // player experiences as "that one hit harder".
                    double doubleChance = DoubleStrikeChance(in payload);
                    if (doubleChance > 0 && rng.NextDouble() <= doubleChance)
                    {
                        critMult *= 2f;
                        doubleStrikeLanded = true;
                    }
                }

                // Modul: computed once per tick and cached, because the
                // world boss needs the same number and a second derivation
                // of it would be a second authority over how hard this
                // character hits. See EffectiveMilliAttackFor.
                long effectiveMilliAttack = payload.CachedEffectiveMilliAttack;

                long rawDamage = (long)(effectiveMilliAttack * (double)critMult);

                // Active Skill Tree: a successful RequestCastSkill sets this
                // for exactly one attack resolution, then it is consumed
                // (reset to 0) here - "injected into the next tick's
                // StatsCalculator combat resolution" per the task.
                if (payload.PendingSkillDamageMultiplier > 0f)
                {
                    rawDamage = (long)(rawDamage * (double)payload.PendingSkillDamageMultiplier);
                    payload.PendingSkillDamageMultiplier = 0f;
                }

                // Step 3 (Mitigation). Monsters carry no block stat (no
                // shields modeled on the PvE monster side), so armour is the
                // whole of it - activeMonster.Armor is sourced from content
                // data rather than a hardcoded 0.
                //
                // Modul: it REDUCES. This was `max(1 hp, raw - armour*1000)`
                // and it was the fifth copy of that rule - the one inside
                // the live tick, which is the one that decides what actually
                // happens. It went on subtracting for a few minutes after
                // CombatDamageModel stopped, and the two disagreed by nearly
                // a factor of two: every projection in the game said a kill
                // took half as long as it did.
                // Modul: the War half's Crushing Blow and First Strike (rework
                // slice 2.3), both on the post-armour hit. One roll here;
                // HuntingProjection takes the same terms as an expectation, and
                // WarMechanicParityTests holds the two together.
                var warHit = WarTreeRegistry.Resolve(in payload);
                double crushChance = warHit.CrushingChanceWith(AffixFamilies.Totals(in payload));
                bool crushed = crushChance > 0 && rng.NextDouble() < crushChance;
                // "First" is a monster still at full health: no field to
                // remember, and the projection can price exactly that.
                bool firstStrike = warHit.FirstStrikeTenthsPct > 0 && payload.CurrentMonsterHp >= SpawnHpFor(in payload);
                // Sunder: the stacks this monster already carries thin its armour.
                int defenderArmor = SunderedArmour(activeMonster.Armor, payload.SunderStacks, in warHit);
                // Modul: and the player's armour PENETRATION, which this
                // call never passed and nothing else applied - so Might's
                // second effect and every armor_pen_flat affix in the game
                // were worth exactly nothing. See Mitigate's own note.
                long netDamage = CombatDamageModel.Mitigate(
                    rawDamage,
                    defenderArmor,
                    CombatDamageModel.MonsterArmourHalvingConstant(activeMonster.RegionTier),
                    combatStats.FlatArmorPenetration);
                if (crushed) netDamage *= WarEffects.CrushingMultiplier;
                // Ruin: every RuinEvery-th connecting hit, counted across fights.
                bool ruined = false;
                if (warHit.Ruin)
                {
                    payload.WarHitCount++;
                    ruined = payload.WarHitCount % WarEffects.RuinEvery == 0;
                    if (ruined) netDamage = (long)(netDamage * (double)WarEffects.RuinMultiplier);
                }
                if (firstStrike) netDamage += (netDamage * warHit.FirstStrikeTenthsPct / 1000L);
                // Juggernaut: the stacks this monster carries add damage too.
                if (warHit.SunderDamageTenthsPerStack > 0 && payload.SunderStacks > 0)
                    netDamage += (netDamage * Math.Min((int)payload.SunderStacks, WarEffects.SunderMaxStacks) * warHit.SunderDamageTenthsPerStack / 1000L);
                // Executioner below 35% of the bar, judged on the health
                // before this hit lands.
                int knowYourPrey = KnowYourPreyTenthsPct(in payload, in activeMonster);
                if (knowYourPrey > 0) netDamage += (netDamage * knowYourPrey / 1000L);
                // A2: step 10 cleared on this region's boss - +2% against its monsters.
                int mastery = BossAscensionRegistry.MasteryTenthsPctFor(payload.BossAscensionPacked, activeMonster.RegionTier);
                if (mastery > 0) netDamage += (netDamage * mastery / 1000L);
                // Boss-bane Philtre: against a region's boss only.
                int bossbane = BossbaneTenthsPctFor(in payload, in activeMonster);
                if (bossbane > 0) netDamage += (netDamage * bossbane / 1000L);
                // Giant's Bane (skill tree v3 step D): a region's boss only.
                if (warHit.RegionBossDamageTenthsPct > 0 && IsRegionBossId(activeMonster.Id))
                    netDamage += (netDamage * warHit.RegionBossDamageTenthsPct / 1000L);
                if (warHit.ExecuteTenthsPct > 0
                    && payload.CurrentMonsterHp * 100L < (long)SpawnHpFor(in payload) * WarEffects.ExecuteBelowPct)
                    netDamage += (netDamage * warHit.ExecuteTenthsPct / 1000L);
                // Echo: a crit repeats at half its damage. Folded into this
                // hit, so it heals, kills and logs as one blow; an echo is
                // never rolled again.
                bool echoed = payload.LastHitWasCrit == 1 && warHit.EchoTenths > 0 && rng.NextDouble() < warHit.EchoChance;
                if (echoed) netDamage += (long)(netDamage * (double)WarEffects.EchoShare);
                netDamage = (long)(netDamage * (double)LiveCodexDamageMultiplier(in payload));

                // Modul: set bonuses made real. The Chiming Steel 4-piece
                // grants FireDamageMultiplierPct, which reached CombatStats
                // and was read by nothing - so the whole 4-piece tier paid
                // out zero. Applied here rather than to rawDamage so it
                // scales the post-armour figure, matching how the codex
                // multiplier directly above it behaves.
                if (combatStats.SetFireDamageMultiplierPct > 0f)
                {
                    netDamage = (long)(netDamage * (1.0 + (combatStats.SetFireDamageMultiplierPct / 100.0)));
                }

                // A2 Ward: a warded boss takes nothing; each quarter of its bar
                // it loses raises a new ward for 3 s of the fight clock.
                bool warded = false;
                if (TryGetAscensionModifiers(in payload, out var wardMods) && wardMods.Ward)
                {
                    long spawn = SpawnHpFor(in payload);
                    if (payload.CurrentMonsterHp >= spawn) { payload.AscensionWardQuarters = 0; payload.AscensionWardUntilTenths = 0; }
                    if (payload.CombatTargetTickAccumulator < payload.AscensionWardUntilTenths) { netDamage = 0; warded = true; }
                }

                payload.CurrentMonsterHp -= netDamage;

                // Modul: THE FIGHT-SHAPED MECHANICS (skill tree v3 step B). A
                // landed hit adds a Sunder stack for the NEXT hit, and a crit
                // opens (or feeds) a bleed that RunCombatTick pays once a second
                // of the fight clock. Both die with the monster, so they pay in a
                // long fight and barely at all against trash.
                if (warHit.SunderTenthsPerStack > 0 && netDamage > 0 && payload.SunderStacks < WarEffects.SunderMaxStacks)
                    payload.SunderStacks++;
                if (warHit.BleedTenthsPct > 0 && payload.LastHitWasCrit == 1 && netDamage > 0)
                {
                    payload.BleedMilliPool += netDamage * warHit.BleedTenthsPct / 1000L;
                    payload.BleedSecondsLeft = WarEffects.BleedSeconds;
                }

                if (!warded && wardMods.Ward && payload.CurrentMonsterHp > 0)
                {
                    long spawn = SpawnHpFor(in payload);
                    int quartersLost = (int)Math.Min(3L, (spawn - payload.CurrentMonsterHp) * 4L / Math.Max(1L, spawn));
                    if (quartersLost > payload.AscensionWardQuarters)
                    {
                        payload.AscensionWardQuarters = (byte)quartersLost;
                        payload.AscensionWardUntilTenths = payload.CombatTargetTickAccumulator + AscensionModifiers.WardTenths;
                    }
                }

                // Modul: Bloodthirst, the Cruelty bough - a share of the
                // damage dealt comes back as health.
                //
                // On the POST-armour figure, so it scales with what you
                // actually did rather than with what you swung for, and
                // capped at the health pool because overhealing into a
                // bar the client scales against observedMaxPlayerHp would
                // draw a health bar longer than the bar.
                if (BloodthirstTenths(in payload) > 0 && netDamage > 0)
                {
                    long healed = netDamage
                        * BloodthirstTenths(in payload) / 1000L;
                    if (healed > 0)
                    {
                        payload.PlayerHp = Math.Min(
                            effectiveMaxHp, payload.PlayerHp + healed);
                    }
                }

                // Modul: set bonuses made real. Burn - the Chiming Steel
                // 4-piece's damage-over-time half. Deliberately modelled as
                // a deterministic fraction of the hit that applied it,
                // resolved immediately, rather than as a timed DoT: this
                // combat loop has no per-target effect timers, and adding a
                // scheduler for one effect would be a much larger change
                // than the effect is worth. The player-visible result is
                // the same - a matching 4-piece set burns for extra damage.
                // Modul: hoisted out of the block below only so the combat
                // event at the end of this swing can say the burn happened.
                // The arithmetic is unchanged.
                long burnDamageDealt = 0;
                if (combatStats.SetBurnApplicationActive)
                {
                    long burnDamage = (long)(netDamage * (double)BurnDamageFraction);
                    if (burnDamage > 0)
                    {
                        payload.CurrentMonsterHp -= burnDamage;
                        payload.TargetStatusEffectBitmask |= ActiveSkillEngine.StatusFlagBurning;
                        burnDamageDealt = burnDamage;
                    }
                }

                // Modul: LIFESTEAL HEALED SEVEN HUNDRED PERCENT OF THE HIT.
                //
                // `netDamage * LifestealPct` with no divide by 100, and the
                // stat arrives as a percentage - so a 7% lifesteal affix
                // healed seven TIMES the damage the swing dealt. One hit
                // refilled the bar from anywhere, always, and the auto-eat
                // larder never fired because health never fell.
                //
                // Reported from a live playtest as "weird regeneration": a
                // player in three low-rarity pieces from the first monster
                // in the game killed the region 1, 2 and 3 bosses without
                // dropping below half health. It is the third stat in this
                // codebase found being written as a percent and read as a
                // fraction; attack speed was the last one, four hours ago.
                //
                // The cap is the second half and it is not paranoia. Damage
                // grows faster than the health pool does, so even a correct
                // percentage of a large hit is a full heal at depth -
                // lifesteal has to be sustain, not immunity, or it makes
                // every fight after the first one unloseable.
                long lifestealHealed = 0;
                if (combatStats.LifestealPct > 0)
                {
                    long lifestealAmount = (long)(netDamage * (combatStats.LifestealPct / 100.0));
                    long lifestealCeiling = effectiveMaxHp / 100; // 1% of the bar per hit
                    if (lifestealAmount > lifestealCeiling) lifestealAmount = lifestealCeiling;

                    payload.PlayerHp = HealClamped(payload.PlayerHp, lifestealAmount, effectiveMaxHp);
                    lifestealHealed = lifestealAmount;
                }

                // Modul: AND THE PLAYER IS TOLD WHAT JUST HAPPENED.
                //
                // Everything above resolved a blow and then discarded the
                // detail, leaving the client to infer the whole fight from
                // the difference between two CurrentMonsterHp snapshots.
                // Measured 2026-09-04: snapshots arrive every ~1090ms and a
                // geared character kills an early monster every ~1400ms, so
                // across 27 consecutive snapshots that field took exactly
                // ONE value and there was nothing to infer. See
                // ResponseCombatEventPacket.
                //
                // Reported in whole hit points, and burn is folded into the
                // hit that applied it - it is not a second swing, and a log
                // that split it would read as one.
                // Task 51: the same whole-hit-point figure the feed reports,
                // so the record and the log line can never disagree.
                Domain.Progression.PersonalRecords.ObserveHit(ref payload, WholeHitPoints(netDamage + burnDamageDealt));

                CombatEventFeed.Publish(
                    payload.PlayerId,
                    payload.CurrentMonsterId,
                    Network.ResponseCombatEventPacket.KindPlayerHit,
                    WholeHitPoints(netDamage + burnDamageDealt),
                    (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L),
                    (byte)((payload.LastHitWasCrit == 1 ? Network.ResponseCombatEventPacket.FlagCrit : 0)
                         | (doubleStrikeLanded ? Network.ResponseCombatEventPacket.FlagDoubleStrike : 0)
                         | (burnDamageDealt > 0 ? Network.ResponseCombatEventPacket.FlagBurn : 0)),
                    ruined ? Network.SkillProc.Ruin
                    : perfect && payload.LastHitWasCrit == 1 ? Network.SkillProc.PerfectSequence
                    : crushed ? Network.SkillProc.CrushingBlow
                    : echoed ? Network.SkillProc.Echo
                    : Network.SkillProc.None);

                // Separate line, because it is health arriving rather than
                // damage leaving - folding it into the hit above would make
                // the two indistinguishable in a log.
                if (lifestealHealed >= 1000L)
                {
                    CombatEventFeed.Publish(
                        payload.PlayerId,
                        payload.CurrentMonsterId,
                        Network.ResponseCombatEventPacket.KindLifesteal,
                        (int)(lifestealHealed / 1000L),
                        (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L));
                }
            }
            else
            {
                // Modul: A MISS IS THE ONE EVENT NO HEALTH DIFFERENCE CAN
                // IMPLY. It moves nothing, so before this feed existed it
                // was invisible by construction - the fight simply appeared
                // to pause. Accuracy and the monster's dodge rating are both
                // real stats, and this is the only place they are ever
                // visible.
                CombatEventFeed.Publish(
                    payload.PlayerId,
                    payload.CurrentMonsterId,
                    Network.ResponseCombatEventPacket.KindPlayerMiss,
                    0,
                    (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L));
            }
        }

        /// <summary>
        /// The monster's swing, resolved once (phase 2b.1, roadmap 5). Moved out of
        /// RunCombatTick verbatim, so the live tick and the per-swing simulator run
        /// the same code: hit, crit, mitigation, block, thorns, lifesteal replies and Riposte. The caller decides WHEN a swing happens (the
        /// interval test); this decides what it does. Every roll uses
        /// <paramref name="rng"/>, which is what lets a seeded hour replay.
        /// </summary>
        /// <remarks>Modul: the roadmap sketched a separate CombatState struct; the
        /// payload already IS that state (health, accumulators, War counters,
        /// LastHitWasCrit, eat cooldown, food), so it is passed by ref instead of
        /// being copied in and out of a second struct that could drift from it.</remarks>
        internal static void ResolveMonsterSwing(ref TickStatePayload payload, in CombatStats combatStats, in MonsterDefinition activeMonster, long effectiveMaxHp, Random rng)
        {
            // Step 1 (Hit Determination). Monsters have no authored
            // accuracy stat (their content data only defines DodgeRating
            // and Armor, both defensive), so attackerAccuracy stays the
            // fixed baseline; combatStats.DodgeChancePct (defensive
            // potions, Vila's innate racial passive) is the player's own
            // defensive stat and was already wired here.
            // (MonsterHitChance - shared with the two projections.)
            float hitChance = MonsterHitChance(in combatStats);

            if (rng.NextDouble() <= hitChance)
            {
                // Step 2 (Monster Crit Check): 5% base + 0.5% per region
                // tier (region now resolved via
                // ContentRegistry.GetMonsterRegionTier, which uses each
                // monster's authored RegionTier instead of wrapping ids
                // 31+ back onto tiers 1-5). Vodnik's innate
                // CritMitigationPct subtracts directly from the crit
                // damage multiplier, floored at 1.0 so mitigation can never
                // make a crit deal less than a normal hit.
                int monsterRegionTier = ContentRegistry.GetMonsterRegionTier(payload.CurrentMonsterId);
                float monsterCritChance = MonsterCritChance(monsterRegionTier);
                float monsterCritMult = 1.0f;
                if (rng.NextDouble() <= monsterCritChance)
                {
                    monsterCritMult = MonsterCritMultiplier(in combatStats);
                }

                // Computed in long, then saturated. AttackPower * 1000 * 1.5
                // overflows int for the highest-tier authored monsters
                // (Perun's Shattered Aspect sits at 5,368,903 AP, which
                // reaches 8.05e9 on a crit against an int ceiling of
                // 2.15e9). The wrapped value went negative, the Math.Max
                // floor below caught it, and the deadliest monster in the
                // game dealt exactly 1 HP per hit - the inverse of the
                // spawn-already-dead bug on the HP side.
                // Modul: AND IT STAYS A LONG, 2026-09-12.
                //
                // Saturating at int.MaxValue was a correct fix for the
                // overflow described above and a CEILING on the boss wall
                // nobody had noticed: 2.147e9 milli-damage is about twelve
                // times Malakor's authored attack, and the per-region wall
                // needs more than that to out-pace a level-100 larder. The
                // saturation would have silently eaten the difference and
                // the wall would have looked tuned while being capped.
                //
                // Nothing downstream needs an int: Mitigate already takes a
                // long, and the only int in the chain is PlayerHp, which is
                // assigned through a clamped subtraction below.
                //
                // Step 3+4 (Armor then Block, combined): armor REDUCES (see
                // CombatDamageModel.Mitigate - this was once `raw - armour *
                // 1000`, full hit or 1 HP with nothing between), then
                // BlockStrengthPct (CON-derived, see StatsCalculator) shaves
                // a fraction off what armour did not stop, clamped below
                // 100% so a high-CON build nears the floor but never zero.
                //
                // Step 5, the Eternal Dreadnought 4-piece: any single hit is
                // capped at a share of max HP. It replaced CcImmunityActive,
                // which could never fire (the game has no player-facing crowd
                // control); it targets burst, since the larder can only
                // respond BETWEEN hits. Applied after armour and block so it
                // is a true ceiling, and before the subtraction so thorns
                // below reflects the capped figure.
                //
                // Modul: all of it in LandedMonsterMilliDamage, which the
                // hunting advisor and the offline projection price from too.
                //
                // Task 87: an armed Ascension step raises the boss's attack
                // BEFORE mitigation, as the step's calibration assumes.
                long monsterAttackPower = BossFirstClearRules.AttackPowerFor(FightBossMask(in payload), payload.CurrentMonsterId);
                if (TryGetSeasonalBoss(in payload, out var seasonalTier))
                {
                    monsterAttackPower = BossAscensionRules.ScaleBossAttack(monsterAttackPower, seasonalTier.Modifiers);
                }
                else if (TryGetAscensionModifiers(in payload, out var ascensionMods))
                {
                    monsterAttackPower = BossAscensionRules.ScaleBossAttack(monsterAttackPower, in ascensionMods);
                    // A2 Enrage: below 30% of its bar the boss hits a third harder.
                    if (ascensionMods.Enrage
                        && payload.CurrentMonsterHp * 100L < (long)SpawnHpFor(in payload) * AscensionModifiers.EnrageBelowPct)
                        monsterAttackPower = monsterAttackPower * AscensionModifiers.EnrageAttackPct / 100L;
                }
                float blockStrengthFraction = PlayerBlockFraction(in combatStats);
                long finalDamage = LandedMonsterMilliDamage(
                    monsterAttackPower,
                    monsterCritMult,
                    in combatStats,
                    monsterRegionTier,
                    effectiveMaxHp);

                // Modul: floored, because a lethal hit can be enormous. Any
                // value at or below zero is death, so flooring the result costs
                // nothing, and it keeps the bar far from any wrap - the exact
                // shape of the overflow bug this block already records.
                long playerHpAfterHit = payload.PlayerHp - finalDamage;
                payload.PlayerHp = playerHpAfterHit < MinimumRepresentableHp
                    ? MinimumRepresentableHp
                    : playerHpAfterHit;

                // Modul: set bonuses made real. Thorns - the Eternal
                // Dreadnought 4-piece. Reflects a fraction of what actually
                // landed (post-armour, post-block), so a heavily armoured
                // build reflects less rather than more; reflecting the raw
                // pre-mitigation figure would make stacking armour and
                // thorns together absurd.
                //
                // Only reflects while a monster is actually alive, so the
                // final blow cannot reflect into an already-dead target and
                // drive CurrentMonsterHp further negative.
                // Riposte (War half): the block's share of a connecting swing
                // answers back. RiposteChance prices exactly this roll.
                if (payload.CurrentMonsterHp > 0 && rng.NextDouble() < blockStrengthFraction)
                    Riposte(ref payload, in combatStats, in activeMonster);

                long thornsReflected = 0;
                double thornsFraction = ThornsFractionFor(in payload, in combatStats);
                if (thornsFraction > 0 && payload.CurrentMonsterHp > 0)
                {
                    long reflectedDamage = (long)(finalDamage * thornsFraction);
                    if (reflectedDamage > 0)
                    {
                        payload.CurrentMonsterHp -= reflectedDamage;
                        thornsReflected = reflectedDamage;
                    }
                }

                // Modul: the incoming half of the fight log.
                //
                // BLOCKED IS A REAL THING HERE and only here: monsters carry
                // no block stat, so a player's swing is never blocked, but
                // the player's own BlockStrengthPct (CON-derived) shaves a
                // fraction off what armour did not already stop. Armour
                // itself is deliberately NOT an event - it reduces every hit
                // rather than stopping any, so it is a smaller number on
                // this line, not a line of its own.
                CombatEventFeed.Publish(
                    payload.PlayerId,
                    payload.CurrentMonsterId,
                    Network.ResponseCombatEventPacket.KindMonsterHit,
                    (int)Math.Min(int.MaxValue, finalDamage / 1000),
                    (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L),
                    (byte)((monsterCritMult > 1.0f ? Network.ResponseCombatEventPacket.FlagCrit : 0)
                         | (blockStrengthFraction > 0f ? Network.ResponseCombatEventPacket.FlagBlocked : 0)));

                if (thornsReflected > 0)
                {
                    CombatEventFeed.Publish(
                        payload.PlayerId,
                        payload.CurrentMonsterId,
                        Network.ResponseCombatEventPacket.KindPlayerHit,
                        (int)Math.Min(int.MaxValue, thornsReflected / 1000),
                        (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L),
                        Network.ResponseCombatEventPacket.FlagThorns);
                }
            }
            else
            {
                // The monster swung and the player's dodge took it. Same
                // argument as the player's own miss above: nothing moves, so
                // without this line the fight looks like it stopped.
                CombatEventFeed.Publish(
                    payload.PlayerId,
                    payload.CurrentMonsterId,
                    Network.ResponseCombatEventPacket.KindMonsterMiss,
                    0,
                    (int)(Math.Max(0L, payload.CurrentMonsterHp) / 1000L));

                // Riposte: every dodged swing answers back.
                if (payload.CurrentMonsterHp > 0) Riposte(ref payload, in combatStats, in activeMonster);
            }
        }
    }
}
