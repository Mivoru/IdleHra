// Modul: WHERE THE STORE ADAPTER GETS PLUGGED IN, kept apart from the adapter
// itself so `billing.ts` still knows nothing about a vendor.
//
// The split is the point. `billing.ts` declares the interface, `storeAdapter.ts`
// implements it against cordova-plugin-purchase, and this file is the single
// line of glue that decides whether to use it. Swapping vendors touches two
// files and neither of them is the one that spends money.
import { registerStoreAdapter } from './billing';
import { createStoreAdapter, storePluginPresent } from './storeAdapter';
import { fetchStoreCatalog } from './rest';

let registered = false;

/**
 * Registers the platform store, if this build has one.
 *
 * Modul: THE PRODUCT IDS COME FROM THE SERVER, and that is not incidental.
 * `/api/v1/store/catalog` reads `GameBalanceConfig.json`'s `IapProductPrices`,
 * which is also what `ResolvePremiumDiamondsForProduct` prices a verified
 * receipt against. A hardcoded list here would be that mapping written down
 * twice, and the failure mode is the worst kind: a product the store happily
 * sells, whose receipt then verifies and grants nothing, after the player has
 * been charged.
 *
 * Never throws and never blocks a sign-in. Failing to register leaves the Buy
 * buttons disabled with a stated reason, which is the state the client was in
 * before any of this existed.
 */
export async function registerPlatformStore(): Promise<void> {
  if (registered) return;
  if (!storePluginPresent()) return;

  try {
    const catalog = await fetchStoreCatalog();
    const productIds = catalog.map((entry) => entry.ProductId).filter((id) => id.length > 0);

    // Registering zero products would initialise the plugin against nothing
    // and report "no products for sale", which reads to a player as the store
    // being broken rather than as a server that answered oddly.
    if (productIds.length === 0) return;

    registerStoreAdapter(createStoreAdapter(productIds));
    registered = true;
  } catch {
    // An unreachable catalog is a sign-in that still works. The store screen
    // shows its own error for the same request.
  }
}
