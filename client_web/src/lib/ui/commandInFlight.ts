// Modul: THE ONE pending-command tracker the screens share, bound to the
// command result feed. Kept apart from inFlight.ts so that file stays free of
// the game store and can be tested on its own. Keys are namespaced by screen
// ('market:42', 'mail:7') because ids from different tables collide.
import { commandResults } from '../stores/game';
import { createInFlight, watchNewResults } from './inFlight';

export const commandInFlight = createInFlight();

// Module lifetime, like the store it watches: never unsubscribed.
watchNewResults(commandResults, () => commandInFlight.settle());
