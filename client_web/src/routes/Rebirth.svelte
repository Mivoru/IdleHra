<script lang="ts">
  // Modul: REBIRTH HAS ITS OWN SCREEN (owner, 2026-10-11). It used to be a
  // panel at the foot of the Hall of Ancestors, below the roster, so a player
  // had to scroll past their whole bloodline to learn what a rebirth pays -
  // and the Hall's own text still said a rebirth took "all gear" after the
  // Vault existed. Here it is the page: where you stand, the road to ten, the
  // Vault, and the terms. Every number is the server's (RebirthPanel).
  import RebirthPanel from '../lib/ui/RebirthPanel.svelte';
  import { requestScreen } from '../lib/stores/navigation';
</script>

<div class="wrap" data-testid="rebirth-screen">
  <section class="panel intro">
    <h2>Rebirth</h2>
    <p class="dim small">
      End this run whenever you choose and start again at level 1 - stronger. Every rebirth pays something
      that lasts: skill points, faster levelling, and at the milestones avatars, a second pet slot and the
      Phoenix. You may be reborn ten times, so a newcomer can always catch up. Who your bloodline keeps is
      decided in the Hall of Ancestors; every rule is in the Wiki.
    </p>
    <div class="links">
      <button type="button" onclick={() => requestScreen('ancestors')}>Hall of Ancestors</button>
      <button type="button" onclick={() => requestScreen('wiki')}>Wiki</button>
    </div>
  </section>

  <RebirthPanel />
</div>

<style>
  .wrap {
    padding: 1rem;
    display: grid;
    gap: 1rem;
  }

  .intro {
    display: grid;
    gap: 0.4rem;
  }

  h2 {
    margin: 0;
    font-size: 1.1rem;
  }

  p {
    margin: 0;
    line-height: 1.45;
    max-width: 80ch;
  }

  .dim {
    color: var(--text-dim);
  }

  .small {
    font-size: 0.88rem;
  }

  .links {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem;
  }

  .links button {
    flex-shrink: 0;
  }
</style>
