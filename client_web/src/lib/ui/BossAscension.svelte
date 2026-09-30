<script lang="ts">
  // Task 87: a boss's Ascension ladder, under its region on the Combat screen.
  // Ten steps, each the one below it plus one more modifier; clearing a step for
  // the first time pays a title (and at steps 5 and 10 a frame) - cosmetics
  // only. Every word here is the server's: the effects, the time limit in
  // seconds and the reward names arrive with the ladder, and this file keeps no
  // copy of any of it. The steps are BUTTONS, not a <select> (a list whose
  // state changes as fights end is exactly what Android's system dialog drops).
  import { startBossAscension } from '../net/commands';
  import { connectionStatus, playerState, pushLocalNotice } from '../stores/game';
  import type { AscensionBoss } from '../net/cosmetics';

  interface Props {
    boss: AscensionBoss;
  }

  let { boss }: Props = $props();

  // The step being looked at. Null follows the next step, so a clear moves the
  // view up the ladder by itself without stomping a step the player picked.
  let picked = $state<number | null>(null);
  const shown = $derived(picked ?? boss.NextStep);
  const step = $derived(boss.Steps.find((s) => s.Step === shown) ?? boss.Steps[0]);
  const next = $derived(boss.Steps.find((s) => s.Step === boss.NextStep) ?? null);

  const live = $derived($connectionStatus.phase === 'live');
  // The wire's armed step, while the fight on screen really is this boss.
  const running = $derived(
    ($playerState?.AscensionStep ?? 0) > 0 && Number($playerState?.CurrentMonsterId ?? 0) === boss.BossMonsterId
      ? Number($playerState?.AscensionStep)
      : 0,
  );

  function stateOf(s: { Cleared: boolean; Startable: boolean }): 'done' | 'next' | 'locked' {
    if (s.Cleared) return 'done';
    return s.Startable ? 'next' : 'locked';
  }

  function start() {
    const outcome = startBossAscension(boss.Region, step.Step);
    if (!outcome.ok) pushLocalNotice(outcome.reason);
  }
</script>

<div class="ascension" data-testid="boss-ascension-{boss.Region}">
  <p class="head">
    <strong>Boss Ascension</strong>
    <span class="dim tiny">
      {boss.HighestStep} / {boss.Steps.length} cleared · cosmetics and titles only
    </span>
  </p>

  {#if !boss.BossDefeated}
    <p class="dim tiny">Beat this boss once, and its ladder opens.</p>
  {:else}
    <div class="steps" role="group" aria-label="Ascension steps">
      {#each boss.Steps as s (s.Step)}
        <button
          type="button"
          class="step {stateOf(s)}"
          class:picked={s.Step === shown}
          class:running={s.Step === running}
          aria-pressed={s.Step === shown}
          aria-label="Step {s.Step}, {stateOf(s)}"
          data-testid="ascension-step-{boss.Region}-{s.Step}"
          onclick={() => (picked = s.Step)}
        >
          <span class="n">{s.Step}</span>
          <span class="tag">{stateOf(s) === 'done' ? '✓' : stateOf(s) === 'next' ? 'open' : 'locked'}</span>
        </button>
      {/each}
    </div>

    <div class="detail">
      {#if next && shown === boss.NextStep && !next.Cleared}
        <p class="tiny"><strong>Next, step {next.Step}:</strong> {next.Summary}</p>
      {:else}
        <p class="tiny"><strong>Step {step.Step}:</strong> {step.Summary}</p>
      {/if}
      <ul class="effects">
        {#each step.Effects as line (line)}
          <li class="tiny">{line}</li>
        {/each}
      </ul>
      <p class="tiny reward">
        {step.Cleared ? 'Earned' : 'First clear pays'}: <strong>{step.RewardTitle}</strong>{step.RewardFrame
          ? ` and the ${step.RewardFrame} frame`
          : ''}
      </p>
      <div class="actions">
        <button
          type="button"
          class="start"
          data-testid="ascension-start-{boss.Region}"
          disabled={!live || !step.Startable}
          onclick={start}
        >
          {running === step.Step ? `Step ${step.Step} is running` : `Start step ${step.Step}`}
        </button>
        {#if running > 0}
          <span class="dim tiny">Changing activity or falling ends the attempt.</span>
        {/if}
      </div>
    </div>
  {/if}
</div>

<style>
  .ascension {
    margin: 0.25rem 0 1rem;
    padding: 0.5rem 0.75rem;
    border-left: 3px solid var(--accent, #c9a227);
    background: color-mix(in srgb, var(--accent, #c9a227) 7%, transparent);
  }

  .head {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem 0.6rem;
    align-items: baseline;
    margin: 0 0 0.4rem;
  }

  .steps {
    display: grid;
    grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: 0.35rem;
  }

  .step {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0.05rem;
    min-width: 44px;
    min-height: 44px;
    padding: 0.2rem 0.1rem;
    flex-shrink: 0;
  }

  .step .n {
    font-weight: 700;
  }

  .step .tag {
    font-size: 0.68rem;
    opacity: 0.8;
  }

  .step.done {
    border-color: var(--good, #4a4);
  }

  .step.locked {
    opacity: 0.55;
  }

  .step.picked {
    outline: 2px solid var(--accent, #c9a227);
    outline-offset: 1px;
  }

  .step.running {
    box-shadow: 0 0 0 2px var(--good, #4a4);
  }

  .detail {
    margin-top: 0.5rem;
    display: grid;
    gap: 0.25rem;
  }

  .detail p {
    margin: 0;
  }

  .effects {
    list-style: disc;
    margin: 0;
    padding-left: 1.1rem;
  }

  .reward strong {
    color: var(--accent, #c9a227);
  }

  .actions {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.5rem;
  }

  .start {
    min-height: 44px;
    min-width: 44px;
    flex-shrink: 0;
  }
</style>
