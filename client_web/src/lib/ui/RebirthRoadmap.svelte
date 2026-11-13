<script lang="ts">
  // The rebirths and what each one pays (owner, 2026-10-11: "a roadmap
  // what a player gets each rebirth, with nice design"). Every step is the
  // SERVER's (RebirthRoadmap.Build, sent on the preview) - the same rules the
  // rebirth grants from - so this draws and decides nothing.
  //
  // Cards, not <li>: a phone gets one column, a desktop a grid, and nothing
  // scrolls sideways (check:clipping measures every screen at 360px).
  import type { RebirthRoadmapStep } from '../net/rest';
  import { spriteUrl } from './spriteUrl';

  interface Props {
    steps: RebirthRoadmapStep[];
    /** Rebirths already taken. */
    taken: number;
  }

  let { steps, taken }: Props = $props();

  // Modul: the road has gaps past twenty (2026-11-13: steps 1..20, then the
  // title milestones 30 and 50), so "next" is the first step still ahead, not
  // taken + 1, and the final step is the last card rather than step N.
  const nextRebirth = $derived(steps.find((s) => s.Rebirth > taken)?.Rebirth ?? -1);
  const lastRebirth = $derived(steps.length > 0 ? steps[steps.length - 1].Rebirth : -1);

  // Modul: FOLDED BY DEFAULT (2026-11-13). Twenty-two full cards made the
  // Rebirth screen over 6,000px tall on a phone, and a 20-rebirth account
  // scrolled past twenty taken steps to find the one that matters. The road
  // shows the next few steps and the final one; "Show the whole road" opens
  // every card, taken ones included.
  const UPCOMING_SHOWN = 4;
  let showAll = $state(false);
  const upcoming = $derived(steps.filter((s) => s.Rebirth > taken).slice(0, UPCOMING_SHOWN).map((s) => s.Rebirth));
  const visible = $derived(
    showAll ? steps : steps.filter((s) => upcoming.includes(s.Rebirth) || s.Rebirth === lastRebirth),
  );
  const hiddenCount = $derived(steps.length - visible.length);
  const takenCount = $derived(steps.filter((s) => s.Rebirth <= taken).length);

  function stateOf(n: number): 'done' | 'next' | 'ahead' {
    if (n <= taken) return 'done';
    return n === nextRebirth ? 'next' : 'ahead';
  }

  const ICONS: Record<string, string> = {
    bloodline: '⚘',
    skill_points: '✦',
    xp: '⇧',
    pet_slot: '⧉',
    title: '❖',
    badge: '★',
  };
</script>

{#if !showAll && takenCount > 0}
  <p class="dim tiny road-note">{takenCount} {takenCount === 1 ? 'step' : 'steps'} of the road already taken.</p>
{/if}
<div class="roadmap" data-testid="rebirth-roadmap" data-expanded={showAll}>
  {#each visible as step (step.Rebirth)}
    {@const state = stateOf(step.Rebirth)}
    {@const big = step.Rewards.some((r) => r.Kind !== 'skill_points' && r.Kind !== 'bloodline')}
    <div class="step {state}" class:big class:final={step.Rebirth === lastRebirth} data-testid="rebirth-step-{step.Rebirth}" data-state={state}>
      <div class="head">
        <span class="shield" class:max={step.Rebirth === lastRebirth}>{step.Rebirth}</span>
        <span class="tag">{state === 'done' ? 'Taken' : state === 'next' ? 'Next' : 'Ahead'}</span>
      </div>
      <div class="rewards">
        {#each step.Rewards as r (r.Kind + r.Name)}
          <div class="reward {r.Kind}">
            {#if r.Art}
              <img src={spriteUrl(r.Art)} alt="" loading="lazy" decoding="async" class:portrait={r.Kind === 'avatar'} />
            {:else}
              <span class="icon" aria-hidden="true">{ICONS[r.Kind] ?? '•'}</span>
            {/if}
            <span class="what">
              <strong>{r.Name}</strong>
              <span class="dim tiny">{r.Detail}</span>
            </span>
          </div>
        {/each}
      </div>
    </div>
  {/each}
</div>
{#if hiddenCount > 0 || showAll}
  <button type="button" class="road-toggle" data-testid="rebirth-road-toggle" onclick={() => (showAll = !showAll)}>
    {showAll ? 'Show only the next steps' : `Show the whole road (${steps.length} steps)`}
  </button>
{/if}

<style>
  .road-note {
    margin: 0 0 0.4rem;
  }

  .road-toggle {
    margin-top: 0.6rem;
    min-height: 44px;
  }

  .roadmap {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(min(100%, 13rem), 1fr));
    gap: 0.6rem;
  }

  .step {
    display: grid;
    align-content: start;
    gap: 0.45rem;
    padding: 0.6rem;
    border: 1px solid var(--border);
    border-radius: var(--radius);
    background: var(--bg);
    min-width: 0;
  }

  .step.big {
    border-color: color-mix(in srgb, var(--accent) 45%, var(--border));
  }

  .step.final {
    border-color: #d4a017;
    background: color-mix(in srgb, #ffd700 9%, var(--bg));
  }

  .step.next {
    border-color: var(--accent);
    box-shadow: 0 0 0 1px var(--accent) inset;
  }

  .step.done {
    opacity: 0.62;
  }

  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.4rem;
  }

  /* The same shield the badge beside a name draws (RebirthBadge). */
  .shield {
    display: inline-grid;
    place-items: center;
    /* width, not min-width: app.css's phone rule `.panel * { min-width: 0 !important }` flattens a min-width to a sliver. */
    flex: 0 0 auto;
    width: 2.2rem;
    height: 2.1rem;
    clip-path: polygon(0 0, 100% 0, 100% 62%, 50% 100%, 0 62%);
    background: color-mix(in srgb, var(--accent) 35%, var(--bg));
    color: var(--text);
    font-weight: 800;
    padding-bottom: 0.3rem;
  }

  .shield.max {
    background: linear-gradient(160deg, #fff4b0, #ffd700 45%, #b8860b);
    color: #3a2a00;
  }

  .tag {
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  .step.next .tag {
    color: var(--accent);
    font-weight: 700;
  }

  .rewards {
    display: grid;
    gap: 0.35rem;
  }

  .reward {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 0;
  }

  .reward img {
    flex-shrink: 0;
    width: 2.6rem;
    height: 2.6rem;
    object-fit: contain;
  }

  .reward img.portrait {
    border-radius: 50%;
    object-fit: cover;
    border: 2px solid color-mix(in srgb, var(--accent) 60%, transparent);
  }

  .icon {
    flex-shrink: 0;
    display: inline-grid;
    place-items: center;
    width: 1.6rem;
    height: 1.6rem;
    border-radius: 50%;
    background: color-mix(in srgb, var(--accent) 18%, transparent);
    color: var(--accent);
    font-size: 0.9rem;
  }

  .what {
    display: grid;
    min-width: 0;
    overflow-wrap: anywhere;
    font-size: 0.85rem;
    line-height: 1.25;
  }
</style>
