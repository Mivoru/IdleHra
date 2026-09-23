namespace FolkIdle.Server.Engine
{
    // Modul: what a mid-session reload must NOT throw away, 2026-09-12.
    //
    // Every engine that writes the database out-of-band - AffixRerollEngine,
    // ForgeSplicingEngine, the market, the village, crafting - suspends the
    // player, does its work, and finishes by enqueuing ReloadState so the live
    // payload picks the change up. The drain then replaces the live payload with
    // one StateCheckpointManager.LoadPlayerState built from the database.
    //
    // That is right for everything the database holds and wrong for everything
    // it does not. The command result ring is the case that was reported: the
    // engine's answer to the command is written into the live payload's ring,
    // the player is suspended so no broadcast carries it, and then the reload
    // replaces the payload and the answer is gone. A reroll changed the affix,
    // took the gold, and said nothing - in a browser, for a single reroll, a
    // fifty-attempt run and an impossible stop condition alike.
    //
    // Deliberately narrow. The payload has a whole family of runtime-only
    // fields that this same reload also zeroes - the victory and death cards,
    // the offline summary, the live monster and its health, gathering progress -
    // and every one of them is a separate judgement about what a mid-command
    // reload should mean. They are recorded in
    // StateUpdatePacketFieldCoverageTests.RuntimeOnlyByDesign and in the
    // backlog; carrying them all over blind would be trading a known defect for
    // an unknown one. What is carried here is only what the command being
    // answered needs in order to be answerable - plus, since task 24
    // (2026-09-23), the in-flight activity of each slot whose character is
    // unchanged; see CarryLiveActivity for why that judgement came out "carry".
    public static class StateReloadMerge
    {
        /// <summary>
        /// Copies the live payload's runtime-only command feedback onto a
        /// freshly loaded payload, so the reload that follows a command does not
        /// erase the server's reply to it.
        ///
        /// The COUNTER matters as much as the slots. The client's
        /// CommandResultFeed holds a high-water mark of result ticks it has
        /// already shown and discards anything at or below it, so a counter that
        /// restarts at zero leaves every later result in the session below the
        /// mark - silence that looks exactly like a server with nothing to say.
        /// </summary>
        public static void CarryLiveOnlyFields(in TickStatePayload live, ref TickStatePayload reloaded)
        {
            reloaded.CommandResultSlot0 = live.CommandResultSlot0;
            reloaded.CommandResultSlot1 = live.CommandResultSlot1;
            reloaded.CommandResultSlot2 = live.CommandResultSlot2;
            reloaded.CommandResultSlot3 = live.CommandResultSlot3;
            reloaded.CommandResultRingWriteIndex = live.CommandResultRingWriteIndex;
            reloaded.CommandResultTickCounter = live.CommandResultTickCounter;

            CarryLiveActivity(in live, ref reloaded);
        }

        /// <summary>
        /// Modul: A MARKET LISTING ENDED THE FIGHT (task 24, 2026-09-23).
        ///
        /// The single-character ChangeActivity path (TargetGuid empty) and the
        /// death reset change ActiveActivityId in memory only, and no flush
        /// writes it to the characters row LoadPlayerState reads it from. So
        /// every ReloadState put a fighting or gathering player back on whatever
        /// the row last said - usually idle - with a fresh monster and a full
        /// health bar. SustainedLoadTests measured 40 of 40 sessions landing
        /// nothing when each touched the market every few seconds; it needs no
        /// load, one player and one listing will do it.
        ///
        /// Carried per slot, and ONLY while the same character still holds that
        /// slot. The Hall of Ancestors rewrites slot membership out-of-band and
        /// benches the displaced character idle before its reload; carrying by
        /// slot position alone would put the benched character's fight onto
        /// whoever replaced it. It is also why this is not a write-back in the
        /// flush: the flush runs before that reload, and would write the stale
        /// activity over the bench reset the Hall had just committed.
        ///
        /// Equipment is deliberately NOT carried - a reload is how a fresh
        /// equip reaches the live session. Only what exists in memory and in no
        /// table: the activity, its progress, the monster and the health bar,
        /// which also stops a market listing from being a free full heal.
        /// </summary>
        private static void CarryLiveActivity(in TickStatePayload live, ref TickStatePayload reloaded)
        {
            if (live.Slot1_CharacterId != System.Guid.Empty && live.Slot1_CharacterId == reloaded.Slot1_CharacterId)
            {
                reloaded.ActiveActivityId = live.ActiveActivityId;
                reloaded.CurrentProgressTicks = live.CurrentProgressTicks;
                reloaded.RequiredProgressTicks = live.RequiredProgressTicks;
                reloaded.CurrentMonsterId = live.CurrentMonsterId;
                reloaded.CurrentMonsterHp = live.CurrentMonsterHp;
                reloaded.PlayerHp = live.PlayerHp;
                reloaded.CombatTargetTickAccumulator = live.CombatTargetTickAccumulator;
                reloaded.TargetStatusEffectBitmask = live.TargetStatusEffectBitmask;
                reloaded.GatheringProgressTicks = live.GatheringProgressTicks;
                reloaded.HarvestLoopCount = live.HarvestLoopCount;
                reloaded.ActivityHaltReason = live.ActivityHaltReason;
            }

            if (live.Slot2_CharacterId != System.Guid.Empty && live.Slot2_CharacterId == reloaded.Slot2_CharacterId)
            {
                CarrySlot(in live.Slot2Activity, ref reloaded.Slot2Activity);
            }

            if (live.Slot3_CharacterId != System.Guid.Empty && live.Slot3_CharacterId == reloaded.Slot3_CharacterId)
            {
                CarrySlot(in live.Slot3Activity, ref reloaded.Slot3Activity);
            }
        }

        private static void CarrySlot(in CharacterActivityState live, ref CharacterActivityState reloaded)
        {
            reloaded.ActiveActivityId = live.ActiveActivityId;
            reloaded.CurrentProgressTicks = live.CurrentProgressTicks;
            reloaded.RequiredProgressTicks = live.RequiredProgressTicks;
            reloaded.CurrentMonsterId = live.CurrentMonsterId;
            reloaded.CurrentMonsterHp = live.CurrentMonsterHp;
            reloaded.PlayerHp = live.PlayerHp;
            reloaded.CombatTargetTickAccumulator = live.CombatTargetTickAccumulator;
            reloaded.TargetStatusEffectBitmask = live.TargetStatusEffectBitmask;
            reloaded.GatheringProgressTicks = live.GatheringProgressTicks;
            reloaded.HarvestLoopCount = live.HarvestLoopCount;
            reloaded.ActivityHaltReason = live.ActivityHaltReason;
        }
    }
}
