<script lang="ts">
  // Modul: A RARITY'S RANK AS A NUMBER, in its own shape (task 100).
  //
  // Fourteen rarities share a palette in which two greys, two blues and three
  // close purples are hard to tell apart - so colour alone cannot say whether
  // a piece is rank 6 or 7. The number can. It is drawn as a filled square
  // with the rank inside, deliberately unlike the "T2" text of the REGION tier
  // that sits beside it on the same rows: two different axes that both get
  // called "tier", and the old badge let them be read as one.
  import { rarityColor, rarityName, rarityTitle, MAX_QUALITY_TIER } from './rarity';

  interface Props {
    tier: number;
  }

  const { tier }: Props = $props();
</script>

<span class="pip" style="--pip: {rarityColor(tier)}" title={rarityTitle(tier)} aria-label="{rarityName(tier)}, rank {tier} of {MAX_QUALITY_TIER}">{tier}</span>

<style>
  .pip {
    display: inline-grid;
    place-items: center;
    flex-shrink: 0;
    min-width: 1.15rem;
    height: 1.15rem;
    padding: 0 0.15rem;
    box-sizing: border-box;
    border-radius: var(--radius-xs);
    border: 1px solid var(--pip);
    background: color-mix(in srgb, var(--pip) 22%, transparent);
    color: var(--text);
    font-size: 0.68rem;
    font-weight: 700;
    line-height: 1;
    font-variant-numeric: tabular-nums;
  }
</style>
