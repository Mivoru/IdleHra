<script lang="ts">
  // Modul: ONE COLLAPSIBLE SETTINGS PANEL (task 96). Settings was one 7,417 px
  // page at 390 px, with Sign out about 6,900 px down, because every panel was
  // always fully open. Each section is closed until asked for now.
  //
  // The body is behind {#if}, not inside a <details>: an author `display` rule
  // on a direct child defeats the UA rule that hides a closed <details>, and
  // the Chest once kept live buttons on top of its list that way. A closed
  // panel here has no controls in the DOM at all.
  import type { Snippet } from 'svelte';

  interface Props {
    /** Stable key; also the body's id suffix. */
    id: string;
    title: string;
    /** A short state line beside the title, readable while closed. */
    summary?: string;
    open?: boolean;
    /** Danger border, for the panel that can destroy something. */
    danger?: boolean;
    children: Snippet;
  }

  let { id, title, summary = '', open = $bindable(false), danger = false, children }: Props = $props();

  const bodyId = $derived(`settings-${id}`);
</script>

<section class="panel fold" class:danger data-fold={id}>
  <h2 class="fold-title">
    <button
      type="button"
      class="fold-toggle"
      aria-expanded={open}
      aria-controls={bodyId}
      onclick={() => (open = !open)}
    >
      <span class="chev" aria-hidden="true">{open ? '▾' : '▸'}</span>
      <span class="label">{title}</span>
      {#if summary}<span class="summary">{summary}</span>{/if}
    </button>
  </h2>
  {#if open}
    <div class="fold-body" id={bodyId}>
      {@render children()}
    </div>
  {/if}
</section>

<style>
  .fold {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    /* The same 0.7rem app.css forces on every .panel below 40rem, so the
       header does not jump between a phone and a desktop. */
    padding: 0.7rem;
  }

  .fold.danger {
    border-color: var(--danger);
  }

  .fold-title {
    margin: 0;
    font-size: 1.05rem;
  }

  /* The whole header row is the control, so it is a thumb target at every
     width rather than a small chevron. */
  .fold-toggle {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    width: 100%;
    min-height: 44px;
    padding: 0.2rem 0;
    background: none;
    border: none;
    border-radius: var(--radius);
    font: inherit;
    color: inherit;
    text-align: left;
    cursor: pointer;
  }

  .chev {
    width: 1em;
    flex-shrink: 0;
    color: var(--text-dim);
  }

  .label {
    flex: 1 1 auto;
  }

  /* Shrinks and wraps before the title does: at 360 px the longest title
     and a summary do not both fit on one line. */
  .summary {
    flex: 0 1 auto;
    text-align: right;
    overflow-wrap: anywhere;
    font-family: var(--font-ui, inherit);
    font-size: 0.78rem;
    font-weight: 400;
    color: var(--text-dim);
    letter-spacing: 0;
  }

  .fold-body {
    padding-top: 0.5rem;
  }
</style>
