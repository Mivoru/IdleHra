import { writable } from 'svelte/store';

/**
 * Whether the floating chat window is open.
 *
 * Modul: THIS WAS COMPONENT STATE, AND IT HAD TO STOP BEING.
 *
 * ChatDock owns its own openness and nothing else needed to know - until the
 * Android back button, which has to answer "is there a layer covering the
 * screen?" before it decides whether to navigate. On a phone the open chat
 * window is the largest such layer in the game, and a back press that changed
 * the screen behind it would look like the button did nothing.
 *
 * A store rather than a prop threaded from App.svelte, and rather than a
 * general modal registry: exactly one component writes it, exactly one reads
 * it, and a registry would have to be kept in step by hand every time a panel
 * was added. See lib/net/backButton.ts for the layers that are NOT here and
 * why.
 */
export const chatDockOpen = writable(false);
