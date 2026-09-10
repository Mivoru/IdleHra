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

  /*
    Modul: THE PANEL RESERVES ITS OWN SPACE, because a fixed overlay that does
    not is a control-burying bug wearing a hint's clothes.

    Found by exercise.mjs the day tier three landed: the coach sat over the
    Village screen's "Marry" button and the click timed out after thirty
    seconds. It was always capable of this - it has been fixed to the bottom of
    the viewport since it was written - but until an objective could stand
    there indefinitely, the panel that happened to be showing was short enough
    to miss the controls underneath. That is not a fix, that is luck.

    Measured rather than hard-coded: the panel's height depends on how long the
    body text is and on how many lines the action row wraps onto at 390px, so a
    constant would be wrong at one width or the other - which is exactly the
    class check:clipping exists to catch.
  */
  let panel = $state<HTMLElement | null>(null);

  /*
    Modul: COLLAPSIBLE, BECAUSE A HINT MAY NOT BURY A CONTROL.

    check:overlap found eleven pairs the day tier three landed - "Reroll" under
    "Got it" on the Chest, "Contribute gold" under "Skip onboarding" in the
    Guild, the auto-eat threshold input under the panel entirely, all at 390px.
    The panel had always been a fixed overlay; what changed is that an
    objective can stand there indefinitely on a mature account, where a
    discovery used to be dismissed and gone.

    Reserving space in the body (above) fixes the screens that scroll the
    document. It cannot fix a screen with its OWN scroll region, because the
    content at the bottom of an inner scroller passes underneath a fixed
    element whatever the body does. So the panel also has to be small enough to
    get out of the way, and collapsing to its title line is what does that -
    the player keeps the tag, the title and one tap to bring it back.

    Collapsed state is deliberately NOT persisted: it is a per-glance
    convenience, not a preference, and a hint that stays collapsed forever is a
    hint that was never shown.
  */
  /*
    Modul: FOLDED IS A FUNCTION OF THE VIEWPORT, not a one-off set when a cue
    arrives - and the first version of this got that wrong in a way only
    check:overlap could see. It read window.innerWidth once per new cue, so a
    session that started wide and was then narrowed kept an expanded panel, and
    the checker (which loads at 1500px and re-measures at 390px without
    changing the cue) saw exactly that. A layout rule that samples the viewport
    once is not a layout rule.

    So: derived from a width that a resize listener keeps current, with the
    player's own toggle taking precedence until the next cue replaces it.
  */
  let viewportWidth = $state(typeof window === 'undefined' ? 1024 : window.innerWidth);
  let userToggled = $state<boolean | null>(null);

  const collapsed = $derived(userToggled ?? viewportWidth < 560);

  $effect(() => {
    if (typeof window === 'undefined') return;
    const onResize = () => (viewportWidth = window.innerWidth);
    window.addEventListener('resize', onResize);
    onResize();
    return () => window.removeEventListener('resize', onResize);
  });

  // A new cue is a new thing to say, so it drops the player's fold choice and
  // goes back to whatever the viewport says.
  let lastCueId = $state('');
  $effect(() => {
    if (cue && cue.id !== lastCueId) {
      lastCueId = cue.id;
      userToggled = null;
    }
  });

  $effect(() => {
    // Read cue so this re-runs when the panel's content (and height) changes.
    const showing = cue;
    if (typeof document === 'undefined') return;

    if (!showing || !panel) {
      document.body.style.paddingBottom = '';
      return;
    }

    document.body.style.paddingBottom = `${panel.offsetHeight + 24}px`;

    return () => {
      document.body.style.paddingBottom = '';
    };
  });

  function goThere() {
    if (!cue) return;
    // Modul: a discovery is acknowledged by ACTING on it as well as by "Got
    // it". Being taken to the screen is reading the explanation; making the
    // player then dismiss a panel about a screen they are now looking at is
    // the kind of thing that gets a tutorial turned off.
    const target = cue.screen;
    if (cue.kind !== 'step') acknowledgeCue();
    requestScreen(target);
  }
</script>

