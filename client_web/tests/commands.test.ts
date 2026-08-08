import { describe, it, expect, beforeEach, vi } from 'vitest';

// The guard layer's whole job is to NOT send certain things, so the test has
// to see what reached the socket. Mocked at the connection module rather than
// the WebSocket so the assertions read as "what command was issued".
const sent: Record<string, unknown>[] = [];
vi.mock('../src/lib/net/connection', () => ({
  connection: {
    send: (draft: Record<string, unknown>) => {
      sent.push(draft);
    },
    // Mentorship refuses self-targeting, so the guard needs an identity.
    currentPlayerId: 0,
  },
}));

const {
  listItemOnMarket,
  buyMarketListing,
  depositToBank,
  withdrawFromBank,
  startTreeCraft,
  executeForgeFusion,
  rerollAffix,
  addFriend,
  removeFriend,
  blockPlayer,
  unblockPlayer,
  contributeToWarSupply,
  launchGuildRaid,
  contributeGuildGold,
  establishMentorship,
  terminateMentorship,
  executeVillagerBreeding,
} = await import('../src/lib/net/commands');
const { CommandType } = await import('../src/lib/net/protocol.generated');

// Modul: THE SERVER DISCONNECTS on an invalid economy command - it calls
// TerminateSessionForSecurity, not a rejection code. So every refusal below is
// a player who did NOT get kicked for a mis-click, and every one of these
// tests is guarding a real session-ending path rather than a validation nicety.

beforeEach(() => {
  sent.length = 0;
});

