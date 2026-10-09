// Task 54: cosmetic chests, avatars and frames - REST, not the wire.
//
// Modul: THE CLIENT KEEPS NO LIST OF COSMETICS. The catalogue is the server's
// (GET /api/v1/cosmetics/catalogue), and this file only turns an id into a
// picture: an avatar's `Art` is a monster name matched to its portrait, a
// frame is drawn by CosmeticFrame.svelte from its id. A second list here would
// be the two-sources-of-truth drift this codebase keeps paying for.
import { authedGet, authedPost } from './auth';
import { api } from './config';

export const COSMETIC_KIND = { Chest: 0, Avatar: 1, Frame: 2 } as const;

export interface CosmeticDefinition {
  Id: string;
  Kind: number;
  Rarity: number;
  Name: string;
  /** For an avatar, the monster whose portrait it is. */
  Art: string | null;
  /** Earned, not found (Boss Ascension frames): never in a chest, never on the market. */
  Bound: boolean;
}

export interface CosmeticCatalogue {
  RarityNames: string[];
  Items: CosmeticDefinition[];
  ChestChancePerKill: number[];
  LevelsPerChest: number;
  /**
   * What a chest gives, per mille: [chestRarity][resultRarity]
   * (CosmeticRegistry.ChestContentPermille, 2026-10-08). A chest's rarity is
   * its ODDS, not its contents.
   */
  ChestContentPermille?: number[][];
}

export interface OwnedCosmetic {
  Id: number;
  DefinitionId: string;
  Kind: number;
  Rarity: number;
  IsListed: boolean;
}

export interface OpenedCosmetic {
  Id: number;
  DefinitionId: string;
  Kind: number;
  Rarity: number;
  Name: string;
}

/** Mirrors CosmeticResult in CosmeticEngine.cs - pinned by tests/cosmetics.test.ts. */
export type CosmeticResult = 'Ok' | 'NoChest' | 'UnknownCosmetic' | 'NotOwned' | 'PlayerNotFound';

export const COSMETIC_RESULT_SENTENCES: Record<CosmeticResult, string> = {
  Ok: '',
  NoChest: 'You have no unopened chest of that rarity.',
  UnknownCosmetic: 'That is not something you can wear there.',
  NotOwned: "You don't own that - or your only copy is on the market.",
  PlayerNotFound: 'Your account could not be found. Try signing in again.',
};

export interface CosmeticsView {
  Result: CosmeticResult | null;
  /** Unopened, unlisted chests, indexed by rarity 1-4 (index 0 unused). */
  Chests: number[];
  Owned: OwnedCosmetic[];
  EquippedAvatarId: string | null;
  EquippedFrameId: string | null;
  Opened: OpenedCosmetic | null;
}

export interface WornCosmetics {
  PlayerId: number;
  AvatarId: string | null;
  FrameId: string | null;
  RaceId: number;
  IsFemale: boolean;
  /** The worn title's display name and colour, as TitleRegistry has them, or null. */
  Title: string | null;
  TitleColor: string | null;
}

export const cosmeticKeys = {
  catalogue: ['cosmetics', 'catalogue'] as const,
  mine: ['cosmetics', 'mine'] as const,
  worn: (ids: readonly number[]) => ['cosmetics', 'worn', [...ids].sort((a, b) => a - b).join(',')] as const,
};

let cataloguePromise: Promise<CosmeticCatalogue> | null = null;

/** The catalogue is content: fetched once per page load, like /gamedata. */
export function loadCosmeticCatalogue(): Promise<CosmeticCatalogue> {
  if (!cataloguePromise) {
    cataloguePromise = fetch(api('/api/v1/cosmetics/catalogue'))
      .then((r) => {
        if (!r.ok) throw new Error(`catalogue ${r.status}`);
        return r.json() as Promise<CosmeticCatalogue>;
      })
      .catch((err) => {
        cataloguePromise = null;
        throw err;
      });
  }
  return cataloguePromise;
}

export function fetchCosmetics(): Promise<CosmeticsView> {
  return authedGet<CosmeticsView>('/api/v1/cosmetics');
}

export function openChest(rarity: number): Promise<CosmeticsView | null> {
  return authedPost<CosmeticsView>('/api/v1/cosmetics/open', { Rarity: rarity });
}

/** Wear an avatar/frame by definition id, or go back to the default with null. */
export function equipCosmetic(kind: number, id: string | null): Promise<CosmeticsView | null> {
  return authedPost<CosmeticsView>('/api/v1/cosmetics/equip', { Kind: kind, Id: id });
}

export function fetchWorn(ids: readonly number[]): Promise<WornCosmetics[]> {
  const clean = [...new Set(ids.filter((id) => id > 0))];
  if (clean.length === 0) return Promise.resolve([]);
  return authedGet<{ Players: WornCosmetics[] }>(`/api/v1/cosmetics/worn?ids=${clean.join(',')}`).then(
    (r) => r?.Players ?? [],
  );
}

export function rarityClass(rarity: number): string {
  return ['', 'common', 'rare', 'epic', 'legendary'][rarity] ?? 'common';
}

// ---------------------------------------------------------------------------
// The cosmetic market (task 54 phase 4). The seller sets the price.
// ---------------------------------------------------------------------------

