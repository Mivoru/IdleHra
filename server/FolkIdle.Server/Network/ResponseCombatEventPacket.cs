using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    // Modul: Combat Event Feed. Server to client, one message per resolved
    // blow - a swing that landed, a swing that missed, a monster's hit back, a
    // kill.
    //
    // WHY THIS EXISTS AT ALL, measured 2026-09-04:
    //
    // A player reported that they could not see the fight - the monster's
    // health bar never moved, only their own. It is not a rendering fault. The
    // wire carried no combat event of any kind, only CurrentMonsterHp on a
    // snapshot, so everything on screen was INFERRED from the difference
    // between two snapshots (see the client's stores/damage.ts). Snapshots
    // arrive about every 1090 ms, and a geared character kills an early monster
    // every ~1400 ms: across 27 consecutive snapshots CurrentMonsterHp took
    // exactly ONE value, its full health, because spawn and death both happened
    // between two samples. There was nothing to animate and nothing to infer.
    //
    // No amount of work on the health bar could fix that, because the data was
    // constant. This packet is the fix: the tick already resolves every one of
    // these events and then threw the detail away, so a fight that resolves
    // inside a single snapshot can still be READ afterwards.
    //
    // Why a dedicated packet rather than fields on StateUpdatePacket: the same
    // reasons as ResponseLootDropPacket, whose shape this copies. One tick can
    // resolve several events (a swing, a burn, a monster's reply), that struct
    // is a fixed-size snapshot with no room for a variable-length list, and
    // these are bursty rather than per-tick.
    //
    // Fixed size and unmanaged by construction, so both the send and receive
    // paths are a single blittable write/read with no allocation - and see
    // NetworkPacketLayoutGuard for why the exact byte count matters (the binary
    // receive loops distinguish message types by size alone).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ResponseCombatEventPacket
    {
        // What happened. These are the events the simulation ACTUALLY
        // resolves - deliberately not a wish list.
        //
        // There is no "blocked" on a player's swing, because monsters carry no
        // block stat: armour is the whole of their mitigation, and armour
        // REDUCES rather than stops (CombatDamageModel.Mitigate), so it is a
        // smaller number on a hit line and never an event of its own. The
        // player DOES block - BlockStrengthPct, derived from CON - so
        // MonsterHit carries a flag for it.
        public const byte KindPlayerHit = 0;
        public const byte KindPlayerMiss = 1;
        public const byte KindMonsterHit = 2;
        public const byte KindMonsterMiss = 3;
        public const byte KindLifesteal = 4;
        public const byte KindKill = 5;

        public const byte FlagCrit = 1 << 0;
        /// <summary>The player's block shaved this incoming hit.</summary>
        public const byte FlagBlocked = 1 << 1;
        /// <summary>Extra damage from the Chiming Steel burn, folded into Amount.</summary>
        public const byte FlagBurn = 1 << 2;
        /// <summary>Damage reflected back by Eternal Dreadnought thorns.</summary>
        public const byte FlagThorns = 1 << 3;

        public long PlayerId;

        /// <summary>
        /// ContentRegistry monster id, so the client resolves the name and
        /// sprite through its own content mirror. No string ever goes over
        /// this wire.
        /// </summary>
        public int MonsterId;

        /// <summary>
        /// Whole hit points, not milli. The simulation works in milli
        /// throughout; a log line reading "you hit for 412000" would be
        /// technically true and useless.
        /// </summary>
        public int Amount;

        /// <summary>
        /// The monster's health AFTER this event, in whole hit points.
        ///
        /// This is the field that makes a sub-second fight legible: the
        /// snapshot stream cannot show a health value it never sampled, and
        /// this one is stated at the moment the blow resolved. Zero on a kill.
        /// </summary>
        public int MonsterHpAfter;

        /// <summary>
        /// Per-server monotonic counter.
        ///
        /// Two jobs, and the second is why it is here rather than being left
        /// out to save four bytes. It gives the client a total order for events
        /// that can be dispatched concurrently, and it lets a reconnect drop
        /// what it has already shown instead of replaying a burst of stale
        /// lines into the log.
        ///
        /// It also takes this packet to 26 bytes. That matters:
        /// ResponseLootDropPacket is 22, and without this field these two would
        /// be the same size - which the binary receive loops, demultiplexing on
        /// length alone, could not tell apart. NetworkPacketLayoutGuard fails
        /// the build on exactly that collision.
        /// </summary>
        public uint Sequence;

        public byte EventKind;
        public byte Flags;
    }
}
