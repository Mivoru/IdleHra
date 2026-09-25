<script lang="ts">
  // Modul: the world boss. A server-wide encounter that scales with how many
  // accounts are online and their combined race mastery, so its health bar is
  // shared by everyone - the one place in this game where a player's progress
  // is visible to strangers in real time.
  //
  // Missing from the web client entirely until the 2026-08-02 audit. Its rules
  // used to be enforced by SILENT ROLLBACK; since task 25 the server answers
  // every refused strike with a result code, and this screen still says each
  // rule out loud before the player has to press anything.

  import {
    attackWorldBoss,
    BossEventState,
    nextBossMonday,
    nextStrikeRefill,
    MAX_BOSS_ATTEMPTS,
    BOSS_PLATE_COUNT,
    BOSS_WEAK_PLATE_HIDDEN,
    BOSS_WEAK_PLATE_MULTIPLIER,
  } from '../lib/net/commands';
  import { playerState, pushLocalNotice } from '../lib/stores/game';
  import { play } from '../lib/ui/audio';
  import ShieldWheel from '../lib/ui/ShieldWheel.svelte';
  import { fetchBossChallenge, issueBossChallenge, type ShieldWheelChallenge } from '../lib/net/rest';
  import { worldBossResultSentence } from '../lib/game/worldBossResults';

  // Modul: THE SHIELD WHEEL, PRACTICE ONLY (task 36 Phase 1). The server says
  // whether the wheel is open (FOLKIDLE_BOSS_MINIGAME); with it off, GET
  // /challenge answers Disabled and this screen is exactly what it was. The
  // plate buttons below are still the real strike until Phase 2.
  let wheelMode = $state('off');
  let practiceChallenge = $state<ShieldWheelChallenge | null>(null);
  let openingPractice = $state(false);

  $effect(() => {
    fetchBossChallenge()
      .then((answer) => (wheelMode = answer.Result === 'Disabled' ? 'off' : answer.Mode))
      .catch(() => (wheelMode = 'off'));
  });

  async function openPractice() {
    if (openingPractice) return;
    openingPractice = true;
    try {
      const answer = await issueBossChallenge(true);
      if (answer && (answer.Result === 'Issued' || answer.Result === 'Outstanding') && answer.Challenge) {
        practiceChallenge = answer.Challenge;
      } else if (answer) {
        pushLocalNotice(worldBossResultSentence(answer.Result) || 'Practice is not available right now.');
      }
    } catch (err) {
      pushLocalNotice(err instanceof Error ? err.message : 'Practice could not be opened.');
    } finally {
      openingPractice = false;
    }
  }

  function closePractice() {
    practiceChallenge = null;
  }

  async function practiceAgain() {
    practiceChallenge = null;
    await openPractice();
  }

  const snap = $derived($playerState);

  const eventState = $derived(snap?.WorldBossEventState ?? BossEventState.Dormant);
  const maxHp = $derived(Number(snap?.WorldBossMaxHp ?? 0));
  const currentHp = $derived(Number(snap?.WorldBossCurrentHp ?? 0));
  const attempts = $derived(snap?.WorldBossAttemptCount ?? 0);
  const endEpoch = $derived(Number(snap?.WorldBossEventEndEpoch ?? 0));

  const hpPct = $derived(maxHp > 0 ? Math.max(0, Math.min(1, currentHp / maxHp)) : 0);

  const attemptsLeft = $derived(Math.max(0, MAX_BOSS_ATTEMPTS - attempts));

  // Modul: WHEN, not "at some point". A correct "0 attempts" was once reported
  // as a broken feature because nothing on the screen said when they come back.
  // Since 2026-09-25 there are two "whens": the strike refills at UTC midnight,
  // and a fallen boss is replaced on Monday (WorldBossCalendar, pinned by
  // serverMirrors.test.ts).
  // en-GB, not the browser's locale: every other sentence on this screen is
  // English, and a Czech phone printed "arrives on pondělí" mid-sentence.
  const onDay = (d: Date) => d.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'UTC' });
  const returnsLabel = $derived(`A new boss arrives on ${onDay(nextBossMonday(new Date()))} at 00:00 UTC.`);
  const refillLabel = $derived.by(() => {
    const at = nextStrikeRefill(new Date());
    return `Your strike comes back at ${at.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', timeZoneName: 'short' })}.`;
  });
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

  // Modul: THE 300-SECOND BATTLE SESSION IS GONE (owner, 2026-09-24), and with
  // it the countdown this screen used to show. With one strike a day there is
  // nothing for a session to fence.
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

  // Modul: ONE STRIKE IN FLIGHT AT A TIME (task 25). A double-tap on a phone
  // used to send two strikes inside 100 ms, and the server answered the second
  // by DISCONNECTING. The server no longer does, but a second tap before the
  // first is answered is never what the player meant: the button waits for
  // the attempt count to move, or five seconds, whichever is first. A refused
  // strike reports itself through the result toast in the meantime.
  let striking = $state(false);
  let attemptsAtStrike = 0;
  let strikeTimer: ReturnType<typeof setTimeout> | undefined;

  $effect(() => {
    if (striking && attempts !== attemptsAtStrike) {
      striking = false;
      clearTimeout(strikeTimer);
    }
  });

  $effect(() => () => clearTimeout(strikeTimer));

  function attack() {
    if (striking) return;
    const outcome = attackWorldBoss({
      plateIndex: selectedPlate,
      eventState,
      bossCurrentHp: currentHp,
      attemptCount: attempts,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason);
    striking = true;
    attemptsAtStrike = attempts;
    clearTimeout(strikeTimer);
    strikeTimer = setTimeout(() => (striking = false), 5000);
    play('playerHit');
  }

  // Modul: WHY THE BUTTON IS GREY, said NEXT TO the button (task 25). The
  // owner pressed a grey Strike on a day between encounters and read it as
  // broken: the reason was at the top of the panel, a screen away on a phone,
  // and nothing beside the button connected the two.
  const strikeBlockedReason = $derived.by(() => {
    if (eventState !== BossEventState.Active) {
      return `The boss is not here right now. ${returnsLabel}`;
    }
    if (currentHp <= 0) return `The boss has been defeated. ${returnsLabel}`;
    if (attemptsLeft === 0) {
      return `You have used today's strike. ${refillLabel}`;
    }
    if (striking) return 'Striking...';
    return '';
  });

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
        This encounter is over. {returnsLabel} There is nothing to do here until
        then.
      </p>
    {:else}
      <p class="dim">No encounter is running. {returnsLabel}</p>
    {/if}

    <h3>Today's strike</h3>
    <div class="attempts" aria-label={attemptsLeft > 0 ? "Today's strike is ready" : "Today's strike is used"}>
      {#each Array(MAX_BOSS_ATTEMPTS) as _, index}
        <span class="pip" class:spent={index < attempts}></span>
      {/each}
      <span class="dim tiny">{attemptsLeft > 0 ? 'Ready' : 'Used'}</span>
    </div>
    <p class="dim tiny">
      One strike a day, every day. {attemptsLeft > 0 ? 'It refills at midnight UTC.' : refillLabel} A new
      boss arrives every Monday.
    </p>
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
      disabled={strikeBlockedReason !== ''}
      onclick={attack}
    >
      Strike plate {selectedPlate + 1}
    </button>
    {#if strikeBlockedReason}
      <p class="strike-reason dim tiny" role="status">{strikeBlockedReason}</p>
    {/if}

    {#if wheelMode !== 'off'}
      <h3>The shield wheel</h3>
      <p class="dim tiny">
        A new way to strike is coming: spin, read the boss's blows, and aim for the seams. Practice it
        here for free - it spends no attempt and deals no damage.
      </p>
      <button class="practice" disabled={openingPractice} onclick={openPractice}>
        Practice the shield wheel
      </button>
    {/if}
  </section>
</div>

{#if practiceChallenge}
  <!-- Keyed on the challenge: ShieldWheel reads its schedule ONCE (a challenge
       never changes mid-run), so a new challenge must mean a new component. -->
  {#key practiceChallenge.ChallengeId}
    <ShieldWheel challenge={practiceChallenge} onclose={closePractice} onagain={practiceAgain} />
  {/key}
{/if}

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

  .strike-reason {
    margin: 0.4rem 0 0;
    text-align: center;
  }

  .practice {
    width: 100%;
    min-height: 44px;
    margin-top: 0.4rem;
    font-weight: 700;
  }

  .attack:not(:disabled) {
    border-color: var(--rarity-10);
    color: var(--rarity-10);
  }
</style>
