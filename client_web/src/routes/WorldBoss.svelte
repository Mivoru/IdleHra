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
    BOSS_PLATE_COUNT,
    BOSS_WEAK_PLATE_HIDDEN,
    BOSS_WEAK_PLATE_MULTIPLIER,
    BOSS_SESSION_CAP_SECONDS,
  } from '../lib/net/commands';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
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

  const attemptsLeft = $derived(Math.max(0, MAX_BOSS_ATTEMPTS - attempts));

  // Modul: THE ARMOUR, and it is the whole interaction now.
  //
  // This screen used to show an estimate of the player's own damage, because
  // the CLIENT computed that number and posted it. It does not any more - the
  // server takes the damage from the character's real attack power, so there
  // is nothing here to predict and nothing to display about it.
  //
  // What replaces it is a decision: five plates, one of them soft, and the
  // board in front of you is what everyone who attacked before you found out.
  const brokenMask = $derived(snap?.WorldBossBrokenPlateMask ?? 0);
  const weakPlate = $derived(snap?.WorldBossWeakPlate ?? BOSS_WEAK_PLATE_HIDDEN);
  const weakPlateFound = $derived(weakPlate !== BOSS_WEAK_PLATE_HIDDEN);

  function isBroken(index: number): boolean {
    return (brokenMask & (1 << index)) !== 0;
  }

  const brokenCount = $derived(
    Array.from({ length: BOSS_PLATE_COUNT }, (_, i) => i).filter(isBroken).length,
  );

  // Modul: the deduction, stated for the player rather than left implicit.
  //
  // If every plate but one has been broken and nobody has found the weak point,
  // the survivor IS the weak point. Saying so out loud is the difference
  // between a puzzle and a guess - the information is on screen either way, and
  // hiding the conclusion from someone who can see the premises is a riddle,
  // not a decision.
  const deducedPlate = $derived.by(() => {
    if (weakPlateFound) return weakPlate;
    if (brokenCount !== BOSS_PLATE_COUNT - 1) return -1;
    for (let i = 0; i < BOSS_PLATE_COUNT; i++) {
      if (!isBroken(i)) return i;
    }
    return -1;
  });

  let selectedPlate = $state(0);

  // Modul: THE BATTLE SESSION, which used to be invisible from every angle.
  //
  // The server gives a player 300 seconds from their FIRST strike to spend the
  // other two, then rolls every later attack back in silence - inside an
  // encounter that runs for up to seven days. Nothing carried the deadline, so
  // the button stayed enabled and did nothing, forever, with no message. An
  // idle player who strikes once and comes back later is the NORMAL case in
  // this genre; it cost them two thirds of their participation and never said
  // why.
  const sessionEndsEpoch = $derived(Number(snap?.WorldBossSessionEndsEpoch ?? 0));

  let nowEpoch = $state(Math.floor(Date.now() / 1000));
  $effect(() => {
    // One second is the resolution the countdown is displayed at; anything
    // faster is a timer nobody can read.
    const id = setInterval(() => (nowEpoch = Math.floor(Date.now() / 1000)), 1000);
    return () => clearInterval(id);
  });

  const sessionStarted = $derived(sessionEndsEpoch > 0);
  const sessionExpired = $derived(sessionStarted && nowEpoch >= sessionEndsEpoch);
  const sessionSecondsLeft = $derived(sessionStarted ? Math.max(0, sessionEndsEpoch - nowEpoch) : 0);

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
      plateIndex: selectedPlate,
      eventState,
      bossCurrentHp: currentHp,
      attemptCount: attempts,
      larderEmpty,
      sessionEndsEpoch,
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

    {#if sessionExpired && attemptsLeft > 0}
      <!-- The second most important sentence on this screen, for the same
           reason as the larder warning above it: the server accepts the attack
           and applies nothing. -->
      <p class="warn" role="status">
        <strong>Your battle session has closed.</strong> It lasts
        {BOSS_SESSION_CAP_SECONDS / 60} minutes from your first strike, and your
        remaining {attemptsLeft} {attemptsLeft === 1 ? 'attempt' : 'attempts'} cannot be
        used until the next encounter.
      </p>
    {:else if sessionStarted && attemptsLeft > 0}
      <p class="dim tiny" role="status">
        Battle session closes in {Math.floor(sessionSecondsLeft / 60)}m {sessionSecondsLeft % 60}s -
        spend your remaining {attemptsLeft} before then.
      </p>
    {/if}

    <h3>Its armour</h3>
    <p class="dim tiny">
      Five plates, one of them soft. A strike on the soft one does
      <strong>{BOSS_WEAK_PLATE_MULTIPLIER}x</strong> damage. A strike anywhere else does full
      damage and <strong>breaks</strong> that plate - for everyone, for the rest of this
      encounter. Which plate is soft changes every encounter.
    </p>

    <div class="armour-plates" role="radiogroup" aria-label="Which plate to strike">
      {#each Array(BOSS_PLATE_COUNT) as _, index}
        <button
          type="button"
          role="radio"
          aria-checked={selectedPlate === index}
          class="armour-plate"
          class:selected={selectedPlate === index}
          class:broken={isBroken(index)}
          class:weak={deducedPlate === index}
          onclick={() => (selectedPlate = index)}
        >
          <span class="armour-plate-index">{index + 1}</span>
          <span class="armour-plate-state">
            {#if deducedPlate === index}
              soft
            {:else if isBroken(index)}
              broken
            {:else}
              intact
            {/if}
          </span>
        </button>
      {/each}
    </div>

    <p class="dim tiny" role="status">
      {#if weakPlateFound}
        Somebody found the soft plate: it is <strong>plate {weakPlate + 1}</strong>. Every
        strike on it pays {BOSS_WEAK_PLATE_MULTIPLIER}x.
      {:else if deducedPlate >= 0}
        Every other plate is broken and nobody has found the soft one, so it must be
        <strong>plate {deducedPlate + 1}</strong>.
      {:else if brokenCount === 0}
        Nobody has struck this boss yet. Whatever you learn, everyone else will see.
      {:else}
        {brokenCount} of {BOSS_PLATE_COUNT} plates broken, and the soft one is not among them.
      {/if}
    </p>

    <button
      class="attack"
      disabled={eventState !== BossEventState.Active || attemptsLeft === 0 || larderEmpty || currentHp <= 0 || sessionExpired}
      onclick={attack}
    >
      Strike plate {selectedPlate + 1}
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

  .armour-plates {
    display: grid;
    grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: 0.4rem;
    margin: 0.6rem 0;
  }

  .armour-plate {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.15rem;
    padding: 0.5rem 0.2rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-panel);
    cursor: pointer;
    font-size: 0.75rem;
    line-height: 1.2;
  }

  .armour-plate-index {
    font-size: 1.1rem;
    font-weight: 600;
  }

  .armour-plate-state {
    opacity: 0.7;
    /* The five states have to fit a 390px phone, so the word truncates rather
       than wrapping the grid into two rows. */
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .armour-plate.broken {
    opacity: 0.55;
    border-style: dashed;
  }

  .armour-plate.weak {
    border-color: var(--good);
    color: var(--good);
    opacity: 1;
  }

  .armour-plate.selected {
    outline: 2px solid var(--accent);
    outline-offset: -2px;
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
