<script lang="ts">
  // Modul: WHY A BUTTON IS GREY, said where the thumb is. A reason carried only
  // by `title=` does not exist on a phone - no touch path shows a tooltip, and
  // none at all reaches a DISABLED button - so a greyed skill node, deposit or
  // Fuse read as broken. Village's upgrade button is the model (the reason is
  // in the label); where the label has no room, this line goes directly under
  // the control. Its own component because `.dim`/`.tiny` are scoped per
  // screen, not global, and the line should look the same everywhere.
  interface Props {
    /** Empty or null renders nothing, so a caller can pass a reason unconditionally. */
    text: string | null | undefined;
    class?: string;
  }

  const { text, class: extraClass = '' }: Props = $props();
</script>

{#if text}<span class="why {extraClass}">{text}</span>{/if}

<style>
  /* flex-basis 100%: inside a wrapping flex row (Village's cost cell) it
     takes its own line instead of squeezing in beside the costs. */
  .why {
    display: block;
    flex: 1 1 100%;
    font-size: 0.72rem;
    line-height: 1.3;
    color: var(--text-dim);
    overflow-wrap: anywhere;
  }
</style>
