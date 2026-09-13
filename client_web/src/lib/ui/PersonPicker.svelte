<script lang="ts">
  // Modul: A PICKER THAT IS NOT A <select>.
  //
  // Reported from the APK: the breeding pickers glitched and sometimes did not
  // work. Android's WebView draws a <select> as a native dialog, and the options
  // behind it were rewritten every second (a countdown in the label, disabled
  // flags off the same clock), which closes the dialog or drops the choice. The
  // labels were also sixty to ninety characters, cut to one line by that dialog,
  // so two heroes could not be told apart at all.
  //
  // Plain buttons instead. On a phone the list opens as a sheet from the bottom
  // edge; on a wide screen it opens in place. What a card says - and why a card
  // is refused - is decided in breedingPicker.ts, not here.
  //
  // `{#if open}`, not <details>: a closed <details> can leave live buttons on
  // top of the page (CLAUDE.md, the chest sweep panel).
  import RaceIcon from './RaceIcon.svelte';
  import type { PickerPerson } from './breedingPicker';
  import { openSheetCloser } from '../stores/sheet';

  interface Group {
    title: string;
    hint?: string;
    people: PickerPerson[];
  }

  interface Props {
    label: string;
    placeholder: string;
    groups: Group[];
    selectedKey: string;
    onSelect: (key: string) => void;
    testId: string;
    disabled?: boolean;
    emptyText?: string;
  }

  const {
    label,
    placeholder,
    groups,
    selectedKey,
    onSelect,
    testId,
    disabled = false,
    emptyText = 'Nobody here yet.',
  }: Props = $props();

  let open = $state(false);

  const selected = $derived(groups.flatMap((g) => g.people).find((p) => p.key === selectedKey));

  // Modul: ON A PHONE THE SHEET LIVES ON <body>, NOT HERE.
  //
  // The first version was `position: fixed` at z-index 1401 inside the panel,
  // and the onboarding coach and the chat button - both z-index 40 - still
  // drew on top of it, covering the bottom cards. An ancestor of this screen
  // forms a stacking context, and no z-index can climb out of one. Moving the
  // node to <body> is the only fix that does not depend on what the layout
  // around this component happens to be. On a wide screen the list opens in
  // place, where it belongs.
  const PHONE_QUERY = '(max-width: 40rem)';
  let isPhone = $state(typeof window !== 'undefined' && window.matchMedia(PHONE_QUERY).matches);

  $effect(() => {
    const media = window.matchMedia(PHONE_QUERY);
    const update = () => (isPhone = media.matches);
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  });

  // While this sheet covers a phone screen, the hardware back button closes it
  // (App.svelte asks through openSheetCloser). Cleared only if it is still OUR
  // closer, so a second picker opening cannot be unregistered by the first.
  $effect(() => {
    if (!(open && isPhone)) return;
    const close = () => (open = false);
    openSheetCloser.set(close);
    return () => openSheetCloser.update((current) => (current === close ? null : current));
  });

  function portal(node: HTMLElement) {
    document.body.appendChild(node);
    return {
      destroy() {
        node.remove();
      },
    };
  }

  const APTITUDE_LABELS = [
    { short: 'STR', name: 'Strength' },
    { short: 'SKL', name: 'Skill' },
    { short: 'END', name: 'Endurance' },
    { short: 'FOR', name: 'Fortune' },
  ] as const;

  function choose(person: PickerPerson) {
    if (person.blocked !== null) return;
    onSelect(person.key);
    open = false;
  }

  function onWindowKey(event: KeyboardEvent) {
    if (open && event.key === 'Escape') open = false;
  }
</script>

<svelte:window onkeydown={onWindowKey} />

