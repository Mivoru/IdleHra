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
    // answered needs in order to be answerable.
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
        }
    }
}
