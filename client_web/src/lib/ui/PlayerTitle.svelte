<script lang="ts">
  // Another player's worn title, by id, through the same batched lookup as
  // their portrait (stores/worn.ts) - every row that shows a face can show the
  // title with no request of its own. Renders nothing when none is worn.
  import TitleChip from './TitleChip.svelte';
  import { requestWorn, wornByPlayer } from '../stores/worn';

  interface Props {
    playerId: number;
  }

  let { playerId }: Props = $props();

  $effect(() => {
    requestWorn(playerId);
  });

  const worn = $derived($wornByPlayer.get(playerId)?.worn ?? null);
</script>

{#if worn?.Title}
  <TitleChip name={worn.Title} color={worn.TitleColor} />
{/if}
