// Modul: DAILY MISSIONS, the client's half (owner, 2026-11-15). The server
// owns the day; this file only holds the window's open flag and where each
// mission sends the player. A mission whose screen is not listed simply has no
// "Go" button - the hint says where to do it.
import { writable } from 'svelte/store';

/** Which tab of the Chronicle window is open, or null when it is closed. The side bubble opens it. */
export const chronicleView = writable<null | 'pass' | 'missions'>(null);

/** Where each MissionAction is done, as a requestScreen key. */
export const MISSION_SCREEN: Record<string, string> = {
  DungeonFight: 'dungeon',
  ExpeditionSent: 'expeditions',
  WorldBossStrike: 'worldboss',
  AffixReforge: 'forge',
  AffixRefine: 'forge',
  ItemFused: 'forge',
  ChildBorn: 'breeding',
  FeastThrown: 'village',
  DelveDeep: 'delve',
  BossAscension: 'combat',
  GuildDonation: 'guildops',
  ToolCrafted: 'crafting',
  PotionCrafted: 'crafting',
  MarketListed: 'market',
  AttributePointSpent: 'character',
  SkillPointSpent: 'character',
  WardrobeChestOpened: 'wardrobe',
  LootChestOpened: 'chest',
};
