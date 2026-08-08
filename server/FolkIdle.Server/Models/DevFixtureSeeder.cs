using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Models
{
    // Modul: dev fixture. A repeatable, fully-provisioned account for driving
    // the client by hand or through the MCP Play Mode harness.
    //
    // DbSeeder already creates a login-capable dev account, but only on a
    // completely empty database (`if (!await db.PlayerRecords.AnyAsync())`) and
    // with nothing on it: no characters, no equipment, no Town Hall level. So
    // every attempt to verify the things most likely to be broken - multiple
    // character slots, the seven equip slots, region progression - started with
    // hand-writing rows through a throwaway console app, which is not
    // repeatable and not reviewable.
    //
    // This is deliberately NOT wired into normal startup. It runs only from the
    // --seed-dev flag, which additionally refuses to do anything unless
    // FOLKIDLE_ALLOW_DEV_SEED is set - see Program.cs. It writes a known
    // account with a known password, so it must never be reachable in
    // production by accident.
    //
    // Idempotent: re-running it updates the same account in place rather than
    // creating a second one or throwing on the unique Email index.
    public static class DevFixtureSeeder
    {
        public const string Email = "dev@folkidle.local";
        public const string Username = "dev";
        public const string Password = "FolkIdleDev123!";

        private const long Gold = 5_000_000L;
        private const int Diamonds = 5_000;
        private const int PlayerLevel = 40;

        // Town Hall 5 unlocks all three character slots (slot 2 at level 3,
        // slot 3 at level 5 - see CharacterSlotEngine), which is the whole
        // point of the fixture.
        private const int TownHallLevel = 5;
        private const int WorkshopLevel = 5;
        private const int ForgeLevel = 5;

        // Modul: the fixture could not breed, at all.
        //
        // Three things were missing and each one alone was fatal: no Breeding
        // Grounds (the engine refuses without it), no character_lineage_registry
        // rows (the roster endpoint SKIPS a character with no lineage, so the
        // parent list came back empty), and no sexes (every character defaulted
        // to male, and a pair needs one of each). The account that exists
        // specifically for driving the client by hand therefore had a dead
        // Breeding screen.
        //
        // Characters are seeded at 50 rather than at PlayerLevel, because 50 is
        // the gate the standard pair is built around - a fixture that stops one
        // level short exercises the refusal and nothing else.
        private const int CharacterLevel = 50;

        public static async Task<long> SeedAsync(FolkIdleDbContext db)
        {
            string normalizedEmail = Email.ToLowerInvariant();

            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Email == normalizedEmail);
            if (player == null)
            {
                player = new PlayerRecord
                {
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    Email = normalizedEmail,
                    Username = Username,
                    SelectedLineageId = 1
                };
                db.PlayerRecords.Add(player);
                await db.SaveChangesAsync();
            }

            player.PasswordHash = PasswordHasher.Hash(Password);
            player.CurrentLevel = PlayerLevel;
            player.CurrentXp = 0;
            player.PremiumDiamonds = Diamonds;
            player.AvailableSkillPoints = PlayerLevel;

            // A fixture account must never arrive quarantined - an automated
            // session that drove the client hard enough to trip the anti-cheat
            // heuristic once would otherwise be unusable forever after.
            player.IsQuarantined = false;
            player.Quarantine_Active = false;

            // Modul: dev fixture. STOCK THE LARDER. Found the hard way: the
            // first version of this seeder produced a character that halted
            // with ActivityHaltReason.OutOfFood about a minute into the first
            // fight, because auto-eat fires the moment HP crosses the threshold
            // and an empty larder stops the activity outright. A fixture whose
            // whole purpose is unattended playtesting must be able to run
            // unattended, so it ships fed.
            //
            // Ids 194-203 are the ten real cooked foods; the three stocked here
            // are mid-tier so healing is meaningful without being infinite.
            player.LarderSlot1ItemId = 198; // cooked_steppe_salmon_t5_food
            player.LarderSlot1Count = 999;
            player.LarderSlot2ItemId = 197; // cooked_chasm_pike_t4_food
            player.LarderSlot2Count = 999;
            player.LarderSlot3ItemId = 196; // cooked_mud_carp_t3_food
            player.LarderSlot3Count = 999;

            // Eat at 50% rather than the default 0, which never triggers.
            player.AutoEatThresholdPct = 50;

            await db.SaveChangesAsync();
            long playerId = player.Id;

            await UpsertCommodityAsync(db, playerId, "gold", Gold);

            // Modul: THERE ARE TWO MATERIAL NAMESPACES and the fixture needs
            // both, 2026-08-02.
            //
            //   GetMaterialString(1-6) - the original six gathering slugs
            //     ("copper_ore", "raw_log", ...). Produced by gathering nodes,
            //     consumed by VillageManagementEngine's building upgrades.
            //   GetItemBaseId(id)      - the full items.json catalogue
            //     ("iron_ore_crafting_material", "coal_node_crafting_material",
            //     ...). Produced by combat loot, consumed by every crafting
            //     recipe.
            //
            // Both are legitimate and both have real producers and consumers,
            // but they are DIFFERENT CommodityRecords rows. The fixture stocked
            // only the first four gathering slugs under a comment claiming it
            // was "enough to actually craft something without gathering first",
            // which was simply untrue: no recipe consumes those slugs, so the
            // fixture could craft exactly none of the 103 recipes. The Crafting
            // screen showed "0 of 103 craftable" on an account with five
            // million gold, which is how this surfaced.
            //
            // Gathering slugs stay, because village upgrades spend them.
            await UpsertCommodityAsync(db, playerId, ContentRegistry.GetMaterialString(1), 5_000L); // copper_ore
            await UpsertCommodityAsync(db, playerId, ContentRegistry.GetMaterialString(2), 5_000L); // raw_log
            await UpsertCommodityAsync(db, playerId, ContentRegistry.GetMaterialString(3), 5_000L); // iron_ore
            await UpsertCommodityAsync(db, playerId, ContentRegistry.GetMaterialString(4), 5_000L); // oak_log

            // And every material any recipe actually asks for, derived FROM the
            // recipe table rather than listed by hand - a hardcoded slug list
            // is precisely what went stale above, and it would go stale again
            // the first time a recipe changed its inputs.
            await UpsertEveryCraftingMaterialAsync(db, playerId);

            await UpsertBuildingAsync(db, playerId, VillageManagementEngine.TownHallBuildingId, TownHallLevel);
            await UpsertBuildingAsync(db, playerId, VillageManagementEngine.CraftingWorkshopBuildingId, WorkshopLevel);
            await UpsertBuildingAsync(db, playerId, VillageManagementEngine.ForgeBuildingId, ForgeLevel);
            await UpsertBuildingAsync(db, playerId, VillageManagementEngine.InnBuildingId, 5);
            await UpsertBuildingAsync(db, playerId, VillageManagementEngine.MentorshipAcademyBuildingId, 2);
            await UpsertBuildingAsync(db, playerId, VillageManagementEngine.BreedingGroundsBuildingId, 1);

            await EnsureCharactersAsync(db, playerId);
            await EnsureLineagesAsync(db, playerId);
            await EnsureVillagersAsync(db, playerId);
            await EnsureEquipmentAsync(db, playerId);

            await db.SaveChangesAsync();
            return playerId;
        }

        private static async Task UpsertCommodityAsync(FolkIdleDbContext db, long playerId, string itemId, long quantity)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            var row = await db.CommodityRecords.FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == itemId);
            if (row == null)
            {
                db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = itemId, Quantity = quantity });
                return;
            }

            // Set rather than add, so re-running does not inflate the balance.
            row.Quantity = quantity;
        }

        private static async Task UpsertBuildingAsync(FolkIdleDbContext db, long playerId, int buildingId, int level)
        {
            var row = await db.VillageInfrastructures
                .FirstOrDefaultAsync(v => v.PlayerId == playerId && v.BuildingId == buildingId);

            if (row == null)
            {
                db.VillageInfrastructures.Add(new VillageInfrastructure
                {
                    PlayerId = playerId,
                    BuildingId = buildingId,
                    CurrentLevel = level,
                    UpgradeTargetLevel = 0,
                    UpgradeCompletesAtEpoch = 0
                });
                return;
            }

            row.CurrentLevel = level;
            row.UpgradeTargetLevel = 0;
            row.UpgradeCompletesAtEpoch = 0;
        }

        // Every distinct material any recipe consumes, stocked deep enough that
        // a few crafts of anything do not exhaust it.
        //
        // Derived from ContentRegistry.Recipes so it cannot drift: a recipe
        // that changes its inputs, or a new one added to the table, is covered
        // without touching this method. Intermediate outputs (a bar that feeds
        // an equipment recipe) are stocked too, because they appear as some
        // other recipe's Mat1Id/Mat2Id - which is exactly what makes deriving
        // this better than listing the "raw" ones by hand.
        //
        // Modul: THESE GO IN THE VILLAGE STASH, NOT THE BACKPACK, and that is
        // the difference between a playable fixture and a bricked one.
        //
        // CountOccupiedBackpackSlotsAsync counts one slot per CommodityRecords
        // ROW - a thousand Iron Ore is one slot, but fifty different materials
        // are fifty slots, against a capacity of 20 (25 with Human mastery).
        // ProcessSubTick returns immediately when no space remains, so seeding
        // every recipe material into the backpack put the fixture at 58/20 and
        // it could never fight or gather again: ChangeActivity was accepted,
        // ActiveActivityId became 91, and CurrentMonsterId never left 0.
        //
        // VillageStashInstances is a separate table that the census does not
        // count, and crafting spends the UNIFIED balance across both. So the
        // stash gives the fixture everything it needs to craft while leaving
        // the backpack empty enough to play with. Found by driving the real UI
        // and then probing the wire, not by reading.
        private static async Task UpsertEveryCraftingMaterialAsync(FolkIdleDbContext db, long playerId)
        {
            foreach (int materialId in CollectRecipeMaterialIds())
            {
                string baseId = ContentRegistry.GetItemBaseId(materialId);
                if (string.IsNullOrEmpty(baseId))
                {
                    continue;
                }

                await UpsertStashAsync(db, playerId, baseId, CraftingMaterialStock);
            }
        }

        private static async Task UpsertStashAsync(FolkIdleDbContext db, long playerId, string itemId, long quantity)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            var row = await db.VillageStashInstances.FirstOrDefaultAsync(s => s.PlayerId == playerId && s.ItemId == itemId);
            if (row == null)
            {
                db.VillageStashInstances.Add(new VillageStashInstance { PlayerId = playerId, ItemId = itemId, Quantity = quantity });
                return;
            }

            // Set rather than add, so re-running does not inflate the balance.
            row.Quantity = quantity;
        }

        private const long CraftingMaterialStock = 2_000L;

        // Split out of the async method above: ContentRegistry.Recipes is a
        // ReadOnlySpan, whose enumerator is a ref struct and therefore cannot
        // live across an await. Collecting first keeps the derivation next to
        // the recipe table rather than pushing it back into a hand-written list.
        private static SortedSet<int> CollectRecipeMaterialIds()
        {
            var materialIds = new SortedSet<int>();
            foreach (ContentRegistry.RecipeDefinition recipe in ContentRegistry.Recipes)
            {
                if (recipe.Mat1Id > 0) materialIds.Add(recipe.Mat1Id);
                if (recipe.Mat2Id > 0) materialIds.Add(recipe.Mat2Id);
            }
            return materialIds;
        }

        // Three adult characters, one per unlocked slot.
        //
        // Modul: THE MAIN CHARACTER'S Id MUST BE THE PLAYER'S PlayerGuid, 2026-08-02.
        //
        // That is a real invariant of this schema, not a coincidence:
        // EquipmentSlotEngine.ResolveCharacterForUpdateAsync resolves
        // Guid.Empty ("the main character", which is what every equip and
        // unequip command sends) by looking up the character whose Id equals
        // PlayerRecords.PlayerGuid, and StateCheckpointManager hydrates the
        // same row into slot 1. Normal account provisioning establishes it.
        //
        // This seeder used Guid.NewGuid() for all three slots, so the fixture
        // had no character matching its own PlayerGuid - and EVERY equip and
        // unequip on it was silently rejected. Silently in the strongest
        // sense: that rejection path logs nothing and, before the companion
        // fix in EquipAttemptOutcome.Rejected, reported no result code either,
        // so the button did nothing and no diagnostic existed anywhere.
        //
        // Worth the emphasis because this is the account that exists
        // specifically for driving the client by hand, so the one thing it
        // must not do is behave differently from a real one.
        private static async Task EnsureCharactersAsync(FolkIdleDbContext db, long playerId)
        {
            var existing = await db.CharacterRecords
                .Where(c => c.PlayerId == playerId)
                .ToListAsync();

            Guid playerGuid = await db.PlayerRecords.AsNoTracking()
                .Where(p => p.Id == playerId)
                .Select(p => p.PlayerGuid)
                .FirstAsync();

            // THE ACCOUNT'S OWN CHARACTER COMES TO THE FRONT FIRST, before any
            // gap is filled.
            //
            // Slots move now - AssignCharacterSlot can swap a bred child into
            // slot 0 and bench the PlayerGuid character - so "slot 0 is empty"
            // is no longer the same statement as "the PlayerGuid character does
            // not exist". Filling the gap first minted a second character with
            // that id and EF refused to track two of them, which is how
            // re-seeding this fixture came to fail outright.
            var accountCharacter = existing.FirstOrDefault(c => c.Id == playerGuid);
            if (accountCharacter != null && accountCharacter.SlotIndex != 0)
            {
                var atFront = existing.FirstOrDefault(c => c.SlotIndex == 0);
                if (atFront != null) atFront.SlotIndex = accountCharacter.SlotIndex;
                accountCharacter.SlotIndex = 0;
            }

            for (int slotIndex = 0; slotIndex < CharacterSlotEngine.MaxCharacterSlots; slotIndex++)
            {
                if (existing.Any(c => c.SlotIndex == slotIndex)) continue;

                db.CharacterRecords.Add(new CharacterRecord
                {
                    Id = slotIndex == 0 ? playerGuid : Guid.NewGuid(),
                    PlayerId = playerId,
                    Level = CharacterLevel,
                    AgePhase = 1,
                    SlotIndex = slotIndex,
                    // One male and two females, so BOTH pairings can be driven
                    // by hand: hero x villager, and the roster crossing that
                    // needs one of each sex.
                    IsFemale = slotIndex != 0
                });
            }

            // The same repair for the level and the sex. A fixture seeded
            // before this existed has three level-40 men on it, so re-running
            // the seeder would leave it exactly as unable to breed as before -
            // and "re-run the seeder" is the documented fix for everything
            // about this account.
            //
            // ONLY THE FIXTURE'S OWN THREE. An earlier version bumped every
            // character on the roster, which after a session of breeding meant
            // re-seeding turned every bred INFANT into a level-50 one - a state
            // no gameplay path can produce, and one that reads as eligible on
            // every screen while BreedingEngine refuses it for AgePhase. The
            // fixture must look like a real account, so it may not manufacture
            // shapes a real account cannot reach.
            foreach (var character in existing)
            {
                if (character.SlotIndex >= CharacterSlotEngine.MaxCharacterSlots) continue;

                if (character.Level < CharacterLevel) character.Level = CharacterLevel;
                character.AgePhase = 1;
                character.IsFemale = character.SlotIndex != 0;
            }

            // Repairs a fixture seeded before this was fixed. The seeder is
            // documented as idempotent and re-runnable, so a fixture that
            // predates the fix has to be brought into line rather than left
            // permanently unable to equip anything.
            // Reached only when the PlayerGuid character exists NOWHERE - the
            // swap above has already handled the case where it exists and is
            // merely benched. That is the genuinely broken fixture this repair
            // was written for: seeded before the invariant, with three random
            // ids and no row matching the account.
            var mainSlot = existing.FirstOrDefault(c => c.SlotIndex == 0);
            if (mainSlot != null && mainSlot.Id != playerGuid && accountCharacter == null)
            {
                // The Id is the primary key, so this is a delete-and-reinsert
                // rather than an update. Safe here because the fixture's
                // characters carry no history worth preserving - and an
                // unequippable main character is worth less than none.
                db.CharacterRecords.Remove(mainSlot);
                await db.SaveChangesAsync();

                db.CharacterRecords.Add(new CharacterRecord
                {
                    Id = playerGuid,
                    PlayerId = playerId,
                    Level = CharacterLevel,
                    AgePhase = 1,
                    SlotIndex = 0,
                    IsFemale = false
                });
            }

            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Somebody the fixture's heroes can actually marry.
        ///
        /// The village fills on a 24-48h clock and rolls a race per arrival, so
        /// a fixture that waits for one is a fixture on which hero x villager
        /// cannot be driven by hand for a day or two - and then only if the
        /// dice agree, because breeding refuses a mixed-race pair. Two Humans,
        /// one of each sex, make the standard pair exercisable the moment the
        /// account is seeded.
        ///
        /// Aptitudes of 10 against a starter's 4 on purpose: the whole reason
        /// to marry out is a number the bloodline does not have, and a villager
        /// who matched the hero would demonstrate the mechanism while hiding
        /// the point of it.
        /// </summary>
        private static async Task EnsureVillagersAsync(FolkIdleDbContext db, long playerId)
        {
            var existing = await db.VillageNewcomers
                .Where(v => v.PlayerId == playerId && !v.IsElder && v.RaceId == RaceIds.Human)
                .ToListAsync();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (bool isFemale in new[] { false, true })
            {
                if (existing.Any(v => v.IsFemale == isFemale)) continue;

                var newcomer = new VillageNewcomer
                {
                    PlayerId = playerId,
                    RaceId = RaceIds.Human,
                    IsFemale = isFemale,
                    ArrivedAtEpoch = now,
                    IsElder = false,
                };
                newcomer.SetAptitudeVector(new[] { 10, 10, 10, 10 });
                db.VillageNewcomers.Add(newcomer);
            }

            await db.SaveChangesAsync();
        }

        /// <summary>
        /// A lineage row per character, because without one a character does
        /// not exist to breeding at all.
        ///
        /// HandleBreedingRosterSnapshot `continue`s past any character with no
        /// row in character_lineage_registry and BreedingEngine rolls back on
        /// the same absence, so the fixture's three characters were invisible
        /// to the Breeding screen and unbreedable behind it - which is the
        /// "output side was never wired" shape this project keeps shipping.
        ///
        /// Human, generation 0, starting aptitudes: exactly what
        /// CharacterGrantEngine gives a real account, so the fixture cannot
        /// behave differently from one.
        /// </summary>
        private static async Task EnsureLineagesAsync(FolkIdleDbContext db, long playerId)
        {
            var characterIds = await db.CharacterRecords
                .Where(c => c.PlayerId == playerId)
                .Select(c => c.Id)
                .ToListAsync();

            var existing = await db.CharacterLineages
                .Where(l => characterIds.Contains(l.CharacterId))
                .Select(l => l.CharacterId)
                .ToListAsync();

            var genome = new GeneticVector(0L);
            genome.LocusRace = new Locus { Dominant = RaceIds.Human, Recessive = RaceIds.Human };

            foreach (var characterId in characterIds)
            {
                if (existing.Contains(characterId)) continue;

                var lineage = new CharacterLineageRegistry
                {
                    CharacterId = characterId,
                    GenerationIndex = 0,
                    GeneticVector = genome.RawValue
                };
                lineage.SetAptitudeVector(BreedingAptitudes.Starting());
                db.CharacterLineages.Add(lineage);
            }

            await db.SaveChangesAsync();
        }

        // One item per equip slot, worn by the main character. Tier 2 so the
        // base-power contribution is visibly non-zero without being endgame -
        // a tier-1 set would make it hard to tell "base power reached stats"
        // apart from "base power is still zero".
        private static readonly string[] FixtureLoadout =
        {
            "eq_hunter_dagger_melee_weapon_slot_base",
            "eq_sentry_helm_helmet_armor_slot_base",
            "eq_sentry_cuirass_chest_armor_slot_base",
            "eq_sentry_gauntlets_gloves_armor_slot_base",
            "eq_sentry_leggings_leggings_armor_slot_base",
            "eq_sentry_sabatons_boots_armor_slot_base",
            // Modul: the offhand is gone (see EquipmentSlotEngine); the
            // fixture dresses the jewellery slots instead, which is what the
            // catalogue has actually authored all along.
            "eq_hunter_amulet_amulet_slot_base",
            "eq_iron_signet_ring_1/2_slot_base"
        };

        /// <summary>
        /// Owned but NOT worn: something to swap to.
        ///
        /// Modul: the fixture wore one of everything and owned nothing spare,
        /// so the paper doll's item picker could only ever offer the piece
        /// already in that slot - and "wear the thing you are wearing" is a
        /// no-op with no outcome to observe. exercise.mjs read that as the
        /// equip path being broken. A fixture that cannot exercise a swap
        /// cannot test one.
        /// </summary>
        private static readonly string[] FixtureSpares =
        {
            "eq_moss_staff_magic_weapon_slot_base",
        };

        private static async Task EnsureEquipmentAsync(FolkIdleDbContext db, long playerId)
        {
            var mainCharacter = await db.CharacterRecords
                .Where(c => c.PlayerId == playerId)
                .OrderBy(c => c.SlotIndex)
                .FirstOrDefaultAsync();

            if (mainCharacter == null) return;

            // Already kitted out - leave it alone so a session's own equip
            // changes survive a re-seed.
            bool alreadyEquipped = mainCharacter.EquippedWeaponId.HasValue
                || mainCharacter.EquippedChestId.HasValue
                || mainCharacter.EquippedAmuletId.HasValue;
            if (alreadyEquipped) return;

            foreach (string baseItemId in FixtureLoadout.Concat(FixtureSpares))
            {
                // Skip anything not in items.json rather than creating an
                // instance the registry cannot resolve - that would be an item
                // with no stats, which is exactly the failure this fixture
                // exists to make visible.
                if (!ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out _))
                {
                    Console.WriteLine($"Dev seed: skipping unknown BaseItemId '{baseItemId}'.");
                    continue;
                }

                // Modul: fixture affixes, 2026-08-01. Was a literal "{}", so
                // every seeded item had NO affixes and the reroll, affix and
                // rarity UI could not be exercised from the fixture at all - a
                // live test had to hand-write a payload into the database first.
                //
                // Rolled through AffixRegistry exactly as a drop would, so the
                // fixture exercises the same code path players do, including
                // slot legality and the per-affix rarity roll.
                // Modul: rarity 9, up from 3, AND a guaranteed health roll
                // below.
                //
                // The fixture is a level-41 character and rarity 3 left it with
                // 156 max HP, because a Warrior gains NO health from levels -
                // the entire pool is affixes. Region 2's first monster hits for
                // 230, so the fixture was two-shot by the weakest thing it is
                // allowed to fight, and exercise.mjs's combat check became a
                // coin toss that failed as "combat is broken" whenever the
                // larder happened to be empty.
                //
                // A fixture is supposed to stand in for a player who has been
                // playing. This one stood in for a level-41 character wearing
                // starter drops, which is not a player, it is a bug report
                // waiting to be misread - and it was misread, twice.
                const int fixtureRarityTier = 9;
                var seededAffixes = new Dictionary<string, int>();
                AffixRegistry.RollAffixes(
                    baseItemId,
                    regionTier: 3,
                    itemRarityTier: fixtureRarityTier,
                    affixCount: RarityTier.GetAffixCount(fixtureRarityTier),
                    destination: seededAffixes);

                // Modul: and one health roll per armour piece, guaranteed.
                //
                // Rolling affixes the way a drop does is right - it exercises
                // the real path - but it means the fixture's HEALTH is left to
                // chance, and health is the one stat with no other source. A
                // run where flat_hp happened not to roll produced a character
                // that dies to everything, which reads as a combat defect.
                if (AffixRegistry.TryGetDefinition("flat_hp", out var fixtureHealth)
                    && EquipmentSlotEngine.ResolveSlotIndex(baseItemId) > 0)
                {
                    string healthKey = AffixRegistry.BuildPayloadKey("flat_hp", 1, AffixRarity.Epic);
                    if (!seededAffixes.ContainsKey(healthKey))
                    {
                        seededAffixes[healthKey] =
                            AffixRegistry.CalculateMagnitude(fixtureHealth, 3, AffixRarity.Epic);
                    }
                }

                var instance = new EquipmentInstance
                {
                    PlayerId = playerId,
                    BaseItemId = baseItemId,
                    QualityTier = fixtureRarityTier,
                    AffixPayload = System.Text.Json.JsonSerializer.Serialize(seededAffixes)
                };
                db.EquipmentInstances.Add(instance);
                await db.SaveChangesAsync();

                // A spare is granted and left in the chest - equipping it would
                // put it in the very slot it exists to be swapped INTO.
                if (FixtureSpares.Contains(baseItemId)) continue;

                int slotIndex = EquipmentSlotEngine.ResolveSlotIndex(baseItemId);
                switch (slotIndex)
                {
                    case EquipmentSlotEngine.SlotWeapon: mainCharacter.EquippedWeaponId = instance.Id; break;
                    case EquipmentSlotEngine.SlotHelmet: mainCharacter.EquippedHelmetId = instance.Id; break;
                    case EquipmentSlotEngine.SlotChest: mainCharacter.EquippedChestId = instance.Id; break;
                    case EquipmentSlotEngine.SlotGloves: mainCharacter.EquippedGlovesId = instance.Id; break;
                    case EquipmentSlotEngine.SlotLeggings: mainCharacter.EquippedLeggingsId = instance.Id; break;
                    case EquipmentSlotEngine.SlotBoots: mainCharacter.EquippedBootsId = instance.Id; break;
                    case EquipmentSlotEngine.SlotAmulet: mainCharacter.EquippedAmuletId = instance.Id; break;
                    case EquipmentSlotEngine.SlotRing: mainCharacter.EquippedRingId = instance.Id; break;
                    default:
                        Console.WriteLine($"Dev seed: '{baseItemId}' resolved to no equip slot.");
                        break;
                }
            }

            await db.SaveChangesAsync();
        }
    }
}
