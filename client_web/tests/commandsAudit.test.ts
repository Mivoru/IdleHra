import { describe, it, expect, beforeEach, vi } from 'vitest';

// Modul: the guards added by the 2026-08-02 protocol audit, which found
// fourteen commands the web client could not send at all.
//
// A separate file from commands.test.ts for one reason: these need
// `serverNowMs` on the mocked connection, because two of them stamp a WALL
// CLOCK timestamp rather than the epoch counter every other command carries.
// Pinning it here keeps that assertion exact instead of "some number arrived".

const sent: Record<string, unknown>[] = [];
vi.mock('../src/lib/net/connection', () => ({
  connection: {
    send: (draft: Record<string, unknown>) => {
      sent.push(draft);
    },
    currentPlayerId: 0,
    serverNowMs: () => 1_700_000_000_000,
  },
}));

const {
  placeLimitOrder,
  claimMailItem,
  attackWorldBoss,
  consumeConsumable,
  depositGuildMaterial,
  contributeToGuildStock,
  registerGuildDefense,
  submitShardAttack,
  executeCombatTurn,
  assignMentor,
  triggerGdprPurge,
  MAX_BUFF_TICKS,
  MAX_BOSS_ATTEMPTS,
  BOSS_PLATE_COUNT,
  ACTIVE_BOSS_INSTANCE_ID,
} = await import('../src/lib/net/commands');
const { CommandType } = await import('../src/lib/net/protocol.generated');

beforeEach(() => {
  sent.length = 0;
});

