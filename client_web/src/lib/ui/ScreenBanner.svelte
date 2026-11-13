<script lang="ts">
  // Modul: A PAINTED STRIP AT THE TOP OF A SCREEN (owner, 2026-11-13: "not
  // only buttons, text and a brown background"). The game already owns the
  // paintings - the region scenes, Yggdrasil - and these screens used none of
  // them. A banner is a crop of one (Backgrounds/banner_*.webp, ~40-80 KB,
  // made from the full scenes) under a dark gradient carrying one short line.
  // Decoration only: the screen's own heading stays the heading, so nothing
  // here is read twice by a screen reader (the line is aria-hidden).
  import { backgroundUrl } from './spriteUrl';

  interface Props {
    art: string;
    line: string;
  }

  const { art, line }: Props = $props();
</script>

<div class="screen-banner" style:--banner={`url(${backgroundUrl(art)})`} role="presentation" data-testid="screen-banner">
  <p class="line" aria-hidden="true">{line}</p>
</div>

<style>
  .screen-banner {
    position: relative;
    height: clamp(5.5rem, 22vw, 9.5rem);
    border-radius: var(--radius, 10px);
    overflow: hidden;
    background-image:
      linear-gradient(180deg, rgba(0, 0, 0, 0) 35%, rgba(0, 0, 0, 0.62) 100%),
      var(--banner);
    background-size: cover;
    background-position: center;
    border: 1px solid var(--border);
    box-shadow: 0 2px 10px rgba(0, 0, 0, 0.25);
    margin-bottom: 0.75rem;
  }

  .line {
    position: absolute;
    left: 0.9rem;
    right: 0.9rem;
    bottom: 0.6rem;
    margin: 0;
    color: #f6ecd2;
    font-family: var(--font-display, inherit);
    font-size: clamp(0.95rem, 3.4vw, 1.25rem);
    letter-spacing: 0.02em;
    text-shadow:
      0 1px 2px rgba(0, 0, 0, 0.9),
      0 0 8px rgba(0, 0, 0, 0.6);
  }
</style>
