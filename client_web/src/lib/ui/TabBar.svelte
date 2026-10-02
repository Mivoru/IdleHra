<script lang="ts">
  // Modul: THE FIVE PLACES A THUMB GOES MOST (owner, 2026-09-28).
  //
  // On a phone every screen sat behind "Menu", so going anywhere was two taps
  // and a scan of twenty-seven words - including to the four screens a session
  // actually lives on. A bottom bar is where a phone player's thumb already
  // rests, and five is the most that fit at 360px with a 44px touch target and
  // a readable label each.
  //
  // WHICH FIVE, and why these: Home (where a session starts and the "right
  // now" cards live), Combat and Gathering (the two jobs a character does),
  // Character (gear and attributes - where every drop and level-up sends you)
  // and More (task 95): every other destination, grouped, in a sheet that
  // opens from here. Village held the fifth slot before More; it is in the
  // sheet now. On a desktop the header shows the same groups as dropdowns,
  // and this bar does not exist.
  //
  // Labels are the nav's own words, so a player who uses both never meets two
  // names for one place.
  import { playerState } from '../stores/game';
  import { MAIN_TABS, MORE_TAB } from './tabs';
  import MailBadge from './MailBadge.svelte';
  import { unopenedChests } from '../stores/cosmeticChests';
  import { isAutomationNote } from './slots';

  interface Props {
    current: string;
    onNavigate: (screen: string) => void;
    /** Whether the More sheet is open. */
    moreOpen: boolean;
    onMore: () => void;
  }

  const { current, onNavigate, moreOpen, onMore }: Props = $props();

  const SCREEN_TABS: ReadonlySet<string> = new Set(MAIN_TABS.map((t) => t.key).filter((k) => k !== MORE_TAB));

  // More is "where you are" while its sheet is open, and also while the screen
  // on show is one of the sheet's - the bar always marks the current place.
  function isActive(key: string): boolean {
    if (key === MORE_TAB) return moreOpen || !SCREEN_TABS.has(current);
    return !moreOpen && current === key;
  }

  const snap = $derived($playerState);

  // A dot, not a number: it says "something here wants you", and the screen
  // says what. Halted = not earning; unspent points = power already owned.
  // Task 85: an automation note (a rule acting) is not a halt - no dot.
  const combatAlert = $derived(snap ? Number(snap.ActivityHaltReason) !== 0 && !isAutomationNote(Number(snap.ActivityHaltReason)) : false);
  // Task 54: an unopened cosmetic chest also wants the player - the Wardrobe
  // sits beside Character in the Menu, and Character links to it.
  const characterAlert = $derived((snap ? Number(snap.UnspentAttributePoints) > 0 : false) || $unopenedChests > 0);

  const TABS = MAIN_TABS;

  // Task 95: the sheet's own wants - skill points to spend - dot the More tab;
  // unclaimed mail rides on it as a count (MailBadge below).
  const moreAlert = $derived(snap ? Number(snap.AvailableSkillPoints) > 0 : false);

  function alertFor(key: string): boolean {
    if (key === 'combat') return combatAlert;
    if (key === 'character') return characterAlert;
    if (key === MORE_TAB) return moreAlert;
    return false;
  }
</script>

