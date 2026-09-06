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

// Modul: THE BANK VAULT IS RETIRED, and depositToBank/withdrawFromBank are
// gone with it. It was a hundred-slot store that relieved a backpack cap this
// game no longer has, and an item inside it could not be worn, upgraded,
// rerolled or sold - every one of those reads EquipmentInstances. Its rows were
// moved there and the table dropped; see the RetireTheBank migration. Neither
// function had a caller in this client.

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

/** StateUpdatePacket.WorldBossEventState. */
export const BossEventState = { Dormant: 0, Active: 1, Concluded: 2 } as const;

/** WorldBossEngine.PlateCount. */
export const BOSS_PLATE_COUNT = 5;

/** WorldBossEngine.WeakPlateHidden - nobody has found the weak point yet. */
export const BOSS_WEAK_PLATE_HIDDEN = 255;

/** WorldBossEngine.WeakPlateDamageMultiplier. */
export const BOSS_WEAK_PLATE_MULTIPLIER = 3;

/** WorldBossEngine.BattleSessionCapSeconds. */
export const BOSS_SESSION_CAP_SECONDS = 300;

/**
 * Mirrors ValidateWorldBossAttackRequest - and then goes further, because that
 * validator is only half the story.
 *
 * The validator DISCONNECTS on: the event not being active, a plate index
 * outside 0-4, a boss id other than the active one, the boss already being
 * dead, or any of twenty unrelated fields being non-zero - INCLUDING
 * ClientPredictedDamage, which this command stopped carrying on 2026-09-05.
 * The server takes the damage from the player's own attack power now; there is
 * no number here to get wrong or to inflate.
 *
 * ExecuteAttackAsync then SILENTLY ROLLS BACK - no damage, no message, no
 * telemetry the player will ever see - on three further conditions:
 *
 *   - the player has already used all three attempts this encounter
 *   - THE BATTLE SESSION CAP HAS ELAPSED: 300 seconds from the FIRST strike to
 *     spend the other two, inside an encounter that runs for up to seven days
 *   - AUTO-EAT FOOD IS DEPLETED (all three larder slots empty)
 *
 * All three are refused here with an explanation rather than sent into the
 * void. The session cap was the worst of them and went unsaid the longest: the
 * deadline was not on the wire at all until 2026-09-05, so the button stayed
 * enabled and did nothing for the rest of the encounter. An idle player who
 * strikes once and comes back an hour later is the NORMAL case in this genre,
 * and it silently cost them two thirds of their participation.
 */
