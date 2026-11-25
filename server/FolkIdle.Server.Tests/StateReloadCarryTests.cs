using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE RELOAD WIPED THE ANSWER TO THE COMMAND THAT CAUSED IT.
    ///
    /// Every engine that changes the database out-of-band - reroll, fusion, the
    /// market, the village, crafting - suspends the player, does its work, and
    /// finishes by enqueuing ReloadState so the live payload picks the change
    /// up. ReloadState loads a FRESH payload with StateCheckpointManager
    /// .LoadPlayerState and the drain replaces the live one with it wholesale.
    ///
    /// The command result ring (CommandResultSlot0-3) and its tick counter are
    /// runtime-only: they exist on the payload and in no table. So the sequence
    /// was
    ///
    ///   1. the engine enqueues its result, the tick writes it into the ring;
    ///   2. the player is suspended, so no broadcast carries the ring;
    ///   3. the reload lands and replaces the payload - ring zeroed, counter 0;
    ///   4. broadcasts resume, carrying nothing.
    ///
    /// Measured in a browser on the dev fixture: a single reroll, a
    /// fifty-attempt auto-reroll run, and a deliberately impossible stop
    /// condition all produced NO toast whatsoever. The affix changed and the
    /// gold was deducted, so the command worked; the only thing missing was
    /// every word the server said about it.
    ///
    /// That is this codebase's signature defect - the output side was never
    /// wired - sitting underneath the complaint that a reroll pops too many
    /// notifications. Both are true: too many while the run was in flight on
    /// some timings, none at all on these.
    ///
    /// The counter also has to carry, not just the slots. The client's
    /// CommandResultFeed keeps a high-water mark of ticks it has shown and
    /// filters anything at or below it, so a counter that restarts at zero makes
    /// every later result in the session invisible even once the slots survive.
    /// </summary>
    public class StateReloadCarryTests
    {
        private static TickStatePayload LivePayloadWithAResult()
        {
            return new TickStatePayload
            {
                PlayerId = 4711L,
                CommandResultTickCounter = 9,
                CommandResultSlot0 = new CommandResultEntry { ResultCode = 23, ResultTick = 9 },
                CommandResultSlot1 = new CommandResultEntry { ResultCode = 5, ResultTick = 8 },
                CommandResultRingWriteIndex = 1,
                CurrentGold = 1_000L
            };
        }

        [Fact]
        public void AReloadKeepsTheResultOfTheCommandThatTriggeredIt()
        {
            var live = LivePayloadWithAResult();

            // What LoadPlayerState returns: everything persisted, nothing
            // runtime-only.
            var reloaded = new TickStatePayload { PlayerId = 4711L, CurrentGold = 900L };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(23, reloaded.CommandResultSlot0.ResultCode);
            Assert.Equal(9u, reloaded.CommandResultSlot0.ResultTick);
            Assert.Equal(5, reloaded.CommandResultSlot1.ResultCode);
            Assert.Equal(1, reloaded.CommandResultRingWriteIndex);
        }

        [Fact]
        public void TheResultTickCounterCarriesToo()
        {
            var live = LivePayloadWithAResult();
            var reloaded = new TickStatePayload { PlayerId = 4711L };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            // A counter that restarts at 0 puts every later result below the
            // client's watermark, which is indistinguishable from the server
            // going quiet.
            Assert.Equal(9u, reloaded.CommandResultTickCounter);
        }

        /// <summary>
        /// The whole point of a reload is that the database is now the truth
        /// about everything it stores. Carrying the ring must not drag a stale
        /// balance back with it.
        /// </summary>
        [Fact]
        public void TheReloadedValuesStillWinForEverythingTheDatabaseHolds()
        {
            var live = LivePayloadWithAResult();
            var reloaded = new TickStatePayload { PlayerId = 4711L, CurrentGold = 900L };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(900L, reloaded.CurrentGold);
        }

        // Modul: task 24. A ReloadState after a market listing, a guild action
        // or a craft put a fighting player back to idle, because the activity
        // lived in memory and LoadPlayerState read a row nothing had written.
        private static readonly System.Guid Fielded = new System.Guid("24242424-0000-0000-0000-000000000001");

        private static TickStatePayload LiveFighter()
        {
            return new TickStatePayload
            {
                PlayerId = 4711L,
                Slot1_CharacterId = Fielded,
                ActiveActivityId = 91L,
                CurrentMonsterId = 91,
                CurrentMonsterHp = 12_000L,
                PlayerHp = 40_000,
                CurrentProgressTicks = 7,
                GatheringProgressTicks = 3
            };
        }

        [Fact]
        public void AReloadKeepsTheFightOfACharacterStillInTheSlot()
        {
            var live = LiveFighter();
            // What LoadPlayerState returns for a row nobody wrote: idle, full HP.
            var reloaded = new TickStatePayload { PlayerId = 4711L, Slot1_CharacterId = Fielded, ActiveActivityId = 0L, PlayerHp = 100_000 };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(91L, reloaded.ActiveActivityId);
            Assert.Equal(91, reloaded.CurrentMonsterId);
            Assert.Equal(12_000L, reloaded.CurrentMonsterHp);
            Assert.Equal(7, reloaded.CurrentProgressTicks);
            Assert.Equal(3, reloaded.GatheringProgressTicks);
            // A market listing is not a free full heal.
            Assert.Equal(40_000, reloaded.PlayerHp);
        }

        /// <summary>
        /// The Hall of Ancestors changes who holds a slot and benches the
        /// displaced character idle before its reload. The old occupant's
        /// fight must not follow the slot onto the newcomer.
        /// </summary>
        [Fact]
        public void AReloadDoesNotCarryAFightOntoADifferentCharacter()
        {
            var live = LiveFighter();
            var newcomer = new System.Guid("24242424-0000-0000-0000-000000000002");
            var reloaded = new TickStatePayload { PlayerId = 4711L, Slot1_CharacterId = newcomer, ActiveActivityId = 0L, PlayerHp = 100_000 };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(0L, reloaded.ActiveActivityId);
            Assert.Equal(100_000, reloaded.PlayerHp);
        }

        [Fact]
        public void TheSecondAndThirdSlotsKeepTheirActivityToo()
        {
            var second = new System.Guid("24242424-0000-0000-0000-000000000003");
            var live = new TickStatePayload { PlayerId = 4711L, Slot2_CharacterId = second };
            live.Slot2Activity.ActiveActivityId = 92L;
            live.Slot2Activity.CurrentMonsterHp = 5_000L;
            var reloaded = new TickStatePayload { PlayerId = 4711L, Slot2_CharacterId = second };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(92L, reloaded.Slot2Activity.ActiveActivityId);
            Assert.Equal(5_000L, reloaded.Slot2Activity.CurrentMonsterHp);
        }

        /// <summary>
        /// Equipment is database truth, and a reload is how a fresh equip
        /// reaches the live session. Carrying the activity must not drag the
        /// old gear back with it.
        /// </summary>
        [Fact]
        public void EquipmentStillComesFromTheReload()
        {
            var live = LiveFighter();
            live.EquippedWeaponId = 1L;
            var reloaded = new TickStatePayload { PlayerId = 4711L, Slot1_CharacterId = Fielded, EquippedWeaponId = 2L };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(2L, reloaded.EquippedWeaponId);
        }
        /// <summary>
        /// Run E2 (2026-11-24): a fusion, equip or village upgrade right after
        /// StartBossAscension reloaded the payload and dropped the armed step
        /// while the fight carried on - the boss died at plain strength and the
        /// ladder never moved. The attempt crosses with its fight.
        /// </summary>
        [Fact]
        public void AReloadKeepsAnArmedAscensionAttempt()
        {
            var live = LiveFighter();
            live.AscensionStep = 3;
            live.AscensionRegion = 1;
            live.AscensionCharacterId = Fielded;
            live.AscensionWardQuarters = 2;
            live.AscensionWardUntilTenths = 450L;
            live.SeasonalBossTier = 2;
            live.SeasonalBossCharacterId = Fielded;
            live.SeasonalBossReturnActivityId = 3002L;
            var reloaded = new TickStatePayload { PlayerId = 4711L, Slot1_CharacterId = Fielded };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(3, reloaded.AscensionStep);
            Assert.Equal(1, reloaded.AscensionRegion);
            Assert.Equal(Fielded, reloaded.AscensionCharacterId);
            Assert.Equal(2, reloaded.AscensionWardQuarters);
            Assert.Equal(450L, reloaded.AscensionWardUntilTenths);
            Assert.Equal(2, reloaded.SeasonalBossTier);
            Assert.Equal(Fielded, reloaded.SeasonalBossCharacterId);
            Assert.Equal(3002L, reloaded.SeasonalBossReturnActivityId);
        }

        [Fact]
        public void AnArmedAttemptDoesNotFollowTheSlotOntoANewcomerOrThroughARebirth()
        {
            var live = LiveFighter();
            live.AscensionStep = 3;
            live.AscensionRegion = 1;
            live.AscensionCharacterId = Fielded;
            var newcomer = new System.Guid("24242424-0000-0000-0000-000000000002");
            var benched = new TickStatePayload { PlayerId = 4711L, Slot1_CharacterId = newcomer };
            StateReloadMerge.CarryLiveOnlyFields(in live, ref benched);
            Assert.Equal(0, benched.AscensionStep);

            live.RebirthPending = true;
            var reborn = new TickStatePayload { PlayerId = 4711L, Slot1_CharacterId = Fielded };
            StateReloadMerge.CarryLiveOnlyFields(in live, ref reborn);
            Assert.Equal(0, reborn.AscensionStep);
        }

        /// <summary>
        /// Run G (2026-11-24): the death and victory ticks are edge bytes, and a
        /// reload put them back to zero - a change, so the client raised a death
        /// card naming monster 0 at the first reroll or equip after a death.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AReloadKeepsTheDeathAndVictoryEdges(bool throughARebirth)
        {
            var live = LiveFighter();
            live.RebirthPending = throughARebirth;
            live.LastDeathTick = 4;
            live.LastDeathMonsterId = 97;
            live.LastVictoryTick = 2;
            live.LastVictoryMonsterId = 95;
            live.LastVictoryDurationSeconds = 31;
            live.LastVictoryGold = 1_200L;
            live.LastVictoryXp = 900L;
            var reloaded = new TickStatePayload { PlayerId = 4711L, Slot1_CharacterId = Fielded };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(4, reloaded.LastDeathTick);
            Assert.Equal(97, reloaded.LastDeathMonsterId);
            Assert.Equal(2, reloaded.LastVictoryTick);
            Assert.Equal(95, reloaded.LastVictoryMonsterId);
            Assert.Equal(31, reloaded.LastVictoryDurationSeconds);
            Assert.Equal(1_200L, reloaded.LastVictoryGold);
            Assert.Equal(900L, reloaded.LastVictoryXp);
        }

        /// <summary>
        /// The village's output is written once a minute and lives only on the
        /// payload until then. A reload every thirty seconds used to mean a
        /// village that paid nothing for as long as its owner was playing.
        /// </summary>
        [Fact]
        public void AReloadDoesNotDropWhatTheVillageHasMade()
        {
            static TickStatePayload Village() => new() { PlayerId = 77_004_711L, LumberjackLevel = 20, MineLevel = 20, TownHallLevel = 3 };
            var steady = Village();
            var reloading = Village();

            // Short of the 600-tick grant, so the shared queue is never touched.
            for (int tick = 1; tick <= 590; tick++)
            {
                SimulationEngine.ProcessPassiveVillageTick(ref steady, 0.1, 0L);
                SimulationEngine.ProcessPassiveVillageTick(ref reloading, 0.1, 0L);
                if (tick % 100 != 0) continue;
                var reloaded = Village();
                StateReloadMerge.CarryLiveOnlyFields(in reloading, ref reloaded);
                reloading = reloaded;
            }

            Assert.True(steady.PendingVillageLog > 0);
            Assert.Equal(steady.PendingVillageLog, reloading.PendingVillageLog);
            Assert.Equal(steady.PendingVillageRareLog, reloading.PendingVillageRareLog);
            Assert.Equal(steady.PendingVillageOre, reloading.PendingVillageOre);
            Assert.Equal(steady.PendingVillageRareOre, reloading.PendingVillageRareOre);
            Assert.Equal(590, reloading.VillageProductionTicksSinceGrant);
            Assert.Equal(steady.TownHallGoldAccumulator, reloading.TownHallGoldAccumulator);
        }

        [Fact]
        public void AReloadKeepsTheFightsCounters()
        {
            var live = LiveFighter();
            live.KillsSinceFleece = 96;
            live.LastStandCooldownRemaining = 400;
            live.AutoEatCooldownTicks = 11;
            live.AteThisFight = true;
            live.RationTicksSinceMeal = 55;
            live.Hungry = true;
            live.WarSwingCount = 3;
            live.WarHitCount = 4;
            var reloaded = new TickStatePayload { PlayerId = 4711L, Slot1_CharacterId = Fielded };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(96, reloaded.KillsSinceFleece);
            Assert.Equal(400, reloaded.LastStandCooldownRemaining);
            Assert.Equal(11, reloaded.AutoEatCooldownTicks);
            Assert.True(reloaded.AteThisFight);
            Assert.Equal(55, reloaded.RationTicksSinceMeal);
            Assert.True(reloaded.Hungry);
            Assert.Equal(3, reloaded.WarSwingCount);
            Assert.Equal(4, reloaded.WarHitCount);
        }

        // Modul: A FIELD THAT LIVES NOWHERE BUT THE PAYLOAD DIES AT A RELOAD.
        // The armed Ascension step, the death card and the village's minute of
        // output were each lost that way, months apart, silently. A new runtime
        // field now has to be carried or explained here.
        private static readonly System.Collections.Generic.Dictionary<string, string> LostOnPurpose = new()
        {
            ["IsSuspended"] = "the reload clears it",
            ["RebirthPending"] = "what the reload resolves",
            ["RequiresRedisFlush"] = "the reload flushed first",
            ["LastClientCommandAtMs"] = "anti-cheat: forgetting can only excuse a miss",
            ["LastCommandTimestamp"] = "anti-cheat: rate baseline, re-set by the next command",
            ["ActiveChallengeSeed"] = "anti-cheat: the next broadcast issues a fresh one, a late answer is Stale",
            ["ActiveChallengeIssuedAtMs"] = "anti-cheat, as the seed",
            ["ActiveChallengeIssuedEpoch"] = "anti-cheat, as the seed",
            ["ActiveChallengeAnswered"] = "anti-cheat, as the seed",
            ["ConsecutiveChallengeMisses"] = "anti-cheat: forgetting can only excuse a miss",
            ["NetworkDiagnosticsToken"] = "diagnostic",
            ["ActiveUiContextBitmask"] = "written, read by nothing",
            ["CachedEffectiveMilliAttack"] = "recomputed every combat tick",
            ["PendingSkillDamageMultiplier"] = "set and consumed within one swing",
            ["LastHitWasCrit"] = "describes the last swing",
            ["VillagePopulation"] = "twin of CurrentPopulationCount, which is loaded",
            ["ActiveChildMaturationMs"] = "never set above zero",
            ["AccumulatedStone"] = "nothing produces stone",
            ["OfflineElapsedSeconds"] = "offline summary: the client shows it on a session's first packet only",
            ["OfflineSummaryTick"] = "offline summary",
            ["OfflineGoldEarned"] = "offline summary",
            ["OfflineXpEarned"] = "offline summary",
            ["OfflineMaterialDropsGranted"] = "offline summary",
            ["OfflineMaterialsLostToFullWarehouse"] = "offline summary",
            ["OfflineSlot1Gold"] = "offline summary", ["OfflineSlot1Xp"] = "offline summary", ["OfflineSlot1Drops"] = "offline summary",
            ["OfflineSlot2Gold"] = "offline summary", ["OfflineSlot2Xp"] = "offline summary", ["OfflineSlot2Drops"] = "offline summary",
            ["OfflineSlot3Gold"] = "offline summary", ["OfflineSlot3Xp"] = "offline summary", ["OfflineSlot3Drops"] = "offline summary",
            ["EquippedWeaponAffixLocked"] = "inert (audit 2026-09-05 A)",
            ["EquippedArmorAffixLocked"] = "inert (audit 2026-09-05 A)",
            ["EquippedLeggingsAffixLocked"] = "inert (audit 2026-09-05 A)",
            ["WorldBossMaxHp"] = "the packet reads the engine",
            ["WorldBossCurrentHp"] = "the packet reads the engine",
            ["ActiveGlobalEventId"] = "the packet reads the engine",
            ["CachedWarMultiplier"] = "Guild Wars, hidden",
            ["GuildCombatVanguardPoints"] = "Guild Wars, hidden",
            ["GuildProductionLogisticsPoints"] = "Guild Wars, hidden",
            ["GuildGatheringSupplyChainPoints"] = "Guild Wars, hidden",
            ["EnemyCombatVanguardPoints"] = "Guild Wars, hidden",
            ["EnemyProductionLogisticsPoints"] = "Guild Wars, hidden",
            ["EnemyGatheringSupplyChainPoints"] = "Guild Wars, hidden",
            // KNOWN GAPS, not judgements: pushed by a notification and loaded by
            // nothing, so a login loses them as surely as a reload does.
            ["CachedGuildLogisticsLevel"] = "gap: never loaded",
            ["CachedGuildRaidTier"] = "gap: never loaded",
            ["CachedGuildRaidBossCurrentHp"] = "gap: never loaded",
            ["CachedGuildRaidBossMaxHp"] = "gap: never loaded",
            ["HighestForgeSynthesisTier"] = "gap: never loaded; the Forging award only ever moves up",
        };

        [Fact]
        public void EveryPayloadFieldIsLoadedCarriedOrLostOnPurpose()
        {
            string merge = Source("Engine", "StateReloadMerge.cs");
            string loader = Source("Domain", "Shared", "StateCheckpointManager.cs") + Source("Domain", "Progression", "QuestEngine.cs");

            // Values behind a property or an indexer, loaded through it.
            var throughAProperty = new[] { "ObfuscatedGold", "ObfuscatedPremiumCurrency", "ObfuscatedLegacyShards", "ObfuscationSessionKey", "SkillRanks" };
            var lost = new System.Collections.Generic.List<string>();
            int seen = 0;
            foreach (var field in typeof(TickStatePayload).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                seen++;
                string name = field.Name;
                if (System.Array.IndexOf(throughAProperty, name) >= 0 || LostOnPurpose.ContainsKey(name)) continue;
                bool carried = System.Text.RegularExpressions.Regex.IsMatch(merge, @"\breloaded\." + name + @"\b");
                bool loaded = System.Text.RegularExpressions.Regex.IsMatch(loader, @"\b" + name + @"\s*=[^=]");
                if (!carried && !loaded) lost.Add(name);
            }

            Assert.True(seen > 300, $"only {seen} fields found - the reflection is wrong, not the code");
            Assert.True(lost.Count == 0, "a mid-session reload drops these payload fields. Carry them in StateReloadMerge, "
                + "or add them to LostOnPurpose WITH A REASON: " + string.Join(", ", lost));
        }

        private static string Source(params string[] parts)
        {
            for (var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string path = System.IO.Path.Combine(dir.FullName, "FolkIdle.Server", System.IO.Path.Combine(parts));
                if (System.IO.File.Exists(path)) return System.IO.File.ReadAllText(path);
            }
            throw new System.IO.FileNotFoundException(string.Join("/", parts));
        }
    }
}
