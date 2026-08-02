// Modul: the guarded command layer.
//
// THE SERVER'S ANSWER TO AN INVALID ECONOMY COMMAND IS TO DISCONNECT YOU.
//
// Not a rejection code, not a toast - `TerminateSessionForSecurity` followed
// by `ForceDisconnect`, which the player sees as close code 1008 "Violent
// termination" with no explanation anywhere. That is a reasonable posture for
// a server treating malformed input as tampering, and a terrible thing to
// expose an honest player's mis-click to.
//
// So every Phase 3 command goes through a function here that checks the
// server's own precondition FIRST and refuses to send rather than letting the
// session die. Screens never call `connection.send` for these directly.
//
// The preconditions below are transcribed from ClientCommandValidator, and
// each one names the validator it mirrors. If one of these ever disagrees with
// the server, the symptom is a disconnected player, so they are worth keeping
// boring and explicit.
//
// A second hazard this layer contains: `LimitPrice` is overloaded THREE ways
// on this wire - a market price, the auto-eat threshold, and the reroll's
// affix index. Naming the parameters here means no screen has to remember
// which meaning applies to it.

import { connection } from './connection';
import { CommandType } from './protocol.generated';

export interface CommandRefusal {
  ok: false;
  /** Shown to the player. Explains what to change, not what the server does. */
  reason: string;
}

export interface CommandAccepted {
  ok: true;
}

export type CommandOutcome = CommandAccepted | CommandRefusal;

const OK: CommandAccepted = { ok: true };

function refuse(reason: string): CommandRefusal {
  return { ok: false, reason };
}

// ---------------------------------------------------------------------------
// Market
// ---------------------------------------------------------------------------

/**
 * Mirrors ValidateMarketCommands: a listing needs a positive price and every
 * market command needs a positive target. Either violation disconnects.
 */
export function listItemOnMarket(equipmentInstanceId: number, price: number): CommandOutcome {
  if (!Number.isInteger(equipmentInstanceId) || equipmentInstanceId <= 0) {
    return refuse('Pick an item to list.');
  }
  if (!Number.isInteger(price) || price <= 0) {
    return refuse('Set a price above zero.');
  }

  connection.send({
    Command: CommandType.MarketListItem,
    TargetId: equipmentInstanceId,
    LimitPrice: price,
  });
  return OK;
}

/** Buying carries no price - the listing's own price applies. */
export function buyMarketListing(orderId: number): CommandOutcome {
  if (!Number.isInteger(orderId) || orderId <= 0) {
    return refuse('Pick a listing to buy.');
  }

  connection.send({ Command: CommandType.MarketBuyItem, TargetId: orderId });
  return OK;
}

// ---------------------------------------------------------------------------
// Bank vault
// ---------------------------------------------------------------------------

export function depositToBank(equipmentInstanceId: number): CommandOutcome {
  if (!Number.isInteger(equipmentInstanceId) || equipmentInstanceId <= 0) {
    return refuse('Pick an item to deposit.');
  }
  connection.send({ Command: CommandType.DepositToBank, TargetId: equipmentInstanceId });
  return OK;
}

/** Withdraw addresses the BANK ROW id, not the equipment instance id. */
export function withdrawFromBank(bankRowId: number): CommandOutcome {
  if (!Number.isInteger(bankRowId) || bankRowId <= 0) {
    return refuse('Pick a stored item to withdraw.');
  }
  connection.send({ Command: CommandType.WithdrawFromBank, TargetId: bankRowId });
  return OK;
}

// ---------------------------------------------------------------------------
// Crafting - TWO SEPARATE SYSTEMS WITH CONFUSABLE NAMES
// ---------------------------------------------------------------------------

// Modul: there are two recipe tables and two commands, and the names point the
// opposite way from what you would guess:
//
//   ContentRegistry.Recipes  (103 entries, /api/v1/crafting/recipes)
//     -> CommandType.InitializeCrafting (18), RESULT ITEM ID on TargetId
//   CraftingReceptuary       (the Forge's own, /api/v1/forge/inventory)
//     -> CommandType.CraftItem (42), RECIPE ID on TargetRecipeId
//
// So `CraftItem` does NOT craft from the crafting tree, despite the name.
// Wiring the tree to it is not merely broken, it is dangerous:
// ValidateCraftingRequest DISCONNECTS when TargetRecipeId is not a
// CraftingReceptuary recipe, and where the two id spaces happen to overlap the
// request silently addresses a completely different recipe instead. Both
// failure modes were reachable from one wrong line, and neither says anything.

/** Crafting tree. `resultItemId` is ContentRegistry's ResultItemId. */
export function startTreeCraft(resultItemId: number): CommandOutcome {
  if (!Number.isInteger(resultItemId) || resultItemId <= 0) {
    return refuse('Pick a recipe.');
  }

  connection.send({ Command: CommandType.InitializeCrafting, TargetId: resultItemId });
  return OK;
}

/**
 * Forge equipment crafting. `recipeId` MUST come from /api/v1/forge/inventory -
 * that endpoint returns CraftingReceptuary ids and is the only way this client
 * can know one is real. There is no client-side recipe table to check against,
 * and an unknown id disconnects rather than being rejected.
 */
