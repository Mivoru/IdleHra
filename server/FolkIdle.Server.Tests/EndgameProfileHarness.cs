using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Calibration instrument, not a regression test: the ENDGAME profile the
    /// dungeon's top stage is set against (owner, 2026-11-19: "high level, the
    /// best pets, Transcendent full armour, every character, aptitudes, the
    /// best affixes with refine, all inheritance, the best potions and guild
    /// bonuses - a profile that takes extremely long, and it should not have a
    /// problem with stage 20, maybe even without ideal dodges").
    /// Skipped unless FOLKIDLE_CALIBRATION_DB is set (a restored production
    /// copy) - FOLKIDLE_CALIBRATION_PLAYER picks the real character it builds on.
    /// </summary>
    /// <remarks>
    /// Modul: WHY A NEW PROFILE. AscensionCalibrationHarness's "maxed" takes the
    /// better of the real gear and a reference loadout whose affixes are a
    /// round-robin at the MIDPOINT of their range - deliberately not optimal,
    /// so a ladder is not tuned against a perfect build. That is right for the
    /// walls and wrong for the last dungeon stage, which is meant for the
    /// perfect build: measured 2026-11-19, the owner (with dodges) cleared all
    /// fifteen stages while that "maxed" profile cleared 13-14 without them.
    /// Here every affix is chosen by search and rolled at the top of its range
    /// (refine reaches the cap), the pets and the combat potion are searched
    /// too, and the party is three such characters.
    /// </remarks>
    [Collection("Static loot queues")]
    public class EndgameProfileHarness
    {
        private readonly ITestOutputHelper _output;
        private static string? Conn => Environment.GetEnvironmentVariable("FOLKIDLE_CALIBRATION_DB");
        private static readonly int[] Party = { 0, 1, 2 };
        private static readonly int BestFish = FoodRegistry.FirstRawFishOfTier(5);
        private const long FakeGuildId = 987_654_321L;

        public EndgameProfileHarness(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        [Fact]
        public async Task Print_TheEndgameProfileAgainstTheDungeons()
        {
            if (string.IsNullOrEmpty(Conn)) return;
            long playerId = long.Parse(Environment.GetEnvironmentVariable("FOLKIDLE_CALIBRATION_PLAYER") ?? "8");
            int[] levels = (Environment.GetEnvironmentVariable("FOLKIDLE_ENDGAME_LEVELS") ?? "140,160")
                .Split(',').Select(int.Parse).ToArray();

            var services = new ServiceCollection();
            services.AddDbContextFactory<FolkIdleDbContext>(o => o.UseNpgsql(Conn));
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<FolkIdleDbContext>>().CreateDbContext());
            services.AddSingleton(new RetryingDbContextOptions(new DbContextOptionsBuilder<FolkIdleDbContext>().UseNpgsql(Conn).Options));
            var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync("localhost:6379,abortConnect=false");
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(redis);
            services.AddSingleton(new RedisPlayerSessionLock(redis));
            var manager = new StateCheckpointManager(services.BuildServiceProvider());
            TickStatePayload real = await manager.LoadPlayerState(playerId);

            _output.WriteLine($"real: level {real.CurrentLevel}, STR {real.STR} DEX {real.DEX} CON {real.CON} LCK {real.LCK}, rebirths {real.RenownedRebirths}, trials {real.CompletedRebirthTrials:X}, inherit dmg {real.Inherit_Damage} hp {real.Inherit_MaxHp}, guild {real.GuildId}, potions {real.ActiveOffensivePotionId}/{real.ActiveDefensivePotionId}");
            for (int s = 0; s < 3; s++)
            {
                var p = real;
                SimulationEngine.SwapSlotIntoActiveRegister(ref p, s);
                var (dps, hp, worn) = DungeonRunner.ChampionPower(p, 0);
                _output.WriteLine($"real slot {s}: char {(s == 0 ? p.Slot1_CharacterId : s == 1 ? real.Slot2_CharacterId : real.Slot3_CharacterId)}, apt {p.Aptitude_Strength}/{p.Aptitude_Skill}/{p.Aptitude_Endurance}/{p.Aptitude_Fortune}, worn {worn}, dps {dps}, hp {hp}");
            }

            // The real account, today, no dodges: the party the lobby would pick
            // (every champion in full gear), and the main character alone.
            int[] realParty = Party.Where(s => { var p = real; SimulationEngine.SwapSlotIntoActiveRegister(ref p, s); return DungeonRunner.ChampionPower(p, 0).Worn >= 8; }).ToArray();
            if (realParty.Length == 0) realParty = new[] { 0 };
            PrintLadder("real", real, realParty);
            PrintAscension("real", real);
            PrintAscensionTopEdge("real", real);

            foreach (int level in levels)
            {
                TickStatePayload maxed = BuildEndgame(real, level);
                PrintLadder($"endgame L{level}", maxed, Party);
                PrintAscension($"endgame L{level}", maxed);
                PrintAscensionTopEdge($"endgame L{level}", maxed);
            }
        }

        private void PrintLadder(string name, TickStatePayload payload, int[] party)
        {
            foreach (var kind in DungeonBoss.All)
            {
                var line = new System.Text.StringBuilder($"{name,-13} {kind,-9} party {party.Length}:");
                int highest = 0;
                for (int stage = 1; stage <= DungeonSimulator.StageCount; stage++)
                {
                    var run = RunOnce(payload, kind, stage, party);
                    if (stage >= 10) line.Append(run.Won ? $" {stage}={run.Seconds:F0}s/{run.MealsEaten}m" : $" {stage}=X({run.WavesCleared}w,{run.BossHpLeftPct}%)");
                    if (run.Won) highest = stage;
                }
                double head = Headroom(payload, kind, 15, party);
                _output.WriteLine(line.ToString());
                _output.WriteLine($"{name,-13} {kind,-9}: highest {highest}, stage-15 headroom x{head:F2} (attack, health ^0.68)");
            }
        }

        /// <summary>Malakor's Ascension (region 5) step by step, the main character alone, the live tick.</summary>
        private void PrintAscension(string name, TickStatePayload payload)
        {
            var line = new System.Text.StringBuilder($"{name,-13} Ascension r5:");
            for (int step = 0; step <= BossAscensionRegistry.MaxStep; step++)
            {
                var (won, seconds, limit) = AscensionCalibrationHarness.Fight(payload, BossAscensionRegistry.LastRegion, step);
                line.Append(won ? $" A{step}={seconds:F0}s" : $" A{step}=X");
                if (won && limit > 0 && seconds > limit) line.Append($"(>{limit})");
            }
            _output.WriteLine(line.ToString());
            // Headroom: the largest attack multiplier on Malakor at step 1 still
            // beaten (health x^0.68), the number the ladder is solved against.
            double lo = 1.0, hi = 1.0;
            if (AscensionCalibrationHarness.Beats(payload, BossAscensionRegistry.LastRegion, 1.0))
            {
                while (hi < 1e6 && AscensionCalibrationHarness.Beats(payload, BossAscensionRegistry.LastRegion, hi)) { lo = hi; hi *= 2; }
                for (int i = 0; i < 12; i++) { double mid = Math.Sqrt(lo * hi); if (AscensionCalibrationHarness.Beats(payload, BossAscensionRegistry.LastRegion, mid)) lo = mid; else hi = mid; }
            }
            _output.WriteLine($"{name,-13} Ascension r5 headroom x{lo:F1}; ladder A3 x{BossAscensionRegistry.ModifiersFor(5, 3).AttackPct / 100.0 + 1:F1} A6 x{BossAscensionRegistry.ModifiersFor(5, 6).AttackPct / 100.0 + 1:F1} A10 x{BossAscensionRegistry.ModifiersFor(5, 10).AttackPct / 100.0 + 1:F1}");
        }

        /// <summary>
        /// The strongest Malakor A10 this payload still beats, with A10's own
        /// rules (time limit, Enrage, Ward): attack x, health x^0.68. Fights roll,
        /// so a multiplier counts as beaten on two wins of three.
        /// </summary>
        private void PrintAscensionTopEdge(string name, TickStatePayload payload)
        {
            var top = BossAscensionRegistry.ModifiersFor(BossAscensionRegistry.LastRegion, BossAscensionRegistry.MaxStep);
            bool Wins(double attack)
            {
                int atkPct = (int)Math.Round((attack - 1.0) * 100.0);
                int hpPct = (int)Math.Round((Math.Pow(attack, 0.68) - 1.0) * 100.0);
                BossAscensionRegistry.CalibrationOverride = new AscensionModifiers(atkPct, hpPct, top.TimeLimitPctOfSwift) { Enrage = top.Enrage, Ward = top.Ward };
                try
                {
                    int won = 0;
                    for (int i = 0; i < 3; i++) if (AscensionCalibrationHarness.Fight(payload, BossAscensionRegistry.LastRegion, BossAscensionRegistry.MaxStep).Won) won++;
                    return won >= 2;
                }
                finally { BossAscensionRegistry.CalibrationOverride = null; }
            }
            double lo = 1.0, hi = 2.0;
            if (!Wins(lo)) { _output.WriteLine($"{name,-13} Ascension A10 rules: loses even at x1"); return; }
            while (hi < 1e6 && Wins(hi)) { lo = hi; hi *= 2; }
            for (int i = 0; i < 10; i++) { double mid = Math.Sqrt(lo * hi); if (Wins(mid)) lo = mid; else hi = mid; }
            _output.WriteLine($"{name,-13} Ascension A10 rules (limit {top.TimeLimitPctOfSwift}%, enrage, ward): beats up to x{lo:F0}");
        }

        private static DungeonRunResult RunOnce(TickStatePayload payload, DungeonKind kind, int stage, int[] party)
        {
            var runner = new DungeonRunner(payload, kind, stage, BestFish, 9999, party, challenges: false);
            runner.Advance();
            return runner.Result!;
        }

        /// <summary>
        /// How many times stronger the stage's boss could be (attack x, health
        /// x^0.68 - the split every stage step uses) and still lose: the band
        /// scale this payload still wins at, over the shipped one.
        /// </summary>
        private static double Headroom(TickStatePayload payload, DungeonKind kind, int stage, int[] party)
        {
            bool Wins(double scale)
            {
                DungeonSimulator.CalibrationScale = scale;
                try { return RunOnce(payload, kind, stage, party).Won; }
                finally { DungeonSimulator.CalibrationScale = 0; }
            }
            double baseline = DungeonSimulator.AscensionBandScale;
            double lo = 0.001, hi = baseline;
            if (!Wins(lo)) return 0;
            while (Wins(hi) && hi < 1e4) { lo = hi; hi *= 2; }
            for (int i = 0; i < 14; i++) { double mid = Math.Sqrt(lo * hi); if (Wins(mid)) lo = mid; else hi = mid; }
            return lo / baseline;
        }

        private static double Fitness(TickStatePayload payload, int[] party)
            => Math.Exp(DungeonBoss.All.Average(k => Math.Log(Math.Max(1e-6, Headroom(payload, k, 15, party)))));

        // ---------------------------------------------------------------- the build

        private TickStatePayload BuildEndgame(TickStatePayload real, int level)
        {
            TickStatePayload p = real;
            p.CurrentLevel = level;
            // Attribute points: 7 a level (RaceAttributeGrowth), split the way the
            // owner splits his - the strongest account is the best guide to a split.
            int total = RaceAttributeGrowth.AttributePointsPerLevel * (level - 1);
            int realTotal = Math.Max(1, real.STR + real.DEX + real.CON + real.LCK);
            p.STR = real.STR * total / realTotal;
            p.DEX = real.DEX * total / realTotal;
            p.CON = real.CON * total / realTotal;
            p.LCK = total - p.STR - p.DEX - p.CON;
            p.UnspentAttributePoints = 0;

            p.Inherit_Damage = InheritanceRegistry.MaxLevel;
            p.Inherit_MaxHp = InheritanceRegistry.MaxLevel;
            p.Inherit_XpGain = InheritanceRegistry.MaxLevel;
            p.Inherit_GoldGain = InheritanceRegistry.MaxLevel;
            p.Inherit_GatheringYield = InheritanceRegistry.MaxLevel;
            p.Inherit_LootLuck = InheritanceRegistry.MaxLevel;
            p.RenownedRebirths = RebirthRules.BloodlineRebirths;
            p.ActiveRebirthTrial = 0;
            p.CompletedRebirthTrials = RebirthTrials.All.Aggregate(0, (m, t) => m | RebirthTrials.BitOf(t.Id));

            // The guild's Damage buff at its top tier (GuildContributionEngine: 1-5).
            p.GuildId = FakeGuildId;
            GuildBonusesCache.Apply(FakeGuildId, "Damage", 5, DateTime.UtcNow.AddDays(1));

            // Every character: the bloodline's ceiling in every aptitude.
            byte apt = (byte)BreedingAptitudes.MaxValue;
            p.Aptitude_Strength = p.Aptitude_Skill = p.Aptitude_Endurance = p.Aptitude_Fortune = apt;
            p.Slot2Activity.AptitudeStrength = p.Slot2Activity.AptitudeSkill = p.Slot2Activity.AptitudeEndurance = p.Slot2Activity.AptitudeFortune = apt;
            p.Slot3Activity.AptitudeStrength = p.Slot3Activity.AptitudeSkill = p.Slot3Activity.AptitudeEndurance = p.Slot3Activity.AptitudeFortune = apt;
            if (p.Slot2_CharacterId == Guid.Empty) p.Slot2_CharacterId = Guid.NewGuid();
            if (p.Slot3_CharacterId == Guid.Empty) p.Slot3_CharacterId = Guid.NewGuid();
            p.Slot2_AgePhase = p.Slot3_AgePhase = Math.Max(p.Slot1_AgePhase, 2);
            // The sets the main character wears, on everyone (a set is a choice of
            // pieces; the endgame player has the pieces).
            p.Slot2Activity.CachedSetIds = p.CachedSetIds;
            p.Slot3Activity.CachedSetIds = p.CachedSetIds;
            p.Slot2Activity.TraitMask = p.Slot3Activity.TraitMask = p.TraitMask;

            // The gear: Transcendent region-5 pieces, every affix at the top of
            // its Legendary range, chosen by coordinate search on the main
            // character alone, then worn by all three.
            var gear = new Dictionary<EquipmentSlotKind, string[]>();
            foreach (var slot in Slots) gear[slot] = StartingAffixes(slot);
            PetDefinition[] pets = { };
            ApplyGear(ref p, gear, pets);

            var combatPets = PetRegistry.All.Where(pet => pet.Bonuses.Any(b => b.Stat is PetStat.Damage or PetStat.CritDamage or PetStat.MaxHp or PetStat.Armour)).ToArray();
            double best = Fitness(p, new[] { 0 });
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var slot in Slots)
                {
                    int[] legal = LegalFighting(slot);
                    for (int pos = 0; pos < gear[slot].Length; pos++)
                    {
                        foreach (int idx in legal)
                        {
                            string id = AffixRegistry.Definitions[idx].Id;
                            if (gear[slot].Contains(id)) continue;
                            string old = gear[slot][pos];
                            gear[slot][pos] = id;
                            ApplyGear(ref p, gear, pets);
                            double f = Fitness(p, new[] { 0 });
                            if (f > best * 1.0001) best = f; else gear[slot][pos] = old;
                        }
                    }
                    ApplyGear(ref p, gear, pets);
                }

                // Two pet slots (five rebirths), the best pair of the combat pets.
                PetDefinition[] bestPets = pets;
                foreach (var a in combatPets)
                foreach (var b in combatPets)
                {
                    if (string.CompareOrdinal(a.Id, b.Id) >= 0) continue;
                    ApplyGear(ref p, gear, new[] { a, b });
                    double f = Fitness(p, new[] { 0 });
                    if (f > best * 1.0001) { best = f; bestPets = new[] { a, b }; }
                }
                pets = bestPets;
                ApplyGear(ref p, gear, pets);

                // One combat potion at a time; the strongest of each kind.
                int bestPotion = p.ActiveDefensivePotionId;
                foreach (int potion in StrongCombatPotions())
                {
                    p.ActiveDefensivePotionId = potion;
                    double f = Fitness(p, new[] { 0 });
                    if (f > best * 1.0001) { best = f; bestPotion = potion; }
                }
                p.ActiveDefensivePotionId = bestPotion;
                _output.WriteLine($"endgame L{level} pass {pass}: solo stage-15 headroom x{best:F2}");
            }

            foreach (var slot in Slots) _output.WriteLine($"endgame L{level} {slot,-8}: {string.Join(", ", gear[slot])}");
            _output.WriteLine($"endgame L{level} pets: {string.Join(", ", pets.Select(x => x.Name))}; potion {ContentRegistry.GetItemBaseId(p.ActiveDefensivePotionId)}; STR {p.STR} DEX {p.DEX} CON {p.CON} LCK {p.LCK}");
            for (int s = 0; s < 3; s++)
            {
                var (dps, hp, worn) = DungeonRunner.ChampionPower(p, s);
                _output.WriteLine($"endgame L{level} slot {s}: dps {dps}, hp {hp}, worn {worn}");
            }
            return p;
        }

        private static readonly EquipmentSlotKind[] Slots =
        {
            EquipmentSlotKind.Weapon, EquipmentSlotKind.Helmet, EquipmentSlotKind.Chest, EquipmentSlotKind.Leggings,
            EquipmentSlotKind.Boots, EquipmentSlotKind.Gloves, EquipmentSlotKind.Amulet, EquipmentSlotKind.Ring,
        };

        private static int[] LegalFighting(EquipmentSlotKind slot)
        {
            Span<int> all = stackalloc int[32];
            int n = AffixRegistry.GetLegalAffixIndices(slot, all);
            var list = new List<int>();
            for (int i = 0; i < n; i++) if (!AffixRegistry.Definitions[all[i]].Utility) list.Add(all[i]);
            return list.ToArray();
        }

        private static string[] StartingAffixes(EquipmentSlotKind slot)
        {
            int count = RarityTier.GetAffixCount(RarityTier.Transcendent);
            int[] legal = LegalFighting(slot);
            return Enumerable.Range(0, Math.Min(count, legal.Length)).Select(i => AffixRegistry.Definitions[legal[i]].Id).ToArray();
        }

        private static List<int> StrongCombatPotions()
        {
            var found = new List<int>();
            var items = ContentRegistry.ItemDefinitions;
            for (int i = 0; i < items.Length; i++)
            {
                int id = items[i].Id;
                string baseId = ContentRegistry.GetItemBaseId(id);
                if (!baseId.StartsWith("strong_", StringComparison.Ordinal)) continue;
                if (PotionRegistry.MagnitudeOf(id, PotionKind.Ironbark) > 0
                    || PotionRegistry.MagnitudeOf(id, PotionKind.Swiftroot) > 0
                    || PotionRegistry.MagnitudeOf(id, PotionKind.Bossbane) > 0)
                    found.Add(id);
            }
            return found;
        }

        /// <summary>The totals of the whole outfit plus the pets, on all three characters.</summary>
        private static void ApplyGear(ref TickStatePayload p, Dictionary<EquipmentSlotKind, string[]> gear, PetDefinition[] pets)
        {
            EquippedAffixTotals totals = default;
            double quality = RarityTier.PowerMultiplier(RarityTier.Transcendent);
            foreach (var slot in Slots)
            {
                if (TryBestItem(slot, out ItemDefinition item))
                {
                    totals.FlatAttack += (int)Math.Round(item.FlatAttackPower * quality);
                    totals.FlatDefense += (int)Math.Round(item.FlatDefenseRating * quality);
                }
                var json = new JsonObject();
                foreach (string id in gear[slot])
                {
                    var def = DefinitionOf(id);
                    var (_, max) = AffixRegistry.CalculateMagnitudeRange(def, 5, AffixRarity.Legendary);
                    json[AffixRegistry.BuildPayloadKey(id, 1, AffixRarity.Legendary)] = max;
                }
                EquipmentSlotEngine.AddAffixTotals(json.ToJsonString(), ref totals);
            }
            foreach (var pet in pets) PetRegistry.AddTo(pet, ref totals);
            p.CachedAffixTotals = totals;
            p.Slot2Activity.CachedAffixTotals = totals;
            p.Slot3Activity.CachedAffixTotals = totals;
            // Worn pieces, so ChampionPower and the lobby see a dressed party.
            p.EquippedWeaponId = p.EquippedHelmetId = p.EquippedArmorId = p.EquippedLeggingsId = 1;
            p.EquippedBootsId = p.EquippedGlovesId = p.EquippedAmuletId = p.EquippedRingId = 1;
            p.Slot2Activity.EquippedWeaponId = p.Slot2Activity.EquippedHelmetId = p.Slot2Activity.EquippedChestId = p.Slot2Activity.EquippedLeggingsId = 1;
            p.Slot2Activity.EquippedBootsId = p.Slot2Activity.EquippedGlovesId = p.Slot2Activity.EquippedAmuletId = p.Slot2Activity.EquippedRingId = 1;
            p.Slot3Activity.EquippedWeaponId = p.Slot3Activity.EquippedHelmetId = p.Slot3Activity.EquippedChestId = p.Slot3Activity.EquippedLeggingsId = 1;
            p.Slot3Activity.EquippedBootsId = p.Slot3Activity.EquippedGlovesId = p.Slot3Activity.EquippedAmuletId = p.Slot3Activity.EquippedRingId = 1;
        }

        private static AffixDefinition DefinitionOf(string id)
        {
            var defs = AffixRegistry.Definitions;
            for (int i = 0; i < defs.Length; i++) if (defs[i].Id == id) return defs[i];
            throw new InvalidOperationException(id);
        }

        private static readonly Dictionary<EquipmentSlotKind, ItemDefinition?> ItemCache = new();

        private static bool TryBestItem(EquipmentSlotKind slot, out ItemDefinition found)
        {
            if (!ItemCache.TryGetValue(slot, out var cached))
            {
                ItemDefinition? best = null;
                int bestPower = -1;
                var items = ContentRegistry.ItemDefinitions;
                for (int i = 0; i < items.Length; i++)
                {
                    var c = items[i];
                    if (c.RegionTier != 5) continue;
                    if (AffixRegistry.ResolveSlot(ContentRegistry.GetItemBaseId(c.Id)) != slot) continue;
                    int power = c.FlatAttackPower + c.FlatDefenseRating;
                    if (power > bestPower) { bestPower = power; best = c; }
                }
                ItemCache[slot] = cached = best;
            }
            found = cached ?? default;
            return cached.HasValue;
        }
    }
}
