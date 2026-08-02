// Modul: the anti-cheat challenge response.
//
// The server puts a challenge seed on the broadcast path (StateUpdate's
// ActiveChallengeSeed) and expects an answer within 15 seconds. Four
// consecutive misses quarantines the account - measured at about 60 seconds
// for a client that never answers, and the quarantine PERSISTS as
// PlayerRecords.IsQuarantined, so the account stays broken across reconnects
// until someone clears the flag in the database.
//
// This obligation is written down nowhere on the server side; it exists only
// inside the Unity WebSocketClient's 1398 lines. It is easy to miss because a
// short test session survives and looks completely healthy.
//
// Mirrors AntiCheatTelemetryEngine.ComputeChallengeHash exactly. JavaScript
// has no uint32, so every step forces back through `>>> 0` - and the two
// multiplication-free xorshift steps are why this can be mirrored at all
// without BigInt.

/** AntiCheatTelemetryEngine.XorShift32. Never returns 0, matching the server. */
export function xorShift32(value: number): number {
  let v = value >>> 0;
  v = (v ^ (v << 13)) >>> 0;
  v = (v ^ (v >>> 17)) >>> 0;
  v = (v ^ (v << 5)) >>> 0;
  return v === 0 ? 0x6d2b79f5 : v >>> 0;
}

/**
 * AntiCheatTelemetryEngine.ComputeChallengeHash.
 *
 * `logicEpochCounter` must be the epoch from the SAME StateUpdate that carried
 * the seed. The server judges the answer against the epoch the challenge was
 * ISSUED under (ActiveChallengeIssuedEpoch), not the live one - using the
 * current counter instead turns a correct answer into a recorded miss every
 * time a checkpoint flush lands between broadcast and reply.
 */
export function computeChallengeHash(
  challengeSeed: number,
  playerId: number,
  logicEpochCounter: number,
): number {
  let value = challengeSeed >>> 0;
  value = (value ^ low32(playerId)) >>> 0;
  value = xorShift32(value);
  value = (value ^ high32(playerId)) >>> 0;
  // Server-side this is `value + (uint)logicEpochCounter` in unchecked uint32
  // arithmetic, so the add wraps rather than growing into a double.
  value = xorShift32((value + low32(logicEpochCounter)) >>> 0);
  value = (value ^ 0xc2b2ae35) >>> 0;
  return xorShift32(value);
}

/** The low 32 bits of a C# long, as C#'s `(uint)value` would produce. */
export function low32(value: number): number {
  return value >>> 0 === value ? value >>> 0 : Number(BigInt.asUintN(32, BigInt(Math.trunc(value))));
}

/** The high 32 bits, as C#'s `(uint)(value >> 32)` would produce. */
export function high32(value: number): number {
  return Number(BigInt.asUintN(32, BigInt(Math.trunc(value)) >> 32n));
}
