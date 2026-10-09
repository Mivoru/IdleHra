// Modul: THE SEASONAL EVENT'S LOOK, one attribute on <html>.
//
// `data-event="samhain"` re-points a handful of colour tokens in app.css; the
// game's markup does not know an event exists. The key is remembered on this
// device so boot.js can put the same attribute (and the event's loading art)
// up before the bundle arrives - the server is still the only judge, since
// the key is only ever written from the wire's SeasonalEventId.
export const EVENT_THEME_STORAGE_KEY = 'folkidle.eventTheme';

export function applyEventTheme(key: string | null): void {
  const root = document.documentElement;
  if (key) root.dataset.event = key;
  else delete root.dataset.event;
  try {
    if (key) localStorage.setItem(EVENT_THEME_STORAGE_KEY, key);
    else localStorage.removeItem(EVENT_THEME_STORAGE_KEY);
  } catch {
    // Private mode or blocked storage: the theme still applies this session.
  }
}