export function craftForgeRecipe(recipeId: number, craftingSlotIndex = 0): CommandOutcome {
  if (!Number.isInteger(recipeId) || recipeId <= 0) {
    return refuse('Pick a forge recipe.');
  }
  // ValidateCraftingRequest disconnects at >= 5.
  if (!Number.isInteger(craftingSlotIndex) || craftingSlotIndex < 0 || craftingSlotIndex >= 5) {
    return refuse('Crafting slot must be 0-4.');
  }

  connection.send({
    Command: CommandType.CraftItem,
    TargetRecipeId: recipeId,
    CraftingSlotIndex: craftingSlotIndex,
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Forge fusion
// ---------------------------------------------------------------------------

/**
 * Mirrors ValidateFusionCommand, which is the most dangerous validator on this
 * wire: it disconnects if any id is non-positive, if ANY TWO OF THE THREE ARE
 * EQUAL, or if the player's Forge level is 0.
 *
 * The duplicate check is the one a UI is most likely to violate by accident -
 * two dropdowns defaulting to the same item is the obvious way to build this
 * screen, and it would disconnect the player on first use.
 */
export function executeForgeFusion(
  targetId: number,
  sacrificeOneId: number,
  sacrificeTwoId: number,
  forgeLevel: number,
): CommandOutcome {
  if (forgeLevel <= 0) {
    return refuse('Build a Forge in your village first.');
  }
  if (targetId <= 0 || sacrificeOneId <= 0 || sacrificeTwoId <= 0) {
    return refuse('Choose a target item and two different items to sacrifice.');
  }
  if (targetId === sacrificeOneId || targetId === sacrificeTwoId || sacrificeOneId === sacrificeTwoId) {
    return refuse('The target and both sacrifices must be three different items.');
  }

  connection.send({
    Command: CommandType.ExecuteForgeFusion,
    TargetId: targetId,
    SecondaryId: sacrificeOneId,
    TertiaryId: sacrificeTwoId,
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Affix reroll
// ---------------------------------------------------------------------------

/** RerollOperation. UpgradeRarity is the only one priced in Diamonds. */
export const REROLL_OPERATIONS = [
  { kind: 0, label: 'Reroll value', currency: 'gold', hint: 'Same stat and rarity, new magnitude inside its band.' },
  { kind: 1, label: 'Reroll stat', currency: 'gold', hint: 'New stat, rarity preserved. Costs 2.5x - it can turn a dead affix into the one a build wants.' },
  { kind: 2, label: 'Upgrade rarity', currency: 'diamonds', hint: 'One rarity step up. The only operation priced in Diamonds.' },
] as const;

export interface AutoRerollOptions {
  /** 0 runs a single reroll. Anything higher is a REQUEST - the server clamps it. */
  maxAttempts?: number;
  /** Stop at or above this affix rarity, 1-5. 1 means "any". */
  stopMinRarity?: number;
  /** 1-based index into AffixRegistry.Definitions. 0 means "any stat". */
  stopAffixIndex?: number;
}

/**
 * Mirrors ValidateAffixReroll: a non-positive item id or a negative affix
 * index disconnects.
 *
 * `affixIndex` rides on LimitPrice - the same field that carries a market
 * price elsewhere. It is a 0-based index into the item's own affix list.
 */
export function rerollAffix(
  equipmentInstanceId: number,
  affixIndex: number,
  operationKind: number,
  options: AutoRerollOptions = {},
): CommandOutcome {
  if (!Number.isInteger(equipmentInstanceId) || equipmentInstanceId <= 0) {
    return refuse('Pick an item to reroll.');
  }
  if (!Number.isInteger(affixIndex) || affixIndex < 0) {
    return refuse('Pick an affix to reroll.');
  }
  if (![0, 1, 2].includes(operationKind)) {
    return refuse('Pick a reroll operation.');
  }

  const maxAttempts = Math.max(0, Math.trunc(options.maxAttempts ?? 0));
  // Rarity is a FLOOR, 1-5, and 1 means "any". Sending 0 would be read as
  // rarity 1 anyway (the server clamps `stopMinRarity < 1` up to 1), but
  // sending it deliberately keeps the client's intent legible.
  const stopMinRarity = Math.min(5, Math.max(1, Math.trunc(options.stopMinRarity ?? 1)));
  const stopAffixIndex = Math.max(0, Math.trunc(options.stopAffixIndex ?? 0));

  connection.send({
    Command: CommandType.RerollItemAffix,
    TargetId: equipmentInstanceId,
    LimitPrice: affixIndex,
    RerollOperationKind: operationKind,
    RerollAutoMaxAttempts: maxAttempts,
    RerollStopMinRarity: stopMinRarity,
    RerollStopAffixIndex: stopAffixIndex,
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Relationships
// ---------------------------------------------------------------------------

// Modul: all four relationship commands resolve their target through
// TargetPlayerId - never TargetId, which several neighbouring commands use for
// their own purposes. The field is a uint on the wire, so a player id above
// 2^32 would silently wrap; ids are sequential and nowhere near that, but the
// bound is checked rather than assumed.
const MAX_UINT32 = 0xffffffff;

function relationshipCommand(command: number, targetPlayerId: number, verb: string): CommandOutcome {
  if (!Number.isInteger(targetPlayerId) || targetPlayerId <= 0 || targetPlayerId > MAX_UINT32) {
    return refuse(`Pick a player to ${verb}.`);
  }
  connection.send({ Command: command, TargetPlayerId: targetPlayerId });
  return OK;
}

export function addFriend(targetPlayerId: number): CommandOutcome {
  return relationshipCommand(CommandType.AddFriend, targetPlayerId, 'add');
}

export function removeFriend(targetPlayerId: number): CommandOutcome {
  return relationshipCommand(CommandType.RemoveFriend, targetPlayerId, 'remove');
}

export function blockPlayer(targetPlayerId: number): CommandOutcome {
  return relationshipCommand(CommandType.BlockPlayer, targetPlayerId, 'block');
}

export function unblockPlayer(targetPlayerId: number): CommandOutcome {
  return relationshipCommand(CommandType.UnblockPlayer, targetPlayerId, 'unblock');
}