/** Mirrors CosmeticMarketResult in CosmeticMarketEngine.cs - pinned by tests/cosmetics.test.ts. */
export type CosmeticMarketResult =
  | 'Ok'
  | 'NotFound'
  | 'NotYours'
  | 'Worn'
  | 'AlreadyListed'
  | 'InvalidPrice'
  | 'NoGuildLicense'
  | 'InsufficientGold'
  | 'OwnListing'
  | 'Quarantined'
  | 'PlayerNotFound'
  | 'Bound';

export const COSMETIC_MARKET_SENTENCES: Record<CosmeticMarketResult, string> = {
  Ok: '',
  NotFound: 'That listing is gone - someone was quicker, or it was taken down.',
  NotYours: "That isn't yours.",
  Worn: "You're wearing your only copy. Take it off in the Wardrobe first.",
  AlreadyListed: 'That is already on the market.',
  InvalidPrice: 'Choose a price between 1 and 1,000,000,000 gold.',
  NoGuildLicense: 'The market needs a guild - join one to trade.',
  InsufficientGold: "You don't have enough gold for that.",
  OwnListing: 'That is your own listing. Take it down instead.',
  Quarantined: 'This account cannot trade while it is quarantined.',
  PlayerNotFound: 'Your account could not be found. Try signing in again.',
  Bound: 'That was earned on the Boss Ascension ladder. It is yours to wear, not to sell.',
};

/** The most a price can be (CosmeticRegistry.MaxMarketPrice). */
export const MAX_COSMETIC_PRICE = 1_000_000_000;

export interface CosmeticListing {
  Id: number;
  SellerId: number;
  SellerName: string;
  CosmeticItemId: number;
  DefinitionId: string;
  Kind: number;
  Rarity: number;
  Price: number;
  IsMine: boolean;
}

export interface CosmeticMarketResponse {
  Result: CosmeticMarketResult;
  Cosmetics: CosmeticsView;
}

export const cosmeticMarketKeys = {
  listings: (kind: number | null, rarity: number | null) => ['market', 'cosmetics', kind, rarity] as const,
};

export function fetchCosmeticListings(kind: number | null, rarity: number | null): Promise<CosmeticListing[]> {
  const params = new URLSearchParams();
  if (kind !== null) params.set('kind', String(kind));
  if (rarity !== null) params.set('rarity', String(rarity));
  const query = params.toString();
  return authedGet<{ Listings: CosmeticListing[] }>(`/api/v1/market/cosmetics${query ? `?${query}` : ''}`).then(
    (r) => r?.Listings ?? [],
  );
}

export function listCosmetic(cosmeticItemId: number, price: number): Promise<CosmeticMarketResponse | null> {
  return authedPost<CosmeticMarketResponse>('/api/v1/market/cosmetics/list', { CosmeticItemId: cosmeticItemId, Price: price });
}

export function buyCosmetic(listingId: number): Promise<CosmeticMarketResponse | null> {
  return authedPost<CosmeticMarketResponse>('/api/v1/market/cosmetics/buy', { ListingId: listingId });
}

export function cancelCosmeticListing(listingId: number): Promise<CosmeticMarketResponse | null> {
  return authedPost<CosmeticMarketResponse>('/api/v1/market/cosmetics/cancel', { ListingId: listingId });
}

// ---------------------------------------------------------------------------
// Boss challenges (task 55) - their words and numbers are the server's.
// ---------------------------------------------------------------------------

export interface BossChallengeStatus {
  Id: string;
  Title: string;
  Description: string;
  Completed: boolean;
}

export interface BossChallengeRegion {
  Region: number;
  BossMonsterId: number;
  ChestRarity: number;
  Challenges: BossChallengeStatus[];
}

export const bossChallengeKeys = { all: ['bossChallenges'] as const };

// Task 87: the Boss Ascension ladder. Everything a step MEANS - its effects,
// its time limit in seconds, its reward names - is the server's sentence; this
// file keeps no copy of the ladder, only the shape of the answer.
export interface AscensionStep {
  Step: number;
  /** What this step ADDS on top of the one below it. */
  Summary: string;
  /** Every modifier in force at this step, cumulative. */
  Effects: string[];
  TimeLimitSeconds: number;
  Cleared: boolean;
  /** Boss beaten once and this step is at most one above the highest cleared. */
  Startable: boolean;
  RewardTitle: string;
  RewardFrame: string | null;
}

export interface AscensionBoss {
  Region: number;
  BossMonsterId: number;
  BossDefeated: boolean;
  HighestStep: number;
  NextStep: number;
  Steps: AscensionStep[];
}

export const bossAscensionKeys = { all: ['bossAscension'] as const };

export function fetchBossAscension(): Promise<AscensionBoss[]> {
  return authedGet<{ MaxStep: number; Bosses: AscensionBoss[] }>('/api/v1/boss-ascension').then((r) => r?.Bosses ?? []);
}

export function fetchBossChallenges(): Promise<BossChallengeRegion[]> {
  return authedGet<{ Regions: BossChallengeRegion[] }>('/api/v1/boss-challenges').then((r) => r?.Regions ?? []);
}
