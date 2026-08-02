// Modul: the ONE place the server address is written down.
//
// The Unity client learned this lesson already: ClientServerConfig exists
// there because the address had been pasted into several scripts and they
// drifted. Same rule here - nothing else in this client may contain a host or
// a port.

const DEFAULT_HTTP_BASE = 'http://localhost:8080';

/** Overridable per environment via a Vite env var, e.g. in .env.production. */
export const HTTP_BASE: string = import.meta.env?.VITE_FOLKIDLE_SERVER ?? DEFAULT_HTTP_BASE;

/** Derived, never configured separately - two settings would be two truths. */
export const WS_URL: string = HTTP_BASE.replace(/^http/, 'ws') + '/';

export const GAMEDATA_BASE = `${HTTP_BASE}/gamedata`;

export const api = (path: string): string => `${HTTP_BASE}${path}`;
