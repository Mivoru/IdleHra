using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// One dungeon run that can stop and go on: the simulator's loop with its
    /// state kept between calls, so a live run can pause at a telegraph, wait
    /// for the player's swipes, change the party's numbers and continue.
    /// </summary>
    /// <remarks>
    /// Modul: ONE LOOP FOR BOTH. DungeonSimulator.Run (calibration, sweep,
    /// tests) is this runner without pauses, so the numbers a live run plays are
    /// the numbers the calibration measured; with no party and no bonuses every
    /// formula reduces to the single-character loop it replaced.
    ///
    /// Modul: THE PARTY IS ONE POOL (slice 4, owner 2026-10-31: the roster grows
    /// to three). Each champion swings with their own gear and speed, and each
    /// enemy swing lands on the next champion in turn with that champion's
    /// armour - but the health is one shared bar, so the bosses' rules (burns of
    /// max health, drains, the meal cap) keep working on "the player" unchanged.
    /// A party of three is therefore much stronger than one champion; the stages
    /// are not yet calibrated for it (CHESTS_DUNGEONS_DIAMONDS.md 2.8).
    /// </remarks>
    public sealed class DungeonRunner
    {
        private sealed class Member
        {
            public required HuntingProjection.PlayerSwing Swing;
            public double BossHitFactor;
            public int IntervalMs;
            public long MaxMilliHp;
            public double WaveHit, BossHit;
            public int Acc;
            public int ModsVersion = -1;
            public double ModFactor = 1.0;
        }

        private readonly DungeonKind _kind;
        private readonly int _stage;
        private readonly DungeonBoss _rules;
        private readonly Member[] _party;
        private readonly bool _challenges;
        private readonly int _bossIntervalMs;
        private readonly long _bossMilliHp;
        private readonly bool _enrage;
        private readonly int _foodItemId;
        private readonly int _foodInLarder;
        private readonly int _thresholdPct;
        private readonly long _baseMaxMilliHp;
        private readonly List<DungeonEvent> _events = new();
        private readonly DungeonFightState _f;

        /// <summary>The waves (0-based) that open with a telegraph.</summary>
        public static readonly int[] TelegraphWaves = { 1, 3 };
        /// <summary>Telegraphs inside the boss fight after its opening one.</summary>
        public const int BossMidTelegraphs = 1;

        private int _fight;
        private bool _fightStarted;
        private bool _startChallengeDone;
        private int _bossChallenges;
        private int _tick, _meals, _eatCooldown, _wavesCleared, _bossHpLeftPct = 100, _fightStart, _emitted;
        private double _enemyAcc;
        private int _appliedMaxHpPct;

        public DungeonMods Mods { get; } = new();
        public DungeonRunResult? Result { get; private set; }
        /// <summary>True while the run waits for a telegraph to be answered.</summary>
        public bool Paused { get; private set; }
        public bool PausedOnBoss { get; private set; }
        public int Stage => _stage;
        public int PartySize => _party.Length;
        public int Dodged { get; private set; }
        public int Missed { get; private set; }
        public int MealsEaten => _meals;

        /// <param name="slots">The roster slots in the party, 0 first; at least one.</param>
        public DungeonRunner(TickStatePayload payload, DungeonKind kind, int stage, int foodItemId, int foodInLarder, IReadOnlyList<int> slots, bool challenges)
        {
            _stage = Math.Clamp(stage, 1, DungeonSimulator.StageCount);
            _kind = kind;
            _rules = DungeonBoss.For(kind);
            _challenges = challenges;
            _foodItemId = foodItemId;
            _foodInLarder = foodInLarder;

            MonsterDefinition boss = ContentRegistry.Monsters[DungeonSimulator.BossMonsterIdFor(_stage) - 1];
            var (bossMilliHp, bossAttack, enrage) = DungeonSimulator.BossStrength(_stage);
            _bossMilliHp = bossMilliHp;
            _enrage = enrage;
            _bossIntervalMs = boss.AttackIntervalMs > 0 ? boss.AttackIntervalMs : 2000;

            var party = new List<Member>();
            foreach (int slot in (slots.Count == 0 ? new[] { 0 } : slots).Distinct().Take(3))
            {
                var p = payload;
                SimulationEngine.SwapSlotIntoActiveRegister(ref p, slot);
                CombatStats stats = SimulationEngine.LiveCombatStats(in p);
                long maxMilliHp = Math.Max(1000L, SimulationEngine.EffectiveMaxMilliHpFor(in p, in stats));
                // The tick's own attack (aptitudes, inheritance, the lineage's scale) -
                // computed here, not read from the cache, so a payload loaded from the
                // database (the calibration harness) gets the same number as a live one.
                long rawMilliAttack = SimulationEngine.EffectiveMilliAttackFor(ref p, in stats, SimulationEngine.LineageOf(in p).DamageScalePerLevelPct);
                // Modul: THE OFFLINE PROJECTION'S SWING, parity-tested against the
                // live tick (crit, double hit, the skill tree, the codex, burn,
                // Riposte). The plain CombatDamageModel swing dealt about a
                // twentieth of a real character's damage.
                var swing = HuntingProjection.PlayerSwing.For(in p, in stats, in boss, rawMilliAttack);
                party.Add(new Member
                {
                    Swing = swing,
                    BossHitFactor = _rules.PlayerHitFactor(_stage, in swing),
                    IntervalMs = Math.Max(DungeonSimulator.TickMs, SimulationEngine.LiveAttackIntervalMs(in p, in stats)),
                    MaxMilliHp = maxMilliHp,
                    WaveHit = BossGearBenchmark.ExpectedBossMilliDamagePerSwing(in stats, in boss, (long)(bossAttack * DungeonSimulator.WaveStrength)),
                    BossHit = BossGearBenchmark.ExpectedBossMilliDamagePerSwing(in stats, in boss, bossAttack),
                });
            }
            _party = party.ToArray();
            _baseMaxMilliHp = _party.Sum(m => m.MaxMilliHp);
            _thresholdPct = payload.AutoEatThreshold > 0 ? payload.AutoEatThreshold : Shared.AutoEatDefaults.ThresholdPct;

            _f = new DungeonFightState { Stage = _stage, PlayerMax = _baseMaxMilliHp, PlayerHp = _baseMaxMilliHp };
            _f.Note = (_, text, v) => Note("ability", text, v);
        }

        /// <summary>
        /// What one champion brings, for the lobby's party picker: damage a
        /// second against the stage-10 boss's body, max health, and how many of
        /// the eight combat slots they wear. Level and attributes are the
        /// account's; the gear is the champion's own, which is the difference.
        /// </summary>
        public static (long Dps, long MaxHp, int Worn) ChampionPower(TickStatePayload payload, int slot)
        {
            var p = payload;
            SimulationEngine.SwapSlotIntoActiveRegister(ref p, slot);
            CombatStats stats = SimulationEngine.LiveCombatStats(in p);
            long maxHp = SimulationEngine.EffectiveMaxMilliHpFor(in p, in stats) / 1000;
            long rawMilliAttack = SimulationEngine.EffectiveMilliAttackFor(ref p, in stats, SimulationEngine.LineageOf(in p).DamageScalePerLevelPct);
            MonsterDefinition boss = ContentRegistry.Monsters[DungeonSimulator.BossMonsterIdFor(10) - 1];
            var swing = HuntingProjection.PlayerSwing.For(in p, in stats, in boss, rawMilliAttack);
            int interval = Math.Max(DungeonSimulator.TickMs, SimulationEngine.LiveAttackIntervalMs(in p, in stats));
            int worn = new[] { p.EquippedWeaponId, p.EquippedHelmetId, p.EquippedArmorId, p.EquippedLeggingsId,
                p.EquippedBootsId, p.EquippedGlovesId, p.EquippedAmuletId, p.EquippedRingId }.Count(id => id > 0);
            return ((long)(swing.Mean / 1000.0 * 1000.0 / interval), maxHp, worn);
        }

        /// <summary>The events added since the last call, for the next playback segment.</summary>
        public IReadOnlyList<DungeonEvent> TakeNewEvents()
        {
            var fresh = _events.Skip(_emitted).ToList();
            _emitted = _events.Count;
            return fresh;
        }

        public IReadOnlyList<DungeonEvent> AllEvents => _events;

        private void Note(string kind, string text, int value = 0)
        {
            // Pauses and the dodge's outcome are never dropped: the playback waits on them.
            bool essential = kind is "challenge" or "dodged" or "missed" or "perk" or "penalty" or "victory" or "defeat" or "boss" or "wave";
            if (_events.Count >= DungeonSimulator.MaxEvents && !essential) return;
            _events.Add(new DungeonEvent(_tick * DungeonSimulator.TickMs / 1000.0, kind, text, value,
                DungeonSimulator.Pct(_f.PlayerHp, _f.PlayerMax), DungeonSimulator.Pct(_f.Enemy, _f.EnemyMax), DungeonSimulator.Pct(_f.Shield, _f.EnemyMax)));
        }

        // --- the telegraph's outcome ------------------------------------------

        /// <summary>A clean dodge: nothing lands. The caller offers the bonuses.</summary>
        public void Dodge()
        {
            if (!Paused) return;
            Dodged++;
            Note("dodged", "Dodged!");
        }

        /// <summary>A bonus the player chose, then the fight goes on.</summary>
        public bool Choose(string perkId)
        {
            if (!DungeonPerks.Apply(Mods, perkId)) return false;
            ApplyImmediate(perkId);
            Note("perk", DungeonPerks.View(perkId).Name);
            Paused = false;
            return true;
        }

        /// <summary>Ordinary hits a missed telegraph lands on each champion at once (owner, 2026-10-31: three).</summary>
        public const double MissBlowHits = 3.0;

        /// <summary>
        /// A missed telegraph: a heavy blow on the whole party (MissBlowHits
        /// ordinary hits on each champion, through armour) and a penalty.
        /// Returns the penalty's id, or null when every floor is reached.
        /// </summary>
        public string? Miss(Random rng)
        {
            if (!Paused) return null;
            Missed++;
            bool boss = _fight >= DungeonSimulator.Waves;
            double blow = _party.Sum(m => (boss ? m.BossHit : m.WaveHit) * MissBlowHits) * TakenFactor();
            _f.PlayerHp = Math.Max(1, _f.PlayerHp - blow);
            Note("missed", "The blow lands!");
            var penalty = DungeonPerks.RollPenalty(Mods, rng);
            if (penalty != null)
            {
                DungeonPerks.Apply(Mods, penalty.Id);
                ApplyImmediate(penalty.Id);
                Note("penalty", penalty.Name);
            }
            Paused = false;
            return penalty?.Id;
        }

        /// <summary>Goes on without a choice (no bonus left to offer).</summary>
        public void Resume() => Paused = false;

        private void ApplyImmediate(string id)
        {
            // Max health follows MaxHpPct; a raise is healed, a cut only lowers the ceiling.
            if (_appliedMaxHpPct != Mods.MaxHpPct)
            {
                double before = _f.PlayerMax;
                _f.PlayerMax = _baseMaxMilliHp * (1 + Mods.MaxHpPct / 100.0);
                if (_f.PlayerMax > before) _f.PlayerHp += _f.PlayerMax - before;
                _f.PlayerHp = Math.Min(_f.PlayerHp, _f.PlayerMax);
                _appliedMaxHpPct = Mods.MaxHpPct;
            }
            if (id == "wind") _f.PlayerHp = Math.Min(_f.PlayerMax, _f.PlayerHp + _f.PlayerMax * 0.5);
        }

        private double TakenFactor() => Math.Max(0.2, 1 - Mods.ArmorPct / 100.0);

        /// <summary>
        /// The bonus to a champion's average swing from crit chance, crit
        /// damage and damage percent, as a factor on the projection's mean.
        /// </summary>
        private double ModFactor(Member m)
        {
            if (m.ModsVersion == Mods.Version) return m.ModFactor;
            var s = m.Swing;
            double pHit = s.PNormal + s.PCrit + s.PDouble;
            double factor = 1.0;
            if (pHit > 0 && (Mods.CritChancePct != 0 || Mods.CritDamagePct != 0))
            {
                double pc = (s.PCrit + s.PDouble) / pHit;
                double pd = s.PCrit + s.PDouble > 0 ? s.PDouble / (s.PCrit + s.PDouble) : 0;
                double Mean(double crit, double critScale)
                    => (1 - crit) * s.NormalDamage + crit * ((1 - pd) * s.CritDamage + pd * s.DoubleDamage) * critScale;
                double baseMean = Mean(pc, 1.0);
                double pc2 = Math.Clamp(pc + Mods.CritChancePct / 100.0, 0, 1);
                if (baseMean > 0) factor = Mean(pc2, 1 + Mods.CritDamagePct / 100.0) / baseMean;
            }
            factor *= Math.Max(0.2, 1 + Mods.DamagePct / 100.0);
            m.ModFactor = factor;
            m.ModsVersion = Mods.Version;
            return factor;
        }

        // --- the loop -----------------------------------------------------------

        /// <summary>Runs until the run ends or a telegraph needs an answer.</summary>
        public void Advance()
        {
            while (Result == null && !Paused)
            {
                bool isBoss = _fight == DungeonSimulator.Waves;
                if (!_fightStarted)
                {
                    StartFight(isBoss);
                    _fightStarted = true;
                    _startChallengeDone = false;
                }
                if (_challenges && !_startChallengeDone)
                {
                    // Four telegraphs a run (owner, second pass: fewer, harder):
                    // as waves 2 and 4 open, as the boss fight opens, and once
                    // more when the boss falls below half his health.
                    _startChallengeDone = true;
                    if (isBoss || TelegraphWaves.Contains(_fight))
                    {
                        Pause(isBoss);
                        return;
                    }
                }
                if (_challenges && isBoss && _bossChallenges < BossMidTelegraphs && _f.Enemy > 0
                    && _f.Enemy <= _f.EnemyMax * 0.5)
                {
                    _bossChallenges++;
                    Pause(true);
                    return;
                }
                Step(isBoss);
            }
        }

        private void Pause(bool boss)
        {
            Paused = true;
            PausedOnBoss = boss;
            Note("challenge", boss ? $"{_rules.BossName} winds up - dodge!" : "The enemy lunges - dodge!", DungeonPerks.SequenceLength(_stage, boss));
        }

        private void StartFight(bool isBoss)
        {
            _f.EnemyMax = isBoss ? _bossMilliHp : _bossMilliHp * DungeonSimulator.WaveStrength;
            _f.Enemy = _f.EnemyMax;
            _f.Shield = 0;
            _f.EnemySwings = 0;
            _f.BurnPerTick = 0;
            _f.BurnUntil = 0;
            _f.BossState = null;
            _f.Now = 0;
            foreach (var m in _party) m.Acc = 0;
            _enemyAcc = 0;
            _fightStart = _tick;
            if (isBoss) _rules.Begin(_f);
            Note(isBoss ? "boss" : "wave", isBoss ? _rules.Intro : $"Wave {_fight + 1} of {DungeonSimulator.Waves}.", _fight + 1);
        }

        private void Step(bool isBoss)
        {
            _tick++;
            if (_tick > DungeonSimulator.MaxTicks)
            {
                Note("timeout", "The fight drags on past half an hour - the run is lost.");
                Finish(false);
                return;
            }
            _f.Now = (_tick - _fightStart) * DungeonSimulator.TickMs / 1000.0;
            if (isBoss) _rules.OnTime(_f);

            double speed = Math.Max(0.2, 1 + Mods.SpeedPct / 100.0);
            foreach (var m in _party)
            {
                m.Acc += DungeonSimulator.TickMs;
                double interval = m.IntervalMs / speed;
                if (m.Acc < interval) continue;
                m.Acc -= (int)Math.Round(interval);
                double factor = ModFactor(m);
                double playerHit = m.Swing.Mean * (isBoss ? m.BossHitFactor : 1.0) * factor;
                double healPerSwing = m.Swing.ExpectedHeal(m.MaxMilliHp / 100) * (isBoss ? m.BossHitFactor : 1.0);
                double dealt = isBoss ? _rules.ModifyDealt(_f, playerHit) : playerHit;
                if (_f.Shield > 0)
                {
                    _f.Shield -= dealt;
                    if (_f.Shield <= 0)
                    {
                        _f.Shield = 0;
                        Note("ability", "The shield shatters.");
                    }
                }
                else
                {
                    _f.Enemy -= dealt;
                }
                if (healPerSwing > 0) _f.PlayerHp = Math.Min(_f.PlayerMax, _f.PlayerHp + healPerSwing * (dealt / Math.Max(1.0, playerHit)));
                if (Mods.LeechTenths > 0) _f.PlayerHp = Math.Min(_f.PlayerMax, _f.PlayerHp + _f.PlayerMax * Mods.LeechTenths / 1000.0 / _party.Length);
                if (isBoss) _rules.AfterPlayerSwing(_f);
            }

            if (_f.Enemy <= 0 && !(isBoss && _rules.Survives(_f)))
            {
                EndFight(isBoss);
                return;
            }

            _enemyAcc += DungeonSimulator.TickMs;
            double enemyInterval = _bossIntervalMs * (isBoss ? _rules.EnemyIntervalFactor(_f) : 1.0);
            if (_enemyAcc >= enemyInterval)
            {
                _enemyAcc -= enemyInterval;
                // Each swing lands on the next champion in turn, through their armour.
                var target = _party[_f.EnemySwings % _party.Length];
                _f.EnemySwings++;
                double enemyHit = isBoss ? target.BossHit : target.WaveHit;
                bool enraged = isBoss && _enrage && _f.Enemy * 100.0 < _f.EnemyMax * AscensionModifiers.EnrageBelowPct;
                double hit = (enraged ? enemyHit * AscensionModifiers.EnrageAttackPct / 100.0 : enemyHit) * (isBoss ? _rules.EnemyHitFactor(_f) : 1.0) * TakenFactor();
                _f.PlayerHp -= hit;
                if (isBoss) _rules.AfterEnemySwing(_f, hit);
            }
            if (_f.BurnPerTick > 0 && _f.Now < _f.BurnUntil) _f.PlayerHp -= _f.BurnPerTick;

            if (_f.PlayerHp <= 0)
            {
                _f.PlayerHp = 0;
                _bossHpLeftPct = isBoss ? Math.Max(1, (int)Math.Ceiling(_f.Enemy * 100 / _f.EnemyMax)) : 100;
                Note("defeat", isBoss
                    ? $"Defeated by {_rules.BossName} at {_bossHpLeftPct}% of the boss's health."
                    : $"Defeated in wave {_fight + 1}.");
                Finish(false);
                return;
            }

            int mealsAvailable = Math.Min(DungeonSimulator.MealCap + Mods.ExtraMeals, Math.Max(0, _foodInLarder));
            if (_eatCooldown > 0) _eatCooldown--;
            else if (_f.PlayerHp <= _f.PlayerMax * (_thresholdPct / 100.0) && _foodItemId > 0 && _meals < mealsAvailable && (!isBoss || _rules.CanEat(_f)))
            {
                // A bite heals what it heals one champion, for each champion in the pool.
                double bite = FoodRegistry.GetHealMilliHp(_foodItemId, (long)(_f.PlayerMax / _party.Length)) * (double)_party.Length;
                _f.PlayerHp = Math.Min(_f.PlayerMax, _f.PlayerHp + bite);
                _meals++;
                _eatCooldown = SimulationEngine.AutoEatCooldownTicks;
                Note("meal", _meals == mealsAvailable ? "The last meal of the run." : $"A meal ({_meals} of {mealsAvailable}).", _meals);
            }
        }

        private void EndFight(bool isBoss)
        {
            _f.Enemy = 0;
            if (isBoss)
            {
                Note("victory", _rules.Fall);
                _bossHpLeftPct = 0;
                Finish(true);
                return;
            }
            _wavesCleared++;
            int left = (int)Math.Ceiling(_f.PlayerHp * 100 / _f.PlayerMax);
            Note("cleared", $"Wave {_fight + 1} cleared, {left}% health left.", left);
            _fight++;
            _fightStarted = false;
        }

        private void Finish(bool won)
        {
            Paused = false;
            Result = new DungeonRunResult(won, _stage, _wavesCleared, _wavesCleared >= DungeonSimulator.Waves, won ? 0 : _bossHpLeftPct,
                _tick * DungeonSimulator.TickMs / 1000.0, _meals, _events, _kind, DungeonSimulator.WaveMonsterIdsFor(_stage),
                _party.Length, Dodged, Missed);
        }

        /// <summary>Plays the rest out with every telegraph missed: an abandoned run still ends.</summary>
        public void FinishAbandoned(Random rng)
        {
            int guard = 0;
            while (Result == null && guard++ < 20)
            {
                if (Paused) Miss(rng);
                Advance();
            }
        }
    }
}