{#snippet card(person: PickerPerson)}
  <span class="card">
    <RaceIcon raceId={person.raceId} />
    <span class="who">
      <span class="name">
        {person.name}
        {#each person.marks as mark (mark)}<span class="mark">{mark}</span>{/each}
      </span>
      <span class="detail">{person.detail}</span>
    </span>
    <span class="apts">
      {#each person.aptitudes as value, index (index)}
        <span class="apt" title={APTITUDE_LABELS[index].name}>
          <small>{APTITUDE_LABELS[index].short}</small>{value}
        </span>
      {/each}
    </span>
  </span>
{/snippet}

<div class="picker" data-testid={testId}>
  <span class="picker-label">{label}</span>

  <button
    type="button"
    class="trigger"
    aria-haspopup="listbox"
    aria-expanded={open}
    {disabled}
    onclick={() => (open = !open)}
  >
    {#if selected}
      {@render card(selected)}
    {:else}
      <span class="placeholder">{placeholder}</span>
    {/if}
    <span class="chevron" aria-hidden="true">{open ? '▴' : '▾'}</span>
  </button>

  {#if selected?.blocked}
    <p class="selected-reason">{selected.name}: {selected.blocked}</p>
  {/if}

  {#if open && !isPhone}
    {@render list()}
  {/if}
</div>

{#snippet list()}
  <!-- data-picker, because on a phone this node is NOT inside the picker's own
       element any more - scripts find the list by it either way. -->
  <div class="sheet" class:phone={isPhone} role="listbox" aria-label={label} data-picker={testId}>
    <div class="sheet-head">
      <strong>{label}</strong>
      <button type="button" class="close" onclick={() => (open = false)}>Close</button>
    </div>

    {#each groups as group (group.title)}
      <section class="group">
        <h4>{group.title}</h4>
        {#if group.hint}<p class="hint">{group.hint}</p>{/if}
        {#if group.people.length === 0}
          <p class="hint">{emptyText}</p>
        {/if}
        {#each group.people as person (person.key)}
          <button
            type="button"
            role="option"
            class="option"
            aria-selected={person.key === selectedKey}
            disabled={person.blocked !== null}
            data-key={person.key}
            onclick={() => choose(person)}
          >
            {@render card(person)}
            {#if person.blocked}<span class="reason">{person.blocked}</span>{/if}
          </button>
        {/each}
      </section>
    {/each}
  </div>
{/snippet}

{#if open && isPhone}
  <div class="layer" use:portal>
    <button
      type="button"
      class="backdrop touch-exempt"
      aria-label="Close the list"
      onclick={() => (open = false)}
    ></button>
    {@render list()}
  </div>
{/if}

<style>
  .picker {
    display: grid;
    gap: 0.25rem;
    margin-bottom: 0.6rem;
    min-width: 0;
  }

  .picker-label {
    font-size: 0.8rem;
    color: var(--text-dim);
  }

  .trigger,
  .option {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    min-width: 0;
    text-align: left;
    font: inherit;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.4rem 0.5rem;
    cursor: pointer;
  }

  .trigger {
    min-height: 44px;
  }

  .trigger:disabled {
    opacity: 0.55;
    cursor: default;
  }

  .placeholder {
    flex: 1;
    color: var(--text-dim);
  }

  .chevron {
    flex: none;
    color: var(--text-dim);
  }

  /* One card: portrait, who, and the four aptitudes. It WRAPS rather than
     crushing the name - a flex child squeezed to zero width is invisible to
     every geometry checker (CLAUDE.md, the Chest row at 360px). */
  .card {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.35rem 0.5rem;
    flex: 1;
    min-width: 0;
  }

  .who {
    display: grid;
    gap: 0.05rem;
    flex: 1 1 8rem;
    min-width: 8rem;
  }

  .name {
    font-weight: 600;
    font-size: 0.9rem;
    overflow-wrap: anywhere;
  }

  .mark {
    margin-left: 0.3rem;
    padding: 0 0.3rem;
    font-size: 0.65rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    border: 1px solid var(--brass, var(--border));
    border-radius: 3px;
    color: var(--brass-lit, inherit);
  }

  .detail {
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  .apts {
    display: flex;
    gap: 0.25rem;
    flex: none;
    margin-left: auto;
  }

  .apt {
    display: grid;
    justify-items: center;
    min-width: 2.1rem;
    padding: 0.05rem 0.2rem;
    border-radius: 3px;
    background: var(--bg-panel);
    font-variant-numeric: tabular-nums;
    font-weight: 700;
    font-size: 0.82rem;
    color: var(--brass-lit, inherit);
  }

  .apt small {
    font-size: 0.55rem;
    font-weight: 600;
    color: var(--text-dim);
    letter-spacing: 0.04em;
  }

  .selected-reason {
    margin: 0;
    font-size: 0.75rem;
    color: var(--warn, var(--danger));
  }

  .sheet {
    display: grid;
    gap: 0.6rem;
    margin-top: 0.3rem;
    padding: 0.5rem;
    max-height: 28rem;
    overflow-y: auto;
    overscroll-behavior: contain;
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
  }

  .sheet-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.5rem;
  }

  .close {
    flex-shrink: 0;
    font: inherit;
    font-size: 0.8rem;
    color: inherit;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.25rem 0.6rem;
    cursor: pointer;
  }

  .group {
    display: grid;
    gap: 0.3rem;
  }

  h4 {
    margin: 0;
    font-size: 0.72rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--text-dim);
  }

  .hint {
    margin: 0;
    font-size: 0.72rem;
    color: var(--text-dim);
  }

  .option {
    flex-direction: column;
    align-items: stretch;
    gap: 0.2rem;
  }

  .option[aria-selected='true'] {
    border-color: var(--brass-lit, var(--border));
  }

  .option:disabled {
    opacity: 0.55;
    cursor: default;
  }

  .reason {
    font-size: 0.72rem;
    color: var(--warn, var(--danger));
  }

  /* A PHONE GETS A SHEET, portalled to <body> (see the script). Fixed to the
     bottom edge above the chat dock (40), the coach (40) and the app header
     (1100), with its own safe-area inset - body padding never reaches a fixed
     overlay (CLAUDE.md, "the phone draws under the status bar"). */
  .backdrop {
    position: fixed;
    inset: 0;
    z-index: 1400;
    border: 0;
    padding: 0;
    min-height: 0;
    background: rgba(0, 0, 0, 0.55);
  }

  .sheet.phone {
    position: fixed;
    left: 0;
    right: 0;
    bottom: 0;
    z-index: 1401;
    margin: 0;
    max-height: 78vh;
    border-radius: 12px 12px 0 0;
    box-shadow: 0 -6px 24px rgba(0, 0, 0, 0.35);
    padding: 0.75rem max(0.75rem, var(--safe-area-inset-left, env(safe-area-inset-left, 0px)))
      calc(0.75rem + var(--safe-area-inset-bottom, env(safe-area-inset-bottom, 0px)));
  }
</style>
