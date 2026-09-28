<script lang="ts">
  // Task 54: another player's face, looked up by id through the batched
  // worn-cosmetics cache (stores/worn.ts). Until the answer arrives - or if it
  // never does - the letter placeholder stands in, so a row never jumps.
  import Avatar from './Avatar.svelte';
  import { requestWorn, wornByPlayer } from '../stores/worn';

  interface Props {
    playerId: number;
    name?: string;
    size?: 'sm' | 'md' | 'tile' | 'lg';
  }

  let { playerId, name = '', size = 'sm' }: Props = $props();

  $effect(() => {
    requestWorn(playerId);
  });

  const worn = $derived($wornByPlayer.get(playerId)?.worn ?? null);
</script>

<Avatar
  avatarId={worn?.AvatarId ?? null}
  frameId={worn?.FrameId ?? null}
  raceId={worn?.RaceId ?? 0}
  female={worn?.IsFemale ?? false}
  {size}
  {name}
/>
