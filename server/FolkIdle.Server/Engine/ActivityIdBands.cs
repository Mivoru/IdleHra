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
        // Modul: HERBALISM IS BACK, 2026-10-30 (owner decision H1). It was
        // retired once because the design list had no herb and the twelve
        // nodes behind it grew invented plants nothing used. It returns as
        // one herb pair per region (HERBS_POTIONS_ASCENSION.md 1.2), feeding
        // the potions, in the band it always had.
        //
        // Gathered BY HAND: there is no herbalism tool, by the owner's call -
        // the speed is the mastery, so the tool switches below answer 0 for
        // profession 3 rather than borrowing the rod. Measured before the
        // band came back: 0 of 239 production characters held an id in
        // 4000-4999, so no stale assignment woke up as a herb patch.
        public const long HerbalismBand = 4000L;

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
                3 => HerbalismBand,
                _ => FishingBand
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

            // Modul: the legacy re-key is HISTORY and must not follow the
            // profession table. A persisted legacy id of 412 was a HERB node and
            // has to keep mapping into the herbalism band (where 4012 is no node
            // today and resolves to nothing). Routing it to 3012 would silently
            // turn one character's abandoned herb patch into a fishing spot.
            long band = professionType switch
            {
                0 => WoodcuttingBand,
                1 => MiningBand,
                2 => FishingBand,
                _ => HerbalismBand
            };
            return band + index;
        }
    }
}
