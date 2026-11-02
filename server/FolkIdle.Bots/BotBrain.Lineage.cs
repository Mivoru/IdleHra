using System.Text.Json;
using FolkIdle.Server.Network;

namespace FolkIdle.Bots
{
    /// <summary>
    /// The long game: characters 2 and 3 and their jobs, breeding with the
    /// village, the permanent inheritance bonuses, and rebirth.
    /// </summary>
    public sealed partial class BotBrain
    {
        private int _lastLevelSeen = -1;
        private double _lastLevelUpAt;

        private async Task LineageChoresAsync(CancellationToken token)
        {
            if (_state.CurrentLevel != _lastLevelSeen)
            {
                _lastLevelSeen = _state.CurrentLevel;
                _lastLevelUpAt = Now;
            }
            if (Due("inheritance", 60)) Inheritance();
            if (Due("roster", 60)) await RosterAsync(token);
            if (_state.BreedingLevel > 0 && Due("breeding", 180)) await BreedAsync(token);
            if (Due("rebirth", 360)) await RebirthAsync(token);
        }

        // ------------------------------------------------------------------
        // Inheritance
        // ------------------------------------------------------------------

        /// <summary>Diamonds into the permanent bonuses: XP first, then damage, health, loot luck, gold, gathering.</summary>
        private void Inheritance()
        {
            var levels = new (int Id, string Name, int Level)[]
            {
                (2, "Experience", _state.Inherit_XpGain), (0, "Damage", _state.Inherit_Damage), (1, "Max health", _state.Inherit_MaxHp),
                (5, "Loot luck", _state.Inherit_LootLuck), (3, "Gold", _state.Inherit_GoldGain), (4, "Gathering", _state.Inherit_GatheringYield),
            };
            // The cheapest of the wanted ones, earlier in the list on a tie, so the bonuses climb together.
            var pick = levels.Where(l => l.Level < InheritanceMax).OrderBy(l => InheritanceCost(l.Level)).ThenBy(l => Array.IndexOf(levels, l)).FirstOrDefault();
            if (pick.Name == null || _state.PremiumCurrencyBalance < InheritanceCost(pick.Level)) return;
            Enqueue(new ClientCommandPacket { Command = CommandType.PurchaseInheritanceLevel, TargetId = pick.Id },
                $"inheritance: {pick.Name} {pick.Level} -> {pick.Level + 1} for {InheritanceCost(pick.Level)} diamonds", 50, () => Count("inheritance_levels"));
        }

        /// <summary>InheritanceRegistry: ten levels, 100 diamonds x 1.25 per level (mirrored in commands.ts too).</summary>
        private const int InheritanceMax = 10;
        private static long InheritanceCost(int level) => (long)Math.Floor(100 * Math.Pow(1.25, Math.Max(0, level)));

        // ------------------------------------------------------------------
        // Characters 2 and 3
        // ------------------------------------------------------------------

        /// <summary>
        /// Fill the playable slots from the Hall (the oldest benched member
        /// first), and give characters 2 and 3 a job each: woodcutting and
        /// mining at the best reachable node - the materials the village and the
        /// tools need, earned without the main character leaving the fight.
        /// </summary>
        private async Task RosterAsync(CancellationToken token)
        {
            var hall = await _client.GetAsync("/api/v1/ancestors/hall", token);
            if (hall is not { } h) return;
            int playable = h.Int("PlayableSlots", 1);
            var members = h.Arr("Members").ToList();
            for (int slot = 1; slot < Math.Min(3, playable); slot++)
            {
                if (members.Any(m => m.Int("PlayableSlot", -1) == slot)) continue;
                var bench = members.Where(m => m.Int("PlayableSlot", -1) < 0 && !m.Bool("IsMainCharacter"))
                    .OrderByDescending(m => m.Int("AgePhase")).FirstOrDefault();
                if (bench.ValueKind != JsonValueKind.Object) break;
                Enqueue(new ClientCommandPacket { Command = CommandType.AssignCharacterSlot, TargetGuid = Guid.Parse(bench.Str("CharacterId")), RequestedSlotIndex = (byte)slot },
                    $"roster: {bench.Str("Name")} into slot {slot + 1}", 30);
                return;
            }

            // Modul: A NEW ACCOUNT ALREADY HAS A SECOND CHARACTER (the founder's
            // spouse, in slot 2), but PlayableSlots is 1 until the Town Hall
            // opens slot 2 - a job sent to it is refused. Only playable slots
            // get work.
            foreach (var (slotIndex, characterId, activity, profession) in new[]
            {
                (1, _state.Slot2_CharacterId, (long)_state.Slot2ActivityId, ProfessionWoodcutting),
                (2, _state.Slot3_CharacterId, (long)_state.Slot3ActivityId, ProfessionMining),
            })
            {
                if (characterId == Guid.Empty || slotIndex >= playable) continue;
                long node = NodeFor(0, profession);
                if (node <= 0 || node == activity || IsRefused(node + characterId.GetHashCode())) continue;
                Enqueue(new ClientCommandPacket { Command = CommandType.ChangeActivity, TargetId = node, TargetGuid = characterId },
                    $"roster: character {characterId.ToString()[..8]} to {(profession == ProfessionWoodcutting ? "woodcutting" : "mining")} at node {node}", 35,
                    () => _refused[node + characterId.GetHashCode()] = (_state.CurrentLevel, _state.HighestLocationReached, _state.HighestUnlockedRegion));
                return;
            }
        }

        // ------------------------------------------------------------------
        // Breeding
        // ------------------------------------------------------------------

