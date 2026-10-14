<script lang="ts">
  import { formatNumber, numberTitle } from '../lib/ui/format';
  import { formatWhen, minuteClock } from '../lib/ui/when';
  import PlayerAvatar from '../lib/ui/PlayerAvatar.svelte';
  import PlayerTitle from '../lib/ui/PlayerTitle.svelte';
  import { profileLink } from '../lib/ui/profileLink';
  // Modul: the world boss - Chernobog since 2026-10-13. One encounter a week,
  // shared by the whole server. Monday and Tuesday are registration with the
  // health hidden; on Wednesday it is revealed, sized to what the registered
  // players dealt (WorldBossHpRules), and only their damage moves it. Weekly
  // milestones pay the moment they are crossed; Sunday pays the top three and,
  // if he fell, everyone registered. Every number on this screen is read from
  // /api/v1/worldboss/board - it keeps no reward table of its own.
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
  import {
    fetchBossBoard,
    fetchBossChallenge,
    issueBossChallenge,
    strikeBoss,
    type WorldBossBoardView,
    type ShieldWheelChallenge,
    type StrikeResponse,
  } from '../lib/net/rest';
  import { worldBossResultSentence, worldBossResultTone } from '../lib/game/worldBossResults';
  import { tap } from '../lib/net/haptics';
  import { spriteUrl } from '../lib/ui/spriteUrl';
  import { avatarIcon } from '../lib/ui/sprites';
  import SeasonalBossPanel from '../lib/ui/SeasonalBossPanel.svelte';
  import { backgroundUrl } from '../lib/ui/sprites';
  import { bossTimeLeft } from '../lib/game/worldBossTime';

  // Modul: THE SHIELD WHEEL (task 36). The server says which mode it runs
  // (FOLKIDLE_BOSS_MINIGAME); with it off, GET /challenge answers Disabled and
  // this screen is what it always was. Under `practice` the wheel is a free
  // drill and the plate buttons are the real strike over opcode 32. Under
  // `wheel` (Phase 2) the real strike IS the wheel, and the plate buttons
  // become the auto-strike over REST - opcode 32 only tells an old bundle to
  // update.
  let wheelMode = $state('off');
  const wheelStrikes = $derived(wheelMode === 'wheel');
  let practiceChallenge = $state<ShieldWheelChallenge | null>(null);
  let strikeChallenge = $state<ShieldWheelChallenge | null>(null);
  let resumable = $state<ShieldWheelChallenge | null>(null);
  let openingPractice = $state(false);
  let openingStrike = $state(false);
  let autoResult = $state<StrikeResponse | null>(null);

  function sayResolved(resolved: StrikeResponse | null | undefined) {
    if (resolved) pushLocalNotice(worldBossResultSentence(resolved.Result, resolved.Damage), worldBossResultTone(resolved.Result));
  }

  $effect(() => {
    fetchBossChallenge()
      .then((answer) => {
        wheelMode = answer.Result === 'Disabled' ? 'off' : answer.Mode;
        sayResolved(answer.Resolved);
        // A real strike left open (the app was closed mid-run) can be picked
        // up where it was: the server kept its throws and its clock.
        if (answer.Challenge && !answer.Challenge.Practice) resumable = answer.Challenge;
      })
      .catch(() => (wheelMode = 'off'));
  });

  async function openStrike() {
    if (openingStrike) return;
    openingStrike = true;
    autoResult = null;
    try {
      const answer = await issueBossChallenge(false);
      sayResolved(answer?.Resolved);
      if (answer && (answer.Result === 'Issued' || answer.Result === 'Outstanding') && answer.Challenge) {
        resumable = null;
        strikeChallenge = answer.Challenge;
      } else if (answer) {
        pushLocalNotice(worldBossResultSentence(answer.Result) || 'The strike could not be started.', 'error');
      }
    } catch (err) {
      pushLocalNotice(err instanceof Error ? err.message : 'The strike could not be started.', 'error');
    } finally {
      openingStrike = false;
    }
  }

  function closeStrike() {
    strikeChallenge = null;
  }

  // "Leave - finish later" (task 105): the server keeps the run, its throws
  // and its clock, so leaving is only closing the overlay - and the strike
  // button becomes "Finish your strike" until it is picked up again. A run
  // whose clock runs out meanwhile is resolved by the server at the floor.
  function leaveStrike() {
    resumable = strikeChallenge;
    strikeChallenge = null;
  }

  async function openPractice() {
    if (openingPractice) return;
    openingPractice = true;
    try {
      const answer = await issueBossChallenge(true);
      if (answer && (answer.Result === 'Issued' || answer.Result === 'Outstanding') && answer.Challenge) {
        practiceChallenge = answer.Challenge;
      } else if (answer) {
        pushLocalNotice(worldBossResultSentence(answer.Result) || 'Practice is not available right now.', 'error');
      }
    } catch (err) {
      pushLocalNotice(err instanceof Error ? err.message : 'Practice could not be opened.', 'error');
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

  // Modul: THE BOSS DOES NOT HAVE TO FALL (owner, 2026-09-26). Everyone is
  // paid at the end of the week by their rank in damage, so the board is what
  // a strike is FOR: your place, your bracket, and what the whole server has
  // dealt together. Refetched when a strike lands (the pip moves), not on a
  // timer - it only changes when somebody strikes.
  let board = $state<WorldBossBoardView | null>(null);
  const strikesSpent = $derived(snap?.WorldBossAttemptCount ?? 0);
  $effect(() => {
    void strikesSpent;
    // Also when the health appears or moves (the Wednesday reveal, a kill, the
    // window opening): the phase lives on the board, not on the wire.
    void snap?.WorldBossMaxHp;
    void snap?.WorldBossEventState;
    fetchBossBoard()
      .then((view) => (board = view))
      .catch(() => {});
  });

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
  // Task 95: both "whens" go through the shared formatter - the player's own
  // clock and the distance ("Mon 02:00 - in 3 d") - instead of "midnight UTC",
  // which made a player do timezone arithmetic, and which the Delve wrote
  // differently for the same Monday.
  const returnsLabel = $derived(`A new boss arrives ${formatWhen(nextBossMonday($minuteClock), $minuteClock)}.`);
  const refillAt = $derived(formatWhen(nextStrikeRefill($minuteClock), $minuteClock));
  const refillLabel = $derived(`Your strike comes back ${refillAt}.`);
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
      remainingLabel = `${bossTimeLeft(seconds)} left`;
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

  // Modul: THE AUTO-STRIKE (task 36 Phase 2) is today's plate strike exactly -
  // M 1.0, the chosen plate, triple if it happens to be this attempt's weak
  // plate - sent over REST so its answer can say so to this player alone.
  async function autoStrike() {
    if (striking) return;
    striking = true;
    attemptsAtStrike = attempts;
    autoResult = null;
    play('hitMelee');
    try {
      const answer = await strikeBoss({ Mode: 'Auto', Plate: selectedPlate });
      if (answer && answer.Result === 'Landed') {
        autoResult = answer;
        if (answer.BrokePlate >= 0) tap('heavy');
      } else pushLocalNotice(answer ? worldBossResultSentence(answer.Result, answer.Damage) || 'The strike was not recorded.' : 'The strike could not be sent.', answer ? worldBossResultTone(answer.Result) : 'error');
    } catch (err) {
      pushLocalNotice(err instanceof Error ? err.message : 'The strike could not be sent.', 'error');
    } finally {
      striking = false;
    }
  }

  function attack() {
    if (wheelStrikes) return void autoStrike();
    if (striking) return;
    const outcome = attackWorldBoss({
      plateIndex: selectedPlate,
      eventState,
      bossCurrentHp: currentHp,
      attemptCount: attempts,
    });
    if (!outcome.ok) return pushLocalNotice(outcome.reason, 'error');
    striking = true;
    attemptsAtStrike = attempts;
    clearTimeout(strikeTimer);
    strikeTimer = setTimeout(() => (striking = false), 5000);
    play('hitMelee');
  }

  // Modul: WHY THE BUTTON IS GREY, said NEXT TO the button (task 25). The
  // owner pressed a grey Strike on a day between encounters and read it as
  // broken: the reason was at the top of the panel, a screen away on a phone,
  // and nothing beside the button connected the two.
  const strikeBlockedReason = $derived.by(() => {
    if (eventState !== BossEventState.Active) {
      return `The boss is not here right now. ${returnsLabel}`;
    }
    if (attemptsLeft === 0) {
      return `You have used today's strike. ${refillLabel}`;
    }
    if (striking) return 'Striking...';
    return '';
  });

  // Modul: WHO THE BOSS IS comes from the server (WorldBossIdentity, read off
  // monsters.json), on the board answer. Until that answer lands the heading
  // says what it always said, rather than a name this file made up.
  const bossName = $derived(board?.BossName ?? 'World Boss');
  const banner = backgroundUrl('yggdrasil');

  // Modul: CHERNOBOG'S WEEK, as the board reports it. The wire's health is 0
  // during registration (hidden, not dead), so the phase - not the number -
  // decides what the health area says.
  const phase = $derived(board?.Phase ?? 'Closed');
  const registering = $derived(phase === 'Registration');
  const defeated = $derived(board?.Defeated ?? false);
  const revealLabel = $derived(board && board.RevealEpoch > 0 ? formatWhen(new Date(board.RevealEpoch * 1000), $minuteClock) : '');
  // Below a quarter of his health he is the Black Serpent - the same line the
  // server uses for the enraged shield wheel (WorldBossStrikeRules.IsEnraged).
  const enraged = $derived(phase === 'Revealed' && maxHp > 0 && currentHp > 0 && currentHp * 4 <= maxHp);
  const heroArt = $derived(board ? spriteUrl(enraged ? board.EnragedArt : board.BossArt) : '');
  const heroName = $derived(enraged ? 'the Black Serpent' : bossName);
  const registered = $derived(board?.Me?.Registered ?? false);

  // The next milestone the player has not reached this week, for the progress bar.
  const myDamage = $derived(board?.Me?.Damage ?? 0);
  const nextMilestone = $derived(board?.Milestones.find((m) => !m.Reached) ?? null);
  const prevMilestoneDamage = $derived.by(() => {
    if (!board) return 0;
    const reached = board.Milestones.filter((m) => m.Reached);
    return reached.length ? reached[reached.length - 1].Damage : 0;
  });
  const milestonePct = $derived(
    nextMilestone ? Math.max(0, Math.min(1, (myDamage - prevMilestoneDamage) / (nextMilestone.Damage - prevMilestoneDamage))) : 1,
  );
  const goldHoursLabel = (hours: number) => (hours === 1 ? '1 hour' : `${hours} hours`);

  // Modul: THE RULES ARE A DISCLOSURE, OPEN UNTIL THE FIRST STRIKE (task 105).
  // An 8-line armour rule and two more paragraphs pushed the strike to about
  // y 640 on a phone. A player who has never struck needs them; one who has
  // struck this boss has read them, so they fold - and either can toggle.
  const hasStruck = $derived(attempts > 0 || !!board?.Me);
  let rulesToggled = $state<boolean | null>(null);
  const rulesOpen = $derived(rulesToggled ?? !hasStruck);

  // The plate is chosen only where it matters: the quick strike.
  let quickOpen = $state(false);

  const stateLabel = $derived(
    eventState === BossEventState.Active
      ? 'Active'
      : eventState === BossEventState.Concluded
        ? 'Concluded'
        : 'Dormant',
  );
</script>

<!-- Modul: THE PLATE IS A CHOICE ONLY WHERE IT CHANGES SOMETHING (task 105).
     The five plates used to be a radio group at the top of the screen that
     fed only the secondary "Auto-strike" button - the prominent wheel strike
     ignored it, so the most visible decision on the page did nothing to the
     action most players took. The armour is shown as status; the picker lives
     inside "Quick strike", or beside the strike when the strike IS the plate
     (no wheel). -->
{#snippet platePicker()}
  <div class="plate-picker" role="radiogroup" aria-label="Which plate to strike">
    {#each Array(BOSS_PLATE_COUNT) as _, index}
      <button
        type="button"
        role="radio"
        aria-checked={selectedPlate === index}
        class="plate-pick"
        class:selected={selectedPlate === index}
        class:broken={isBroken(index)}
        class:weak={deducedPlate === index}
        onclick={() => (selectedPlate = index)}
      >
        {index + 1}
      </button>
    {/each}
  </div>
{/snippet}

<div class="wrap">
  <div class="col main-col">
    <section class="panel hero-panel" class:live={eventState === BossEventState.Active}>
      <header class="hero" style="--banner: url('{banner}')">
        {#if heroArt}
          <img class="boss-art" class:enraged src={heroArt} alt={heroName} decoding="async" data-testid="boss-art" />
        {/if}
        <div class="hero-text">
          <p class="eyebrow">
            <span>World Boss</span>
            <span class="state" data-state={stateLabel.toLowerCase()}>{stateLabel}</span>
            {#if remainingLabel}
              <span class="time">{remainingLabel}</span>
            {/if}
          </p>
          <h2 data-testid="boss-name">{bossName}{#if enraged}<span class="form"> - the Black Serpent</span>{/if}</h2>
        </div>
      </header>

      {#if eventState === BossEventState.Active && registering}
        <p class="phase-note small" data-testid="boss-phase" data-phase="Registration">
          <strong>Registration.</strong> His health is hidden until {revealLabel}. Strike before then
          (level {board?.RegistrationLevel ?? 10}+) to register: your damage counts against him, and you share
          the reward if he falls. {board?.RegisteredCount ?? 0} registered so far.
        </p>
      {:else if eventState === BossEventState.Active && defeated}
        <p class="phase-note small fallen" data-testid="boss-phase" data-phase="Defeated">
          <strong>Driven back into Svarog's chest!</strong> Everyone who registered shares the reward on
          Sunday. Keep striking for your milestones and your place until then.
        </p>
      {:else if eventState === BossEventState.Active && maxHp === 0}
        <p class="phase-note small" data-testid="boss-phase" data-phase="Unopposed">
          Nobody registered this week, so he cannot fall - but your milestones and your place still pay.
        </p>
      {:else if eventState === BossEventState.Active}
        <!-- The numbers sit ABOVE the bar, on the panel: a label inside the
             fill was dark text on dark orange (audit J2). -->
        <div class="hp-row">
          <span class="hp-nums" title={numberTitle(currentHp)}>{formatNumber(currentHp)} / {formatNumber(maxHp)}</span>
          <span class="dim">{(hpPct * 100).toFixed(1)}%</span>
        </div>
        <div class="bar hp" role="progressbar" aria-label="{bossName} health" aria-valuenow={currentHp} aria-valuemin="0" aria-valuemax={maxHp}>
          <div class="bar-fill boss" class:serpent={enraged} style="width: {hpPct * 100}%"></div>
        </div>
      {:else if eventState === BossEventState.Concluded}
        <p class="dim small">This encounter is over. {returnsLabel}</p>
      {:else}
        <p class="dim small">No encounter is running. {returnsLabel}</p>
      {/if}

      {#if eventState === BossEventState.Active && board}
        <p class="dim tiny register-note" data-testid="boss-registered" data-registered={registered}>
          {#if registered}
            You are registered this week.
          {:else if registering}
            You are not registered yet - today's strike will register you.
          {:else}
            You did not register this week: your damage counts for your milestones and your place, not for the kill.
          {/if}
        </p>
      {/if}

      <div class="strike-head">
        <h3>Today's strike</h3>
        <span
          class="strike-chip"
          class:ready={attemptsLeft > 0}
          data-testid="strike-chip"
          data-spent={attempts}
        >
          {attemptsLeft > 0 ? `${attemptsLeft} strike ready` : 'Strike used'}
        </span>
      </div>

      {#if wheelStrikes}
        <!-- The strike: the shield wheel. Up to 2x for a skilled run, and never
             less than a quick strike on the best plate it struck. -->
        <button
          class="attack primary"
          data-guide="worldboss-strike"
          data-testid="wheel-strike"
          disabled={(strikeBlockedReason !== '' && !resumable) || openingStrike}
          onclick={openStrike}
        >
          {resumable ? 'Finish your strike' : 'Strike with the shield wheel'}
        </button>
      {:else}
        {@render platePicker()}
        <button class="attack primary" data-guide="worldboss-strike" disabled={strikeBlockedReason !== ''} onclick={attack}>
          Strike plate {selectedPlate + 1}
        </button>
      {/if}
      {#if strikeBlockedReason && !resumable}
        <p class="strike-reason dim tiny" role="status">{strikeBlockedReason}</p>
      {:else if resumable}
        <p class="strike-reason dim tiny" role="status">Your strike is waiting where you left it.</p>
      {/if}

      {#if autoResult}
        <p class="auto-result small" role="status" data-testid="auto-card" data-damage={autoResult.Damage}>
          {#if autoResult.Landings.some((l) => l.WeakHit)}
            Plate {selectedPlate + 1} was the soft one this time:
          {:else if autoResult.BrokePlate >= 0}
            You broke plate {autoResult.BrokePlate + 1} for everyone:
          {/if}
          <strong title={numberTitle(autoResult.Damage)}>{formatNumber(autoResult.Damage)}</strong> damage ({autoResult.Played.toFixed(2)}x).
        </p>
      {/if}

      <h3>Its armour</h3>
      <ul class="armour-plates" aria-label="The boss's armour">
        {#each Array(BOSS_PLATE_COUNT) as _, index}
          <li class="armour-plate" class:broken={isBroken(index)} class:weak={deducedPlate === index}>
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
          </li>
        {/each}
      </ul>
      <p class="dim tiny plate-note" role="status">
        {#if weakPlateFound}
          Somebody found the soft plate: it is <strong>plate {weakPlate + 1}</strong>. Every
          strike on it pays {BOSS_WEAK_PLATE_MULTIPLIER}x.
        {:else if deducedPlate >= 0 && wheelStrikes}
          Every other plate is broken, so the next strike's soft plate is certain:
          <strong>plate {deducedPlate + 1}</strong>.
        {:else if deducedPlate >= 0}
          Every other plate is broken and nobody has found the soft one, so it must be
          <strong>plate {deducedPlate + 1}</strong>.
        {:else if brokenCount === 0 && wheelStrikes}
          No plate is broken yet today: the soft one could be any of the five.
        {:else if wheelStrikes}
          {brokenCount} of {BOSS_PLATE_COUNT} plates broken today: the soft one is among the other
          {BOSS_PLATE_COUNT - brokenCount}.
        {:else if brokenCount === 0}
          Nobody has struck this boss yet. Whatever you learn, everyone else will see.
        {:else}
          {brokenCount} of {BOSS_PLATE_COUNT} plates broken, and the soft one is not among them.
        {/if}
      </p>

      {#if wheelStrikes}
        <button
          type="button"
          class="disclosure"
          data-testid="quick-strike-toggle"
          aria-expanded={quickOpen}
          onclick={() => (quickOpen = !quickOpen)}
        >
          <span>Quick strike (no skill bonus)</span>
          <span class="chev" aria-hidden="true">{quickOpen ? '▴' : '▾'}</span>
        </button>
        {#if quickOpen}
          <div class="quick">
            <p class="dim tiny">
              No wheel: pick a plate and strike it at 1x skill - {BOSS_WEAK_PLATE_MULTIPLIER}x if it is
              this strike's soft one. It spends today's strike.
            </p>
            {@render platePicker()}
            <button
              class="auto"
              data-testid="auto-strike"
              disabled={strikeBlockedReason !== '' || resumable !== null}
              onclick={attack}
            >
              Quick strike plate {selectedPlate + 1}
            </button>
          </div>
        {/if}
      {/if}

      {#if wheelMode !== 'off'}
        <button class="practice" disabled={openingPractice} onclick={openPractice}>
          Practice the shield wheel
        </button>
        <p class="dim tiny practice-note">Free: it spends no strike and deals no damage.</p>
      {/if}
    </section>
  </div>

  <div class="col side-col">
    <!-- The seasonal boss sits beside the world boss (owner, 2026-10-09); it
         renders only while an event with a boss is on. -->
    <SeasonalBossPanel compact />

    {#if board}
      <section class="panel" data-testid="boss-milestones">
        <h3 class="first">Your milestones this week</h3>
        <p class="small together">
          <strong title={numberTitle(myDamage)}>{formatNumber(myDamage)}</strong> damage
          {#if nextMilestone}- next at {formatNumber(nextMilestone.Damage)}{:else}- every milestone reached{/if}
        </p>
        <div class="bar ms" role="progressbar" aria-label="Progress to the next milestone" aria-valuenow={Math.round(milestonePct * 100)} aria-valuemin="0" aria-valuemax="100">
          <div class="bar-fill ms-fill" style="width: {milestonePct * 100}%"></div>
        </div>
        <ul class="milestones">
          {#each board.Milestones as m (m.Damage)}
            <li class:reached={m.Reached} data-reached={m.Reached}>
              <span class="ms-damage" title={numberTitle(m.Damage)}>{formatNumber(m.Damage)}</span>
              <span class="ms-reward">
                {#if m.Gold > 0}<span title={numberTitle(m.Gold)}>{formatNumber(m.Gold)} gold</span>{/if}
                {#if m.Diamonds > 0}<span>{m.Diamonds} diamonds</span>{/if}
                {#if m.AvatarArt}
                  <span class="ms-cosmetic"><img src={avatarIcon(m.AvatarArt)} alt="" decoding="async" />{m.AvatarName} avatar</span>
                {/if}
                {#if m.Title}<span class="ms-cosmetic">title "{m.Title}"</span>{/if}
                {#if m.PetArt}
                  <span class="ms-cosmetic"><img src={spriteUrl(m.PetArt)} alt="" decoding="async" />{m.PetName} pet</span>
                {/if}
                {#if m.CosmeticOwned}<span class="dim">(yours already)</span>{/if}
              </span>
              <span class="ms-state" aria-label={m.Reached ? 'reached' : 'not yet'}>{m.Reached ? '✓' : ''}</span>
            </li>
          {/each}
        </ul>
        <p class="dim tiny">Paid into your mailbox the moment you cross it. Gold is time of your own income.</p>
      </section>

      <section class="panel" data-testid="boss-sunday">
        <h3 class="first">On Sunday</h3>
        <p class="small together">
          {#if defeated}He fell:{:else}If he falls,{/if} everyone registered gets
          <strong title={numberTitle(board.KillGold)}>{formatNumber(board.KillGold)} gold</strong>
          ({goldHoursLabel(board.KillGoldHours)} of your income) and a <strong>Legendary chest</strong>.
        </p>
        <ol class="places">
          {#each board.Places as place (place.Place)}
            <li>
              <span class="rank">{place.Place}.</span>
              <span class="title-chip" style={place.TitleColor ? `--chip: ${place.TitleColor}` : ''}>{place.Title}</span>
              <span class="dim tiny">{goldHoursLabel(place.GoldHours)} of income{#if place.Holder} - worn by {place.Holder}{/if}</span>
            </li>
          {/each}
        </ol>
        <p class="dim tiny">The week's top three by damage wear these titles until the next Sunday.</p>
      </section>
    {/if}

    <section class="panel">
      <h3 class="first">Damage this week</h3>
      {#if board && board.Participants > 0}
        <p class="small together" data-testid="boss-total">
          Together, {board.Participants} {board.Participants === 1 ? 'player has' : 'players have'} dealt
          <strong title={numberTitle(board.TotalDamage)}>{formatNumber(board.TotalDamage)}</strong> damage{#if board.BossMaxHp > 0}
            &nbsp;({((board.TotalDamage / board.BossMaxHp) * 100).toFixed(2)}% of its health){/if}.
        </p>
        {#if board.Me}
          <p class="small" data-testid="boss-me">
            You are <strong>#{board.Me.Rank}</strong> with {formatNumber(board.Me.Damage)} damage.
          </p>
        {:else}
          <p class="dim tiny">You have not struck this boss yet.</p>
        {/if}
        <ol class="board">
          {#each board.Top.slice(0, 10) as row (row.PlayerId)}
            <li class:me={row.PlayerId === board.Me?.PlayerId}>
              <span class="rank">{row.Rank}</span>
              <span class="who"><PlayerAvatar playerId={row.PlayerId} size="sm" /> <button class="name-link" use:profileLink={{ playerId: row.PlayerId, name: row.Name }}>{row.Name}</button><PlayerTitle playerId={row.PlayerId} /></span>
              <span class="dmg" title={numberTitle(row.Damage)}>{formatNumber(row.Damage)}</span>
            </li>
          {/each}
        </ol>
      {:else}
        <p class="dim tiny">Nobody has struck this boss yet.</p>
      {/if}
    </section>

    <section class="panel rules">
      <button
        type="button"
        class="disclosure"
        data-testid="boss-rules-toggle"
        aria-expanded={rulesOpen}
        onclick={() => (rulesToggled = !rulesOpen)}
      >
        <span>How the World Boss works</span>
        <span class="chev" aria-hidden="true">{rulesOpen ? '▴' : '▾'}</span>
      </button>
      <!-- {#if}, not <details>: see client_web/CLAUDE.md. -->
      {#if rulesOpen}
        <div class="rules-body">
          <p class="dim tiny">
            Svarog sealed Chernobog in a forged chest, but the bargain left darkness half the world - so
            every Monday he breaks the lock and walks among men, and the forge needs mortals to drive him
            back before Sunday.
          </p>
          <p class="dim tiny">
            <strong>Monday and Tuesday are registration</strong>: his health is hidden, and a strike at level
            {board?.RegistrationLevel ?? 10}+ registers you. <strong>On Wednesday it is revealed</strong>, sized
            to what the registered players dealt each day, times a hidden number. Only a registered player's
            damage moves it; anyone can still strike for milestones and a place. Below a quarter of his health
            he becomes the Black Serpent.
          </p>
          <p class="dim tiny">
            One strike a day, every day. {attemptsLeft > 0 ? `It refills ${refillAt}.` : refillLabel}
            A new boss arrives every Monday.
          </p>
          {#if wheelStrikes}
            <p class="dim tiny">
              Five plates, and one of them is soft - a <strong>different one for every strike</strong>,
              chosen among the plates still standing. A hit on it does
              <strong>{BOSS_WEAK_PLATE_MULTIPLIER}x</strong> damage, and only you see where it was. A hit
              anywhere else <strong>breaks</strong> that plate for everyone, so every broken plate makes the
              soft one easier to find. The armour grows back {refillAt}.
            </p>
            <p class="dim tiny">
              The shield wheel: spin, read the boss's blows, and aim for the seams. A skilled run is worth
              up to 2x, and never less than a quick strike on the best plate it hit.
            </p>
          {:else}
            <p class="dim tiny">
              Five plates, one of them soft. A strike on the soft one does
              <strong>{BOSS_WEAK_PLATE_MULTIPLIER}x</strong> damage. A strike anywhere else does full damage
              and <strong>breaks</strong> that plate - for everyone, for the rest of this encounter.
            </p>
          {/if}
          <p class="dim tiny">
            Holding back is noticed: a registered player who has shown the skill before, throws Monday and
            Tuesday away to keep him small, then plays well from Wednesday, gets a quarter of the kill gold,
            no chest - and a word from Chernobog in World chat.
          </p>
        </div>
      {/if}
    </section>
  </div>
</div>

{#if practiceChallenge}
  <!-- Keyed on the challenge: ShieldWheel reads its schedule ONCE (a challenge
       never changes mid-run), so a new challenge must mean a new component. -->
  {#key practiceChallenge.ChallengeId}
    <ShieldWheel challenge={practiceChallenge} onclose={closePractice} onagain={practiceAgain} onleave={closePractice} />
  {/key}
{/if}

{#if strikeChallenge}
  {#key strikeChallenge.ChallengeId}
    <ShieldWheel challenge={strikeChallenge} onclose={closeStrike} onagain={closeStrike} onleave={leaveStrike} />
  {/key}
{/if}

<style>
  /* Centred on a wide screen (owner, 2026-09-28): a 34rem column hugging the
     left edge of a 1900px window read as a layout that had not loaded. Two
     columns from 60rem (task 105): the strike on the left, the board and the
     rules on the right, so the board is no longer below the fold. */
  .wrap {
    padding: 1rem;
    max-width: 34rem;
    margin: 0 auto;
    display: grid;
    gap: 1rem;
    align-items: start;
  }

  @media (min-width: 60rem) {
    .wrap {
      max-width: 68rem;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
    }
  }

  .col {
    display: grid;
    gap: 1rem;
    min-width: 0;
  }

  .panel.live {
    border-color: var(--rarity-10);
  }

  .hero {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin: -1rem -1rem 0.75rem;
    padding: 0.9rem 1rem;
    min-height: 5.5rem;
    border-radius: var(--radius) var(--radius) 0 0;
    /* The painted world tree - Svarog's forge sits at its crown - under a dark
       scrim, and the text on it is the underground palette's light ink: the
       banner is a painting, dark in both themes, so the theme's own --text
       (brown ink on parchment) would vanish on it. */
    background-image:
      linear-gradient(90deg, color-mix(in srgb, var(--ug-bg) 88%, transparent), color-mix(in srgb, var(--ug-bg) 35%, transparent)),
      var(--banner);
    background-size: cover;
    background-position: center 30%;
    color: var(--ug-text);
  }

  .hero h2 {
    color: var(--ug-text);
  }

  .hero-text {
    min-width: 0;
  }

  .boss-art {
    width: 5.5rem;
    height: 5.5rem;
    object-fit: contain;
    flex-shrink: 0;
    filter: drop-shadow(0 0 0.4rem rgba(120, 180, 255, 0.35));
  }

  .boss-art.enraged {
    filter: drop-shadow(0 0 0.5rem rgba(255, 90, 60, 0.5));
  }

  .form {
    font-size: 0.85rem;
    font-weight: 500;
    color: var(--ug-text-dim, var(--ug-text));
  }

  .phase-note {
    margin: 0.2rem 0 0;
    padding: 0.5rem 0.6rem;
    border: 1px solid var(--border);
    border-radius: 6px;
  }

  .phase-note.fallen {
    border-color: var(--good);
  }

  .register-note {
    margin: 0.5rem 0 0;
  }

  .bar-fill.boss.serpent {
    background: linear-gradient(90deg, #2c3e50, #5d6d7e);
  }

  .bar.ms {
    height: 0.5rem;
    margin: 0.3rem 0 0.4rem;
  }

  .ms-fill {
    background: var(--good);
  }

  .milestones,
  .places {
    list-style: none;
    margin: 0.3rem 0;
    padding: 0;
    display: grid;
    gap: 0.25rem;
    font-size: 0.78rem;
  }

  .milestones li {
    display: grid;
    grid-template-columns: 4.5rem minmax(0, 1fr) 1.2rem;
    align-items: center;
    gap: 0.4rem;
    padding: 0.2rem 0.3rem;
    border-radius: 4px;
  }

  .milestones li.reached {
    background: var(--tint-selected);
  }

  .ms-damage {
    font-weight: 700;
    font-variant-numeric: tabular-nums;
  }

  .ms-reward {
    display: flex;
    flex-wrap: wrap;
    gap: 0.2rem 0.5rem;
    min-width: 0;
  }

  .ms-cosmetic {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
  }

  .ms-cosmetic img {
    width: 1.4rem;
    height: 1.4rem;
    border-radius: 50%;
    object-fit: cover;
  }

  .ms-state {
    color: var(--good);
    font-weight: 700;
    text-align: center;
  }

  .places li {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 0.4rem;
  }

  .title-chip {
    border: 1px solid var(--chip, var(--border));
    color: var(--chip, var(--text));
    border-radius: 999px;
    padding: 0 0.5rem;
    font-weight: 600;
  }

  .eyebrow {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.4rem;
    margin: 0 0 0.2rem;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  .time {
    text-transform: none;
    letter-spacing: 0;
    font-variant-numeric: tabular-nums;
  }

  h2 {
    margin: 0;
    font-size: 1.2rem;
    line-height: 1.2;
  }

  h3 {
    margin: 1.1rem 0 0.4rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  h3.first {
    margin-top: 0;
  }

  .state {
    font-size: 0.72rem;
    border-radius: 999px;
    padding: 0.05rem 0.5rem;
    border: 1px solid var(--border);
    background: var(--bg-panel);
    color: var(--text-dim);
  }

  /* "Active" is good news, so it wears the good colour - it was a red pill. */
  .state[data-state='active'] {
    color: var(--good);
    border-color: var(--good);
  }

  .hp-row {
    display: flex;
    justify-content: space-between;
    gap: 0.5rem;
    font-size: 0.85rem;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
    margin-bottom: 0.25rem;
  }

  .bar.hp {
    height: 0.8rem;
  }

  .boss {
    background: linear-gradient(90deg, var(--rarity-11), var(--rarity-10));
  }

  .small {
    font-size: 0.8rem;
    margin: 0.6rem 0 0;
  }

  .strike-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
    margin-top: 0.9rem;
  }

  .strike-head h3 {
    margin: 0;
  }

  .strike-chip {
    font-size: 0.75rem;
    font-weight: 700;
    border-radius: 999px;
    padding: 0.1rem 0.6rem;
    border: 1px solid var(--border);
    color: var(--text-dim);
    white-space: nowrap;
  }

  .strike-chip.ready {
    border-color: var(--good);
    color: var(--good);
  }

  .attack {
    margin-top: 0.6rem;
    width: 100%;
    min-height: 48px;
    padding: 0.6rem;
    font-weight: 700;
  }

  .strike-reason {
    margin: 0.4rem 0 0;
    text-align: center;
  }

  .armour-plates {
    list-style: none;
    padding: 0;
    display: grid;
    grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: 0.4rem;
    margin: 0.4rem 0;
  }

  .armour-plate {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.1rem;
    padding: 0.35rem 0.2rem;
    border: 1px solid var(--border);
    border-radius: 6px;
    font-size: 0.72rem;
    line-height: 1.2;
  }

  .armour-plate-index {
    font-size: 0.95rem;
    font-weight: 600;
  }

  .armour-plate-state {
    color: var(--text-dim);
    /* The five states have to fit a 390px phone, so the word truncates rather
       than wrapping the grid into two rows. */
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .armour-plate.broken {
    opacity: 0.6;
    border-style: dashed;
  }

  .armour-plate.weak {
    border-color: var(--good);
    color: var(--good);
    opacity: 1;
  }

  .armour-plate.weak .armour-plate-state {
    color: var(--good);
  }

  .plate-note {
    margin: 0;
  }

  .plate-picker {
    display: grid;
    grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: 0.4rem;
    margin: 0.6rem 0 0;
  }

  .plate-pick {
    min-height: 44px;
    font-weight: 700;
  }

  .plate-pick.broken {
    border-style: dashed;
  }

  .plate-pick.weak {
    border-color: var(--good);
    color: var(--good);
  }

  .plate-pick.selected {
    outline: 2px solid var(--accent);
    outline-offset: -2px;
  }

  .disclosure {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    min-height: 44px;
    margin-top: 0.8rem;
    font-weight: 600;
    text-align: left;
  }

  .rules .disclosure {
    margin-top: 0;
  }

  .chev {
    color: var(--text-dim);
  }

  .quick {
    display: grid;
    gap: 0.4rem;
    margin-top: 0.5rem;
  }

  .quick p {
    margin: 0;
  }

  .rules-body {
    display: grid;
    gap: 0.5rem;
    margin-top: 0.6rem;
  }

  .rules-body p {
    margin: 0;
  }

  .tiers {
    border-collapse: collapse;
    font-size: 0.78rem;
    width: 100%;
  }

  .tiers th,
  .tiers td {
    text-align: left;
    padding: 0.2rem 0.4rem 0.2rem 0;
    border-bottom: 1px solid var(--line);
    font-variant-numeric: tabular-nums;
  }

  .tiers th {
    font-weight: 600;
  }

  .together {
    margin: 0 0 0.3rem;
  }

  .board {
    list-style: none;
    margin: 0.4rem 0;
    padding: 0;
    display: grid;
    gap: 0.15rem;
    font-size: 0.8rem;
  }

  .board li {
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
    padding: 0.15rem 0.3rem;
    border-radius: 4px;
  }

  .board li.me {
    background: var(--tint-selected);
    font-weight: 700;
  }

  .board .rank {
    width: 1.8rem;
    flex-shrink: 0;
    color: var(--text-dim);
    font-variant-numeric: tabular-nums;
  }

  .board .who {
    flex: 1 1 auto;
    min-width: 4rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .board .dmg {
    flex-shrink: 0;
    font-variant-numeric: tabular-nums;
  }

  .auto {
    width: 100%;
    min-height: 44px;
  }

  .auto-result {
    margin: 0.5rem 0 0;
    text-align: center;
  }

  .practice {
    width: 100%;
    min-height: 44px;
    margin-top: 0.6rem;
  }

  .practice-note {
    margin: 0.3rem 0 0;
    text-align: center;
  }

  /* A board name opens the profile, as everywhere else a name is shown. */
  .name-link {
    background: none;
    border: 0;
    padding: 0;
    min-height: 44px;
    min-width: 0;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    text-decoration: underline dotted;
  }
</style>
