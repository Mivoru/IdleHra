<script lang="ts">
  // Modul: THE LOGIN WEEK AS SEVEN TILES (owner, 2026-11-15), shared by the
  // daily window and the Progress screen so the two can never disagree about
  // what a day pays. Each tile says which of three things it is - taken, today,
  // still ahead - because seven identical tiles once read as "my earlier
  // rewards are still waiting". Day seven is the wide one: it is the reason to
  // come back, so it is drawn as the prize.
  import type { LoginBonusState, LoginWeekDay } from '../net/rest';
  import { TICKET_ICON } from './dungeonTickets';
  import Money from './Money.svelte';
  import { currencyIcon } from './sprites';

  interface Props {
    state: LoginBonusState;
  }

  let { state: week }: Props = $props();

  // A loot chest tier and a wardrobe rarity both map onto the four chest stills.
  const LOOT_STILL: Record<string, string> = {
    Bronze: '/chest/stage/loot-common-closed.webp',
    Silver: '/chest/stage/loot-rare-closed.webp',
    Gold: '/chest/stage/loot-epic-closed.webp',
    Ancient: '/chest/stage/loot-legendary-closed.webp',
  };

  type TileState = 'taken' | 'today' | 'ahead';

  function tileState(day: LoginWeekDay): TileState {
    if (week.ClaimableToday) {
      if (day.Day === week.Day) return 'today';
      // Before today's claim, the days already taken this week are the ones up to the last claimed.
      return week.LastClaimedDay < 7 && day.Day <= week.LastClaimedDay ? 'taken' : 'ahead';
    }
    return day.Day <= week.Day ? 'taken' : 'ahead';
  }
</script>

<ol class="week" data-testid="login-week">
  {#each week.Days as day (day.Day)}
    {@const st = tileState(day)}
    <li class="tile {st}" class:prize={day.Day === 7} data-day={day.Day} data-state={st}>
      <span class="label">{st === 'today' ? 'Today' : `Day ${day.Day}`}</span>
      <div class="loot">
        {#if day.LootChest}
          <img class="chest" src={LOOT_STILL[day.LootChest]} alt="" width="56" height="56" decoding="async" />
        {:else if day.WardrobeChest}
          <img class="chest" src="/chest/stage/cosmetic-closed.webp" alt="" width="56" height="56" decoding="async" />
        {:else if day.TicketsPerGate > 0}
          <span class="tickets">
            <img src={TICKET_ICON.Xp} alt="" width="28" height="28" />
            <img src={TICKET_ICON.Gold} alt="" width="28" height="28" />
            <img src={TICKET_ICON.Equipment} alt="" width="28" height="28" />
          </span>
        {:else}
          <img class="coin" src={currencyIcon(day.Diamonds > 0 && day.Gold === 0 ? "diamond" : "gold") ?? ""} alt="" width="40" height="40" decoding="async" />
        {/if}
      </div>
      <div class="what">
        {#if day.LootChest}<span class="name">{day.LootChest} chest</span>{/if}
        {#if day.WardrobeChest}<span class="name">{day.WardrobeChest} wardrobe chest</span>{/if}
        {#if day.TicketsPerGate > 0}<span class="name">+{day.TicketsPerGate} ticket, every dungeon</span>{/if}
        {#if day.Gold > 0}<Money amount={day.Gold} icon />{/if}
        {#if day.Diamonds > 0}<Money amount={day.Diamonds} kind="diamond" icon />{/if}
      </div>
      {#if st === 'taken'}
        <span class="mark" aria-label="claimed">✓</span>
      {/if}
    </li>
  {/each}
</ol>

<style>
  .week {
    list-style: none;
    margin: 0;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: 0.45rem;
    container-type: inline-size;
  }

  .tile {
    position: relative;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.25rem;
    padding: 0.5rem 0.35rem 0.55rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: linear-gradient(180deg, var(--bg-raised), var(--bg-panel));
    text-align: center;
    min-width: 0;
  }

  .tile.prize {
    grid-column: span 2;
    border-color: var(--brass-lit);
    background:
      radial-gradient(circle at 50% 30%, rgba(255, 190, 80, 0.25), transparent 70%),
      linear-gradient(180deg, var(--bg-raised), var(--bg-panel));
  }

  /* Modul: the pulse animates OPACITY on a layer of its own - an infinite
     box-shadow animation repaints every frame, which gpuBudget.test.ts bans. */
  .tile.today {
    border-color: var(--glow-warm);
    box-shadow: 0 0 0 1px var(--glow-warm), 0 0 12px var(--glow-soft);
  }

  .tile.today::after {
    content: '';
    position: absolute;
    inset: -1px;
    border-radius: inherit;
    box-shadow: 0 0 22px var(--glow-warm);
    opacity: 0;
    pointer-events: none;
    animation: today-pulse 1.8s ease-in-out infinite;
  }

  .tile.taken {
    opacity: 0.55;
  }

  .label {
    font-size: var(--fs-xs);
    letter-spacing: 0.05em;
    text-transform: uppercase;
    color: var(--text-dim);
  }

  /* A filled badge, not a tinted word: the warm glow colour vanished on the
     light scheme's cream panel (screenshot, 2026-11-15). */
  .today .label {
    padding: 0.05rem 0.5rem;
    border-radius: 999px;
    background: var(--accent);
    color: #1b1206;
    font-weight: var(--fw-bold);
  }

  .loot {
    height: 56px;
    display: grid;
    place-items: center;
  }

  .chest {
    width: 56px;
    height: 56px;
    object-fit: contain;
    filter: drop-shadow(0 3px 4px rgba(0, 0, 0, 0.6));
  }

  .prize .chest {
    width: 64px;
    height: 64px;
  }

  .coin {
    width: 40px;
    height: 40px;
    object-fit: contain;
  }

  .tickets {
    display: flex;
  }

  .tickets img {
    margin: 0 -5px;
    filter: drop-shadow(0 2px 3px rgba(0, 0, 0, 0.6));
  }

  .what {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.1rem;
    font-size: var(--fs-sm);
    min-width: 0;
  }

  .name {
    font-size: var(--fs-xs);
    color: var(--text);
    line-height: var(--lh-tight);
    overflow-wrap: anywhere;
  }

  /* Modul: AN AMOUNT MAY BREAK INSIDE A TILE (2026-11-19). Money is nowrap
     everywhere else, which is right in a ledger row - but a quarter of a 360px
     window is ~70px, and "150 diamonds" or "5.11M gold" ran past the tile's
     edge on every phone (measured: 4-11px out on each side). Here the unit
     drops under the number instead, and the coin stays with the figure. */
  .what :global(.money) {
    white-space: normal;
    flex-wrap: wrap;
    justify-content: center;
    row-gap: 0;
    max-width: 100%;
    line-height: var(--lh-tight);
  }

  @container (max-width: 26rem) {
    .what {
      font-size: var(--fs-xs);
    }
  }

  .mark {
    position: absolute;
    top: 0.25rem;
    right: 0.35rem;
    color: var(--good);
    font-weight: var(--fw-black);
  }

  @keyframes today-pulse {
    0%, 100% { opacity: 0; }
    50% { opacity: 0.85; }
  }

  @media (prefers-reduced-motion: reduce) {
    .tile.today::after {
      animation: none;
    }
  }
</style>
