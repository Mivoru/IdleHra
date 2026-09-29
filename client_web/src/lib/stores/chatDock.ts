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

/**
 * Task 71: true while the chat has nothing to say - nobody online, nothing
 * unread, window shut. ChatDock then drops its floating handle, which sat on
 * the corner of whatever control a screen ended with (Gathering's Gather
 * button at 1366px), and App.svelte shows a small Chat button in the header
 * instead. Written by ChatDock only, which is the one that knows both counts.
 */
export const chatHandleInHeader = writable(false);
