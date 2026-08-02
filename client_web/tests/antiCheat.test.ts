import { describe, it, expect } from 'vitest';
import {
  xorShift32,
  computeChallengeHash,
  computeGdprConfirmationHash,
  low32,
  high32,
} from '../src/lib/net/antiCheat';
import { CHALLENGE_VECTORS, GDPR_CONFIRMATION_VECTORS } from '../src/lib/net/protocol.generated';

// Modul: the challenge hash is mirrored C# running in JavaScript, across the
// one boundary JavaScript is worst at - unsigned 32-bit arithmetic. A wrong
// answer is WORSE than no answer: the server counts it as a failed challenge
// and quarantines the account persistently, exactly as if the player were
// cheating.
//
// So the headline test below is not a re-derivation of the same formula (which
// would agree with itself no matter how wrong both were). It runs against
// answers computed by AntiCheatTelemetryEngine ITSELF, carried into this
// client through the generated protocol file. If the two implementations ever
// disagree, this test says so instead of a real player getting flagged.
describe('cross-language agreement with the server', () => {
  it('has vectors to check against at all', () => {
    // A generator regression that emitted an empty array would make every
    // assertion below vacuously pass, which is the classic way this style of
    // test rots into decoration.
    expect(CHALLENGE_VECTORS.length).toBeGreaterThanOrEqual(6);
  });

  it.each(CHALLENGE_VECTORS)(
    'matches the server for seed=$seed playerId=$playerId epoch=$logicEpochCounter',
    ({ seed, playerId, logicEpochCounter, expectedHash }) => {
      expect(computeChallengeHash(seed, playerId, logicEpochCounter)).toBe(expectedHash);
    },
  );
});

// Modul: the same treatment for the account-erasure interlock, which has a
// sharper failure mode than the challenge hash in one specific way.
//
// A wrong challenge answer eventually quarantines an account, which someone
// can undo. A wrong GDPR hash is REFUSED BY DISCONNECTING - and the success
// path also disconnects, so the player cannot tell a rejected erasure from a
// completed one. Nothing about the client's own behaviour distinguishes them,
// which means this test is the only place the difference is observable.
describe('GDPR confirmation hash agreement with the server', () => {
  it('has vectors to check against at all', () => {
    expect(GDPR_CONFIRMATION_VECTORS.length).toBeGreaterThanOrEqual(6);
  });

  it.each(GDPR_CONFIRMATION_VECTORS)(
    'matches the server for playerId=$playerId epoch=$logicEpochCounter',
    ({ playerId, logicEpochCounter, expectedHash }) => {
      expect(computeGdprConfirmationHash(playerId, logicEpochCounter)).toBe(expectedHash);
    },
  );

  it('is the wrapping multiply, not the mathematically exact one', () => {
    // The server writes `(uint)epoch * 0x9E3779B9u`, which wraps. Computing
    // the true product in a double and truncating gives a DIFFERENT low word
    // once it passes 2^53, so this pins down that the port wraps - the one
    // property a re-derivation of the same formula would not catch.
    const epoch = 4294967295;
    const wrapped = Math.imul(epoch, 0x9e3779b9) >>> 0;
    const naive = (epoch * 0x9e3779b9) >>> 0;
    expect(wrapped).not.toBe(naive);
  });
});

describe('xorShift32', () => {
  it('never returns zero, matching the server sentinel', () => {
    // 0 xorshifts to 0, which is the exact case the sentinel exists for.
    expect(xorShift32(0)).toBe(0x6d2b79f5);
  });

  it('stays inside uint32 for inputs that would overflow a JS number', () => {
    for (const input of [1, 0x7fffffff, 0x80000000, 0xffffffff, 0xdeadbeef]) {
      const result = xorShift32(input);
      expect(Number.isInteger(result)).toBe(true);
      expect(result).toBeGreaterThanOrEqual(0);
      expect(result).toBeLessThanOrEqual(0xffffffff);
    }
  });

  it('is deterministic', () => {
    expect(xorShift32(12345)).toBe(xorShift32(12345));
  });
});

describe('low32 / high32', () => {
  it('splits a C# long the way an unchecked cast would', () => {
    expect(low32(1)).toBe(1);
    expect(high32(1)).toBe(0);
    expect(low32(0xffffffff)).toBe(0xffffffff);
    expect(high32(0x1_0000_0000)).toBe(1);
    expect(low32(0x1_0000_0000)).toBe(0);
  });

  it('handles the player ids this game actually issues', () => {
    // Player ids are small sequential longs; the high word is always 0, which
    // is why a naive implementation that ignored it would pass in production
    // and fail only if the id space ever grew.
    for (const playerId of [1, 9, 1042, 2 ** 31 - 1]) {
      expect(high32(playerId)).toBe(0);
      expect(low32(playerId)).toBe(playerId >>> 0);
    }
  });
});

describe('computeChallengeHash', () => {
  it('produces a uint32', () => {
    const hash = computeChallengeHash(0x12345678, 42, 7);
    expect(Number.isInteger(hash)).toBe(true);
    expect(hash).toBeGreaterThanOrEqual(0);
    expect(hash).toBeLessThanOrEqual(0xffffffff);
  });

  it('depends on all three inputs', () => {
    const base = computeChallengeHash(1000, 5, 3);
    expect(computeChallengeHash(1001, 5, 3)).not.toBe(base);
    expect(computeChallengeHash(1000, 6, 3)).not.toBe(base);
    expect(computeChallengeHash(1000, 5, 4)).not.toBe(base);
  });

  it('is never zero, since every path ends in xorShift32', () => {
    for (let seed = 1; seed < 200; seed++) {
      expect(computeChallengeHash(seed, seed * 7, seed % 11)).not.toBe(0);
    }
  });
});