<nav class="tabbar" aria-label="Main">
  {#each TABS as tab (tab.key)}
    <button
      class="tab"
      class:active={isActive(tab.key)}
      aria-current={tab.key !== MORE_TAB && current === tab.key ? 'page' : undefined}
      aria-expanded={tab.key === MORE_TAB ? moreOpen : undefined}
      aria-haspopup={tab.key === MORE_TAB ? 'dialog' : undefined}
      data-tab={tab.key}
      onclick={() => (tab.key === MORE_TAB ? onMore() : onNavigate(tab.key))}
    >
      <span class="icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
          {#if tab.icon === 'map'}
            <path d="M3 6.5 9 4l6 2.5L21 4v13.5L15 20l-6-2.5L3 20z" />
            <path d="M9 4v13.5M15 6.5V20" />
          {:else if tab.icon === 'swords'}
            <path d="M4 4l9.5 9.5M4 4v3.5M4 4h3.5" />
            <path d="M20 4l-9.5 9.5M20 4v3.5M20 4h-3.5" />
            <path d="M8 16l-3 3M16 16l3 3M6.5 13.5l4 4M17.5 13.5l-4 4" />
          {:else if tab.icon === 'pick'}
            <path d="M4.5 9.5C8 5 14 4 19.5 6 15 6.5 11 8.5 8 12" />
            <path d="M10.5 9.5 20 19" />
          {:else if tab.icon === 'hero'}
            <circle cx="12" cy="7.5" r="3.5" />
            <path d="M5 20c.8-4 3.6-6 7-6s6.2 2 7 6" />
          {:else}
            <circle cx="5.5" cy="12" r="1.6" />
            <circle cx="12" cy="12" r="1.6" />
            <circle cx="18.5" cy="12" r="1.6" />
          {/if}
        </svg>
        {#if alertFor(tab.key)}<span class="dot" aria-label="needs attention"></span>{/if}
        {#if tab.key === MORE_TAB}<span class="count"><MailBadge quiet /></span>{/if}
      </span>
      <span class="label">{tab.label}</span>
    </button>
  {/each}
</nav>

<style>
  /* Phone only. On a desktop the header already shows every destination at
     once, and a second nav would be the same list twice. */
  .tabbar {
    display: none;
  }

  @media (max-width: 40rem) {
    .tabbar {
      position: fixed;
      left: 0;
      right: 0;
      bottom: 0;
      z-index: 50;
      display: grid;
      grid-template-columns: repeat(5, 1fr);
      /* The gesture bar is not a place to put a button, and neither is the
         very last edge of the glass on a phone that reports no inset - a
         thumb resting there is holding the phone. check:touch holds pinned
         controls 8px clear. */
      padding-bottom: max(var(--sa-bottom), 0.5rem);
      padding-left: var(--sa-left);
      padding-right: var(--sa-right);
      background: var(--bg-panel);
      border-top: 1px solid var(--border);
      box-shadow: 0 -4px 14px rgba(0, 0, 0, 0.12);
    }
  }

  .tab {
    height: 3.5rem;
    min-width: 0;
    display: grid;
    place-items: center;
    align-content: center;
    gap: 0.15rem;
    padding: 0.3rem 0.1rem 0.25rem;
    border: none;
    border-radius: 0;
    background: transparent;
    color: var(--text-dim);
    position: relative;
    box-shadow: none;
  }

  /* The indicator is a bar along the top edge rather than a filled pill, so
     the active tab reads at a glance without the bar looking like five
     buttons of which one is pressed. */
  .tab.active {
    color: var(--text);
  }

  /* Modul: the press, which .tab's flat transparent look had taken away
     (task 106) - a thumb on the bar got no sign it had landed. A tint while
     the finger is down, nothing that outlives it. */
  .tab:active {
    background: var(--tint-selected);
  }

  .tab.active::before {
    content: '';
    position: absolute;
    top: 0;
    left: 22%;
    right: 22%;
    height: 3px;
    border-radius: 0 0 3px 3px;
    background: var(--accent);
  }

  .icon {
    position: relative;
    display: block;
    line-height: 0;
  }

  .dot {
    position: absolute;
    top: -2px;
    right: -5px;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--danger);
    border: 1.5px solid var(--bg-panel);
  }

  /* The unclaimed-mail count, top-right of the More icon. */
  .count {
    position: absolute;
    top: -7px;
    left: 14px;
    line-height: 1;
  }

  .count :global(.badge) {
    font-size: 0.6rem;
    padding: 0.1rem 0.25rem;
  }

  .label {
    font-size: 0.68rem;
    line-height: 1;
    letter-spacing: 0.01em;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 100%;
  }
</style>