describe('limit orders', () => {
  it('marks the side, because the same field means different things per side', () => {
    // Selling addresses an equipment INSTANCE; buying addresses an item
    // DEFINITION, which the server resolves through ContentRegistry. Copying
    // the sell path to build a buy posts an order against whichever item
    // happens to share that number - no error, wrong order.
    placeLimitOrder({ isBuy: true, targetId: 12, price: 500, qualityTier: 3 });
    expect(sent[0]).toMatchObject({
      Command: CommandType.PlaceLimitOrder,
      TargetId: 12,
      LimitPrice: 500,
      IsBuy: 1,
      QualityTier: 3,
    });
  });

  it('refuses a sell order carrying a quality tier', () => {
    expect(placeLimitOrder({ isBuy: false, targetId: 9, price: 5, qualityTier: 2 }).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses a BUY above the definition count, which would disconnect', () => {
    expect(
      placeLimitOrder({ isBuy: true, targetId: 5000, price: 5, itemDefinitionCount: 400 }).ok,
    ).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('does not apply that bound to a SELL, whose target is an instance id', () => {
    // Instance ids are unrelated to the definition table and routinely exceed
    // its length, so applying the buy-side bound here would refuse valid sells.
    expect(
      placeLimitOrder({ isBuy: false, targetId: 5000, price: 5, itemDefinitionCount: 400 }).ok,
    ).toBe(true);
  });
});

describe('mailbox', () => {
  it('addresses the mail row id', () => {
    claimMailItem(41);
    expect(sent[0]).toMatchObject({ Command: CommandType.ClaimMailItem, TargetId: 41 });
  });

  it('refuses a non-positive id, which disconnects', () => {
    expect(claimMailItem(0).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

describe('world boss', () => {
  const healthy = {
    plateIndex: 2,
    eventState: 1,
    bossCurrentHp: 1_000_000,
    attemptCount: 0,
    larderEmpty: false,
  };

  it('sends the constant boss id, never a caller-supplied one', () => {
    // A boss id other than the active one disconnects, and no field in the
    // state snapshot carries it - it is a server constant.
    attackWorldBoss(healthy);
    expect(sent[0]).toMatchObject({
      Command: CommandType.AttackWorldBoss,
      TargetedBossId: ACTIVE_BOSS_INSTANCE_ID,
      TargetedPlateIndex: 2,
    });
  });

  it('SENDS NO DAMAGE FIGURE AT ALL, which is the point of the rework', () => {
    // Until 2026-09-05 this command posted ClientPredictedDamage - a number
    // the client computed about its own character - and the only thing between
    // it and a shared, server-authoritative health pool was a 100,000,000
    // clamp. The server reads the player's real attack power now, and
    // ValidateWorldBossAttackRequest DISCONNECTS a client that still sends a
    // figure. So the absence of that field is a security property, not a
    // tidiness one.
    attackWorldBoss(healthy);
    expect(sent[0].ClientPredictedDamage ?? 0).toBe(0);
  });

  it('refuses when the event is dormant or concluded', () => {
    expect(attackWorldBoss({ ...healthy, eventState: 0 }).ok).toBe(false);
    expect(attackWorldBoss({ ...healthy, eventState: 2 }).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses once the attempts are spent', () => {
    expect(attackWorldBoss({ ...healthy, attemptCount: MAX_BOSS_ATTEMPTS }).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses on an EMPTY LARDER, which the server discards in silence', () => {
    // ExecuteAttackAsync rolls the transaction back with no message when
    // auto-eat food is depleted. The request is accepted, nothing happens, and
    // the player has no way to find out - so it is refused with a reason.
    expect(attackWorldBoss({ ...healthy, larderEmpty: true }).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses a plate index the server would disconnect over', () => {
    // WorldBossEngine.PlateCount is 5, so 0-4 are the whole range.
    // ValidateWorldBossAttackRequest treats anything else as a client that is
    // either stale or trying, and kills the session either way - so it is
    // refused here instead of sent.
    expect(attackWorldBoss({ ...healthy, plateIndex: 5 }).ok).toBe(false);
    expect(attackWorldBoss({ ...healthy, plateIndex: -1 }).ok).toBe(false);
    expect(attackWorldBoss({ ...healthy, plateIndex: 1.5 }).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses once the BATTLE SESSION has closed, which the server does in silence', () => {
    // WorldBossEngine gives a player 300 seconds from their FIRST strike to
    // spend the other two, then rolls every later attack back with no damage,
    // no message and no telemetry they will ever see - inside an encounter that
    // runs for up to seven days.
    //
    // The deadline was not on the wire at all until 2026-09-05, so the button
    // stayed enabled and did nothing forever. An idle player who strikes once
    // and comes back later is the NORMAL case in this genre.
    const closed = attackWorldBoss({
      ...healthy,
      sessionEndsEpoch: 1_000_000,
      nowEpoch: 1_000_001,
    });
    expect(closed.ok).toBe(false);
    expect(closed.ok === false && closed.reason).toMatch(/battle session/i);
    expect(sent).toHaveLength(0);
  });

  it('allows a strike while the session is still open', () => {
    expect(
      attackWorldBoss({ ...healthy, sessionEndsEpoch: 1_000_000, nowEpoch: 999_999 }).ok,
    ).toBe(true);
    // Zero means the clock has not started - the player has not struck yet.
    expect(attackWorldBoss({ ...healthy, sessionEndsEpoch: 0, nowEpoch: 9_999_999 }).ok).toBe(true);
    expect(sent).toHaveLength(2);
  });

  it('accepts every plate the server does', () => {
    for (let plate = 0; plate < BOSS_PLATE_COUNT; plate++) {
      expect(attackWorldBoss({ ...healthy, plateIndex: plate }).ok).toBe(true);
    }
    expect(sent).toHaveLength(BOSS_PLATE_COUNT);
    expect(sent.map((packet) => packet.TargetedPlateIndex)).toEqual([0, 1, 2, 3, 4]);
  });
});

describe('consumables', () => {
  it('rides on ConsumableItemId, not TargetId', () => {
    consumeConsumable(77, 0);
    expect(sent[0]).toMatchObject({
      Command: CommandType.ConsumeConsumableAsset,
      ConsumableItemId: 77,
    });
    expect(sent[0]).not.toHaveProperty('TargetId');
  });

  it('refuses when already saturated, which an honest player reaches', () => {
    // Two potions in a row is enough, and the server answers it by
    // disconnecting rather than rejecting.
    expect(consumeConsumable(77, MAX_BUFF_TICKS + 1).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('allows exactly at the cap, matching the server comparison', () => {
    expect(consumeConsumable(77, MAX_BUFF_TICKS).ok).toBe(true);
  });
});

describe('guild depot and war', () => {
  it('deposits through MaterialId and DepositQuantity ALONE', () => {
    // ValidateGuildDepositRequest disconnects if any of fourteen unrelated
    // fields is non-zero, so this asserts the exact object, not a subset.
    depositGuildMaterial(12, 30, true);
    expect(sent[0]).toEqual({
      Command: CommandType.DepositGuildMaterial,
      MaterialId: 12,
      DepositQuantity: 30,
    });
  });

  it('refuses a deposit without a guild', () => {
    expect(depositGuildMaterial(12, 30, false).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses a material above the definition count', () => {
    expect(depositGuildMaterial(9999, 1, true, 400).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('uses the OTHER field pair for a logistics contribution', () => {
    // Same intent, different command, different fields, different engine.
    contributeToGuildStock(12, 30, true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.ContributeToGuild,
      TargetId: 12,
      LimitPrice: 30,
    });
  });

  it('registers a defence with no payload at all', () => {
    registerGuildDefense(true, false);
    expect(sent[0]).toEqual({ Command: CommandType.RegisterGuildDefense });
  });

  it('refuses a shard attack against a match other than the committed one', () => {
    // Once committed, only that match may be attacked - a stale id left in a
    // screen after the war rolls over ends the session on the next click.
    const outcome = submitShardAttack({
      matchUuid: '11111111-1111-1111-1111-111111111111',
      predictedDamage: 100,
      hasGuild: true,
      quarantined: false,
      activeMatchUuid: '22222222-2222-2222-2222-222222222222',
    });
    expect(outcome.ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('allows a shard attack when not yet committed to any match', () => {
    const outcome = submitShardAttack({
      matchUuid: '11111111-1111-1111-1111-111111111111',
      predictedDamage: 100,
      hasGuild: true,
      quarantined: false,
      activeMatchUuid: '00000000-0000-0000-0000-000000000000',
    });
    expect(outcome.ok).toBe(true);
  });

  it('refuses a combat turn with no running match', () => {
    expect(executeCombatTurn(0, 3, true).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

describe('mentor assignment', () => {
  it('refuses a slot at or above the Academy level', () => {
    // The Academy's level IS its slot count, so a level 2 Academy has slots 0
    // and 1, and clicking slot 2 ends the session.
    expect(assignMentor('abc', 2, 2).ok).toBe(false);
    expect(assignMentor('abc', 1, 2).ok).toBe(true);
  });

  it('refuses without an Academy', () => {
    expect(assignMentor('abc', 0, 0).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('carries the slot on LimitPrice - a fourth meaning for that field', () => {
    assignMentor('abc', 1, 3);
    expect(sent[0]).toMatchObject({
      Command: CommandType.AssignMentor,
      TargetGuid: 'abc',
      LimitPrice: 1,
    });
  });
});

// Modul: the tool-upgrade command is gone, and so is this test.
//
// VillageBuildingEngine.ExecuteUpgradeToolAsync was `return
// Task.CompletedTask;` - a whole engine holding one empty method, constructed
// in Program, threaded through the simulation constructor, dispatched to on
// every request and accomplishing nothing. The village screen's tool panel had
// already gone (tools are ordinary equipment now: crafted, carried, rerolled
// and raised at the Forge), which left this helper with no callers.
//
// This test passed for months. It asserted that a command was well-formed,
// which it was - it never asked whether anything happened at the other end.
// That is worth writing down: an audit of the wire cannot tell you a feature
// is dead.

describe('account erasure', () => {
  it('carries a confirmation hash bound to the player and the epoch', () => {
    triggerGdprPurge(1042, 7);
    expect(sent[0]).toMatchObject({ Command: CommandType.TriggerGdprPurge });
    // The value is checked against the server's OWN output in
    // antiCheat.test.ts. Here it matters only that one is present and is not
    // zero, which is what an unimplemented hash looks like.
    expect(sent[0].ConfirmationHash).toBeTypeOf('number');
    expect(sent[0].ConfirmationHash).not.toBe(0);
  });

  it('refuses before a player id is known', () => {
    expect(triggerGdprPurge(0, 7).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});
