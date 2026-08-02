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
  },
}));

const {
  listItemOnMarket,
  buyMarketListing,
  depositToBank,
  withdrawFromBank,
  startTreeCraft,
  craftForgeRecipe,
  executeForgeFusion,
  rerollAffix,
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

// Modul: TWO recipe tables, two commands, names pointing the opposite way from
// what you would guess. The crafting TREE runs on InitializeCrafting;
// CraftItem is the FORGE's. This was wired backwards first, and the
// consequences are worse than "nothing happens": ValidateCraftingRequest
// DISCONNECTS on a TargetRecipeId that is not a CraftingReceptuary recipe, and
// where the two id spaces overlap the request silently crafts something else.
describe('crafting tree vs forge crafting', () => {
  it('sends tree crafts as InitializeCrafting with the RESULT item on TargetId', () => {
    expect(startTreeCraft(184).ok).toBe(true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.InitializeCrafting,
      TargetId: 184,
    });
    expect(sent[0].TargetRecipeId).toBeUndefined();
  });

  it('sends forge crafts as CraftItem with the RECIPE id on TargetRecipeId', () => {
    expect(craftForgeRecipe(17, 1).ok).toBe(true);
    expect(sent[0]).toMatchObject({
      Command: CommandType.CraftItem,
      TargetRecipeId: 17,
      CraftingSlotIndex: 1,
    });
    // The dispatcher never reads TargetId for this command.
    expect(sent[0].TargetId).toBeUndefined();
  });

  it('never routes a tree recipe down the forge command', () => {
    // The whole bug in one assertion.
    startTreeCraft(184);
    expect(sent[0].Command).not.toBe(CommandType.CraftItem);
  });

  it('refuses a crafting slot the validator would disconnect for', () => {
    expect(craftForgeRecipe(17, 5).ok).toBe(false);
    expect(craftForgeRecipe(17, -1).ok).toBe(false);
    expect(sent).toHaveLength(0);
  });

  it('refuses non-positive recipe ids on both paths', () => {
    expect(startTreeCraft(0).ok).toBe(false);
    expect(craftForgeRecipe(0).ok).toBe(false);
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
