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
}

export interface CosmeticCatalogue {
  RarityNames: string[];
  Items: CosmeticDefinition[];
  ChestChancePerKill: number[];
  LevelsPerChest: number;
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
