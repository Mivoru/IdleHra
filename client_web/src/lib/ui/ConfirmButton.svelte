<script lang="ts">
  // Modul: ONE INLINE CONFIRM for the actions a mis-tap cannot take back -
  // kicking a guildmate, sending a villager away for good, spending diamonds.
  // They were one tap each, while the attribute respec and a Forge reroll used
  // the native confirm(), which the Android WebView draws as an unstyled system
  // dialog. First tap ARMS and relabels the button ("Really kick?"), the second
  // commits; the arm lapses after a few seconds, on Escape, or when focus moves
  // on. The logic is confirmArm.ts, tested on its own.
  import { onDestroy } from 'svelte';
  import { createConfirmArm } from './confirmArm';

  interface Props {
    label: string;
    /** What the armed button says. Defaults to "Really <label>?". */
    confirmLabel?: string;
    onConfirm: () => void;
    disabled?: boolean;
    /** Paints the ARMED state in the danger colour (default). */
    danger?: boolean;
    /** The compact size the row buttons around it use (still 44px on a phone). */
    small?: boolean;
    title?: string;
    timeoutMs?: number;
    class?: string;
  }

  const {
    label,
    confirmLabel,
    onConfirm,
    disabled = false,
    danger = true,
    small = false,
    title,
    timeoutMs = 4000,
    class: extraClass = '',
  }: Props = $props();

  let armed = $state(false);

  // Modul: the props are read at call time, not captured, so a parent that
  // swaps `onConfirm` (a row re-keyed under the same button) commits the
  // current one.
  const arm = createConfirmArm({
    get timeoutMs() {
      return timeoutMs;
    },
    onCommit: () => onConfirm(),
    onChange: (next) => (armed = next),
  });
  onDestroy(() => arm.dispose());

  // A button that becomes disabled while armed must not stay armed: the next
  // time it enables, one tap would commit.
  $effect(() => {
    if (disabled) arm.disarm();
  });

  const text = $derived(armed ? (confirmLabel ?? `Really ${label.toLowerCase()}?`) : label);
</script>

<button
  type="button"
  class="confirm-button {extraClass}"
  class:armed
  class:danger
  class:small
  {disabled}
  {title}
  onclick={(event) => {
    event.stopPropagation();
    arm.press();
  }}
  onblur={() => arm.disarm()}
  onkeydown={(event) => {
    if (event.key === 'Escape' && arm.armed) {
      event.stopPropagation();
      arm.disarm();
    }
  }}
>
  {text}
</button>
<span class="sr" aria-live="polite">{armed ? 'Press again to confirm.' : ''}</span>

<style>
  .confirm-button {
    flex-shrink: 0;
    white-space: nowrap;
  }

  .small {
    font-size: 0.72rem;
    padding: 0.15rem 0.5rem;
  }

  .armed {
    border-color: var(--brass-lit);
    color: #fff3d6;
  }

  .armed.danger {
    border-color: var(--danger);
    color: var(--danger);
    background-color: color-mix(in srgb, var(--danger) 14%, transparent);
  }

  .sr {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
    white-space: nowrap;
    border: 0;
    padding: 0;
  }
</style>
