// Seasonal event pets: REST for reading and placing. Every pet - its name,
// bonuses and art - is PetRegistry's answer; this file only types it.
//
// Modul: ONE PET PER CHARACTER, EACH OWNED ONCE (owner, 2026-10-09). Placing a
// pet on someone who already has one sends the old one to rest; placing a pet
// that follows someone else moves it. The server does both in one locked
// transaction and re-stats every character it touched.
import { authedGet, authedPost } from './auth';

/** PetSource on the server. */
export const PET_SOURCE = { Shop: 1, RareDrop: 2, Boss: 3 } as const;

export interface Pet {
  Id: string;
  Name: string;
  Source: number;
  EventId: number;
  Art: string;
  /** Each bonus, worded by the server: "+5% combat XP". */
  Bonuses: string[];
  Owned: boolean;
  /** The character it follows, or null while it rests. */
  CharacterId: string | null;
}

export interface PetsView {
  Pets: Pet[];
  Characters: { Id: string; Name: string }[];
}

export const petKeys = { all: ['pets'] as const };

export function fetchPets(): Promise<PetsView> {
  return authedGet<PetsView>('/api/v1/pets');
}

export interface PetAssignAnswer {
  Result: 'Ok' | 'NotOwned' | 'NotYourCharacter' | 'Failed';
  Pets: Pet[];
}

/** Puts a pet on a character, or rests it when `characterId` is null. */
export async function assignPet(petId: string, characterId: string | null): Promise<PetAssignAnswer | null> {
  return authedPost<PetAssignAnswer>('/api/v1/pets/assign', { PetId: petId, CharacterId: characterId });
}

/** How a pet is found, for the screen. */
export function petSourceLine(pet: Pet): string {
  if (pet.Source === PET_SOURCE.RareDrop) return 'A rare find - any kill or harvest during the event may bring it.';
  if (pet.Source === PET_SOURCE.Boss) return 'Follows whoever first breaks The Cailleach’s sixth winter.';
  return 'Sold in the event shop.';
}
