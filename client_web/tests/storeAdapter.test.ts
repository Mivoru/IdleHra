import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

/*
  BUYING SOMETHING, AND THE RECEIPT THAT REACHES THE SERVER.

  The adapter is the last missing piece of billing and the one that showed the
  receipt path could never have verified a real receipt: the server checked a
  bespoke `{provider, payload, signature}` envelope that no store produces and
  that a client could not manufacture, because signing it needs a key the
  client must never hold.

  What the client CAN honestly send is what the store gave it - a purchase
  token or a transaction id - and the server asks Google or Apple about it.
  These pin the shape of that envelope, because it crosses the wire into C# with
  no generated type between the two sides, and the failure mode of a drift is
  every purchase in production being refused after the player has been charged.
*/

let platform = 'android';
let native = true;

vi.mock('../src/lib/net/platform', () => ({
  isNativePlatform: () => native,
  platformName: () => platform,
}));

vi.mock('../src/lib/net/billing', () => ({
  registerStoreAdapter: vi.fn(),
}));

type AdapterModule = typeof import('../src/lib/net/storeAdapter');
let mod: AdapterModule;

interface StubTransaction {
  transactionId?: string;
  purchaseId?: string;
  nativePurchase?: { purchaseToken?: string; orderId?: string };
}

/**
 * A cordova-plugin-purchase good enough for the four calls the adapter makes.
 *
 * Modul: `order()` RESOLVES and the transaction arrives on an EVENT, which is
 * the plugin's real shape and the thing an adapter written against a return
 * value gets wrong. The stub reproduces that ordering deliberately.
 */
function installPlugin(options: {
  transaction?: StubTransaction;
  orderResult?: unknown;
  products?: { id: string; canPurchase?: boolean }[];
  approveBeforeOrderResolves?: boolean;
} = {}) {
  let approvedHandler: ((t: StubTransaction) => void) | null = null;
  const registered: { id: string; type: string; platform: string }[] = [];

  const transaction = options.transaction ?? {
    transactionId: 'GPA.1234-5678',
    nativePurchase: { purchaseToken: 'purchase-token-abcdefghij' },
  };

  const store = {
    register: vi.fn((products: { id: string; type: string; platform: string }[]) => {
      registered.push(...products);
    }),
    initialize: vi.fn(async () => undefined),
    get: vi.fn((id: string) =>
      registered.some((p) => p.id === id)
        ? {
            id,
            canPurchase: true,
            getOffer: () => ({
              order: vi.fn(async () => {
                if (options.approveBeforeOrderResolves !== false) approvedHandler?.(transaction);
                return options.orderResult;
              }),
            }),
          }
        : undefined,
    ),
    when: () => ({
      approved: (handler: (t: StubTransaction) => void) => {
        approvedHandler = handler;
      },
    }),
    products: options.products ?? [{ id: 'diamonds_small' }, { id: 'diamonds_large' }],
  };

  (globalThis as { CdvPurchase?: unknown }).CdvPurchase = {
    store,
    Platform: { GOOGLE_PLAY: 'android-playstore', APPLE_APPSTORE: 'ios-appstore' },
    ProductType: { CONSUMABLE: 'consumable' },
  };

  return { store, registered };
}

/** What the server will parse out of a receipt. */
function decode(receipt: string): Record<string, unknown> {
  return JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(receipt), (c) => c.charCodeAt(0))));
}

beforeEach(async () => {
  platform = 'android';
  native = true;
  vi.resetModules();
  mod = await import('../src/lib/net/storeAdapter');
});

afterEach(() => {
  delete (globalThis as { CdvPurchase?: unknown }).CdvPurchase;
});

describe('whether there is a store here at all', () => {
  it('says no in a browser', () => {
    native = false;
    expect(mod.storePluginPresent()).toBe(false);
  });

  it('says no in a native build with no plugin injected', () => {
    expect(mod.storePluginPresent()).toBe(false);
  });

  it('says yes once the shell injects one', () => {
    installPlugin();
    expect(mod.storePluginPresent()).toBe(true);
  });
});

