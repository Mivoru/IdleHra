// Seasonal event pets: REST for reading and placing. Every pet - its name,
// bonuses and art - is PetRegistry's answer; this file only types it.
//
// Modul: ONE PET PER CHARACTER, EACH OWNED ONCE (owner, 2026-10-09). Placing a
// pet on someone who already has one sends the old one to rest; placing a pet
// that follows someone else moves it. The server does both in one locked
// transaction and re-stats every character it touched.
import { authedGet, authedPost } from './auth';

/** PetSource on the server. */
export const PET_SOURCE = { Shop: 1, RareDrop: 2, Boss: 3, Monster: 4, Rebirth: 5, WorldBoss: 6 } as const;

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
  /** Which of that character's pet slots it sits in (0, or 1 after five rebirths). */
  Slot: number;
  /** Owned, and the "a pet found you" window is still owed (PlayerPet.AnnouncedAtUtc). */
  Unseen: boolean;
  /** Duplicates found since the window was last closed - each paid diamonds by mail. */
  PendingDuplicates: number;
}

export interface PetsView {
  Pets: Pet[];
  Characters: { Id: string; Name: string }[];
  /** The monster pets' odds per kill (PetRegistry.MonsterPetPerKill). */
  MonsterPetChancePerKill: number;
  /** What a monster pet already owned pays instead, by mail. */
  DuplicateDiamonds: number;
  /** Task 114: whether this account's characters have a second pet slot. */
  SecondSlotOpen: boolean;
  /** The rebirths that open it (RebirthRules.SecondPetSlotRebirths). */
  SecondSlotRebirths: number;
}

export const petKeys = { all: ['pets'] as const };

export function fetchPets(): Promise<PetsView> {
  return authedGet<PetsView>('/api/v1/pets');
}

export interface PetAssignAnswer {
  Result: 'Ok' | 'NotOwned' | 'NotYourCharacter' | 'Failed' | 'SlotLocked';
  Pets: Pet[];
}

/** Puts a pet in a character's slot, or rests it when `characterId` is null. */
export async function assignPet(petId: string, characterId: string | null, slot = 0): Promise<PetAssignAnswer | null> {
  return authedPost<PetAssignAnswer>('/api/v1/pets/assign', { PetId: petId, CharacterId: characterId, Slot: slot });
}

/**
 * The player closed the "a pet found you" window. Answers the pets view, so
 * the cache can move straight on to the next owed window.
 */
export async function markPetSeen(petId: string): Promise<PetAssignAnswer | null> {
  return authedPost<PetAssignAnswer>('/api/v1/pets/seen', { PetId: petId });
}

/** How a pet is found, for the screen. */
export function petSourceLine(pet: Pet): string {
  if (pet.Source === PET_SOURCE.RareDrop) return 'A rare find - any kill or harvest during the event may bring it.';
  if (pet.Source === PET_SOURCE.Boss) return 'Follows whoever first breaks The Cailleach’s sixth winter.';
  if (pet.Source === PET_SOURCE.Monster) return 'Found on any kill, any time - rarely.';
  if (pet.Source === PET_SOURCE.Rebirth) return 'Follows whoever takes the tenth and last rebirth.';
  if (pet.Source === PET_SOURCE.WorldBoss) return 'Follows whoever deals Chernobog a million damage in one week.';
  return 'Sold in the event shop.';
}
