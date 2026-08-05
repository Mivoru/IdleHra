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
import { computeGdprConfirmationHash } from './antiCheat';

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

/**
 * Standing limit order - the resting side of the book, as opposed to the
 * instant list/buy above.
 *
 * The two directions address COMPLETELY DIFFERENT THINGS through the same
 * TargetId, and the dispatcher decides which by reading `IsBuy`:
 *
 *   sell (IsBuy 0) - TargetId is an equipment INSTANCE id you own
 *   buy  (IsBuy 1) - TargetId is an item DEFINITION id, resolved server-side
 *                    through ContentRegistry.GetItemBaseId, and QualityTier
 *                    then narrows which quality the order will fill against
 *
 * So a buy order built by copying the sell path sends an instance id where a
 * definition id belongs. That does not disconnect - it silently posts an order
 * for whatever item happens to share that number, which is worse.
 *
 * ValidatePlaceLimitOrderRequest disconnects on a non-positive price, a
 * negative quality tier, a non-positive target, and - for BUY orders only - a
 * target above ContentRegistry.ItemDefinitions.Length. That last bound is the
 * one this client cannot check from first principles, so the caller passes the
 * definition count it loaded from /gamedata; passing 0 skips the check rather
 * than refusing every order, because a missing content table is the screen's
 * problem to report, not a reason to claim the player picked a bad item.
 */
