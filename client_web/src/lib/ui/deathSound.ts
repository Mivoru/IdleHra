// Modul: A DEATH SOUNDS LIKE WHO DIED.
//
// There are two death clips, a man's and a woman's, and the state packet does
// not say which the fighting character is: StateUpdatePacket carries each
// slot's id and race, not their sex. Rather than put a field on every frame
// (task 46 is measuring frame size) for a sound that plays a few times a
// session, the answer is read from the Hall of Ancestors the first time it is
// needed and kept - a character's sex never changes, and an id is never
// reused, so the cache cannot go stale.
//
// The lookup is async, so the first death of a session sounds a moment late.
// A failed lookup plays the man's clip rather than nothing: dying is the one
// moment that must not be silent.

import { fetchAncestorsHall } from '../net/rest';
import { playWithFallback } from './audio';

const femaleById = new Map<string, boolean>();
let inFlight: Promise<void> | null = null;

async function loadHall(): Promise<void> {
  const hall = await fetchAncestorsHall();
  for (const member of hall.Members) femaleById.set(member.CharacterId, member.IsFemale);
}

/** Resolves the sex of `characterId`, or null when it cannot be found out. */
export async function isFemaleCharacter(characterId: string): Promise<boolean | null> {
  if (!femaleById.has(characterId)) {
    inFlight ??= loadHall().finally(() => (inFlight = null));
    try {
      await inFlight;
    } catch {
      return null;
    }
  }
  return femaleById.get(characterId) ?? null;
}

export function playDeathFor(characterId: string): void {
  void isFemaleCharacter(characterId).then((female) =>
    playWithFallback(female ? 'playerDiedFemale' : 'playerDied', 'error'),
  );
}

/** Tests only. */
export function resetDeathSoundCache(): void {
  femaleById.clear();
  inFlight = null;
}
