// Modul: THE ANDROID APP, OFFERED FROM THE BROWSER.
//
// A player who found the game in a browser had no way to learn that an app
// exists. The login screen now carries a permanent link and, once per browser,
// a popup that says so.
//
// Offered only where it can be installed: never inside the app itself (it IS
// the app), and never on an iPhone or iPad, which cannot install an APK and
// would download a file it has no way to open. A desktop browser still gets it
// - plenty of players sit at a desk and play on the phone later - and the
// popup prints the address so it can be typed on the phone.
//
// The file is served by Caddy from ops/oracle/downloads/ (see /download/* in
// the Caddyfile), put there by ops/oracle/publish-apk.ps1. Relative, so it
// follows whichever hostname the page was loaded from.

import { isNativePlatform } from './platform';

export const APP_DOWNLOAD_PATH = '/download/folkidle.apk';

const PROMO_SEEN_KEY = 'folkidle.appPromoSeen';

/** An iPhone, an iPad, or an iPad that asks for the desktop site. */
export function isAppleMobile(userAgent: string, maxTouchPoints: number): boolean {
  if (/iPhone|iPad|iPod/i.test(userAgent)) return true;
  // iPadOS 13+ reports a Mac user agent by default; a touchscreen Mac does
  // not exist, so touch points are what give it away.
  return /Macintosh/i.test(userAgent) && maxTouchPoints > 1;
}

export function shouldOfferApp(
  native: boolean = isNativePlatform(),
  userAgent: string = globalThis.navigator?.userAgent ?? '',
  maxTouchPoints: number = globalThis.navigator?.maxTouchPoints ?? 0,
): boolean {
  return !native && !isAppleMobile(userAgent, maxTouchPoints);
}

/** The full address, for a player who has to type it on another device. */
export function appDownloadUrl(origin: string = globalThis.location?.origin ?? ''): string {
  return `${origin}${APP_DOWNLOAD_PATH}`;
}

// Browser storage can throw (private windows, blocked site data). A failed
// read shows the popup again, which is harmless; it must never break login.
export function promoSeen(): boolean {
  try {
    return localStorage.getItem(PROMO_SEEN_KEY) === '1';
  } catch {
    return false;
  }
}

export function markPromoSeen(): void {
  try {
    localStorage.setItem(PROMO_SEEN_KEY, '1');
  } catch {
    // Nothing to do: at worst the popup comes back next visit.
  }
}

const PLAYED_KEY = 'folkidle.playedBefore';

/**
 * Whether this browser has been through at least one signed-in session.
 *
 * Task 109: the app popup waits for this, so a first-time visitor sees the
 * game's pitch rather than an install prompt. Same failure direction as
 * promoSeen: storage that cannot be read means "not yet", which only delays
 * the popup.
 */
export function playedBefore(): boolean {
  try {
    return localStorage.getItem(PLAYED_KEY) === '1';
  } catch {
    return false;
  }
}

export function markPlayed(): void {
  try {
    localStorage.setItem(PLAYED_KEY, '1');
  } catch {
    // At worst the popup never appears in this browser; the link still does.
  }
}
