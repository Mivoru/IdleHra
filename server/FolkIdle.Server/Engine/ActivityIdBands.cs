namespace FolkIdle.Server.Engine
{
    // Modul: activity id bands. One numeric space is shared by everything a
    // character can be assigned to - combat targets and gathering nodes alike -
    // and until now the two overlapped.
    //
    // The collision was total for one whole region: Region 3's five monsters
    // (Desert Crab, Ashen Basilisk, Ember Elemental, Sandstone Golem and the
    // Magma Wyrm boss) carried ids 101-105, which were also Woodcutting nodes
    // 101-105. ProcessSubTick resolves an activity by asking
    // ContentRegistry.TryGetGatheringNode FIRST, so sending 101 always started
    // chopping wood and Region 3 could not be fought at all. Since the Magma
    // Wyrm was unkillable, the Kobold race unlock that hangs off its first kill
    // was unreachable too.
    //
    // Gathering moved rather than combat. Combat ids are the identity space of
    // MonsterCodexEntries, which stores every player's per-monster kill history
    // and drives both region completion and the race-unlock first-kill probe -
    // renumbering monsters would have silently reassigned that history to
    // different creatures. The 90 legacy monsters also already occupy ids 1-90,
    // so there is no low band free for combat to move into. Gathering nodes,
    // by contrast, are referenced only by their own content file, their loot
    // table keys, and whatever a character is currently assigned to.
    //
    // Bands are far enough apart that a future content pass can add monsters or
    // nodes without either ever reaching the other.
    public static class ActivityIdBands
    {
        // Combat targets. Unchanged: 1-90 legacy, 91-115 the canonical five
        // regions of four monsters plus a boss.
        public const long CombatFirst = 1L;
        public const long CombatLast = 999L;

        public const long WoodcuttingBand = 1000L;
        public const long MiningBand = 2000L;
        public const long FishingBand = 3000L;
        public const long HerbalismBand = 4000L;

        // The spec for this pass named Woodcutting, Mining and Fishing only.
        // Herbalism exists as ProfessionType 3 with twelve authored nodes, so it
        // gets the next band up rather than being left in the colliding space.
        public const long BandSize = 1000L;

        public const long GatheringFirst = WoodcuttingBand;
        public const long GatheringLast = HerbalismBand + BandSize - 1L;

        // Modul: crafting is a JOB, not a button.
        //
        // Every recipe already carried a CraftingTimeMs and nothing used it as
        // a duration - CraftItem consumed the materials and produced the
        // result in the same instant, from any screen, with no character
        // involved. So cooking a hundred meals was a hundred clicks, and a
        // character could gather or fight but never cook.
        //
        // A crafting activity is CraftingBand + the recipe's INDEX in
        // ContentRegistry.Recipes. Index rather than ResultItemId because
        // result ids run into the hundreds and would collide with the
        // gathering bands; the index is dense and bounded by the recipe table.
        public const long CraftingBand = 5000L;

        // Reserved well above every band - see ClientCommandValidator.
        public const long WorldBossActivityId = 9999L;

        // Pure integer comparison, no lookup and no allocation, so the 10Hz
        // tick can use it to classify an activity without touching a dictionary.
        public static bool IsGatheringActivity(long activityId)
        {
            return activityId >= GatheringFirst && activityId <= GatheringLast;
        }

        public static bool IsCraftingActivity(long activityId)
        {
            return activityId >= CraftingBand && activityId < CraftingBand + BandSize;
        }

        public static bool IsCombatActivity(long activityId)
        {
            return activityId >= CombatFirst && activityId <= CombatLast;
        }

        // Band for a profession id, matching GatheringNodeDefinition's
        // ProfessionType: 0 Woodcutting, 1 Mining, 2 Fishing, 3 Herbalism.
        public static long GetBandForProfession(int professionType)
        {
            return professionType switch
            {
                0 => WoodcuttingBand,
                1 => MiningBand,
                2 => FishingBand,
                _ => HerbalismBand
            };
        }

        // The one-time re-key applied to gathering_nodes.json and to every
        // persisted CharacterRecords."ActiveActivityId". Kept in code as well as
        // in the migration so the mapping is documented in one readable place:
        // the old ids were profession*100 + index, so the index survives and
        // node 101 becomes 1001, 205 becomes 2005, 412 becomes 4012.
        public static long MapLegacyGatheringId(long legacyActivityId)
        {
            if (legacyActivityId < 101L || legacyActivityId > 499L)
            {
                return legacyActivityId;
            }

            long professionType = legacyActivityId / 100L - 1L;
            long index = legacyActivityId % 100L;
            return GetBandForProfession((int)professionType) + index;
        }
    }
}
