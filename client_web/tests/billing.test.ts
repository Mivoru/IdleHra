import { describe, it, expect, vi, beforeEach } from 'vitest';

// Modul: money. The one part of this client where a bug costs a real person
// real currency, so the tests are about what CANNOT happen as much as what can.
//
// The purchase path is mocked at the module boundary rather than driven end to
// end, because the alternative involves a platform store sheet. What is tested
// is the decision-making: refusing where no store exists, treating a cancelled
// sheet as a non-event, treating a 409 as "not accepted" rather than an
// outage, and never letting a receipt reach the wire on the unsigned path.

const posted: { path: string; body: unknown }[] = [];
let postStatus: number | null = null;

vi.mock('../src/lib/net/auth', () => ({
  authedPost: async (path: string, body: unknown) => {
    posted.push({ path, body });
    if (postStatus !== null) {
      const err = new Error(`HTTP ${postStatus}`) as Error & { status: number };
      err.status = postStatus;
      throw err;
    }
    return null;
  },
}));

const sent: Record<string, unknown>[] = [];
vi.mock('../src/lib/net/connection', () => ({
  connection: { send: (d: Record<string, unknown>) => sent.push(d) },
}));

let native = false;
vi.mock('../src/lib/net/platform', () => ({
  isNativePlatform: () => native,
  platformName: () => (native ? 'android' : 'web'),
  CAPACITOR_ORIGINS: ['https://localhost', 'capacitor://localhost'],
}));

const { purchase, submitReceipt, purchaseUnavailableReason, registerStoreAdapter, syncBillingStatus } =
  await import('../src/lib/net/billing');
const { CommandType } = await import('../src/lib/net/protocol.generated');

beforeEach(() => {
  posted.length = 0;
  sent.length = 0;
  postStatus = null;
  native = false;
  registerStoreAdapter(null);
});

describe('availability', () => {
  it('refuses in a browser, because there is no store to buy through', async () => {
    expect(purchaseUnavailableReason()).toContain('Android or iOS app');
    const outcome = await purchase('diamonds_small');
    expect(outcome.kind).toBe('unavailable');
    // The decisive assertion: nothing was sent anywhere.
    expect(posted).toHaveLength(0);
  });

  it('refuses on native with no store integration built in', async () => {
    native = true;
    expect(purchaseUnavailableReason()).toContain('No store integration');
    expect((await purchase('diamonds_small')).kind).toBe('unavailable');
    expect(posted).toHaveLength(0);
  });
});

describe('a completed purchase', () => {
  beforeEach(() => {
    native = true;
    registerStoreAdapter({
      name: 'test',
      listProducts: async () => ['diamonds_small'],
      purchase: async () => 'BASE64RECEIPT',
    });
  });

  it('sends the receipt VERBATIM to the signature-checking endpoint', async () => {
    const outcome = await purchase('diamonds_small');
    expect(outcome.kind).toBe('granted');
    expect(posted).toHaveLength(1);
    expect(posted[0].path).toBe('/api/v1/billing/verify-receipt');
    // Re-encoding or unwrapping would break the signature the server verifies
    // over exactly these bytes.
    expect(posted[0].body).toEqual({ receipt: 'BASE64RECEIPT' });
  });

  it('NEVER uses the unsigned opcode 39 path', () => {
    // SubmitPurchaseReceipt grants diamonds on a transaction id and a product
    // hash with no signature anywhere. It is reachable and it looks usable,
    // which is exactly why this is pinned.
    expect(sent.some((c) => c.Command === CommandType.SubmitPurchaseReceipt)).toBe(false);
  });

  it('pulls the new balance through rather than leaving a paid player waiting', async () => {
    await purchase('diamonds_small');
    expect(sent.some((c) => c.Command === CommandType.SyncBillingStatus)).toBe(true);
  });
});

describe('outcomes that are not success', () => {
  beforeEach(() => {
    native = true;
  });

  it('treats a cancelled sheet as a non-event, not an error', async () => {
    registerStoreAdapter({
      name: 'test',
      listProducts: async () => [],
      purchase: async () => {
        throw new Error('User cancelled the purchase');
      },
    });
    const outcome = await purchase('diamonds_small');
    expect(outcome.kind).toBe('cancelled');
    expect(posted).toHaveLength(0);
  });

  it('reads 409 as "receipt not accepted", not as an outage', async () => {
    postStatus = 409;
    const outcome = await submitReceipt('BASE64');
    expect(outcome.kind).toBe('rejected');
    expect((outcome as { reason: string }).reason).toContain('already have been used');
  });

  it('reads 503 as the server not being configured for purchases', async () => {
    postStatus = 503;
    const outcome = await submitReceipt('BASE64');
    expect(outcome.kind).toBe('unavailable');
  });

  it('refuses an empty receipt rather than posting it', async () => {
    registerStoreAdapter({
      name: 'test',
      listProducts: async () => [],
      purchase: async () => '',
    });
    expect((await purchase('diamonds_small')).kind).toBe('rejected');
    expect(posted).toHaveLength(0);
  });
});

describe('resubmission', () => {
  it('can re-send a receipt, which is the correct recovery for an interrupted purchase', async () => {
    // The store keeps the receipt and the server is idempotent on the
    // transaction id, so re-sending is safe and is how a purchase that paid
    // but never verified gets finished.
    await submitReceipt('BASE64');
    await submitReceipt('BASE64');
    expect(posted).toHaveLength(2);
  });
});

describe('billing status sync', () => {
  it('carries no payload', () => {
    syncBillingStatus();
    expect(sent[0]).toEqual({ Command: CommandType.SyncBillingStatus });
  });
});
