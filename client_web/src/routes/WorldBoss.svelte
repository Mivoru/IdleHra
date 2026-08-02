<script lang="ts">
  // Modul: the world boss. A server-wide encounter that scales with how many
  // accounts are online and their combined race mastery, so its health bar is
  // shared by everyone - the one place in this game where a player's progress
  // is visible to strangers in real time.
  //
  // Missing from the web client entirely until the 2026-08-02 audit. Three of
  // its rules are enforced by SILENT ROLLBACK rather than a message, so most of
  // this screen exists to say out loud what the server will not.

  import {
    attackWorldBoss,
    BossEventState,
    MAX_BOSS_ATTEMPTS,
    MAX_PREDICTED_DAMAGE,
  } from '../lib/net/commands';
  import { playerState, typicalHit, pushLocalNotice } from '../lib/stores/game';
  import { play } from '../lib/ui/audio';

  const snap = $derived($playerState);

  const eventState = $derived(snap?.WorldBossEventState ?? BossEventState.Dormant);
  const maxHp = $derived(Number(snap?.WorldBossMaxHp ?? 0));
  const currentHp = $derived(Number(snap?.WorldBossCurrentHp ?? 0));
  const attempts = $derived(snap?.WorldBossAttemptCount ?? 0);
  const endEpoch = $derived(Number(snap?.WorldBossEventEndEpoch ?? 0));

  const hpPct = $derived(maxHp > 0 ? Math.max(0, Math.min(1, currentHp / maxHp)) : 0);

  // Auto-eat food depletion makes the server discard the attack without a
  // word, so the larder is checked on exactly the fields it checks.
  const larderEmpty = $derived(
    !snap || (snap.Food1_Count <= 0 && snap.Food2_Count <= 0 && snap.Food3_Count <= 0),
  );

  // The server floors an attack at 1000 regardless of what is sent, so an
  // account that has never been in combat still contributes something rather
  // than being blocked from an event it is eligible for.
  const SERVER_DAMAGE_FLOOR = 1000;
  const estimate = $derived(Math.max($typicalHit ?? 0, SERVER_DAMAGE_FLOOR));
  const measured = $derived($typicalHit !== null);

  // What the server will actually apply: the estimate clamped up to the floor,
  // down to the ceiling, and finally down to what the boss has left.
  const projected = $derived(Math.min(Math.min(estimate, MAX_PREDICTED_DAMAGE), currentHp));

  const attemptsLeft = $derived(Math.max(0, MAX_BOSS_ATTEMPTS - attempts));

  let remainingLabel = $state('');
  $effect(() => {
    if (eventState !== BossEventState.Active || endEpoch <= 0) {
      remainingLabel = '';
      return;
    }
    const tick = () => {
      const seconds = endEpoch - Math.floor(Date.now() / 1000);
      if (seconds <= 0) {
        remainingLabel = 'closing';
        return;
      }
      const h = Math.floor(seconds / 3600);
      const m = Math.floor((seconds % 3600) / 60);
      remainingLabel = h > 0 ? `${h}h ${m}m left` : `${m}m ${seconds % 60}s left`;
    };
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  });

  function attack() {
    const outcome = attackWorldBoss({
      predictedDamage: estimate,
      eventState,
      bossCurrentHp: currentHp,
      attemptCount: attempts,
      larderEmpty,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    play('playerHit');
  }

  const stateLabel = $derived(
    eventState === BossEventState.Active
      ? 'Active'
      : eventState === BossEventState.Concluded
        ? 'Concluded'
        : 'Dormant',
  );
</script>

<div class="wrap">
  <section class="panel" class:live={eventState === BossEventState.Active}>
    <header class="head">
      <h2>World Boss</h2>
      <span class="state" data-state={stateLabel.toLowerCase()}>{stateLabel}</span>
      {#if remainingLabel}
        <span class="dim tiny">{remainingLabel}</span>
      {/if}
    </header>

    {#if eventState === BossEventState.Active}
      <div class="bar" role="progressbar" aria-valuenow={currentHp} aria-valuemin="0" aria-valuemax={maxHp}>
        <div class="bar-fill boss" style="width: {hpPct * 100}%"></div>
        <span class="bar-label">
          {currentHp.toLocaleString()} / {maxHp.toLocaleString()}
          ({(hpPct * 100).toFixed(1)}%)
        </span>
      </div>

      <p class="dim small">
        Shared by every player on the server. Its health scales with how many
        accounts are online and their combined race mastery, so it moves even
        when you are not attacking.
      </p>
    {:else if eventState === BossEventState.Concluded}
      <p class="dim">
        This encounter is over. The boss returns on the next scheduled window -
        there is nothing to do here until then.
      </p>
    {:else}
      <p class="dim">No encounter is running. This screen wakes up when one starts.</p>
    {/if}

    <h3>Your attempts</h3>
    <div class="attempts" aria-label="{attemptsLeft} of {MAX_BOSS_ATTEMPTS} attempts remaining">
      {#each Array(MAX_BOSS_ATTEMPTS) as _, index}
        <span class="pip" class:spent={index < attempts}></span>
      {/each}
      <span class="dim tiny">{attemptsLeft} of {MAX_BOSS_ATTEMPTS} left</span>
    </div>

    {#if larderEmpty}
      <!-- The single most important sentence on this screen. With an empty
           larder the server ACCEPTS the attack, applies nothing, and reports
           nothing - so without this the player just watches a working button
           do nothing forever. -->
      <p class="warn" role="status">
        <strong>Your larder is empty.</strong> An attack sent now is discarded by
        the server without applying any damage and without telling you. Stock
        food before attacking.
      </p>
    {/if}

    <h3>Damage</h3>
    <dl class="stats">
      <div>
        <dt>Your typical hit</dt>
        <dd>
          {#if measured}
            {estimate.toLocaleString()}
          {:else}
            <span class="dim">not measured</span>
          {/if}
        </dd>
      </div>
      <div>
        <dt>Would apply</dt>
        <dd>{projected.toLocaleString()}</dd>
      </div>
    </dl>

    <p class="dim tiny">
      {#if measured}
        Measured from the median of your last sixteen real hits - there is no
        attack stat on the wire to compute it from.
      {:else}
        Nothing measured yet, so the server's own floor of {SERVER_DAMAGE_FLOOR.toLocaleString()}
        applies. Fight anything once and this becomes your real number.
      {/if}
      The server clamps every attack to between {SERVER_DAMAGE_FLOOR.toLocaleString()} and
      whatever the boss has left.
    </p>

    <button
      class="attack"
      disabled={eventState !== BossEventState.Active || attemptsLeft === 0 || larderEmpty || currentHp <= 0}
      onclick={attack}
    >
      Attack
    </button>
  </section>
</div>

<style>
  .wrap {
    padding: 1rem;
    max-width: 34rem;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .panel.live {
    border-color: var(--rarity-10);
  }

  .head {
    display: flex;
    align-items: baseline;
    gap: 0.6rem;
    flex-wrap: wrap;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .state {
    font-size: 0.72rem;
    border-radius: 999px;
    padding: 0.05rem 0.5rem;
    border: 1px solid var(--border);
    color: var(--text-dim);
  }

  .state[data-state='active'] {
    color: var(--rarity-10);
    border-color: var(--rarity-10);
  }

  .boss {
    background: linear-gradient(90deg, var(--rarity-11), var(--rarity-10));
  }

  .dim {
    color: var(--text-dim);
  }
  .small {
    font-size: 0.8rem;
    margin: 0.6rem 0 0;
  }
  .tiny {
    font-size: 0.72rem;
  }

  .warn {
    font-size: 0.82rem;
    color: var(--danger);
    border-left: 2px solid var(--danger);
    padding-left: 0.55rem;
    margin: 0.7rem 0 0;
  }

  .attempts {
    display: flex;
    align-items: center;
    gap: 0.35rem;
  }

  .pip {
    width: 1.6rem;
    height: 0.4rem;
    border-radius: 999px;
    background: var(--good);
  }

  .pip.spent {
    background: var(--border);
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 0.5rem;
    margin: 0;
  }

  .stats div {
    display: grid;
    gap: 0.1rem;
  }

  dt {
    font-size: 0.7rem;
    color: var(--text-dim);
  }

  dd {
    margin: 0;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .attack {
    margin-top: 0.9rem;
    width: 100%;
    padding: 0.6rem;
    font-weight: 700;
  }

  .attack:not(:disabled) {
    border-color: var(--rarity-10);
    color: var(--rarity-10);
  }
</style>
