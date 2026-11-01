import { describe, expect, it } from 'vitest';
import { isAnyGateFull } from '../src/lib/stores/dungeonTickets';
import type { DungeonView } from '../src/lib/net/dungeon';

const view = (open: boolean, tickets: number[]): DungeonView =>
  ({ Open: open, TicketCap: 4, Dungeons: tickets.map((t) => ({ Tickets: t })) }) as unknown as DungeonView;

describe('isAnyGateFull', () => {
  it('is true when one gate sits at its cap', () => expect(isAnyGateFull(view(true, [1, 4, 0]))).toBe(true));
  it('counts a chest bonus past the cap as full', () => expect(isAnyGateFull(view(true, [5, 0, 0]))).toBe(true));
  it('is false when every gate has room', () => expect(isAnyGateFull(view(true, [3, 3, 0]))).toBe(false));
  it('is false while the dungeons are locked', () => expect(isAnyGateFull(view(false, [4, 4, 4]))).toBe(false));
  it('is false with no answer', () => expect(isAnyGateFull(null)).toBe(false));
});