        /// <summary>
        /// The standard pair: the main character and the villager of the other
        /// sex with the best aptitudes, bred for Strength and Endurance as far
        /// as the Grounds allow, when the server's preview says eligible and
        /// affordable. With nobody suitable in the village, a feast brings
        /// somebody when the gold above the reserve covers it twice.
        /// </summary>
        private async Task BreedAsync(CancellationToken token)
        {
            var village = await _client.GetAsync("/api/v1/village/newcomers", token);
            if (village is not { } v) return;
            var hall = await _client.GetAsync("/api/v1/ancestors/hall", token);
            var hero = hall is { } h ? h.Arr("Members").FirstOrDefault(m => m.Bool("IsMainCharacter")) : default;
            if (hero.ValueKind != JsonValueKind.Object) return;
            string heroId = hero.Str("CharacterId");
            bool heroFemale = hero.Bool("IsFemale");
            int heroRace = hero.Int("RaceId");

            var partners = v.Arr("Newcomers")
                .Where(n => !n.Bool("IsElder") && n.Bool("IsFemale") != heroFemale && n.Int("RaceId") == heroRace)
                .OrderByDescending(n => n.Int("AptitudeStrength") + n.Int("AptitudeSkill") + n.Int("AptitudeEndurance") + n.Int("AptitudeFortune"))
                .ToList();

            foreach (var partner in partners.Take(3))
            {
                long newcomerId = partner.Long("Id");
                var preview = await _client.GetAsync($"/api/v1/breeding/village-preview?heroId={heroId}&newcomerId={newcomerId}", token);
                if (preview is not { } p) continue;
                if (!p.Bool("IsEligible"))
                {
                    if (Due("breeding-why", 720)) Event($"breeding: not eligible with {partner.Str("Name")}: {p.Str("IneligibleReason")}");
                    return; // the reason is usually the hero's (level, cooldown), not the partner's
                }
                if (!p.Bool("HasSufficientGold")) return;
                int allowed = _state.BreedingLevel >= 10 ? 3 : _state.BreedingLevel >= 7 ? 2 : _state.BreedingLevel >= 4 ? 1 : 0;
                uint mask = allowed switch { >= 3 => 0b0111u, 2 => 0b0101u, 1 => 0b0001u, _ => 0u }; // Strength, Endurance, then Skill
                Enqueue(new ClientCommandPacket { Command = CommandType.ExecuteVillagerBreeding, TargetGuid = Guid.Parse(heroId), TargetId = newcomerId, BreedingSelectionMask = (byte)mask },
                    $"breeding: {hero.Str("Name")} x {partner.Str("Name")} for {p.Long("BreedingCostGold")} gold (mask {mask})", 40, () => Count("children_bred"));
                return;
            }

            if (partners.Count == 0 && v.Str("RecruitBlockedReason").Length == 0 && _state.Gold - GoldReserve > v.Long("RecruitCostGold") * 2)
            {
                Enqueue(new ClientCommandPacket { Command = CommandType.RecruitVillager }, $"village: a feast for {v.Long("RecruitCostGold")} gold (no suitable partner)", 45, () => Count("villagers_recruited"));
            }
        }

        // ------------------------------------------------------------------
        // Rebirth
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebirth when it raises Renown and the run has stalled - no level for
        /// six game hours - or reached level 100. The worn weapon is kept when
        /// its price is under half the gold.
        /// </summary>
        private async Task RebirthAsync(CancellationToken token)
        {
            var preview = await _client.GetAsync("/api/v1/rebirth/preview", token);
            if (preview is not { } p || p.Bool("Capped") || !p.Bool("Renowned")) return;
            // Modul: A STALL IS COUNTED IN CHECK-INS FOR A PLAYER WHO LOGS OUT
            // (x200 run, 2026-11-02). Six game hours without a level read
            // every slow offline window as a stall, so the casual bots reborn
            // at level 50 nine times and ended the run at level 1. For them it
            // is three check-in intervals.
            double stallHours = _persona.AlwaysOn ? 6 : Math.Max(6, 3 * _persona.CheckInEveryGameHours);
            bool stalled = Now - _lastLevelUpAt >= stallHours;
            if (!stalled && _state.CurrentLevel < 100) return;

            var keep = new List<long>();
            var prices = p.Arr("KeepPriceByTier").Select(e => e.GetInt64()).ToList();
            var weapon = Pieces().FirstOrDefault(x => x.Equipped && x.ByChar == 0 && x.InSlot == 0);
            if (weapon != null && weapon.Tier < prices.Count && prices[weapon.Tier] * 2 <= _state.Gold) keep.Add(weapon.Id);

            var (status, body) = await _client.PostAsync("/api/v1/rebirth", new { ExpectedRebirthCount = p.Int("RebirthCount"), Confirm = "REBIRTH", KeepEquipmentIds = keep }, token);
            string result = body is { } b ? b.Str("Result") : status.ToString();
            Event($"REBIRTH {p.Int("RebirthCount") + 1} at level {_state.CurrentLevel} ({(stalled ? $"stalled {stallHours:0} h" : "level 100")}): {result}; damage bonus {p.Num("DamageBonusPctNow"):0.#}% -> {p.Num("DamageBonusPctAfter"):0.#}%, kept {keep.Count} piece(s)");
            if (result == "Ok")
            {
                Count("rebirths");
                _log.Milestone(_name, $"rebirth_{p.Int("RebirthCount") + 1}", _state);
                _refused.Clear();
                _avoidUntilLevel.Clear();
                _trip = null;
                _materialGoal = null;
                _queue.Clear();
                _gearTried.Clear();
                _skillTreeTriedAtPoints = -1;
                _inventoryDirty = _projectionDirty = true;
                _lastLevelUpAt = Now;
            }
        }
    }
}
