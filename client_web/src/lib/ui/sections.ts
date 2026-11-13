import { backgroundUrl } from './spriteUrl';

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
  dungeon: 'combat',
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
  expeditions: 'make',
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

// Modul: EACH SECTION HAS ITS OWN PLACE (owner, 2026-11-13: "the brown brick
// wall does not look good ... I really like the skill tree with Yggdrasil
// behind it"). The page's fixed layer drew a tiled cobble pattern on every
// screen; it now draws a painted scene per section - a blood-moon field for
// Combat, a river wood for Gathering, the hero's chamber, the forge, the
// longhouse, the valley for Home - under a dark wash, the same recipe as the
// Skill Tree's Yggdrasil. The URL has to come from here rather than from
// app.css because sprites are served by the game server, whose origin differs
// from the client's in development (spriteUrl).
const sceneUrls = new Map<string, string>();

export function sceneUrl(section: Section | 'underground'): string {
  let url = sceneUrls.get(section);
  if (!url) {
    url = `url("${backgroundUrl(`scene_${section}`)}")`;
    sceneUrls.set(section, url);
  }
  return url;
}

export function applySection(screen: string | null | undefined): void {
  const section = sectionOf(screen);
  const root = document.documentElement;
  root.dataset.section = section;
  root.style.setProperty('--scene', sceneUrl(section));
  // The Delve's underground mood (app.css [data-mood='underground']) paints a
  // cavern instead; set once here so the mood rule only has to name it.
  root.style.setProperty('--scene-underground', sceneUrl('underground'));
}
