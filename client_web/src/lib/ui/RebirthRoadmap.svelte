<script lang="ts">
  // The ten rebirths and what each one pays (owner, 2026-10-11: "a roadmap
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

  function stateOf(n: number): 'done' | 'next' | 'ahead' {
    if (n <= taken) return 'done';
    return n === taken + 1 ? 'next' : 'ahead';
  }

  const ICONS: Record<string, string> = {
    skill_points: '✦',
    xp: '⇧',
    pet_slot: '⧉',
    title: '❖',
    badge: '★',
  };
</script>

<div class="roadmap" data-testid="rebirth-roadmap">
  {#each steps as step (step.Rebirth)}
    {@const state = stateOf(step.Rebirth)}
    {@const big = step.Rewards.some((r) => r.Kind !== 'skill_points')}
    <div class="step {state}" class:big class:final={step.Rebirth === steps.length} data-testid="rebirth-step-{step.Rebirth}" data-state={state}>
      <div class="head">
        <span class="shield" class:max={step.Rebirth === steps.length}>{step.Rebirth}</span>
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

<style>
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

  .dim {
    color: var(--text-dim);
  }

  .tiny {
    font-size: 0.72rem;
  }
</style>