export function placeLimitOrder(options: {
  isBuy: boolean;
  /** Instance id when selling, item definition id when buying. */
  targetId: number;
  price: number;
  /** Buy orders only. 0 means "any quality". */
  qualityTier?: number;
  /** ContentRegistry.ItemDefinitions.Length. Buy orders above it disconnect. */
  itemDefinitionCount?: number;
}): CommandOutcome {
  const { isBuy, targetId, price } = options;

  if (!Number.isInteger(targetId) || targetId <= 0) {
    return refuse(isBuy ? 'Pick an item to bid on.' : 'Pick an item to sell.');
  }
  const definitionCount = options.itemDefinitionCount ?? 0;
  if (isBuy && definitionCount > 0 && targetId > definitionCount) {
    return refuse('That item does not exist.');
  }
  if (!Number.isInteger(price) || price <= 0) {
    return refuse('Set a price above zero.');
  }

  const qualityTier = Math.max(0, Math.trunc(options.qualityTier ?? 0));
  if (!isBuy && qualityTier !== 0) {
    // A sell order's quality comes from the instance itself; sending one here
    // would be ignored, and ignoring it silently hides a caller's confusion
    // about which side it is building.
    return refuse('A sell order takes its quality from the item itself.');
  }

  connection.send({
    Command: CommandType.PlaceLimitOrder,
    TargetId: targetId,
    LimitPrice: price,
    IsBuy: isBuy ? 1 : 0,
    QualityTier: qualityTier,
  });
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
// Mailbox
// ---------------------------------------------------------------------------

/**
 * Mirrors ValidateMailCommands, which disconnects on a non-positive mail id.
 *
 * `mailId` is the MAIL ROW id from /api/v1/mailbox/list, not an item id - the
 * same distinction the bank draws between a row and an instance, and the same
 * way to get it wrong.
 */
export function claimMailItem(mailId: number): CommandOutcome {
  if (!Number.isInteger(mailId) || mailId <= 0) {
    return refuse('Pick a message to claim.');
  }
  connection.send({ Command: CommandType.ClaimMailItem, TargetId: mailId });
  return OK;
}

// ---------------------------------------------------------------------------
// World boss
// ---------------------------------------------------------------------------

/** WorldBossEngine.ActiveBossInstanceId. One boss exists; the id is a constant. */
export const ACTIVE_BOSS_INSTANCE_ID = 1;

/** WorldBossEngine.MaxAttemptsPerEncounter. */
export const MAX_BOSS_ATTEMPTS = 3;

/** WorldBossEngine.MaxClientPredictedDamage. Above this the session dies. */
export const MAX_PREDICTED_DAMAGE = 100_000_000;

/** StateUpdatePacket.WorldBossEventState. */
export const BossEventState = { Dormant: 0, Active: 1, Concluded: 2 } as const;

/**
 * Mirrors ValidateWorldBossAttackRequest - and then goes further, because that
 * validator is only half the story.
 *
 * The validator DISCONNECTS on: the event not being active, a zero or
 * out-of-range predicted damage, a boss id other than the active one, the boss
 * already being dead, or any of twenty unrelated fields being non-zero.
 *
 * ExecuteAttackAsync then SILENTLY ROLLS BACK - no damage, no message, no
 * telemetry the player will ever see - on three further conditions:
 *
 *   - the player has already used all three attempts this encounter
 *   - the battle session cap has elapsed
 *   - AUTO-EAT FOOD IS DEPLETED (all three larder slots empty)
 *
 * That last one is the cruel one: with an empty larder the button works, the
 * request is accepted, and absolutely nothing happens. So it is refused here
 * with an explanation rather than sent into the void.
 */
export function attackWorldBoss(options: {
  predictedDamage: number;
  eventState: number;
  bossCurrentHp: number;
  attemptCount: number;
  /** True when Food1_Count, Food2_Count and Food3_Count are all zero. */
  larderEmpty: boolean;
}): CommandOutcome {
  const { predictedDamage, eventState, bossCurrentHp, attemptCount, larderEmpty } = options;

  if (eventState !== BossEventState.Active) {
    return refuse('No world boss is active right now.');
  }
  if (bossCurrentHp <= 0) {
    return refuse('The boss is already dead.');
  }
  if (attemptCount >= MAX_BOSS_ATTEMPTS) {
    return refuse(`You have used all ${MAX_BOSS_ATTEMPTS} attempts this encounter.`);
  }
  if (larderEmpty) {
    return refuse('Stock your larder first - an attack with no food is discarded silently.');
  }

  const damage = Math.trunc(predictedDamage);
  if (!Number.isFinite(damage) || damage <= 0 || damage > MAX_PREDICTED_DAMAGE) {
    return refuse('Cannot estimate your damage right now.');
  }

  connection.send({
    Command: CommandType.AttackWorldBoss,
    TargetedBossId: ACTIVE_BOSS_INSTANCE_ID,
    ClientPredictedDamage: damage,
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Consumables
// ---------------------------------------------------------------------------

/** ValidateConsumableRequest's saturation cap, in 10 Hz ticks - two hours. */
export const MAX_BUFF_TICKS = 72000;

/**
 * Mirrors ValidateConsumableRequest, which disconnects when the item is not a
 * registered consumable OR when the player is already saturated with buff
 * duration. The saturation case is the one an honest player reaches by simply
 * drinking two potions in a row, so it is refused here with the remaining wait
 * rather than allowed to kill the session.
 *
 * The item id rides on `ConsumableItemId`, not TargetId - one of the few
 * commands on this wire with a field of its own for its subject.
 */
export function consumeConsumable(
  consumableItemId: number,
  remainingBuffTicks: number,
  slotTarget = 0,
): CommandOutcome {
  if (!Number.isInteger(consumableItemId) || consumableItemId <= 0) {
    return refuse('Pick something to use.');
  }
  if (remainingBuffTicks > MAX_BUFF_TICKS) {
    const waitSeconds = Math.ceil((remainingBuffTicks - MAX_BUFF_TICKS) / 10);
    return refuse(`Already saturated - wait about ${Math.ceil(waitSeconds / 60)} more minutes.`);
  }

  connection.send({
    Command: CommandType.ConsumeConsumableAsset,
    ConsumableItemId: consumableItemId,
    ConsumableSlotTarget: Math.max(0, Math.trunc(slotTarget)),
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Chrono bank - AND THE ONE PLACE LogicEpochCounter MEANS SOMETHING ELSE
// ---------------------------------------------------------------------------

// Modul: READ THIS BEFORE TOUCHING EITHER FUNCTION BELOW.
//
// `LogicEpochCounter` carries TWO DIFFERENT QUANTITIES on this wire, and which
// one is expected depends on the command:
//
//   every other command - the save-generation counter echoed back from the
//     last StateUpdate (payload.LogicEpochCounter, which advances by one per
//     checkpoint flush). ValidateEpochSynchronization allows +-5 drift.
//
//   ActivateChronoBoost and ConsumeTimeWarpCore - UNIX EPOCH SECONDS, compared
//     against DateTimeOffset.UtcNow with +-5 SECONDS of tolerance.
//
// SimulationEngine skips ValidateEpochSynchronization for exactly these two
// (`!isChronoManipulationCommand`), which is what makes the reuse possible and
// what makes it invisible. GameConnection.send stamps the counter on every
// command, so these two MUST override it - sending the generation counter
// where a timestamp belongs fails the drift check and kills the session.
//
// Note this uses the SERVER-corrected clock. A browser whose clock is more
// than five seconds off would otherwise be permanently unable to use its own
// banked time, with no way to tell why.

/** ChronoBufferEngine.MaxBankedChronoSeconds - seven days. */
export const MAX_BANKED_CHRONO_SECONDS = 604800;

/** The only two multipliers ValidateChronoManipulation accepts. */
export const CHRONO_MULTIPLIERS = [2, 4] as const;

export function activateChronoBoost(
  multiplier: number,
  bankedSeconds: number,
  quarantined: boolean,
): CommandOutcome {
  if (quarantined) return refuse('Your account is restricted.');
  if (!CHRONO_MULTIPLIERS.includes(multiplier as 2 | 4)) {
    return refuse('Only 2x and 4x are available.');
  }
  if (bankedSeconds <= 0) {
    return refuse('You have no banked time to spend.');
  }

  connection.send({
    Command: CommandType.ActivateChronoBoost,
    RequestedSpeedMultiplier: multiplier,
    LogicEpochCounter: Math.floor(connection.serverNowMs() / 1000),
  });
  return OK;
}

export function consumeTimeWarpCore(
  seconds: number,
  bankedSeconds: number,
  quarantined: boolean,
  targetSlot = 0,
): CommandOutcome {
  if (quarantined) return refuse('Your account is restricted.');

  const requested = Math.trunc(seconds);
  if (!Number.isFinite(requested) || requested <= 0) {
    return refuse('Choose how much time to spend.');
  }
  if (requested > bankedSeconds) {
    return refuse('You do not have that much banked time.');
  }
  if (requested > MAX_BANKED_CHRONO_SECONDS) {
    return refuse('Seven days is the most that can be spent at once.');
  }

  connection.send({
    Command: CommandType.ConsumeTimeWarpCore,
    ChronoWarpDurationSeconds: requested,
    ChronoTargetSlot: Math.max(0, Math.trunc(targetSlot)),
    LogicEpochCounter: Math.floor(connection.serverNowMs() / 1000),
  });
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

// Modul: craftForgeRecipe is gone, with the equipment recipes it sent.
// EQUIPMENT IS MONSTER LOOT AND TOOLS ARE CRAFTED, and nothing is both - see
// CraftingEngine on why a second crafting system that made armour out of ore
// was removed rather than rebalanced. `initializeCrafting` above is the one
// that remains: it drives the real 31-recipe tool tree.

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
  match?: { sameBase: boolean; sameRarity: boolean },
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
  // ForgeSplicingEngine requires all three to share a BaseItemId AND a
  // QualityTier. The rarity rule is new; before it, any two sacrifices of any
  // rarity would do, which let a player climb a Legendary on Normal fodder.
  if (match && (!match.sameBase || !match.sameRarity)) {
    return refuse(
      match.sameBase
        ? 'All three items must be the same rarity.'
        : 'All three items must be the same item.',
    );
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
// Character assignment
// ---------------------------------------------------------------------------

/** Guid.Empty. A ChangeActivity carrying this applies to the live session
 *  payload (the legacy single-character path) rather than to a named slot. */
export const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/**
 * Puts one specific character on one specific activity.
 *
 * SimulationEngine routes ChangeActivity by TargetGuid: empty means "whoever
 * is in slot 1", a real character id means that character. The server has
 * supported this since the multi-slot overhaul and no screen ever sent it, so
 * characters 2 and 3 could be bred, housed and aged but never given a job.
 *
 * ValidateChangeActivityRequest DISCONNECTS on an activity id it does not
 * recognise, and CharacterSlotEngine refuses a node another of your own
 * characters already works (NodeOccupied) - the caller is expected to have
 * offered only legal choices, and this refuses the rest rather than letting
 * the session die.
 */
export function assignCharacterActivity(
  characterId: string,
  activityId: number,
  options?: { unlocked?: boolean; takenBy?: string | null },
): CommandOutcome {
  if (!characterId || characterId === EMPTY_GUID) {
    return refuse('That slot has no character in it.');
  }
  if (options?.unlocked === false) {
    return refuse('That character slot is still locked.');
  }
  if (!Number.isInteger(activityId) || activityId < 0) {
    return refuse('Pick something for this character to do.');
  }
  if (activityId > 0 && options?.takenBy) {
    return refuse(`${options.takenBy} is already working that.`);
  }

  connection.send({
    Command: CommandType.ChangeActivity,
    TargetId: activityId,
    TargetGuid: characterId,
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Affix reroll
// ---------------------------------------------------------------------------

/** RerollOperation. One member: the reroll, priced in gold. */
// Modul: ONE REROLL, AND IT COSTS GOLD.
//
// There were three - value, stat and rarity - which split one decision across
// three purchases with two currencies, and made the player pick an axis before
// they could ask for anything. The rarity step was the one most people wanted
// and it was the one priced in Diamonds.
//
// Kept as a list of one rather than collapsed into a bare number so the UI
// keeps its label and hint in the same place it always read them, and so
// bringing a second operation back later is a data change.
export const REROLL_OPERATIONS = [
  {
    kind: 0,
    label: 'Reroll affix',
    currency: 'gold',
    hint: 'Rolls a new stat, a new rarity and a new value, all at once. It can come out worse - that is the gamble. The other affixes on the item are untouched.',
  },
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

// ---------------------------------------------------------------------------
// Guild war, raids and treasury
// ---------------------------------------------------------------------------

/**
 * War supply contribution. Unusually for this wire it carries no TargetId at
 * all: the commodity rides on SecondaryId and the amount on TertiaryId, and
 * the dispatcher simply ignores the command unless the player is in a guild
 * WITH an active war. No validator, so nothing disconnects - but nothing
 * reports either, which is why the screen checks the same preconditions.
 */
export function contributeToWarSupply(
  commodityId: number,
  quantity: number,
  guildWarId: number,
): CommandOutcome {
  if (guildWarId <= 0) return refuse('Your guild is not in a war right now.');
  if (!Number.isInteger(commodityId) || commodityId <= 0) return refuse('Pick a commodity.');
  if (!Number.isInteger(quantity) || quantity <= 0) return refuse('Contribute at least one.');

  connection.send({
    Command: CommandType.ContributeToWarSupply,
    SecondaryId: commodityId,
    TertiaryId: quantity,
  });
  return OK;
}

/**
 * Raids carry NO payload - the guild is read from the requesting player's own
 * state, and leader-only enforcement happens server-side against the locked
 * GuildMembers row. A non-leader's request rolls back with no effect and no
 * message, so the screen says as much rather than implying it worked.
 */
export function launchGuildRaid(hasGuild: boolean): CommandOutcome {
  if (!hasGuild) return refuse('Join a guild first.');
  connection.send({ Command: CommandType.LaunchGuildRaid });
  return OK;
}

/**
 * Mirrors ValidateGuildTreasuryContribution, which DISCONNECTS when the player
 * has no guild, or when a gold contribution carries a non-positive amount.
 *
 * TargetId == 0 means "this is gold, the amount is on LimitPrice"; anything
 * else is an equipment instance id. Two different meanings for one field
 * again, distinguished only by whether it is zero.
 */
export function contributeGuildGold(amount: number, hasGuild: boolean): CommandOutcome {
  if (!hasGuild) return refuse('Join a guild first.');
  if (!Number.isInteger(amount) || amount <= 0) return refuse('Contribute at least one gold.');

  connection.send({
    Command: CommandType.ContributeGuildTreasury,
    TargetId: 0,
    LimitPrice: amount,
  });
  return OK;
}

/**
 * Guild depot deposit. Mirrors ValidateGuildDepositRequest, which DISCONNECTS
 * when the player has no guild, on a zero quantity, on a material id above
 * ContentRegistry.ItemDefinitions.Length, or when any of FOURTEEN unrelated
 * fields is non-zero - so this sends exactly two.
 *
 * Note this is a different command from `contributeToGuildStock` below despite
 * both putting materials into a guild: this one addresses the depot through
 * MaterialId/DepositQuantity, that one the logistics chain through
 * TargetId/LimitPrice. They are validated by different code with different
 * rules and reach different engines.
 */
export function depositGuildMaterial(
  materialId: number,
  quantity: number,
  hasGuild: boolean,
  itemDefinitionCount = 0,
): CommandOutcome {
  if (!hasGuild) return refuse('Join a guild first.');
  if (!Number.isInteger(materialId) || materialId <= 0) return refuse('Pick a material.');
  if (itemDefinitionCount > 0 && materialId > itemDefinitionCount) {
    return refuse('That material does not exist.');
  }
  if (!Number.isInteger(quantity) || quantity <= 0) return refuse('Deposit at least one.');

  connection.send({
    Command: CommandType.DepositGuildMaterial,
    MaterialId: materialId,
    DepositQuantity: quantity,
  });
  return OK;
}

/**
 * Guild logistics contribution. Mirrors ValidateGuildContributions, which only
 * checks the quantity - but the dispatcher additionally ignores the command
 * outright unless the player is in a guild, saying nothing, so that is checked
 * here too rather than letting the click do nothing.
 */
export function contributeToGuildStock(
  itemDefinitionId: number,
  quantity: number,
  hasGuild: boolean,
): CommandOutcome {
  if (!hasGuild) return refuse('Join a guild first.');
  if (!Number.isInteger(itemDefinitionId) || itemDefinitionId <= 0) return refuse('Pick an item.');
  if (!Number.isInteger(quantity) || quantity <= 0) return refuse('Contribute at least one.');

  connection.send({
    Command: CommandType.ContributeToGuild,
    TargetId: itemDefinitionId,
    LimitPrice: quantity,
  });
  return OK;
}

/**
 * Volunteers this player's roster as the guild's war defence. Mirrors
 * ValidateGuildWarAction's defence branch, which disconnects on no guild, a
 * quarantined account, or any of TargetMatchUuid/ClientPredictedDamage/IsBuy
 * being set - the three fields that belong to its sibling below. So this
 * deliberately sends a bare command.
 */
export function registerGuildDefense(hasGuild: boolean, quarantined: boolean): CommandOutcome {
  if (!hasGuild) return refuse('Join a guild first.');
  if (quarantined) return refuse('Your account is restricted.');

  connection.send({ Command: CommandType.RegisterGuildDefense });
  return OK;
}

/** ValidateGuildWarAction's own damage ceiling for a shard attack. */
export const MAX_SHARD_ATTACK_DAMAGE = 100_000_000;

/**
 * Cross-shard war attack. Mirrors ValidateGuildWarAction's attack branch: an
 * empty match uuid, zero or excessive damage, or a uuid that disagrees with
 * the player's own ActiveCrossShardMatchId all disconnect.
 *
 * That last check is the subtle one - once you are IN a match you may only
 * attack THAT match, so a stale match id left in a screen's state after the
 * war rolls over kills the session on the next click.
 */
export function submitShardAttack(options: {
  matchUuid: string;
  predictedDamage: number;
  hasGuild: boolean;
  quarantined: boolean;
  /** payload.ActiveCrossShardMatchId. All-zero uuid means "not in a match yet". */
  activeMatchUuid: string;
}): CommandOutcome {
  const { matchUuid, hasGuild, quarantined, activeMatchUuid } = options;
  const EMPTY_UUID = '00000000-0000-0000-0000-000000000000';

  if (!hasGuild) return refuse('Join a guild first.');
  if (quarantined) return refuse('Your account is restricted.');
  if (!matchUuid || matchUuid === EMPTY_UUID) return refuse('Pick a war target.');
  if (activeMatchUuid && activeMatchUuid !== EMPTY_UUID && activeMatchUuid !== matchUuid) {
    return refuse('You are already committed to a different match.');
  }

  const damage = Math.trunc(options.predictedDamage);
  if (!Number.isFinite(damage) || damage <= 0 || damage > MAX_SHARD_ATTACK_DAMAGE) {
    return refuse('Cannot estimate your damage right now.');
  }

  connection.send({
    Command: CommandType.SubmitShardAttack,
    TargetMatchUuid: matchUuid,
    ClientPredictedDamage: damage,
  });
  return OK;
}

/**
 * One turn of a guild-versus-guild simulated battle. Mirrors
 * ValidateCombatTurnRequest: no guild, a zero MatchId, or any of fourteen
 * unrelated fields being non-zero disconnects - and unusually, so does the
 * SERVER'S OWN RESULT: if ExecuteCombatTurnAsync returns InvalidRequest or
 * NotFound it force-disconnects after the fact. A turn submitted against a
 * match that has already ended therefore kills the session even though nothing
 * about the packet was malformed, which is why the screen must stop sending
 * the moment a match leaves the state snapshot.
 */
export function executeCombatTurn(
  matchId: number,
  predictedTurnCounter: number,
  hasGuild: boolean,
): CommandOutcome {
  if (!hasGuild) return refuse('Join a guild first.');
  if (!Number.isInteger(matchId) || matchId <= 0) return refuse('No battle is running.');

  const turn = Math.max(0, Math.trunc(predictedTurnCounter));
  if (turn > 0x7fffffff) return refuse('Battle state is out of date - reopen the screen.');

  connection.send({
    Command: CommandType.ExecuteCombatTurn,
    MatchId: matchId,
    ClientPredictedTurnCounter: turn,
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Mentorship
// ---------------------------------------------------------------------------

/**
 * Mirrors ValidateMentorshipRequest, which DISCONNECTS on four separate
 * conditions - and three of them are about fields the screen never sets:
 * a zero target, targeting YOURSELF, a non-zero MentorshipRole, or either Guid
 * field being non-empty. The last two exist to reject a tampered packet, so
 * this function deliberately sends nothing but the target.
 */
function mentorshipCommand(command: number, counterpartyPlayerId: number): CommandOutcome {
  if (!Number.isInteger(counterpartyPlayerId) || counterpartyPlayerId <= 0) {
    return refuse('Pick a player.');
  }
  if (counterpartyPlayerId === connection.currentPlayerId) {
    return refuse('You cannot mentor yourself.');
  }

  connection.send({ Command: command, TargetPlayerId: counterpartyPlayerId });
  return OK;
}

export function establishMentorship(counterpartyPlayerId: number): CommandOutcome {
  return mentorshipCommand(CommandType.EstablishMentorship, counterpartyPlayerId);
}

export function terminateMentorship(counterpartyPlayerId: number): CommandOutcome {
  return mentorshipCommand(CommandType.TerminateMentorship, counterpartyPlayerId);
}

/** ValidateMentorshipAssignment's hard slot ceiling, independent of Academy level. */
export const MAX_MENTOR_SLOTS = 5;

/**
 * Seats one of your own characters in an Academy mentor slot. A different
 * command from establishMentorship above, which pairs you with another PLAYER
 * - this one places a CHARACTER, and the two are easy to confuse by name.
 *
 * Mirrors ValidateMentorshipAssignment, which disconnects on an empty
 * character guid, a slot outside 0-4, an Academy level of 0, or a slot index
 * at or above the Academy level. The last rule is the interesting one: the
 * Academy's level IS the number of slots it has, so a level 2 Academy has
 * slots 0 and 1 and clicking slot 2 ends the session.
 *
 * The slot rides on LimitPrice - the market-price field again, a fourth
 * meaning for it.
 */
export function assignMentor(
  characterId: string,
  slotIndex: number,
  academyLevel: number,
): CommandOutcome {
  if (!characterId) return refuse('Pick a character.');
  if (academyLevel <= 0) return refuse('Build a Mentorship Academy in your village first.');
  if (!Number.isInteger(slotIndex) || slotIndex < 0 || slotIndex >= MAX_MENTOR_SLOTS) {
    return refuse(`Slot must be 0-${MAX_MENTOR_SLOTS - 1}.`);
  }
  if (slotIndex >= academyLevel) {
    return refuse(`Your Academy is level ${academyLevel}, so it has ${academyLevel} slot(s).`);
  }

  connection.send({
    Command: CommandType.AssignMentor,
    TargetGuid: characterId,
    LimitPrice: slotIndex,
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Gathering tools
// ---------------------------------------------------------------------------

/**
 * Upgrades the gathering tool one tier. Carries NO payload at all - the
 * dispatcher passes a literal 0 to ValidateUpgradeRequest and the engine reads
 * everything else from the player's own record, so there is nothing to pick
 * and nothing to get wrong on the wire.
 *
 * The tier ceiling and the cost are both server-side, and a request the player
 * cannot afford simply does nothing without reporting - so the screen shows
 * CachedCurrentToolTier and lets the player see whether it moved.
 */
export function upgradeTool(): CommandOutcome {
  connection.send({ Command: CommandType.UpgradeTool });
  return OK;
}

// ---------------------------------------------------------------------------
// Account erasure
// ---------------------------------------------------------------------------

/**
 * Permanently erases the account. THIS IS NOT REVERSIBLE.
 *
 * Two things about it are worth knowing before wiring a button to it:
 *
 * 1. It is interlocked by a hash of the player id and the CURRENT server epoch
 *    (ComputeGdprConfirmationHash), so a purge cannot be replayed from a
 *    captured packet. The client can only compute it from a live StateUpdate.
 *
 * 2. THE HASH MUST MATCH EXACTLY while the surrounding epoch check tolerates
 *    +-5 of drift. So if a checkpoint flush lands between the StateUpdate this
 *    hash was built from and the command arriving, the hash is stale and the
 *    request is refused - by disconnecting.
 *
 * And the disconnect is indistinguishable from success, because the SUCCESS
 * path also calls TerminateSessionForSecurity. The player sees the same closed
 * socket either way. There is no result code to wait for and no way to make
 * one appear from the client side, so the screen must say plainly that signing
 * back in is the only way to learn which happened.
 */
export function triggerGdprPurge(playerId: number, logicEpochCounter: number): CommandOutcome {
  if (!Number.isInteger(playerId) || playerId <= 0) {
    return refuse('Not signed in.');
  }
  if (!Number.isInteger(logicEpochCounter) || logicEpochCounter < 0) {
    return refuse('Waiting for a fresh state update - try again in a moment.');
  }

  connection.send({
    Command: CommandType.TriggerGdprPurge,
    ConfirmationHash: computeGdprConfirmationHash(playerId, logicEpochCounter),
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Progression
// ---------------------------------------------------------------------------

/** Mirrors ValidateAchievementClaimRequest: a quarantined account cannot claim. */
export function claimAchievement(achievementId: number, quarantined: boolean): CommandOutcome {
  if (quarantined) return refuse('Your account is restricted.');
  if (!Number.isInteger(achievementId) || achievementId <= 0) return refuse('Pick an achievement.');

  connection.send({ Command: CommandType.ClaimAchievementReward, TargetAchievementId: achievementId });
  return OK;
}

/** Mirrors ValidateBattlePassClaimRequest: milestone index must be under 50. */
export function claimBattlePassMilestone(milestoneIndex: number, quarantined: boolean): CommandOutcome {
  if (quarantined) return refuse('Your account is restricted.');
  if (!Number.isInteger(milestoneIndex) || milestoneIndex < 0 || milestoneIndex >= 50) {
    return refuse('That milestone does not exist.');
  }

  connection.send({ Command: CommandType.ClaimBattlePassReward, TargetMilestoneIndex: milestoneIndex });
  return OK;
}

// ---------------------------------------------------------------------------
// Skills
// ---------------------------------------------------------------------------

/** ActiveSkillEngine.MaxSkillId. Exactly four skills exist, ids 1-4. */
export const MAX_SKILL_ID = 4;

function skillCommand(command: number, skillId: number): CommandOutcome {
  // ValidateSkillCommand disconnects outside 1..MaxSkillId.
  if (!Number.isInteger(skillId) || skillId < 1 || skillId > MAX_SKILL_ID) {
    return refuse(`Skill id must be 1-${MAX_SKILL_ID}.`);
  }
  connection.send({ Command: command, TargetId: skillId });
  return OK;
}

export function unlockSkill(skillId: number, availablePoints: number): CommandOutcome {
  if (availablePoints <= 0) return refuse('No skill points available.');
  return skillCommand(CommandType.RequestUnlockSkill, skillId);
}

export function castSkill(skillId: number): CommandOutcome {
  return skillCommand(CommandType.RequestCastSkill, skillId);
}

// ---------------------------------------------------------------------------
// Inheritance
// ---------------------------------------------------------------------------

/**
 * The permanent bonuses diamonds buy — see InheritanceRegistry, which this
 * mirrors. Ids and the cost curve are the server's; this restates only the
 * labels and the shape so the screen can price a level without a round trip.
 */
export const INHERITANCE_STATS: readonly { id: number; name: string; blurb: string }[] = [
  { id: 0, name: 'Damage',          blurb: 'Every hit lands harder, in every region.' },
  { id: 1, name: 'Max health',      blurb: 'A deeper pool before auto-eat has to reach for food.' },
  { id: 2, name: 'Experience',      blurb: 'Levels arrive sooner, which is the slowest part of a season.' },
  { id: 3, name: 'Gold',            blurb: 'More from every kill, online and away.' },
  { id: 4, name: 'Gathering yield', blurb: 'More material from the same swing.' },
  { id: 5, name: 'Loot luck',       blurb: 'Better rarity on what drops.' },
];

/** InheritanceRegistry.MaxLevel and PercentPerLevel. */
export const INHERITANCE_MAX_LEVEL = 20;
export const INHERITANCE_PCT_PER_LEVEL = 2;

/** InheritanceRegistry.GetUpgradeCost — 40 diamonds, x1.28 per level. */
export function inheritanceUpgradeCost(currentLevel: number): number {
  if (currentLevel >= INHERITANCE_MAX_LEVEL) return 0;
  return Math.floor(40 * Math.pow(1.28, Math.max(0, currentLevel)));
}

export function purchaseInheritanceLevel(statId: number, currentLevel: number, diamonds: number): CommandOutcome {
  if (!INHERITANCE_STATS.some((s) => s.id === statId)) return refuse('Unknown inheritance stat.');
  if (currentLevel >= INHERITANCE_MAX_LEVEL) return refuse('That bonus is already at its maximum.');

  const cost = inheritanceUpgradeCost(currentLevel);
  if (diamonds < cost) return refuse(`Needs ${cost.toLocaleString()} diamonds; you have ${diamonds.toLocaleString()}.`);

  connection.send({ Command: CommandType.PurchaseInheritanceLevel, TargetId: statId });
  return { ok: true };
}

// ---------------------------------------------------------------------------
// Village
// ---------------------------------------------------------------------------

// Modul: VillageManagementEngine's building ids. Not contiguous by theme -
// 1-4 are the specialist buildings, 5-8 the resource producers, 9-10 the two
// added later - so the list is authored rather than generated from a range.
export const BUILDINGS: readonly { id: number; name: string; stateField: string }[] = [
  { id: 9, name: 'Town Hall', stateField: 'TownHallLevel' },
  { id: 10, name: 'Crafting Workshop', stateField: 'CraftingWorkshopLevel' },
  { id: 1, name: 'Forge', stateField: 'ForgeLevel' },
  { id: 2, name: 'Inn', stateField: 'InnLevel' },
  { id: 3, name: 'Breeding Grounds', stateField: 'BreedingLevel' },
  { id: 4, name: 'Mentorship Academy', stateField: 'AcademyLevel' },
  { id: 5, name: 'Lumberjack', stateField: 'LumberjackLevel' },
  { id: 6, name: 'Quarry', stateField: 'QuarryLevel' },
  { id: 7, name: 'Mine', stateField: 'MineLevel' },
  { id: 8, name: 'Warehouse', stateField: 'WarehouseLevel' },
];

/**
 * Modul: ValidateVillageManagementRequest is the strictest validator on this
 * wire. It DISCONNECTS unless SIXTEEN unrelated fields are all zero, and it
 * additionally requires that an upgrade carries TargetVillagerSlot == 0 while
 * an eviction carries TargetBuildingId == 0.
 *
 * That is an anti-tamper check, so the only safe way to satisfy it is to send
 * exactly one field and nothing else - which is why these two functions build
 * their payload from scratch rather than sharing a helper that might carry a
 * stray default along.
 */
export function upgradeBuilding(buildingId: number): CommandOutcome {
  if (!BUILDINGS.some((b) => b.id === buildingId)) {
    return refuse('Unknown building.');
  }
  connection.send({ Command: CommandType.UpgradeBuilding, TargetBuildingId: buildingId });
  return OK;
}

export function evictVillager(villagerSlot: number): CommandOutcome {
  if (!Number.isInteger(villagerSlot) || villagerSlot < 0) {
    return refuse('Pick a villager.');
  }
  connection.send({ Command: CommandType.EvictVillager, TargetVillagerSlot: villagerSlot });
  return OK;
}

// ---------------------------------------------------------------------------
// Breeding
// ---------------------------------------------------------------------------

/**
 * Mirrors ValidateBreedingRequest, which disconnects when BreedingLevel is 0,
 * when either parent Guid is empty, when THE TWO PARENTS ARE THE SAME, or when
 * any of sixteen unrelated fields is non-zero.
 *
 * Same-parent is the one a UI produces by accident, exactly as with fusion -
 * so the screen excludes each parent from the other's list AND this refuses.
 */
export function executeBreeding(
  paternalId: string,
  maternalId: string,
  breedingLevel: number,
): CommandOutcome {
  if (breedingLevel <= 0) return refuse('Build Breeding Grounds in your village first.');
  if (!paternalId || !maternalId) return refuse('Choose two parents.');
  if (paternalId === maternalId) return refuse('The two parents must be different characters.');

  connection.send({
    Command: CommandType.ExecuteBreeding,
    TargetGuid: paternalId,
    SecondaryGuid: maternalId,
  });
  return OK;
}

// ---------------------------------------------------------------------------
// Monetisation
// ---------------------------------------------------------------------------

/** Spends PremiumDiamonds server-side; no cash IAP hook is involved. */
export function purchaseBattlePass(): CommandOutcome {
  connection.send({ Command: CommandType.PurchaseBattlePass });
  return OK;
}

/**
 * Mirrors ValidateLegacyStoreRequest: fourteen fields must be zero, so this
 * sends TargetUnlockId alone.
 */
export function purchaseLegacyUnlock(unlockId: number): CommandOutcome {
  if (!Number.isInteger(unlockId) || unlockId <= 0) return refuse('Pick an unlock.');
  connection.send({ Command: CommandType.PurchaseLegacyUnlocks, TargetUnlockId: unlockId });
  return OK;
}

/**
 * Mirrors ValidateChronoCommands: TargetId must be positive, fifteen other
 * fields must be zero, and a QUARANTINED account is rejected outright.
 */
export function consumeChronoCore(itemId: number, quarantined: boolean): CommandOutcome {
  if (quarantined) return refuse('Your account is restricted.');
  if (!Number.isInteger(itemId) || itemId <= 0) return refuse('Pick a chrono core.');
  connection.send({ Command: CommandType.ConsumeChronoCore, TargetId: itemId });
  return OK;
}

/** The requested multiplier rides on TargetId. 1 turns acceleration off. */
export function toggleChronoAcceleration(multiplier: number): CommandOutcome {
  if (!Number.isInteger(multiplier) || multiplier < 1) return refuse('Multiplier must be at least 1.');
  connection.send({ Command: CommandType.ToggleChronoAcceleration, TargetId: multiplier });
  return OK;
}