describe('registering the products', () => {
  it('registers what the SERVER priced, not a hardcoded list', async () => {
    // Modul: the ids come from /api/v1/store/catalog, which reads the same
    // IapProductPrices that ResolvePremiumDiamondsForProduct pays out against.
    // A hardcoded list here would be that mapping written twice, and the
    // failure is a product the store sells whose receipt then grants nothing -
    // after the player has been charged.
    const { store, registered } = installPlugin();
    const adapter = mod.createStoreAdapter(['diamonds_small', 'diamonds_large']);

    await adapter.listProducts();

    expect(store.register).toHaveBeenCalledTimes(1);
    expect(registered.map((p) => p.id)).toEqual(['diamonds_small', 'diamonds_large']);
    expect(store.initialize).toHaveBeenCalledWith(['android-playstore']);
  });

  it('initialises once however many times it is used', async () => {
    const { store } = installPlugin();
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    await adapter.listProducts();
    await adapter.listProducts();
    await adapter.purchase('diamonds_small');

    expect(store.initialize).toHaveBeenCalledTimes(1);
  });

  it('reports only what the store will actually sell here', async () => {
    // A product not yet approved, or unavailable in this country, is registered
    // and still cannot be bought. Reporting it would put a Buy button on
    // something that refuses.
    installPlugin({
      products: [{ id: 'diamonds_small' }, { id: 'diamonds_large', canPurchase: false }],
    });
    const adapter = mod.createStoreAdapter(['diamonds_small', 'diamonds_large']);

    expect(await adapter.listProducts()).toEqual(['diamonds_small']);
  });

  it('registers for the APPLE platform on iOS', async () => {
    platform = 'ios';
    const { store, registered } = installPlugin();
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    await adapter.listProducts();

    expect(store.initialize).toHaveBeenCalledWith(['ios-appstore']);
    expect(registered[0].platform).toBe('ios-appstore');
  });
});

describe('the receipt the server is handed', () => {
  it('carries the purchase token Google will be asked about', async () => {
    installPlugin({
      transaction: {
        transactionId: 'GPA.3311-2244',
        nativePurchase: { purchaseToken: 'token-xyz-0123456789' },
      },
    });
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    const envelope = decode(await adapter.purchase('diamonds_small'));

    expect(envelope).toEqual({
      provider: 'GooglePlay',
      productId: 'diamonds_small',
      transactionId: 'GPA.3311-2244',
      purchaseToken: 'token-xyz-0123456789',
    });
  });

  it('carries a transaction id and nothing else on iOS', async () => {
    // Apple is addressed by transaction id alone; there is no equivalent of a
    // purchase token, and inventing a field for one would be a field the
    // server would have to decide whether to trust.
    platform = 'ios';
    installPlugin({ transaction: { transactionId: '2000000987654321' } });
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    const envelope = decode(await adapter.purchase('diamonds_small'));

    expect(envelope).toEqual({
      provider: 'AppStore',
      productId: 'diamonds_small',
      transactionId: '2000000987654321',
    });
  });

  it('SIGNS NOTHING', async () => {
    // Modul: the absence of a signature is the design. A client cannot sign
    // anything a store would vouch for, so a signature here would be one the
    // client made up - and the server's discriminator between the real scheme
    // and the legacy one is exactly this field's absence.
    installPlugin();
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    const envelope = decode(await adapter.purchase('diamonds_small'));

    expect(envelope).not.toHaveProperty('signature');
    expect(envelope).not.toHaveProperty('payload');
  });

  it('falls back to the order id when the plugin gives no transaction id', async () => {
    installPlugin({
      transaction: { nativePurchase: { orderId: 'GPA.fallback', purchaseToken: 'tok' } },
    });
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    expect(decode(await adapter.purchase('diamonds_small')).transactionId).toBe('GPA.fallback');
  });

  it('refuses a purchase the store approved with no transaction id at all', async () => {
    // The ledger is keyed on it. Sending an empty one would be a purchase that
    // can never be deduplicated.
    installPlugin({ transaction: { nativePurchase: { purchaseToken: 'tok' } } });
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    await expect(adapter.purchase('diamonds_small')).rejects.toThrow(/transaction id/i);
  });
});

describe('when a purchase does not happen', () => {
  it('throws the store message so billing.ts can call it a cancellation', async () => {
    // Modul: v13 RESOLVES with an error object rather than rejecting. An
    // adapter that only catches rejections would hang on every cancelled sheet
    // - the single most common outcome there is - and billing.ts would never
    // get to report it as `cancelled` rather than as an error.
    installPlugin({
      orderResult: { code: 6777006, message: 'Purchase cancelled by user' },
      approveBeforeOrderResolves: false,
    });
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    await expect(adapter.purchase('diamonds_small')).rejects.toThrow(/cancel/i);
  });

  it('refuses a product this device does not offer', async () => {
    installPlugin();
    const adapter = mod.createStoreAdapter(['diamonds_small']);

    await expect(adapter.purchase('diamonds_enormous')).rejects.toThrow(/does not offer/i);
  });

  it('refuses cleanly when there is no plugin', async () => {
    const adapter = mod.createStoreAdapter(['diamonds_small']);
    await expect(adapter.purchase('diamonds_small')).rejects.toThrow(/not present/i);
  });
});
