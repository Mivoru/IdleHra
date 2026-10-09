// The seasonal event (Samhain first): REST for the reading, the
// BuyEventShopItem command (commands.ts) for the buying.
//
// Modul: THE CLIENT KEEPS NO COPY OF THE EVENT. Its dates, rates, prices and
// shop are SeasonalEventRegistry's; this file only types the answer. A shop
// entry is bought by its `Index` in THIS answer, with the answer's event id
// beside it, so a page left open across a rollover cannot buy the wrong thing.
import { authedGet } from './auth';

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
