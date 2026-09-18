// Modul: game.ts pulls in ui/audio.ts, whose volume/muted stores read
// localStorage SYNCHRONOUSLY at module load. This repo's vitest runs
// environment: 'node' with no jsdom (no test before this one imported
// game.ts directly), so a bare import of game.ts throws
// "localStorage is not defined" before a single test body runs.
//
// Imported for its side effect ONLY, and it must be the first import in any
// test file that (transitively) imports game.ts - ESM evaluates each import's
// full dependency subtree in declaration order, so this has to install the
// stub before game.ts's subtree is evaluated, not after.
if (typeof (globalThis as { localStorage?: unknown }).localStorage === 'undefined') {
  const store = new Map<string, string>();
  (globalThis as unknown as { localStorage: Storage }).localStorage = {
    get length() {
      return store.size;
    },
    getItem: (key: string) => (store.has(key) ? store.get(key)! : null),
    setItem: (key: string, value: string) => {
      store.set(key, String(value));
    },
    removeItem: (key: string) => {
      store.delete(key);
    },
    clear: () => store.clear(),
    key: (index: number) => Array.from(store.keys())[index] ?? null,
  };
}
