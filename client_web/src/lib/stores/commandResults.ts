// Modul: command result feedback. The descendant of UiCommandResultToast.
//
// Every rejectable command - equip, forge fusion, market listing, guild
// contribution, reroll - used to be a silent no-op from the player's side: the
// reason existed only as a server-side Console.WriteLine. StateUpdatePacket
// carries a four-slot ring buffer specifically to fix that, and a client that
// ignores it puts the player right back where they started, pressing a button
// that does nothing and says nothing.
//
// A RING BUFFER, NOT A SCALAR, and the reason matters. A single slot could
// only ever carry the most recent rejection, so a client that missed exactly
// one broadcast - across a reconnect, say - while two commands were rejected
// back to back would see only the last one and lose the earlier one silently
// and permanently.
//
// ResultTick is a per-player monotonically increasing counter that is never
// reset, so the client can tell which slots are newer than what it has already
// shown, and in what order. A tick of 0 means the slot has never been
// populated this session.

/** Mirrors CommandResultCode. */
export const COMMAND_RESULT_MESSAGES: Record<number, string> = {
  0: 'Done.',
  1: 'Invalid price.',
  2: 'That item is equipped.',
  3: 'Not enough materials.',
  4: 'That activity is not available.',
  5: 'Not enough gold.',
  6: 'Target not found.',
  7: 'Guild not found.',
  8: 'The server rejected that.',
  9: 'A bank transaction is already in flight - try again in a moment.',
  10: 'Already at maximum tier.',
  // The server no longer returns this - storage is unlimited. Mapped so a
  // stale code cannot render as a bare number.
  11: 'That could not be stored.',
  12: 'Trading requires an active guild membership.',
  // Equipping and buying stopped asking about levels when the region gate
  // replaced the level gate (18 below). Kept mapped so an in-flight or
  // replayed result cannot render as a bare number, the same reason 11 is.
  13: 'Your level is too low for that item.',
  14: 'Another character is already on that node.',
  15: 'That relationship already exists.',
  // 16 and 17 existed server-side with nothing here to render them, so the
  // player saw a number. Added with 18 rather than left for later: an
  // unexplained refusal is the failure this whole gate was meant to stop.
  16: 'Fusion needs three items of the same rarity.',
  17: 'You have not reached that location yet.',
  18: 'That region is still locked - defeat the previous region’s boss first.',
};

export const COMMAND_RESULT_SUCCESS = 0;

export interface CommandResultEntry {
  id: number;
  code: number;
  tick: number;
  message: string;
  atMs: number;
}

export interface CommandResultSlot {
  code: number;
  tick: number;
}

/** Reads the four flattened slot pairs off a StateUpdate. */
export function readResultSlots(packet: Record<string, unknown>): CommandResultSlot[] {
  const slots: CommandResultSlot[] = [];
  for (let i = 0; i < 4; i++) {
    const code = packet[`CommandResult${i}_Code`];
    const tick = packet[`CommandResult${i}_Tick`];
    if (typeof code === 'number' && typeof tick === 'number') slots.push({ code, tick });
  }
  return slots;
}

let sequence = 0;

export class CommandResultFeed {
  /**
   * The highest tick already shown. Everything at or below it has been seen;
   * everything above is new, whichever slot it happens to occupy.
   */
  private highestSeenTick = 0;
  private primed = false;

  /**
   * Returns entries not yet shown, oldest first.
   *
   * The FIRST packet of a session only primes the watermark and emits nothing.
   * The ring buffer persists across a reconnect, so replaying it on connect
   * would pop toasts for commands the player issued minutes ago - and would do
   * it again on every subsequent reconnect.
   */
  accept(packet: Record<string, unknown>, nowMs: number): CommandResultEntry[] {
    const slots = readResultSlots(packet);
    const highest = slots.reduce((max, slot) => Math.max(max, slot.tick), 0);

    if (!this.primed) {
      this.primed = true;
      this.highestSeenTick = highest;
      return [];
    }

    const fresh = slots
      .filter((slot) => slot.tick > this.highestSeenTick)
      .sort((a, b) => a.tick - b.tick)
      .map((slot) => ({
        id: ++sequence,
        code: slot.code,
        tick: slot.tick,
        message: COMMAND_RESULT_MESSAGES[slot.code] ?? `Rejected (code ${slot.code}).`,
        atMs: nowMs,
      }));

    if (highest > this.highestSeenTick) this.highestSeenTick = highest;
    return fresh;
  }

  reset(): void {
    this.highestSeenTick = 0;
    this.primed = false;
  }
}
