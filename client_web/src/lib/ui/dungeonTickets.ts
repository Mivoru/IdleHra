import type { DungeonInfo } from '../net/dungeon';

// Modul: ONE PICTURE PER GATE'S TICKET (owner, 2026-11-01: "tickets should
// have their own icon, we generated pictures for them"). The art is the
// 2026-10-30 Gemini set in tools/assets/gemini.json: Koschei's sealed scroll,
// Zmey's bronze token, Baba Yaga's smith key. A chest's bonus ticket arrives
// as the gate's display NAME (LootChestEngine.Opened.DungeonTicket), so the
// name map mirrors DungeonBoss.DungeonName; an unknown name just draws no icon.
export const TICKET_ICON: Record<DungeonInfo['Key'], string> = {
  Xp: '/dungeon/ticket-xp.webp',
  Gold: '/dungeon/ticket-gold.webp',
  Equipment: '/dungeon/ticket-equipment.webp',
};

const KEY_BY_NAME: Record<string, DungeonInfo['Key']> = {
  "Koschei's Tower": 'Xp',
  "Zmey's Hoard": 'Gold',
  "Baba Yaga's Hut": 'Equipment',
};

export function ticketIconForName(name: string): string | null {
  const key = KEY_BY_NAME[name];
  return key ? TICKET_ICON[key] : null;
}
