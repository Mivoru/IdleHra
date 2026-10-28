// Modul: EVERY PART OF THE GAME HAS A COLOUR (owner, 2026-10-28: "more colour,
// so the eye tells the buttons and windows apart").
//
// The whole UI was one palette - charred oak, parchment, brass - so Combat,
// the Forge and the Market looked like the same page with different words on
// it. Each screen now belongs to a section, the section is written to <html>
// as `data-section`, and app.css re-points three tokens from it (--section,
// --section-deep, --section-ink). Panels take a coloured top edge and title
// rule, the primary button its fill, the tab bar its tint.
//
// Grouped the way the menu groups screens (Game.svelte's NAV_GROUPS), except
// that Gathering is split out of Play: fighting and gathering are the two
// things a player switches between most, and they are the pair that most needs
// to look different. Home, Progress, Wiki and Settings stay brass.
export type Section = 'combat' | 'gather' | 'hero' | 'make' | 'community' | 'home';

const SECTION_OF: Record<string, Section> = {
  combat: 'combat',
  worldboss: 'combat',
  delve: 'combat',
  gathering: 'gather',
  character: 'hero',
  skills: 'hero',
  rebirth: 'hero',
  wardrobe: 'hero',
  chest: 'hero',
  larder: 'hero',
  village: 'make',
  crafting: 'make',
  forge: 'make',
  breeding: 'make',
  ancestors: 'make',
  inheritance: 'make',
  codex: 'make',
  social: 'community',
  guildops: 'community',
  market: 'community',
  mailbox: 'community',
  leaderboards: 'community',
};

export function sectionOf(screen: string | null | undefined): Section {
  return (screen && SECTION_OF[screen]) || 'home';
}

export function applySection(screen: string | null | undefined): void {
  document.documentElement.dataset.section = sectionOf(screen);
}
