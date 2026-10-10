// The seasonal event (Samhain first): REST for the reading, the
// BuyEventShopItem command (commands.ts) for the buying.
//
// Modul: THE CLIENT KEEPS NO COPY OF THE EVENT. Its dates, rates, prices and
// shop are SeasonalEventRegistry's; this file only types the answer. A shop
// entry is bought by its `Index` in THIS answer, with the answer's event id
// beside it, so a page left open across a rollover cannot buy the wrong thing.
import { authedGet } from './auth';

/**
 * A drop chance as a percentage, to two significant figures: 0.025 is "2.5%",
 * 0.0002 is "0.02%". Rounding to a tenth of a percent - what both screens did -
 * printed the 2026-10-10 rates as "0%".
 */
export function chancePct(chance: number): string {
  return `${parseFloat((chance * 100).toPrecision(2))}%`;
}

/** SeasonalEventPhase on the server. */
export const EVENT_PHASE = { None: 0, Live: 1, Grace: 2 } as const;

/** EventShopKind on the server. */
export const EVENT_SHOP_KIND = { Avatar: 1, Pet: 2 } as const;

export interface EventShopEntry {
  Index: number;
  Id: string;
  Kind: number;
  Name: string;
  Price: number;
  /** Sprite path under /sprites/. */
  Art: string;
  /** A pet's bonuses, worded by the server; empty for an avatar. */
  Bonuses: string[];
  Owned: boolean;
}

/** A pet the event gives rather than sells - the rare drop or the boss's. */
export interface EventPet {
  Id: string;
  Name: string;
  /** Sprite path under /sprites/. */
  Art: string;
  /** Worded by the server (PetRegistry.Describe). */
  Bonuses: string[];
  Owned: boolean;
}

/**
 * "1 in 2,000" for a chance of 0.0005. A one-in-a-million odds reads as "0%"
 * in any percentage a person can parse, so rare odds are told as a ratio.
 */
export function oneIn(chance: number): string {
  if (chance <= 0) return 'never';
  return `1 in ${Math.round(1 / chance).toLocaleString('en-US')}`;
}

export interface SeasonalEvent {
  Id: number;
  Key: string;
  Name: string;
  CurrencyName: string;
  StartUtc: number;
  EndUtc: number;
  ShopCloseUtc: number;
  Phase: number;
  KillChance: number;
  GatherChance: number;
  OfflineFactor: number;
  /** The rare drop: each unit of currency also rolls RarePetChancePerCurrency for it. */
  RarePet: EventPet | null;
  RarePetChancePerCurrency: number;
  /** The seasonal boss's pet, paid on the first clear of BossPetTier. */
  BossPet: EventPet | null;
  BossPetTier: number;
  Shop: EventShopEntry[];
}

export const seasonalEventKeys = { all: ['seasonalEvent'] as const };

export async function fetchSeasonalEvent(): Promise<SeasonalEvent | null> {
  const answer = await authedGet<{ Event: SeasonalEvent | null }>('/api/v1/event');
  return answer.Event;
}

/** The theme key a wire event id wears. Ids are SeasonalEventRegistry's. */
export function eventThemeKey(eventId: number): string | null {
  return eventId === 1 ? 'samhain' : null;
}
