<script lang="ts">
  // Modul: THE STORE IS EMPTY ON PURPOSE (owner, 2026-09-28).
  //
  // It showed two panels. The diamond packages listed four products with no
  // price and no way to buy on the web - the storefront below them said "No
  // listings are offered to your account right now" - so it was a shop window
  // with nothing in it, dressed as a shop. Simulation speed replayed a time
  // bank nothing had filled since the chrono deletion, and offline catch-up
  // already pays the same XP and loot as being online, so it offered nothing
  // honest either; the server side went with it.
  //
  // The purchase path (lib/net/billing.ts, /api/v1/billing/*) is kept intact
  // for when payments are set up; only this screen stops offering it.
</script>

<div class="wrap">
  <section class="panel" data-testid="store-empty">
    <h2>Store</h2>
    <p class="dim">Nothing here yet. Come back later.</p>
  </section>
</div>

<style>
  .wrap {
    padding: 1rem;
  }

  .panel {
    background: var(--bg-panel);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
    max-width: 32rem;
  }

  h2 {
    margin: 0 0 0.5rem;
    font-size: 1.05rem;
  }

  p {
    margin: 0;
  }
</style>