{#if cue}
  <div class="coach" bind:this={panel} role="status" data-onboarding-cue={cue.id} data-onboarding-kind={cue.kind}>
    <!-- Modul: THE WHOLE HEADER IS THE TOGGLE, and that is the third attempt.
         A separate 19x18 caret button failed check:touch. Growing it to the
         44px floor then failed check:overlap - a 44px box on a panel whose
         collapsed height is one line of text reaches past the panel and lands
         on whatever is underneath, which on the Character screen was the "+10"
         attribute button. Pulling it back with negative margin fixed the row
         height and not the box, because the box is what the browser
         hit-tests.
         So there is no small button any more. The header IS the control: full
         width, one line tall, comfortably past 44px on a phone, and the caret
         inside it is decoration with no hit area of its own. One target
         instead of a target beside a target is also simply the better
         disclosure pattern - it is what a native list row does. -->
    <button
      class="head"
      aria-expanded={!collapsed}
      title={collapsed ? 'Show this hint' : 'Hide this hint'}
      onclick={() => (userToggled = !collapsed)}
    >
      {#if cue.kind === 'step'}
        <span class="tag">Step {cue.index} / {cue.total}</span>
      {:else if cue.kind === 'objective'}
        <!-- Modul: an objective is labelled DO THIS NEXT rather than "New",
             because it is not describing something the player has found - it
             is naming something they have not. Tier two answers "what is
             this"; this answers "what now", and the tag is the only thing on
             screen that tells them apart. -->
        <span class="tag next">Do this next</span>
      {:else}
        <span class="tag new">New</span>
      {/if}
      <strong>{cue.title}</strong>
      <svg class="caret" class:up={collapsed} viewBox="0 0 12 12" aria-hidden="true">
        <path d="M2 4.5 L6 8.5 L10 4.5" fill="none" stroke="currentColor" stroke-width="1.8"
              stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
    <!-- Modul: {#if}, not a `display` rule and not a <details>. An author
         display rule on a direct child defeats the UA rule that hides a closed
         panel, and engines disagree about whether that rule exists at all -
         the chest's collapsed sweep panel kept live, clickable buttons sitting
         on top of the item list for exactly that reason. Absent is the only
         state that cannot be clicked through. -->
    {#if !collapsed}
      <p>{cue.body}</p>
      <div class="actions">
        <button class="gilded" onclick={goThere}>Take me there</button>
        {#if cue.kind !== 'step'}
          <button onclick={acknowledgeCue}>Got it</button>
        {/if}
        <button class="quiet" onclick={skipTutorial}>Skip onboarding</button>
      </div>
    {/if}
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

  /* Modul: THE CHAT HANDLE WAS SITTING ON THE END OF THE SENTENCE.
     Found in a store screenshot, not by a checker: ChatDock is
     `position: fixed; right: 1rem; bottom: 1rem` at z-index 40, this panel is
     fixed at bottom 0.75rem at z-index 40, and equal z-index means DOM order
     decides - the dock wins. On a 360px phone this panel is nearly full width,
     so the handle covered the last two words of every coach instruction. The
     player reading "You have points wait" is a new player, which is the worst
     possible audience for a truncated sentence.

     check:overlap did not catch it because it compares CONTROL pairs, and the
     thing being covered here is text.

     4.25rem is not a new number - it is the same clearance ChatDock already
     reserves for its own handle further down this stylesheet, so the two
     cannot drift apart. Only on a phone: at desktop widths the centred panel
     never reaches the corner. */
  @media (max-width: 40rem) {
    .coach {
      bottom: 4.25rem;
    }
  }

  .head {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
    width: 100%;
    text-align: left;
    /* It is a button, and it must not look like one - the panel is already the
       raised surface. */
    background: none;
    border: none;
    box-shadow: none;
    padding: 0;
    color: inherit;
    cursor: pointer;
  }

  .head:hover strong {
    color: var(--accent);
  }



  .caret {
    width: 12px;
    height: 12px;
    display: block;
    transition: rotate 140ms ease;
  }

  /* Modul: `rotate`, not `transform` - the same lesson app.css records about
     button:active. `transform` is one property holding a whole list, so
     setting it here would replace anything else the element used it for. */
  .caret.up {
    rotate: 180deg;
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

  .tag.next,
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
