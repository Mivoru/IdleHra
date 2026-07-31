namespace FolkIdle.Server.Engine
{
    // Modul: multi-slot simulation. The complete per-character slice of a
    // player's tick state - everything that belongs to ONE character running
    // ONE activity, and nothing that belongs to the account.
    //
    // Why this exists: TickStatePayload modelled exactly one running activity.
    // CharacterRecord.ActiveActivityId has always been per-character and
    // ChangeCharacterActivityAsync has always written it per-character, but the
    // tick loop only ever simulated Slot1 - so a character assigned to slot 2
    // or 3 sat there doing nothing, forever. Same defect shape as the crafting
    // output and the empty larder: the assignment was recorded and never acted
    // on.
    //
    // The split is deliberately narrow. These eleven fields plus the four
    // identity fields are the only genuinely per-character state; the other
    // ~50 things ProcessSubTick touches (gold, XP, masteries, monoliths,
    // potions, the larder, backpack space, village levels, guild) are
    // account-scoped and are correctly shared between all three characters.
    // That means three characters share one backpack, one pantry and one gear
    // set, and each contributes to the same gold and mastery totals - which is
    // the intent: extra characters multiply how much of the world you can work
    // at once, they are not three separate accounts to gear up.
    //
    // Plain struct of unmanaged fields, so swapping one in and out of the
    // payload's active register is a handful of field copies with zero
    // allocation on the 10Hz path.
    public struct CharacterActivityState
    {
        public long ActiveActivityId;
        public int CurrentProgressTicks;
        public int RequiredProgressTicks;

        // Combat
        public int CurrentMonsterId;
        public int CurrentMonsterHp;
        public int PlayerHp;
        public int CombatTargetTickAccumulator;
        public byte TargetStatusEffectBitmask;

        // Gathering
        public int GatheringProgressTicks;
        public long HarvestLoopCount;

        // Why this character is not currently earning - see
        // Network.ActivityHaltReason. Per-slot, because one character running
        // out of food says nothing about the other two.
        public byte ActivityHaltReason;

        // Modul: per-character equipment. Each character's own gear, and the
        // stat totals derived from it.
        //
        // Equipment used to be account-wide, which could not survive more than
        // one character working at once: a miner needs a pickaxe while a fighter
        // holds a sword, and one shared weapon slot cannot be both. The derived
        // totals ride along because recomputing them would mean a database read
        // per character per swap, on a 10Hz path - they are recalculated only
        // when that character's gear actually changes (see
        // EquipmentSlotUpdateQueue) exactly as the single-character version did.
        public long EquippedWeaponId;
        public long EquippedHelmetId;
        public long EquippedChestId;
        public long EquippedGlovesId;
        public long EquippedLeggingsId;
        public long EquippedBootsId;
        public long EquippedOffhandId;
        public bool EquippedWeaponAffixLocked;
        public bool EquippedArmorAffixLocked;
        public bool EquippedLeggingsAffixLocked;
        public EquippedAffixTotals CachedAffixTotals;
        public int CachedWeaponSetId;
        public int CachedArmorSetId;
        public int CachedLeggingsSetId;

        // Identity. Kept alongside the activity state rather than left behind
        // in the payload's Slot1_*/Slot2_*/Slot3_* fields, because combat stats
        // are derived from the ACTIVE character's race, age phase and genetic
        // loci - a slot has to fight as itself, not as slot 1.
        public System.Guid CharacterId;
        public long AgeTicks;
        public int AgePhase;
        public long GeneticVector;
    }
}
