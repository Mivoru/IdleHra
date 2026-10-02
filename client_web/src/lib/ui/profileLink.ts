import { openProfile, prefetchProfile, openGuildView, prefetchGuildView } from '../stores/profile';

/**
 * `use:profileLink={{ playerId, name }}` on any button that shows a player's
 * name: hover, focus or a finger landing on it starts the fetch, and a click
 * opens the profile. See stores/profile.ts for why the fetch starts early.
 *
 * `open: false` keeps the prefetch but leaves the click to the element - chat
 * names open a menu first, and the profile is one tap further on.
 */
export interface ProfileLinkParams {
  playerId: number;
  name?: string;
  open?: boolean;
}

export function profileLink(node: HTMLElement, params: ProfileLinkParams) {
  let current = params;
  const warm = () => prefetchProfile(current.playerId);
  const click = (event: MouseEvent) => {
    if (current.open === false) return;
    event.stopPropagation();
    openProfile(current.playerId, current.name);
  };
  node.addEventListener('pointerenter', warm);
  node.addEventListener('pointerdown', warm);
  node.addEventListener('focus', warm);
  node.addEventListener('click', click);
  node.dataset.profileLink = String(params.playerId);
  return {
    update(next: ProfileLinkParams) {
      current = next;
      node.dataset.profileLink = String(next.playerId);
    },
    destroy() {
      node.removeEventListener('pointerenter', warm);
      node.removeEventListener('pointerdown', warm);
      node.removeEventListener('focus', warm);
      node.removeEventListener('click', click);
    },
  };
}

/** The same for a guild name: `use:guildLink={{ guildId, name }}`. */
export function guildLink(node: HTMLElement, params: { guildId: number; name?: string }) {
  let current = params;
  const warm = () => prefetchGuildView(current.guildId);
  const click = (event: MouseEvent) => {
    event.stopPropagation();
    openGuildView(current.guildId, current.name);
  };
  node.addEventListener('pointerenter', warm);
  node.addEventListener('pointerdown', warm);
  node.addEventListener('focus', warm);
  node.addEventListener('click', click);
  return {
    update(next: { guildId: number; name?: string }) {
      current = next;
    },
    destroy() {
      node.removeEventListener('pointerenter', warm);
      node.removeEventListener('pointerdown', warm);
      node.removeEventListener('focus', warm);
      node.removeEventListener('click', click);
    },
  };
}
