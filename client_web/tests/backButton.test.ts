import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  resolveBackPress,
  watchHardwareBack,
  exitApp,
  type BackPressState,
} from '../src/lib/net/backButton';

/*
  THE ANDROID HARDWARE BACK BUTTON.

  It exited the app from every screen, because Capacitor's default is to leave
  and nothing had ever answered the event. Attaching a listener disables that
  default, which means this module now owns the only way out of the app - so
  the two things worth pinning are that the layer ordering is right, and that
  the confirmation is genuinely reachable from the bottom.

  The decision is a pure function precisely so it can be checked here. The
  native attach is checked for the property that matters in a browser build:
  that an absent or hostile plugin object is survivable, which is the same
  contract lifecycle.ts holds.
*/

const CLOSED: BackPressState = {
  exitPromptOpen: false,
  deathCardOpen: false,
  victoryCardOpen: false,
  offlineSummaryOpen: false,
  chatDockOpen: false,
  navOpen: false,
  historyDepth: 0,
  atRoot: true,
};

afterEach(() => {
  delete (globalThis as { Capacitor?: unknown }).Capacitor;
});

describe('what one back press means', () => {
  it('asks before exiting, from the bottom of the app', () => {
    expect(resolveBackPress(CLOSED)).toBe('confirm-exit');
  });

  it('never exits from a nested screen', () => {
    // The acceptance criterion, stated directly: anywhere that is not the
    // root, with or without a route behind it, back stays in the app.
    expect(resolveBackPress({ ...CLOSED, atRoot: false })).toBe('root-screen');
    expect(resolveBackPress({ ...CLOSED, atRoot: false, historyDepth: 3 })).toBe('previous-screen');
  });

  it('retraces the route before falling back to the map', () => {
    expect(resolveBackPress({ ...CLOSED, atRoot: false, historyDepth: 1 })).toBe('previous-screen');
  });

  it('closes a layer rather than navigating, whatever is behind it', () => {
    const nested = { ...CLOSED, atRoot: false, historyDepth: 5 };
    expect(resolveBackPress({ ...nested, navOpen: true })).toBe('close-nav');
    expect(resolveBackPress({ ...nested, chatDockOpen: true })).toBe('close-chat-dock');
    expect(resolveBackPress({ ...nested, offlineSummaryOpen: true })).toBe(
      'close-offline-summary',
    );
    expect(resolveBackPress({ ...nested, victoryCardOpen: true })).toBe('close-victory-card');
    expect(resolveBackPress({ ...nested, deathCardOpen: true })).toBe('close-death-card');
  });

  it('closes the layers in paint order, topmost first', () => {
    // Modul: this is the test that would have caught the obvious way to get
    // this wrong. If back closed something UNDERNEATH whatever is covering the
    // screen, the player would press it and see nothing happen at all - the
    // exact "the output side was never wired" symptom this repo keeps
    // shipping. The order mirrors the z-indexes in the components: exit prompt
    // (1100) > death (60, last in the DOM) > victory (60) > offline summary
    // (50) > chat dock (40) > nav.
    const everything: BackPressState = {
      exitPromptOpen: true,
      deathCardOpen: true,
      victoryCardOpen: true,
      offlineSummaryOpen: true,
      chatDockOpen: true,
      navOpen: true,
      historyDepth: 4,
      atRoot: false,
    };

    const peeled: string[] = [];
    const state = { ...everything };
    const clear: Record<string, () => void> = {
      'close-exit-prompt': () => (state.exitPromptOpen = false),
      'close-death-card': () => (state.deathCardOpen = false),
      'close-victory-card': () => (state.victoryCardOpen = false),
      'close-offline-summary': () => (state.offlineSummaryOpen = false),
      'close-chat-dock': () => (state.chatDockOpen = false),
      'close-nav': () => (state.navOpen = false),
    };

    for (let press = 0; press < 6; press += 1) {
      const outcome = resolveBackPress(state);
      peeled.push(outcome);
      clear[outcome]?.();
    }

    expect(peeled).toEqual([
      'close-exit-prompt',
      'close-death-card',
      'close-victory-card',
      'close-offline-summary',
      'close-chat-dock',
      'close-nav',
    ]);

    // And only once every layer is gone does it start navigating.
    expect(resolveBackPress(state)).toBe('previous-screen');
  });

  it('closes the exit prompt rather than opening a second one', () => {
    // Pressing back at the prompt is "no", not "ask me again".
    expect(resolveBackPress({ ...CLOSED, exitPromptOpen: true })).toBe('close-exit-prompt');
  });
});

describe('attaching to the native shell', () => {
  it('does nothing, and throws nothing, in a browser build', () => {
    // Modul: `@capacitor/app` is not a dependency of the web build. There is
    // no plugin object at all in a tab, and reading one that is not there must
    // never be what takes the client down.
    let stop: (() => void) | null = null;
    expect(() => {
      stop = watchHardwareBack(() => {});
    }).not.toThrow();
    expect(() => stop?.()).not.toThrow();
    expect(() => exitApp()).not.toThrow();
  });

  it('routes a native press to the handler', () => {
    const handlers: Record<string, () => void> = {};
    const remove = vi.fn();
    (globalThis as { Capacitor?: unknown }).Capacitor = {
      Plugins: {
        App: {
          addListener: (event: string, handler: () => void) => {
            handlers[event] = handler;
            return { remove };
          },
        },
      },
    };

    const onBack = vi.fn();
    const stop = watchHardwareBack(onBack);

    expect(handlers.backButton).toBeTypeOf('function');
    handlers.backButton();
    expect(onBack).toHaveBeenCalledTimes(1);

    stop();
  });

  it('removes a listener that resolved after the unsubscribe', async () => {
    // Modul: addListener may return a promise. A component that mounted and
    // unmounted before it settled would otherwise leave a live listener behind
    // holding a stale closure - back would then act on a card that is no
    // longer on screen.
    const remove = vi.fn();
    let settle: ((handle: { remove: () => void }) => void) | null = null;
    (globalThis as { Capacitor?: unknown }).Capacitor = {
      Plugins: {
        App: {
          addListener: () =>
            new Promise<{ remove: () => void }>((resolve) => {
              settle = resolve;
            }),
        },
      },
    };

    const stop = watchHardwareBack(() => {});
    stop();
    settle!({ remove });
    await Promise.resolve();

    expect(remove).toHaveBeenCalledTimes(1);
  });

  it('survives a plugin that throws on attach', () => {
    (globalThis as { Capacitor?: unknown }).Capacitor = {
      Plugins: {
        App: {
          addListener: () => {
            throw new Error('no native bridge here');
          },
        },
      },
    };

    expect(() => watchHardwareBack(() => {})()).not.toThrow();
  });

  it('closes the app through the plugin when asked', () => {
    const exit = vi.fn();
    (globalThis as { Capacitor?: unknown }).Capacitor = { Plugins: { App: { exitApp: exit } } };

    exitApp();

    expect(exit).toHaveBeenCalledTimes(1);
  });
});
