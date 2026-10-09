<script lang="ts">
  // A title as a chip: the name in the title's own colour inside a thin border
  // of the same colour. Name AND colour come from the server (TitleRegistry);
  // the client keeps no list of titles, so a title added there needs no client
  // change at all.
  interface Props {
    name: string;
    color?: string | null;
  }

  let { name, color = null }: Props = $props();
</script>

<span class="title-chip" style:--tc={color ?? '#c9b48a'} title={name} data-testid="title-chip">{name}</span>

<style>
  /* Dark (the default) draws the hex as sent; the light parchment theme darkens
     it, because a bright pink or yellow on beige measured unreadable. */
  .title-chip {
    --ink: var(--tc);
    display: inline-block;
    max-width: 12em;
    margin-left: 0.35em;
    flex-shrink: 1;
    min-width: 0;
    padding: 0.05em 0.5em;
    border: 1px solid var(--tc);
    border-radius: 4px;
    color: var(--ink);
    background: color-mix(in srgb, var(--tc) 16%, transparent);
    font-size: 0.8em;
    font-weight: 700;
    line-height: 1.4;
    letter-spacing: 0.02em;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    vertical-align: middle;
    text-shadow: 0 0 6px color-mix(in srgb, var(--tc) 35%, transparent);
  }

  @media (prefers-color-scheme: light) {
    .title-chip {
      --ink: color-mix(in srgb, var(--tc) 55%, #000);
      background: color-mix(in srgb, var(--tc) 22%, transparent);
      text-shadow: none;
    }
  }
</style>