export function attackWorldBoss(options: {
  /** Which armour plate to strike, 0-4. A choice, not a quantity. */
  plateIndex: number;
  eventState: number;
  bossCurrentHp: number;
  attemptCount: number;
  /** True when Food1_Count, Food2_Count and Food3_Count are all zero. */
  larderEmpty: boolean;
  /** StateUpdatePacket.WorldBossSessionEndsEpoch; 0 before the first strike. */
  sessionEndsEpoch?: number;
  /** Unix seconds. Injected so this stays pure and testable. */
  nowEpoch?: number;
}): CommandOutcome {
  const { plateIndex, eventState, bossCurrentHp, attemptCount, larderEmpty } = options;
  const sessionEndsEpoch = options.sessionEndsEpoch ?? 0;
  const nowEpoch = options.nowEpoch ?? Math.floor(Date.now() / 1000);

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
  if (sessionEndsEpoch > 0 && nowEpoch >= sessionEndsEpoch) {
    return refuse(
      `Your battle session for this encounter has closed - it lasts ${BOSS_SESSION_CAP_SECONDS / 60} minutes from your first strike.`,
    );
  }

  if (!Number.isInteger(plateIndex) || plateIndex < 0 || plateIndex >= BOSS_PLATE_COUNT) {
    return refuse('Pick a plate to strike.');
  }

  connection.send({
    Command: CommandType.AttackWorldBoss,
    TargetedBossId: ACTIVE_BOSS_INSTANCE_ID,
    TargetedPlateIndex: plateIndex,
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

/** The largest batch the server will honour - see CraftingEngine.MaxCraftBatchSize. */
export const MAX_CRAFT_BATCH = 10;

/**
 * Crafting tree. `resultItemId` is ContentRegistry's ResultItemId.
 *
 * Modul: this produces `batchSize` units NOW for `batchSize` times the
 * materials, and is a different act from putting a character to work. Assigning
 * a character to a recipe crafts one unit per interval FOREVER while materials
 * last, which is the right shape for idling and the wrong shape for "I want a
 * pickaxe" - the screen offered only the second, so making one tool meant
 * assigning a worker and then remembering to stop them.
 *
 * The batch rides DepositQuantity, an existing field the crafting opcode does
 * not otherwise read, so this needs no wire change. The server clamps it
 * regardless of what is sent here: batch multiplies cost AND output.
 */
export function startTreeCraft(resultItemId: number, batchSize = 1): CommandOutcome {
  if (!Number.isInteger(resultItemId) || resultItemId <= 0) {
    return refuse('Pick a recipe.');
  }
  if (!Number.isInteger(batchSize) || batchSize < 1 || batchSize > MAX_CRAFT_BATCH) {
    return refuse(`Craft between 1 and ${MAX_CRAFT_BATCH} at a time.`);
  }

  connection.send({
    Command: CommandType.InitializeCrafting,
    TargetId: resultItemId,
    DepositQuantity: batchSize,
  });
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
  match?: { sameBase: boolean; sameRarity: boolean; resultTier: number },
): CommandOutcome {
  if (forgeLevel <= 0) {
    return refuse('Build a Forge in your village first.');
  }
  // Modul: THE FORGE'S LEVEL IS THE RARITY CEILING, and the screen never said
  // so. ClientCommandValidator refuses a fusion whose RESULT would exceed
  // ForgeLevel - so a level-2 Forge can produce rarity 2 and no higher - and
  // that refusal used to tear down the session, which a player reads as "fuse
  // is broken". Caught here, in words, before it is sent.
  if (match && match.resultTier > forgeLevel) {
    return refuse(
      `Your Forge is level ${forgeLevel} and this fusion produces rarity ${match.resultTier}. Upgrade the Forge in your village first.`,
    );
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

/* Modul: upgradeTool() removed - it had no callers and no effect.
   The village screen's tool panel is gone (tools are ordinary equipment now)
   and the server's handler was an empty stub, so this helper sent an opcode
   that validated, routed, and accomplished nothing. Kept as a note rather than
   silently deleted, because the opcode still exists on the wire. */

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
// Skill tree
// ---------------------------------------------------------------------------

/**
 * SkillTreeRegistry. Five passive branches, twenty levels each, bought with the
 * skill points a player earns one per account level.
 *
 * This replaced four ACTIVE skills that had to be clicked. Measured, that
 * rotation was +90% damage - +136% with its status synergy - because mana
 * refilled faster than the cooldowns cleared, so nearly every swing could be
 * buffed. In an idle game that made an attentive player twice as fast as an
 * idle one, and the pacing model knew about neither.
 *
 * Mirrors the server table rather than restating it: the magnitudes below are
 * SkillTreeRegistry's, and the cost curve is its GetUpgradeCost.
 */
export const SKILL_TREE_ROOT_MAX = 10;
export const SKILL_TREE_BOUGH_MAX = 8;
export const SKILL_TREE_CROWN_MAX = 1;
export const SKILL_TREE_BOUGH_COST = 2;
export const SKILL_TREE_CROWN_COST = 12;
export const SKILL_TREE_BOUGH_NEEDS_ROOT = 5;
export const SKILL_TREE_CROWN_NEEDS_BOUGH = 5;

/** Kept as the old name for the root cap, which is all any caller meant. */
export const SKILL_TREE_MAX_LEVEL = SKILL_TREE_ROOT_MAX;

export type SkillRing = 'root' | 'bough' | 'crown';

export const SKILL_TREE_NODES: readonly {
  id: number;
  ring: SkillRing;
  /** The root this node hangs from; itself for a root. */
  root: number;
  name: string;
  blurb: string;
  /** Percent added per level. Crit chance is percentage POINTS. */
  perLevel: number;
  unit: 'pct' | 'points' | 'special';
}[] = [
  // roots
  { id: 0, ring: 'root', root: 0, name: 'Fortune', blurb: 'Better rarity on what drops. Not more loot - better loot.', perLevel: 1.0, unit: 'pct' },
  { id: 1, ring: 'root', root: 1, name: 'Giantslayer', blurb: 'Every blow against a world boss lands harder.', perLevel: 2.0, unit: 'pct' },
  { id: 2, ring: 'root', root: 2, name: 'Precision', blurb: 'More of your hits are critical ones.', perLevel: 0.4, unit: 'points' },
  { id: 3, ring: 'root', root: 3, name: 'Cruelty', blurb: 'Your critical hits take a larger bite.', perLevel: 3.0, unit: 'pct' },
  { id: 4, ring: 'root', root: 4, name: 'Insight', blurb: 'Levels arrive sooner, which is the slowest part of a season.', perLevel: 0.4, unit: 'pct' },

  // boughs, two per root - only ONE of each pair may ever be levelled
  { id: 5, ring: 'bough', root: 0, name: 'Plenty', blurb: 'Materials drop in bigger stacks. Crafting eats stacks.', perLevel: 1.5, unit: 'pct' },
  { id: 6, ring: 'bough', root: 0, name: 'Rarity', blurb: 'A drop has a chance to roll one rarity higher than it should.', perLevel: 1.0, unit: 'pct' },
  { id: 7, ring: 'bough', root: 1, name: 'First Blood', blurb: 'A boss you have never beaten is less monstrous the first time.', perLevel: 4.0, unit: 'pct' },
  { id: 8, ring: 'bough', root: 1, name: 'Trophy Hunter', blurb: 'Bosses pay more gold, and always leave a material behind.', perLevel: 2.5, unit: 'pct' },
  { id: 9, ring: 'bough', root: 2, name: 'Guile', blurb: 'Critical hits bite deeper still.', perLevel: 3.0, unit: 'pct' },
  { id: 10, ring: 'bough', root: 2, name: 'Relentless', blurb: 'You swing faster. Everything else scales off how often you hit.', perLevel: 1.0, unit: 'pct' },
  { id: 11, ring: 'bough', root: 3, name: 'Bloodthirst', blurb: 'A share of the damage you deal comes back as health - food you never had to cook.', perLevel: 0.5, unit: 'pct' },
  { id: 12, ring: 'bough', root: 3, name: 'Fortitude', blurb: 'More health and more armour. The difference between a wall and a grind.', perLevel: 2.0, unit: 'pct' },
  { id: 13, ring: 'bough', root: 4, name: 'Craft', blurb: 'A craft sometimes costs you nothing at all - the materials stay in the sack.', perLevel: 2.5, unit: 'pct' },
  { id: 14, ring: 'bough', root: 4, name: 'Harvest', blurb: 'Gathering finishes sooner, and sometimes yields twice.', perLevel: 2.0, unit: 'pct' },

  // crowns, one per root
  { id: 15, ring: 'crown', root: 0, name: 'Golden Fleece', blurb: 'Every hundredth kill drops an item two rarity tiers above its due.', perLevel: 0, unit: 'special' },
  { id: 16, ring: 'crown', root: 1, name: 'Thunderer', blurb: 'You open a boss fight with a free blow at five times your weapon.', perLevel: 0, unit: 'special' },
  { id: 17, ring: 'crown', root: 2, name: 'Double Strike', blurb: 'A critical hit has a chance to land a second time.', perLevel: 0, unit: 'special' },
  { id: 18, ring: 'crown', root: 3, name: 'Last Stand', blurb: 'Once an hour, the blow that would kill you leaves you at one health.', perLevel: 0, unit: 'special' },
  { id: 19, ring: 'crown', root: 4, name: 'Scholar', blurb: 'Everything you earn while away comes in a quarter faster.', perLevel: 0, unit: 'special' },
];

/** Kept for anything that still only wants the five roots. */
export const SKILL_TREE_BRANCHES = SKILL_TREE_NODES.filter((n) => n.ring === 'root');

/**
 * Nodes whose EFFECT is not wired up on the server yet.
 *
 * Mirrors SkillTreeRegistry.EffectPending, and exists for the same reason: a
 * player must never spend a real resource on a bonus that quietly does
 * nothing. This codebase has shipped that defect more than once - crafting
 * that granted nothing, loot that went dead after twenty kills, gather-speed
 * affixes computed and never read. All of them looked finished.
 *
 * Remove an id here in the same commit that wires its effect, never before.
 */
export const SKILL_TREE_EFFECT_PENDING: readonly number[] = [];

export function isSkillEffectPending(nodeId: number): boolean {
  return SKILL_TREE_EFFECT_PENDING.includes(nodeId);
}

export function skillRingOf(nodeId: number): SkillRing {
  if (nodeId >= 15) return 'crown';
  if (nodeId >= 5) return 'bough';
  return 'root';
}

export function skillNodeMaxLevel(nodeId: number): number {
  const ring = skillRingOf(nodeId);
  if (ring === 'root') return SKILL_TREE_ROOT_MAX;
  if (ring === 'bough') return SKILL_TREE_BOUGH_MAX;
  return SKILL_TREE_CROWN_MAX;
}

/** The other bough on the same fork - the one taking this node locks. */
export function siblingBoughOf(nodeId: number): number {
  if (skillRingOf(nodeId) !== 'bough') return -1;
  const offset = nodeId - 5;
  return 5 + (offset % 2 === 0 ? offset + 1 : offset - 1);
}

export function boughsOfRoot(rootId: number): [number, number] {
  return [5 + rootId * 2, 6 + rootId * 2];
}

export function crownOfRoot(rootId: number): number {
  return 15 + rootId;
}

/** SkillTreeRegistry.GetUpgradeCost. */
export function skillTreeUpgradeCost(nodeId: number, currentLevel: number): number {
  if (currentLevel < 0 || currentLevel >= skillNodeMaxLevel(nodeId)) return 0;
  const ring = skillRingOf(nodeId);
  if (ring === 'root') return Math.floor(currentLevel / 5) + 1;
  if (ring === 'bough') return SKILL_TREE_BOUGH_COST;
  return SKILL_TREE_CROWN_COST;
}

/**
 * Mirrors SkillTreeRegistry.BlockedReason.
 *
 * A REASON rather than a bool, because every one of these is something the
 * player needs told. A node greyed out without a cause is a node nobody can
 * plan against, and four of the five reasons here are recoverable.
 */
export function skillNodeBlockedReason(
  nodeId: number,
  levels: readonly number[],
  availablePoints: number,
): string | null {
  const node = SKILL_TREE_NODES.find((n) => n.id === nodeId);
  if (!node) return 'No such skill.';

  if (isSkillEffectPending(nodeId)) return 'Not in the game yet - coming soon.';

  const level = levels[nodeId] ?? 0;
  if (level >= skillNodeMaxLevel(nodeId)) return 'Already at its limit.';

  if (node.ring === 'bough') {
    if ((levels[node.root] ?? 0) < SKILL_TREE_BOUGH_NEEDS_ROOT) {
      return `Needs ${SKILL_TREE_NODES[node.root].name} at ${SKILL_TREE_BOUGH_NEEDS_ROOT}.`;
    }
    const sibling = siblingBoughOf(nodeId);
    if ((levels[sibling] ?? 0) > 0) {
      return `${SKILL_TREE_NODES[sibling].name} was taken instead. One branch per fork.`;
    }
  } else if (node.ring === 'crown') {
    const [a, b] = boughsOfRoot(node.root);
    if (Math.max(levels[a] ?? 0, levels[b] ?? 0) < SKILL_TREE_CROWN_NEEDS_BOUGH) {
      return `Needs a branch of ${SKILL_TREE_NODES[node.root].name} at ${SKILL_TREE_CROWN_NEEDS_BOUGH}.`;
    }
  }

  const cost = skillTreeUpgradeCost(nodeId, level);
  if (availablePoints < cost) return `Costs ${cost} points; you have ${availablePoints}.`;

  return null;
}

/**
 * Why a respec cannot happen, or null if it can. Mirrors
 * SkillTreeEngine.RespecBlockedReason.
 *
 * A respec exists because ring 2 forks and taking one side locks the other for
 * a NINETY-DAY season - far too long to live with a misclick. It is limited
 * because a free and unlimited one would delete that exclusivity, which is the
 * only real choice the tree has.
 */
export function respecBlockedReason(freeRespecUsed: boolean, paidGrants: number): string | null {
  if (!freeRespecUsed) return null;
  if (paidGrants > 0) return null;
  return 'You have used this season’s free respec.';
}

export function respecSkillTree(freeRespecUsed: boolean, paidGrants: number): CommandOutcome {
  const blocked = respecBlockedReason(freeRespecUsed, paidGrants);
  if (blocked) return refuse(blocked);

  connection.send({ Command: CommandType.RespecSkillTree });
  return OK;
}

export function purchaseSkillTreeLevel(
  nodeId: number,
  levels: readonly number[],
  availablePoints: number,
): CommandOutcome {
  const blocked = skillNodeBlockedReason(nodeId, levels, availablePoints);
  if (blocked) return refuse(blocked);

  connection.send({ Command: CommandType.PurchaseSkillTreeLevel, TargetId: nodeId });
  return OK;
}

// ---------------------------------------------------------------------------
// Attribute points
// ---------------------------------------------------------------------------

/**
 * The four attributes, in the order the server switches on
 * (SimulationEngine's SpendAttributePoint handler: 0 STR, 1 DEX, 2 CON, 3 LCK).
 *
 * `effect` is what StatsCalculator actually does with a point, quoted so the
 * screen can say it. A player asked to allocate a stat has to be told what it
 * buys, and until this existed the four attributes had never once been
 * explained anywhere in the game.
 */
export const ATTRIBUTES: readonly {
  id: number;
  key: 'STR' | 'DEX' | 'CON' | 'LCK';
  label: string;
  tagline: string;
  /** Per-point effects, for the card. */
  effects: readonly string[];
  /** A colour identity, so the four are distinguishable at a glance. */
  accent: string;
  start: number;
}[] = [
  {
    id: 0,
    key: 'STR',
    label: 'Might',
    tagline: 'Hits hard, and through armour.',
    effects: ['+2 attack power', '+1 armour penetration'],
    accent: '#d9694a',
    start: 50,
  },
  {
    id: 1,
    key: 'DEX',
    label: 'Finesse',
    tagline: 'Hits often, and precisely.',
    effects: ['+1 accuracy', 'crit chance', 'attack speed'],
    accent: '#4aa3d9',
    start: 50,
  },
  {
    id: 2,
    key: 'CON',
    label: 'Vigour',
    tagline: 'Survives being hit.',
    effects: ['+15 max health', '+1 armour', 'block strength', 'health regen'],
    accent: '#5fbf6a',
    start: 50,
  },
  {
    id: 3,
    key: 'LCK',
    label: 'Fortune',
    tagline: 'Takes more from the world.',
    effects: ['loot luck', 'forge success'],
    accent: '#c9a227',
    start: 25,
  },
];

/**
 * AttributeRegistry.Thresholds and .Milestones, mirrored.
 *
 * A mirror rather than a wire field because it is a static table the server
 * never changes at runtime, and StateUpdatePacket is a fixed-layout struct with
 * a size guard - twenty rows of names and magnitudes do not belong on it.
 * serverMirrors.test.ts parses the C# and compares both, element by element.
 */
export const ATTRIBUTE_THRESHOLDS: readonly number[] = [25, 60, 120, 200, 300];

export const ATTRIBUTE_MILESTONES: readonly {
  attribute: number;
  threshold: number;
  name: string;
  effect: string;
}[] = [
  { attribute: 0, threshold: 25, name: 'Heavy Hands', effect: '+5% attack power' },
  { attribute: 0, threshold: 60, name: 'Sunder', effect: '+40 armour penetration' },
  { attribute: 0, threshold: 120, name: 'Executioner', effect: '+8% attack power' },
  { attribute: 0, threshold: 200, name: "Titan's Grip", effect: '+80 armour penetration' },
  { attribute: 0, threshold: 300, name: 'Worldbreaker', effect: '+12% attack power' },

  { attribute: 1, threshold: 25, name: 'Quick Step', effect: '+3% attack speed' },
  { attribute: 1, threshold: 60, name: 'Keen Eye', effect: '+25 accuracy' },
  { attribute: 1, threshold: 120, name: 'Deadly Precision', effect: '+15% crit damage' },
  { attribute: 1, threshold: 200, name: 'Flurry', effect: '+4% attack speed' },
  { attribute: 1, threshold: 300, name: 'Perfect Form', effect: '+25% crit damage' },

  { attribute: 2, threshold: 25, name: 'Hardy', effect: '+5% max health' },
  { attribute: 2, threshold: 60, name: 'Thick Skin', effect: '+10% armour' },
  { attribute: 2, threshold: 120, name: 'Second Wind', effect: '+2.0 health regen a second' },
  { attribute: 2, threshold: 200, name: 'Ironhide', effect: '+8% max health' },
  { attribute: 2, threshold: 300, name: 'Unbreakable', effect: '+25% crit mitigation' },

  { attribute: 3, threshold: 25, name: 'Scavenger', effect: '+8% loot luck' },
  { attribute: 3, threshold: 60, name: 'Prospector', effect: '+5% gathering yield' },
  { attribute: 3, threshold: 120, name: 'Lucky Strike', effect: '+2% crit chance' },
  { attribute: 3, threshold: 200, name: 'Golden Touch', effect: '+8% gold' },
  { attribute: 3, threshold: 300, name: "Fortune's Favour", effect: '+8% forge success' },
];

/**
 * AttributeRegistry's curves, mirrored so a card can preview what the next
 * point buys. Square root, matching the server exactly.
 */
export const ATTRIBUTE_CURVES = {
  critChancePerRootPoint: 1.5,
  attackSpeedPerRootPoint: 0.8,
  blockStrengthPerRootPoint: 0.6,
  lootLuckPerRootPoint: 1.2,
  forgeSuccessPerRootPoint: 0.6,
} as const;

export function diminishedPercent(perRootPoint: number, value: number): number {
  return value <= 0 ? 0 : perRootPoint * Math.sqrt(value);
}

/** Refund every placed point. Free - see the server handler for why. */
export function respecAttributes(): CommandOutcome {
  connection.send({ Command: CommandType.RespecAttributes });
  return OK;
}

/**
 * Spend attribute points earned by levelling.
 *
 * The server owns the balance: it re-checks the amount against its own copy and
 * refuses what it cannot pay, so this validation is for the player's benefit
 * rather than the server's protection.
 */
export function spendAttributePoint(attributeId: number, amount: number, available: number): CommandOutcome {
  if (!ATTRIBUTES.some((a) => a.id === attributeId)) return refuse('Unknown attribute.');
  if (!Number.isInteger(amount) || amount <= 0) return refuse('Pick how many points to spend.');
  if (amount > available) {
    return refuse(`You have ${available.toLocaleString()} point${available === 1 ? '' : 's'} to spend.`);
  }

  // Which attribute rides on TargetId and the amount on LimitPrice - the same
  // two general-purpose fields RerollItemAffix reuses, rather than growing a
  // fixed-layout packet for four bytes.
  connection.send({ Command: CommandType.SpendAttributePoint, TargetId: attributeId, LimitPrice: amount });
  return OK;
}

// ---------------------------------------------------------------------------
// Breeding aptitudes
// ---------------------------------------------------------------------------

/**
 * Mirrors BreedingAptitudes. Four values a bloodline carries across seasons -
 * the only axis in this game that a rollover does not wipe.
 */
export const APTITUDE_MAX = 50;
export const APTITUDE_VILLAGE_CEILING = 20;

export const APTITUDES: readonly {
  field: string;
  name: string;
  blurb: string;
}[] = [
  { field: 'Aptitude_Strength', name: 'Strength', blurb: 'Every blow lands harder' },
  { field: 'Aptitude_Skill', name: 'Skill', blurb: 'Gathering and crafting finish sooner' },
  { field: 'Aptitude_Endurance', name: 'Endurance', blurb: 'A deeper health pool' },
  { field: 'Aptitude_Fortune', name: 'Fortune', blurb: 'Better rarity on what drops' },
];

/**
 * BreedingAptitudes.BonusPercentFor - three diminishing bands.
 *
 * Flat 1.5% to a cap of 50 would be +75% in one domain, which would make a
 * shared seasonal leaderboard a function of account age rather than of how the
 * season was played. The bands land at +30% / +40.5% / +45%.
 */
export function aptitudeBonusPercent(points: number): number {
  if (points <= 0) return 0;
  const p = Math.min(points, APTITUDE_MAX);

  let total = Math.min(p, 20) * 1.5;
  if (p > 20) total += (Math.min(p, 35) - 20) * 0.7;
  if (p > 35) total += (p - 35) * 0.3;
  return total;
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
/**
 * Modul: WHAT EACH BUILDING DOES AND WHAT IT COSTS, in the table the screen
 * already reads.
 *
 * Reported from play: "the village does not say what any upgrade costs, or
 * what the building does" - and it did not, so raising something was a
 * gamble with an invisible price. Both are server rules, mirrored here
 * because the wire carries levels and not explanations:
 *
 *   - service buildings (Forge/Inn/Breeding) cost GOLD on 100 * 1.5^level,
 *     plus logs and ore on 100 * 1.5^level - VillageManagementEngine
 *   - production buildings (Lumberjack/Quarry/Mine/Warehouse) cost wood and
 *     stone on the same curve
 *   - structural buildings (Town Hall / Crafting Workshop) cost logs and ore,
 *     and the Workshop additionally a rare log
 *
 * The Mentorship Academy is NOT in this list any more: the feature was
 * removed and the server refuses the building id.
 */
export type CostKind = 'service' | 'production' | 'structural';

export const BUILDINGS: readonly {
  id: number;
  name: string;
  stateField: string;
  costKind: CostKind;
  what: string;
}[] = [
  {
    id: 9,
    name: 'Town Hall',
    stateField: 'TownHallLevel',
    costKind: 'structural',
    what: 'Raises the level ceiling every other building is allowed to reach, and unlocks character slots.',
  },
  {
    id: 10,
    name: 'Crafting Workshop',
    stateField: 'CraftingWorkshopLevel',
    costKind: 'structural',
    what: 'Unlocks higher crafting recipe tiers.',
  },
  {
    id: 1,
    name: 'Forge',
    stateField: 'ForgeLevel',
    costKind: 'service',
    what: 'Its level is the rarity ceiling for fusion: a level 5 Forge can fuse up to rarity 5 and no further.',
  },
  {
    id: 2,
    name: 'Inn',
    stateField: 'InnLevel',
    costKind: 'service',
    // Modul: THIS SAID THE WRONG THING ENTIRELY. "Houses villagers, who work
    // the production buildings" describes the old identity-less work-slot
    // table, not the gene pool. The Inn is the single lever behind breeding:
    // it sets the arrival interval (48h - 2h a level, floor 24h), the village's
    // capacity (6 + level, cap 16) AND how good a newcomer's aptitudes roll
    // (2 + random up to the level, ceiling 20). See VillagerArrivalRules and
    // BreedingAptitudes.RollVillager.
    what: 'Newcomers arrive sooner, the village holds more of them, and their aptitudes roll higher - up to 20. This is the whole of your gene pool.',
  },
  {
    id: 3,
    name: 'Breeding Grounds',
    stateField: 'BreedingLevel',
    costKind: 'service',
    // Breeding refuses a mixed-race pair outright, so it cannot make a race
    // rarer - the old blurb promised something the engine forbids.
    what: 'Required to breed at all. Pairs a level-50 adult hero with a newcomer or with another of your own.',
  },
  {
    id: 5,
    name: 'Lumberjack',
    stateField: 'LumberjackLevel',
    costKind: 'production',
    what: 'Produces wood on its own, and speeds up your own woodcutting by 5% a level.',
  },
  {
    id: 7,
    name: 'Mine',
    stateField: 'MineLevel',
    costKind: 'production',
    what: 'Produces iron on its own, and speeds up your own mining by 5% a level.',
  },
  {
    id: 8,
    name: 'Warehouse',
    stateField: 'WarehouseLevel',
    costKind: 'production',
    what: 'Caps how much the production buildings can stockpile: 1,000 per level.',
  },
];

/** Mirrors VillageManagementEngine.CalculateUpgradeCost - gold. */
export function villageGoldCost(currentLevel: number): number {
  // Modul: 500 and 1.4, softened with the server. A level-10 building was
  // 57,665 gold on the old curve - over two hours of region-2 income for one
  // level, on top of the logs and ore it also costs now.
  return Math.ceil(500 * Math.pow(1.4, Math.max(0, currentLevel)));
}

/** Mirrors VillageManagementEngine.CalculateProductionUpgradeCost - materials. */
export function villageMaterialCost(currentLevel: number): number {
  const levelInTier = currentLevel % 5;
  return Math.ceil(100 * Math.pow(1.5, Math.max(0, levelInTier)));
}

/** What the next level of this building will take, in words. */
export function villageCostLabel(costKind: CostKind, currentLevel: number): string {
  const materials = villageMaterialCost(currentLevel).toLocaleString();
  const tier = Math.floor(currentLevel / 5);
  
  // Modul: THESE MUST MATCH VillageManagementEngine.TierMaterials. This is a
  // display copy of a server-side table and there is no generator keeping them
  // together, so it is the kind of thing that goes quietly wrong - it already
  // did. The ore column read Copper / Iron / Silver, the legacy gathering
  // slugs, which is what the village used to charge and what no player could
  // obtain; the server now charges the catalogued ores and this says so.
  const tierLogs = ["Birch Log", "Willow Log", "Acacia Log", "Frostpine Log", "Ebon Log"];
  const tierOres = ["Malachite Ore", "Hematite Ore", "Sulfur Ore", "Cobalt Ore", "Darksteel Ore"];

  const logName = tier < tierLogs.length ? tierLogs[tier] : "Ebon Log";
  const oreName = tier < tierOres.length ? tierOres[tier] : "Darksteel Ore";

  // Structural buildings (Town Hall, Crafting Workshop) are the only ones that
  // cost no gold - everything else does now, so the label says so rather than
  // quoting a price the server will not charge.
  if (costKind === 'structural') return `${materials} ${logName} + ${materials} ${oreName}`;
  return `${villageGoldCost(currentLevel).toLocaleString()}g + ${materials} ${logName} + ${materials} ${oreName}`;
}

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

/**
 * Throws a feast and attracts somebody now.
 *
 * Carries NO fields: the price escalates off a counter only the server has,
 * so there is nothing honest a packet could say beyond "somebody, now" -
 * and ValidateVillageRosterRequest disconnects on a non-zero TargetId.
 *
 * Whether it is affordable is the newcomers endpoint's answer
 * (RecruitBlockedReason), not this function's: the server checks gold and the
 * population cap inside the same transaction that spends them, and a second
 * opinion here could only be a stale one.
 */
export function recruitVillager(): CommandOutcome {
  connection.send({ Command: CommandType.RecruitVillager });
  return OK;
}

/** Turns somebody away, freeing a slot. Elders are refused server-side - they
 * married into the line and are a record of it, not a resident. */
export function dismissNewcomer(newcomerId: number): CommandOutcome {
  if (!Number.isInteger(newcomerId) || newcomerId <= 0) {
    return refuse('Pick somebody to send on their way.');
  }
  connection.send({ Command: CommandType.DismissNewcomer, TargetId: newcomerId });
  return OK;
}

// ---------------------------------------------------------------------------
// The Hall of Ancestors
// ---------------------------------------------------------------------------

/** Buys one of the four extra roster slots with diamonds. Carries no fields;
 * ValidateHallOfAncestorsRequest disconnects on a target. */
export function purchaseAncestorSlot(): CommandOutcome {
  connection.send({ Command: CommandType.PurchaseAncestorSlot });
  return OK;
}

/**
 * Marks a member as one to carry through the rollover, or unmarks them.
 *
 * Two commands rather than a toggle, following AddFriend/RemoveFriend: a
 * toggle that arrives twice undoes itself, and a dropped acknowledgement is
 * not a reason to lose a bloodline.
 */
export function setAncestorKept(characterId: string, kept: boolean): CommandOutcome {
  if (!characterId) return refuse('Pick somebody.');
  connection.send({
    Command: kept ? CommandType.KeepAncestor : CommandType.ReleaseAncestor,
    TargetGuid: characterId,
  });
  return OK;
}

/**
 * Puts a member into one of the three playable slots, swapping out whoever was
 * there.
 *
 * Nothing in the server could change a SlotIndex before this existed, so every
 * child bred past the third slot was unplayable. The Town Hall gate on slots 2
 * and 3 is checked server-side; sending a locked slot is refused, not punished.
 */
export function assignCharacterSlot(characterId: string, slotIndex: number): CommandOutcome {
  if (!characterId) return refuse('Pick somebody.');
  if (!Number.isInteger(slotIndex) || slotIndex < 0 || slotIndex >= 3) {
    return refuse('There are three slots.');
  }
  connection.send({
    Command: CommandType.AssignCharacterSlot,
    TargetGuid: characterId,
    RequestedSlotIndex: slotIndex,
  });
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

/**
 * THE STANDARD PAIR: one of your heroes and somebody from the village.
 *
 * Mirrors ValidateVillagerBreedingRequest, which disconnects on a zero
 * BreedingLevel, an empty hero Guid or a non-positive newcomer id. Everything
 * else it checks - the level 50 gate, the elder flag, the sexes and the race -
 * lives in the database and is the server's to refuse, so the screen shows the
 * preview's reason rather than guessing here.
 */
export function executeVillagerBreeding(
  heroId: string,
  newcomerId: number,
  breedingLevel: number,
): CommandOutcome {
  if (breedingLevel <= 0) return refuse('Build Breeding Grounds in your village first.');
  if (!heroId) return refuse('Choose one of your characters.');
  if (!Number.isInteger(newcomerId) || newcomerId <= 0) return refuse('Choose somebody from the village.');

  connection.send({
    Command: CommandType.ExecuteVillagerBreeding,
    TargetGuid: heroId,
    TargetId: newcomerId,
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
 * The requested multiplier rides on TargetId. 1 turns acceleration off.
 *
 * Modul: was toggleChronoAcceleration, and it never had anything to do with the
 * chrono bank - the server pays for every extra tick out of
 * AccumulatedTimeBankMs, so this can only replay time already owed. Renamed
 * with opcode 8 itself when the bank was deleted.
 */
export function setSimulationSpeed(multiplier: number): CommandOutcome {
  if (!Number.isInteger(multiplier) || multiplier < 1) return refuse('Multiplier must be at least 1.');
  connection.send({ Command: CommandType.SetSimulationSpeed, TargetId: multiplier });
  return OK;
}

// ---------------------------------------------------------------------------
