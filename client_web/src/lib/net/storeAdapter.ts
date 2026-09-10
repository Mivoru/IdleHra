// Modul: THE STORE ADAPTER - the last missing piece of billing, and the one
// that revealed the receipt path could never have verified a real receipt.
//
// `billing.ts` has always declared a `StoreAdapter` interface and refused to
// pick a vendor, because the choice is about fees rather than code. The vendor
// is now cordova-plugin-purchase: no revenue share beyond the stores' own cut,
// one API for both platforms, and the receipt passes straight through.
//
// WHAT THE SERVER ACTUALLY NEEDS, which is not what it used to ask for.
//
// `ProductionIapReceiptValidator.Validate` verifies a bespoke envelope -
// `{provider, payload, signature}` with an RSA signature over the payload -
// and NO STORE ON EARTH PRODUCES THAT. Neither Google nor Apple hands a client
// a receipt in that shape, and a client could not manufacture one anyway
// because signing it needs a private key the client must never hold. The
// scheme was verifiable only by the test harness that created it.
//
// The real check is a server-to-server call to the store, which is strictly
// stronger: it asks Google or Apple whether this purchase happened, rather than
// asking whether a blob the client sent verifies against a key. Both calls
// already existed on the server (VerifyViaGooglePlayDeveloperApiAsync,
// VerifyViaAppleAppStoreServerApiAsync) and were wired to nothing.
//
// So this adapter sends the IDENTIFIERS those calls need, base64-encoded, and
// signs nothing:
//
//   Google Play : { provider, productId, transactionId, purchaseToken }
//   App Store   : { provider, productId, transactionId }
//
// A client that lies about any of them gets caught by the store, which is the
// point. See BillingVerificationEngine.VerifyReceiptAsync for the other half.
//
// WHY THE PLUGIN IS READ OFF THE GLOBAL. Same reason as push.ts and
// lifecycle.ts: `cordova-plugin-purchase` is a native plugin, must never be
// bundled into the web build, and the native shell injects `CdvPurchase` at
// startup. A browser finds nothing there and `purchaseUnavailableReason()`
// already says so.
import type { StoreAdapter } from './billing';
import { isNativePlatform, platformName } from './platform';

/**
 * The bits of cordova-plugin-purchase v13 this file touches.
 *
 * Hand-declared rather than imported: importing the package's types is
 * harmless, importing its runtime is not, and one file describing four call
 * signatures is a smaller risk than a build that accidentally pulls a native
 * plugin into a browser bundle.
 */
interface CdvTransaction {
  transactionId?: string;
  purchaseId?: string;
  nativePurchase?: { purchaseToken?: string; orderId?: string; purchaseState?: number };
}

interface CdvOffer {
  order(): Promise<unknown>;
}

interface CdvProduct {
  id: string;
  canPurchase?: boolean;
  getOffer(): CdvOffer | undefined;
}

interface CdvStore {
  register(products: { id: string; type: string; platform: string }[]): void;
  initialize(platforms: string[]): Promise<unknown>;
  update?(): Promise<unknown>;
  get(productId: string, platform?: string): CdvProduct | undefined;
  when(): {
    approved(handler: (transaction: CdvTransaction) => void): unknown;
  };
  products: CdvProduct[];
  localTransactions?: CdvTransaction[];
}

interface CdvPurchaseGlobal {
  store: CdvStore;
  Platform: { GOOGLE_PLAY: string; APPLE_APPSTORE: string };
  ProductType: { CONSUMABLE: string };
}

function cdv(): CdvPurchaseGlobal | null {
  return (globalThis as { CdvPurchase?: CdvPurchaseGlobal }).CdvPurchase ?? null;
}

function platformConstant(api: CdvPurchaseGlobal): string {
  return platformName() === 'ios' ? api.Platform.APPLE_APPSTORE : api.Platform.GOOGLE_PLAY;
}

/**
 * What the server is handed, before base64.
 *
 * Modul: NO SIGNATURE FIELD, and its absence is the design rather than an
 * omission. The client cannot sign anything the store would vouch for, so a
 * signature here would only ever be one the client made up. The server asks the
 * store instead.
 */
interface ReceiptEnvelope {
  provider: 'GooglePlay' | 'AppStore';
  productId: string;
  transactionId: string;
  /** Google Play only. Apple is addressed by transaction id alone. */
  purchaseToken?: string;
}

