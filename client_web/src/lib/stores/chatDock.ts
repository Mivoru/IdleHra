import { derived, get, writable } from 'svelte/store';
import { chatLog } from './game';

/**
 * Whether the chat window is open.
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
 * general modal registry: a handful of components write it and the back
 * button reads it, and a registry would have to be kept in step by hand every
 * time a panel was added. See lib/net/backButton.ts for the layers that are
 * NOT here and why.
 */
export const chatDockOpen = writable(false);

/**
 * The newest message id the player has actually had on screen. chatLog is
 * newest-first and ids are a monotonic counter assigned on receipt, so one
 * number is enough - no per-channel bookkeeping, and no way for an unread
 * marker to survive a message being trimmed off the end of the log.
 *
 * Modul: TASK 95 - A STORE, because the entry that shows the count is no
 * longer the window. Chat used to have two entry points (a floating handle and,
 * when nobody was online, a header button) and the handle owned the count.
 * There is one entry now - in the header on a desktop, in the More sheet on a
 * phone - and the window, the entry and the More tab's badge all read this.
 */
const chatSeenId = writable(0);

export const chatUnread = derived([chatLog, chatSeenId], ([log, seen]) =>
  Math.max(0, (log[0]?.id ?? 0) - seen),
);

/** Everything in the log so far counts as read. */
export function markChatRead(): void {
  chatSeenId.set(get(chatLog)[0]?.id ?? 0);
}

/** Opens or closes the window; opening it reads what is there. */
export function setChatOpen(open: boolean): void {
  chatDockOpen.set(open);
  if (open) markChatRead();
}
