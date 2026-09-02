<script lang="ts">
  // Modul: THE ONE TEACHING SURFACE, for both tiers.
  //
  // The task board offered three options and said coach-marks on the real
  // control are the most effective and the most work. This is the middle it
  // also named, with the useful half of a coach-mark kept: a dismissible panel
  // docked at the bottom, which PULSES the nav button for the screen it is
  // talking about (see coachTargetScreen, applied in App.svelte).
  //
  // Why not a floating bubble anchored to the control: it would need
  // getBoundingClientRect and a position that survives a wrapped nav, a
  // collapsed hamburger and a scrolled panel - which is a clipping bug
  // waiting to happen, and a separate agent is auditing panels for exactly
  // that class right now. Pulsing the real control points at the same thing
  // with no positioning maths at all, and on a narrow screen where the nav is
  // collapsed the "Take me there" button does the pointing instead.
  //
  // It is never modal. No backdrop, nothing to click through, the game keeps
  // running behind it. An idle game whose whole promise is that it runs
  // without you must not fence the player inside a tutorial.
  import { createQuery } from '@tanstack/svelte-query';
  import { queryKeys, fetchStatistics } from '../net/rest';
  import { requestScreen } from '../stores/navigation';
  import {
    onboardingCue,
    acknowledgeCue,
    setOnboardingFacts,
    skipTutorial,
  } from '../stores/tutorial';

  // Modul: the guild answer, and the ONLY reason this component talks to the
  // network. Same query key as GuildOps.svelte, so the two share one cache
  // entry rather than doubling the request. Refetched on an interval because
  // joining a guild is a REST action that no state packet reflects - without
  // it, the guild explanation would wait for a reload.
  const statistics = createQuery(() => ({
    queryKey: queryKeys.statistics,
    queryFn: fetchStatistics,
    staleTime: 60_000,
    refetchInterval: 120_000,
  }));

  // Modul: pushed on every settle, INCLUDING a failure. Null facts hold the
  // baseline back on purpose (see tutorial.ts), so an endpoint that is down
  // would otherwise freeze onboarding entirely rather than degrade it.
  $effect(() => {
    if (statistics.isPending) return;
    setOnboardingFacts({ hasGuild: (statistics.data?.GuildName ?? '') !== '' });
  });

  const cue = $derived($onboardingCue);

  function goThere() {
    if (!cue) return;
    // Modul: a discovery is acknowledged by ACTING on it as well as by "Got
    // it". Being taken to the screen is reading the explanation; making the
    // player then dismiss a panel about a screen they are now looking at is
    // the kind of thing that gets a tutorial turned off.
    const target = cue.screen;
    if (cue.kind === 'discovery') acknowledgeCue();
    requestScreen(target);
  }
</script>

{#if cue}
  <div class="coach" role="status" data-onboarding-cue={cue.id} data-onboarding-kind={cue.kind}>
    <div class="head">
      {#if cue.kind === 'step'}
        <span class="tag">Step {cue.index} / {cue.total}</span>
      {:else}
        <span class="tag new">New</span>
      {/if}
      <strong>{cue.title}</strong>
    </div>
    <p>{cue.body}</p>
    <div class="actions">
      <button class="gilded" onclick={goThere}>Take me there</button>
      {#if cue.kind === 'discovery'}
        <button onclick={acknowledgeCue}>Got it</button>
      {/if}
      <button class="quiet" onclick={skipTutorial}>Skip onboarding</button>
    </div>
  </div>
{/if}

<style>
  /* Modul: NARROW-CONTAINER SAFE BY CONSTRUCTION. Nothing here is measured
     against another element's box, nothing has a fixed pixel width, and the
     action row is allowed to wrap onto its own lines. The panel grid elsewhere
     in the client means a narrow CONTAINER and a narrow VIEWPORT are different
     things; this one is fixed to the viewport, so the viewport clamp below is
     the whole story. */
  .coach {
    position: fixed;
    left: 50%;
    bottom: 0.75rem;
    transform: translateX(-50%);
    width: max-content;
    max-width: min(38rem, calc(100vw - 1.5rem));
    box-sizing: border-box;
    display: grid;
    gap: 0.4rem;
    padding: 0.7rem 0.9rem;
    background: var(--bg-raised);
    border: 1px solid var(--accent);
    border-radius: 0.7rem;
    font-size: 0.85rem;
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.35);
    z-index: 40;
  }

  .head {
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .tag {
    font-size: 0.65rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--text-dim);
    border: 1px solid var(--border);
    border-radius: 999px;
    padding: 0.05rem 0.45rem;
    white-space: nowrap;
  }

  .tag.new {
    color: var(--accent);
    border-color: var(--accent);
  }

  .coach p {
    margin: 0;
    color: var(--text-dim);
    /* Long words in a 38rem box are fine; a 320px phone is where this matters. */
    overflow-wrap: anywhere;
  }

  .actions {
    display: flex;
    gap: 0.4rem;
    flex-wrap: wrap;
  }

  .actions button {
    min-height: 2rem;
  }

  .quiet {
    background: transparent;
    border-color: transparent;
    color: var(--text-dim);
  }
</style>