function encodeEnvelope(envelope: ReceiptEnvelope): string {
  // btoa over UTF-8 bytes rather than over the string: a product id is ASCII
  // today and btoa throws on anything that is not, which would turn the first
  // non-ASCII id anybody adds into a purchase that fails after the player has
  // been charged.
  const json = JSON.stringify(envelope);
  const bytes = new TextEncoder().encode(json);
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

let initialized = false;
let initializing: Promise<void> | null = null;

/**
 * The transaction the store most recently approved.
 *
 * Modul: A PURCHASE IS AN EVENT, NOT A RETURN VALUE - the same shape as the
 * push token. `offer.order()` resolves when the sheet closes, and the
 * transaction arrives separately through `store.when().approved(...)`. Reading
 * the result off the promise would return undefined for every real purchase.
 */
let lastApproved: CdvTransaction | null = null;
const approvalWaiters: ((transaction: CdvTransaction) => void)[] = [];

function onApproved(transaction: CdvTransaction): void {
  lastApproved = transaction;
  // Copied and cleared before dispatch: a waiter that starts another purchase
  // must not be handed this same transaction by a list it is still inside.
  const waiting = approvalWaiters.splice(0, approvalWaiters.length);
  for (const resolve of waiting) resolve(transaction);
}

async function ensureInitialized(productIds: readonly string[]): Promise<CdvPurchaseGlobal> {
  const api = cdv();
  if (api === null) throw new Error('The store plugin is not present in this build.');

  if (initialized) return api;
  if (initializing !== null) {
    await initializing;
    return api;
  }

  initializing = (async () => {
    const platform = platformConstant(api);
    api.store.register(
      productIds.map((id) => ({ id, type: api.ProductType.CONSUMABLE, platform })),
    );
    api.store.when().approved((transaction) => onApproved(transaction));
    await api.store.initialize([platform]);
    initialized = true;
  })();

  try {
    await initializing;
  } finally {
    initializing = null;
  }
  return api;
}

/**
 * Builds the adapter for this device.
 *
 * `productIds` come from `/api/v1/store/catalog`, which reads
 * `GameBalanceConfig.json`'s `IapProductPrices` - so the ids registered with
 * the store are the same ids the server prices, from one source. Registering a
 * hardcoded list here would be that list written down twice, and a product the
 * server does not price is a purchase that verifies and grants nothing.
 */
export function createStoreAdapter(productIds: readonly string[]): StoreAdapter {
  return {
    name: 'cordova-plugin-purchase',

    async listProducts(): Promise<readonly string[]> {
      const api = await ensureInitialized(productIds);
      // What the STORE will actually sell here, which is not always what was
      // registered: a product not yet approved, or not available in this
      // country, simply does not come back.
      return api.store.products.filter((p) => p.canPurchase !== false).map((p) => p.id);
    },

    async purchase(productIdentifier: string): Promise<string> {
      const api = await ensureInitialized(productIds);

      const product = api.store.get(productIdentifier, platformConstant(api));
      if (!product) throw new Error(`The store does not offer ${productIdentifier} on this device.`);

      const offer = product.getOffer();
      if (!offer) throw new Error(`${productIdentifier} has no purchasable offer.`);

      lastApproved = null;
      const approved = new Promise<CdvTransaction>((resolve, reject) => {
        approvalWaiters.push(resolve);
        // Modul: BOUNDED. Without this a player who backgrounds the sheet and
        // never returns leaves a promise that never settles and a Buy button
        // that spins for the rest of the session. Three minutes is longer than
        // any purchase sheet and short enough to recover from.
        setTimeout(() => reject(new Error('The purchase did not complete in time.')), 180_000);
      });

      const orderResult = (await offer.order()) as { message?: string; code?: number } | undefined;
      // v13 RESOLVES WITH AN ERROR OBJECT rather than rejecting. A thrown
      // check here is what turns "the player pressed cancel" into billing.ts's
      // `cancelled` outcome instead of a promise that hangs until the timeout.
      if (orderResult && typeof orderResult.message === 'string') {
        throw new Error(orderResult.message);
      }

      const transaction = lastApproved ?? (await approved);

      const transactionId =
        transaction.transactionId ?? transaction.nativePurchase?.orderId ?? transaction.purchaseId ?? '';
      if (!transactionId) throw new Error('The store approved a purchase with no transaction id.');

      const isApple = platformName() === 'ios';
      return encodeEnvelope({
        provider: isApple ? 'AppStore' : 'GooglePlay',
        productId: productIdentifier,
        transactionId,
        purchaseToken: isApple ? undefined : transaction.nativePurchase?.purchaseToken,
      });
    },
  };
}

/** Whether this build could have a store at all. Cheap, and never throws. */
export function storePluginPresent(): boolean {
  return isNativePlatform() && cdv() !== null;
}
