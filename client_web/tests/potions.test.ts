// Modul: THE POTION TABLE IS WRITTEN TWICE - PotionRegistry.Table on the
// server decides, src/lib/ui/potions.ts describes it to the player. This test
// reads the server source and compares every row, so a magnitude changed on
// one side fails here instead of the shelf quietly promising the old number.
import { describe, it, expect, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const sent: Record<string, unknown>[] = [];
vi.mock('../src/lib/net/connection', () => ({
  connection: {
    send: (draft: Record<string, unknown>) => {
      sent.push(draft);
    },
    currentPlayerId: 0,
  },
}));

const { POTION_TABLE, potionInfo, formatCountdown, SHELF_CAPACITY } = await import('../src/lib/ui/potions');
const { stockPotionShelf, unloadPotionShelf, drinkPotion, setPotionAutoDrink } = await import('../src/lib/net/commands');
const { CommandType } = await import('../src/lib/net/protocol.generated');

const here = dirname(fileURLToPath(import.meta.url));
const server = join(here, '..', '..', 'server', 'FolkIdle.Server');

describe('potions', () => {
  it('mirrors PotionRegistry.Table row for row', () => {
    const source = readFileSync(join(server, 'Domain', 'Shared', 'PotionRegistry.cs'), 'utf8');
    const rows = [...source.matchAll(/\("(\w+)",\s*PotionKind\.\w+,\s*PotionSlot\.(\w+),\s*(\d+ \* )?HourMs,\s*(\d+),\s*(\d+)\)/g)];
    expect(rows.length).toBe(POTION_TABLE.length);
    const slotIndex: Record<string, number> = { Rewards: 0, Combat: 1, Gathering: 2 };
    for (const [, stem, slot, hours, normal, strong] of rows) {
      const mine = POTION_TABLE.find((r) => r.stem === stem);
      expect(mine, stem).toBeDefined();
      expect(mine!.slot, stem).toBe(slotIndex[slot]);
      expect(mine!.minutes, stem).toBe(60 * (hours ? Number(hours.replace(' * ', '')) : 1));
      expect(mine!.normal, stem).toBe(Number(normal));
      expect(mine!.strong, stem).toBe(Number(strong));
    }
  });

  it('every potion in items.json is one the table knows', () => {
    const items = JSON.parse(readFileSync(join(here, '..', '..', 'server', 'GameData', 'items.json'), 'utf8').replace(/^﻿/, '')) as { BaseId: string }[];
    const potions = items.filter((i) => i.BaseId.endsWith('_potion_consumable'));
    expect(potions.length).toBe(POTION_TABLE.length * 2);
    for (const p of potions) expect(potionInfo(p.BaseId), p.BaseId).not.toBeNull();
  });

  it('reads a BaseId into what it does', () => {
    const strong = potionInfo('strong_kupala_night_gathering_potion_consumable')!;
    expect(strong.title).toBe('Strong Kupala Night');
    expect(strong.summary).toBe('+8.0% rare find while gathering for 2 h');
    expect(potionInfo('hunt_elixir_offensive_potion_consumable')!.summary).toBe('+20% XP from kills for 1 h');
    expect(potionInfo('cooked_trout_food_consumable')).toBeNull();
  });

  it('counts down in minutes, then hours', () => {
    expect(formatCountdown(65_000)).toBe('1:05');
    expect(formatCountdown(3_725_000)).toBe('1:02:05');
    expect(formatCountdown(0)).toBe('0:00');
  });

  it('uses the shelf capacity and opcodes the server declares', () => {
    const state = readFileSync(join(server, 'Network', 'StateUpdatePacket.cs'), 'utf8');
    expect(state.match(/SlotCapacity = ([\d_]+)/)?.[1].replace('_', '')).toBe(String(SHELF_CAPACITY));
    const packet = readFileSync(join(server, 'Network', 'ClientCommandPacket.cs'), 'utf8');
    for (const name of ['StockPotionShelf', 'DrinkPotion', 'SetPotionAutoDrink'] as const) {
      expect(packet.match(new RegExp(`${name} = (\\d+)`))?.[1]).toBe(String(CommandType[name]));
    }
  });

  it('sends the slot on TargetSlotIndex and refuses what could never be a button', () => {
    sent.length = 0;
    expect(stockPotionShelf(1, 454, 5).ok).toBe(true);
    expect(drinkPotion(2).ok).toBe(true);
    expect(setPotionAutoDrink(0, true).ok).toBe(true);
    expect(unloadPotionShelf(1).ok).toBe(true);
    expect(sent).toEqual([
      { Command: CommandType.StockPotionShelf, TargetSlotIndex: 1, ConsumableItemId: 454, DepositQuantity: 5 },
      { Command: CommandType.DrinkPotion, TargetSlotIndex: 2 },
      { Command: CommandType.SetPotionAutoDrink, TargetSlotIndex: 0, IsBuy: 1 },
      { Command: CommandType.StockPotionShelf, TargetSlotIndex: 1, DepositQuantity: 0 },
    ]);
    expect(stockPotionShelf(3, 454, 5).ok).toBe(false);
    expect(stockPotionShelf(0, 0, 5).ok).toBe(false);
    expect(drinkPotion(-1).ok).toBe(false);
  });
});
