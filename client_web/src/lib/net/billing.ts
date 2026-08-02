// Modul: real-money purchases.
//
// THE SERVER HAS TWO PURCHASE PATHS AND ONLY ONE OF THEM IS SAFE.
//
//   REST /api/v1/billing/verify-receipt  -> VerifyReceiptAsync
//     Takes the platform's signed receipt, validates the SIGNATURE against a
//     configured store public key, resolves the product from the verified
//     payload, and only then grants diamonds. Idempotent on the transaction
//     id. This is the path a real purchase must take.
//
//   Opcode 39 SubmitPurchaseReceipt      -> VerifyPurchaseAsync
//     Takes a 64-byte transaction id and an FNV-1a hash of the product id,
//     and grants diamonds on the strength of those alone. There is no
//     signature anywhere in it - the server's own comment says a 64-byte
//     packet "never could" carry a real receipt.
//
// So this client deliberately does NOT use opcode 39. Wiring it would put a
// "type any string, receive diamonds" path in the shipped UI, and the fact
// that it exists and is reachable is the reason to say so here rather than
// leave the next person to discover it looks usable.
//
// Opcode 40 (SyncBillingStatus) is a harmless read-back of the balance and IS
// used, to pull the granted diamonds through without waiting for the next
// natural state flush.

import { authedPost } from './auth';
import { connection } from './connection';
import { CommandType } from './protocol.generated';
import { isNativePlatform, platformName } from './platform';

/**
 * What a platform store has to give us.
 *
 * Deliberately the smallest possible surface, and deliberately NOT tied to a
 * vendor. Every candidate (RevenueCat, cordova-plugin-purchase, a first-party
 * bridge) can satisfy this in a few lines, and choosing between them is a
 * commercial decision about fees and analytics rather than a technical one -
 * so this file does not make it. Register an implementation at startup and
 * everything above this line keeps working unchanged.
 */
export interface StoreAdapter {
  /** Human-readable, shown when a purchase cannot proceed. */
  readonly name: string;
  /** Products the store will actually sell on this device. */
  listProducts(): Promise<readonly string[]>;
  /**
   * Runs the platform's purchase sheet and returns the SIGNED receipt, base64
   * encoded, exactly as the store produced it. Must not be re-encoded or
   * unwrapped - the server verifies the signature over those bytes.
   */
  purchase(productIdentifier: string): Promise<string>;
}

let adapter: StoreAdapter | null = null;

/**
 * Registers the platform store.
 *
 * A nullish implementation CLEARS the registration rather than being stored -
 * "we have an adapter that is undefined" is not a state worth having, and
 * treating it as registered would let a purchase attempt dereference it and
 * fail as a crash instead of as a clean refusal.
 */
export function registerStoreAdapter(implementation: StoreAdapter | null | undefined): void {
  adapter = implementation ?? null;
}

export function storeAdapterName(): string | null {
  return adapter?.name ?? null;
}

export type PurchaseOutcome =
  | { kind: 'granted' }
  | { kind: 'unavailable'; reason: string }
  | { kind: 'cancelled' }
  | { kind: 'rejected'; reason: string };

/**
 * Why a purchase cannot be attempted here, or null if it can.
 *
 * Separated from `purchase` so a screen can disable a button and SAY why
 * instead of letting the player press it and receive a refusal - the same
 * reasoning the guarded command layer uses.
 */
export function purchaseUnavailableReason(): string | null {
  if (!isNativePlatform()) {
    return 'Purchases need the Android or iOS app. A browser has no store to buy through.';
  }
  if (adapter === null) {
    return `No store integration is built into this ${platformName()} build.`;
  }
  return null;
}

/**
 * Buys a product and hands the signed receipt to the server.
 *
 * The receipt goes over REST rather than the WebSocket because it is far too
 * large for the fixed-layout command packet, and because REST is the only path
 * whose server side checks the signature.
 */
export async function purchase(productIdentifier: string): Promise<PurchaseOutcome> {
  const unavailable = purchaseUnavailableReason();
  if (unavailable !== null) return { kind: 'unavailable', reason: unavailable };

  let receipt: string;
  try {
    receipt = await adapter!.purchase(productIdentifier);
  } catch (err) {
    // A cancelled purchase sheet is the single most common outcome and is not
    // an error - reporting it as one would tell players something went wrong
    // every time they changed their mind.
    const message = err instanceof Error ? err.message : String(err);
    if (/cancel/i.test(message)) return { kind: 'cancelled' };
    return { kind: 'rejected', reason: message };
  }

  if (!receipt) return { kind: 'rejected', reason: 'The store returned an empty receipt.' };

  return submitReceipt(receipt);
}

/**
 * Sends an already-obtained receipt for verification.
 *
 * Exposed separately so a purchase interrupted after payment but before
 * verification can be completed later - the store keeps the receipt, the
 * server is idempotent on the transaction id, and re-sending is therefore safe
 * and is the correct recovery.
 */
export async function submitReceipt(base64Receipt: string): Promise<PurchaseOutcome> {
  try {
    await authedPost('/api/v1/billing/verify-receipt', { receipt: base64Receipt });
  } catch (err) {
    // The endpoint answers 409 for a receipt that failed validation or was
    // already redeemed. Both mean "no diamonds from this", and neither is a
    // transport failure, so they are reported as a rejection rather than an
    // outage.
    const status = (err as { status?: number }).status;
    if (status === 409) {
      return { kind: 'rejected', reason: 'The store receipt was not accepted. It may already have been used.' };
    }
    if (status === 503) {
      return { kind: 'unavailable', reason: 'Purchases are not configured on this server.' };
    }
    return { kind: 'rejected', reason: 'Could not reach the server to confirm the purchase.' };
  }

  // Pull the new balance through rather than waiting for whatever would have
  // flushed it next. Cheap, and the alternative is a player who paid watching
  // an unchanged number.
  syncBillingStatus();
  return { kind: 'granted' };
}

/** Opcode 40. A read-back with no payload; safe to send at any time. */
export function syncBillingStatus(): void {
  connection.send({ Command: CommandType.SyncBillingStatus });
}