describe('market', () => {
  it('sends a listing with the price on LimitPrice', () => {
    expect(listItemOnMarket(42, 500).ok).toBe(true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.MarketListItem,
      TargetId: 42,
      LimitPrice: 500,
    });
  });

  it('refuses a non-positive price instead of being disconnected', () => {
    // ValidateMarketCommands: price <= 0 on a listing disconnects.
    for (const price of [0, -1]) {
      const outcome = listItemOnMarket(42, price);
      expect(outcome.ok).toBe(false);
      expect(sent).toHaveLength(0);
    }
  });

  it('refuses a non-positive target instead of being disconnected', () => {
    expect(listItemOnMarket(0, 100).ok).toBe(false);
    expect(buyMarketListing(0).ok).toBe(false);
    expect(buyMarketListing(-5).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('buys without a price - the listing carries its own', () => {
    expect(buyMarketListing(7).ok).toBe(true);
    expect(sent[0]).toMatchObject({ Command: CommandType.MarketBuyItem, TargetId: 7 });
    expect(sent[0].LimitPrice).toBeUndefined();
  });
});

describe('bank', () => {
  it('deposits by equipment instance id and withdraws by BANK ROW id', () => {
    depositToBank(11);
    withdrawFromBank(22);
    expect(sent[0]).toMatchObject({ Command: CommandType.DepositToBank, TargetId: 11 });
    expect(sent[1]).toMatchObject({ Command: CommandType.WithdrawFromBank, TargetId: 22 });
  });

  it('refuses non-positive ids', () => {
    expect(depositToBank(0).ok).toBe(false);
    expect(withdrawFromBank(0).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

// Modul: THERE IS ONE RECIPE TABLE NOW.
//
// This block used to guard the boundary between two of them: the crafting TREE
// on InitializeCrafting and the FORGE's equipment recipes on CraftItem, with
// the names pointing the opposite way from what anyone would guess. It was
// wired backwards once, and the consequence was worse than nothing happening -
// where the two id spaces overlapped, a request silently crafted something
// else.
//
// The boundary is gone because one side of it is: equipment is monster loot
// and tools are crafted, so CraftingReceptuary and the CraftItem path went
// with the recipes they served. What is left to assert is that the one
// remaining path still carries the RESULT ITEM id on TargetId - the shape that
// was wrong first - and that it refuses the ids the validator would have
// disconnected for.
describe('the crafting tree', () => {
  it('sends tree crafts as InitializeCrafting with the RESULT item on TargetId', () => {
    expect(startTreeCraft(184).ok).toBe(true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.InitializeCrafting,
      TargetId: 184,
    });
    expect(sent[0].TargetRecipeId).toBeUndefined();
  });

  it('never routes a tree recipe down the retired forge command', () => {
    // The whole original bug in one assertion, and still worth keeping: the
    // server ignores CraftItem now rather than acting on it, so a regression
    // here would be silent instead of loud.
    startTreeCraft(184);
    expect(sent[0].Command).not.toBe(CommandType.CraftItem);
  });

  it('refuses non-positive recipe ids', () => {
    expect(startTreeCraft(0).ok).toBe(false);
    expect(startTreeCraft(-1).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

describe('forge fusion', () => {
  it('sends three distinct ids across TargetId/SecondaryId/TertiaryId', () => {
    expect(executeForgeFusion(1, 2, 3, 5).ok).toBe(true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.ExecuteForgeFusion,
      TargetId: 1,
      SecondaryId: 2,
      TertiaryId: 3,
    });
  });

  it('REFUSES duplicate ids - the likeliest way a UI disconnects a player', () => {
    // ValidateFusionCommand disconnects when any two of the three match, and
    // two dropdowns defaulting to the same item is the obvious way to build
    // this screen.
    expect(executeForgeFusion(1, 1, 3, 5).ok).toBe(false);
    expect(executeForgeFusion(1, 2, 1, 5).ok).toBe(false);
    expect(executeForgeFusion(1, 2, 2, 5).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses when the player has no Forge, which also disconnects', () => {
    const outcome = executeForgeFusion(1, 2, 3, 0);
    expect(outcome.ok).toBe(false);
    expect(outcome.ok === false && outcome.reason).toContain('Forge');
    expect(sent).toHaveLength(0);
  });

  it('refuses non-positive ids', () => {
    expect(executeForgeFusion(0, 2, 3, 5).ok).toBe(false);
    expect(executeForgeFusion(1, 0, 3, 5).ok).toBe(false);
    expect(executeForgeFusion(1, 2, 0, 5).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

describe('relationships', () => {
  it('routes all four through TargetPlayerId, never TargetId', () => {
    // Neighbouring commands use TargetId for their own purposes, so this is
    // the field that matters rather than the obvious-looking one.
    addFriend(42);
    removeFriend(42);
    blockPlayer(42);
    unblockPlayer(42);

    expect(sent.map((c) => c.Command)).toEqual([
      CommandType.AddFriend,
      CommandType.RemoveFriend,
      CommandType.BlockPlayer,
      CommandType.UnblockPlayer,
    ]);
    for (const command of sent) {
      expect(command.TargetPlayerId).toBe(42);
      expect(command.TargetId).toBeUndefined();
    }
  });

  it('refuses a non-positive or out-of-range player id', () => {
    // The field is a uint on the wire, so anything above 2^32 would wrap
    // silently into a different player.
    expect(addFriend(0).ok).toBe(false);
    expect(addFriend(-1).ok).toBe(false);
    expect(blockPlayer(0x1_0000_0000).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

describe('guild war, raids and treasury', () => {
  it('puts the war commodity on SecondaryId and the amount on TertiaryId', () => {
    expect(contributeToWarSupply(3, 100, 7).ok).toBe(true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.ContributeToWarSupply,
      SecondaryId: 3,
      TertiaryId: 100,
    });
    // This command genuinely carries no TargetId.
    expect(sent[0].TargetId).toBeUndefined();
  });

  it('refuses a war contribution with no active war', () => {
    // The dispatcher silently ignores it otherwise - no error, no effect.
    expect(contributeToWarSupply(3, 100, 0).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('sends a raid with no payload at all', () => {
    expect(launchGuildRaid(true).ok).toBe(true);
    expect(sent[0]).toEqual({ Command: CommandType.LaunchGuildRaid });
  });

  it('refuses a raid or treasury gift without a guild - both disconnect', () => {
    expect(launchGuildRaid(false).ok).toBe(false);
    expect(contributeGuildGold(100, false).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('marks a gold contribution with TargetId 0 and the amount on LimitPrice', () => {
    // TargetId == 0 is what makes it gold; anything else is an item instance.
    expect(contributeGuildGold(500, true).ok).toBe(true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.ContributeGuildTreasury,
      TargetId: 0,
      LimitPrice: 500,
    });
  });

  it('refuses a non-positive gold amount, which disconnects', () => {
    expect(contributeGuildGold(0, true).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

describe('mentorship', () => {
  it('sends only the counterparty, because the validator rejects anything else', () => {
    // ValidateMentorshipRequest disconnects if MentorshipRole is non-zero or
    // either Guid field is set - those checks exist to reject tampering, so
    // the client must send neither.
    expect(establishMentorship(88).ok).toBe(true);
    expect(sent[0]).toEqual({
      Command: CommandType.EstablishMentorship,
      TargetPlayerId: 88,
    });
  });

  it('refuses targeting yourself, which disconnects', () => {
    // connection.currentPlayerId is 0 in this mock, so 0 doubles as "self".
    expect(establishMentorship(0).ok).toBe(false);
    expect(terminateMentorship(0).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

describe('hero x villager pairing', () => {
  it('sends the hero as a Guid and the villager as an id, and nothing else', () => {
    // ValidateVillagerBreedingRequest is the INVERSE of the character
    // pairing's field rule: SecondaryGuid must be empty and TargetId must not
    // be. Sending the villager on SecondaryGuid would disconnect the tab.
    expect(executeVillagerBreeding('a-hero-guid', 42, 1).ok).toBe(true);
    expect(sent[0]).toEqual({
      Command: CommandType.ExecuteVillagerBreeding,
      TargetGuid: 'a-hero-guid',
      TargetId: 42,
    });
  });

  it('refuses without Breeding Grounds, which the server answers by disconnecting', () => {
    expect(executeVillagerBreeding('a-hero-guid', 42, 0).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses an unchosen hero or villager', () => {
    expect(executeVillagerBreeding('', 42, 1).ok).toBe(false);
    expect(executeVillagerBreeding('a-hero-guid', 0, 1).ok).toBe(false);
    expect(executeVillagerBreeding('a-hero-guid', -1, 1).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });
});

describe('affix reroll', () => {
  it('carries the affix index on LimitPrice, not a price', () => {
    expect(rerollAffix(9, 2, 0).ok).toBe(true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.RerollItemAffix,
      TargetId: 9,
      LimitPrice: 2,
      RerollOperationKind: 0,
    });
  });

  it('defaults to a single reroll', () => {
    rerollAffix(9, 0, 0);
    // 0 attempts means "one reroll"; anything higher runs the auto-planner.
    expect(sent[0].RerollAutoMaxAttempts).toBe(0);
  });

  it('clamps the stop rarity into the 1-5 the server expects', () => {
    rerollAffix(9, 0, 0, { maxAttempts: 20, stopMinRarity: 99 });
    expect(sent[0].RerollStopMinRarity).toBe(5);

    sent.length = 0;
    rerollAffix(9, 0, 0, { maxAttempts: 20, stopMinRarity: 0 });
    // The server clamps < 1 up to 1 anyway; sending 1 keeps the intent legible.
    expect(sent[0].RerollStopMinRarity).toBe(1);
  });

  it('never sends a negative affix index, which disconnects', () => {
    expect(rerollAffix(9, -1, 0).ok).toBe(false);
    expect(rerollAffix(0, 0, 0).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses an operation the server does not define', () => {
    expect(rerollAffix(9, 0, 7).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('passes an auto-reroll request through as a REQUEST, not a bound', () => {
    // AutoRerollPlanner.MaxAttemptsPerRequest clamps this server-side; the
    // client number is never trusted as a limit.
    rerollAffix(9, 1, 1, { maxAttempts: 5000, stopMinRarity: 4, stopAffixIndex: 3 });
    expect(sent[0]).toMatchObject({
      RerollAutoMaxAttempts: 5000,
      RerollStopMinRarity: 4,
      RerollStopAffixIndex: 3,
    });
  });
});
