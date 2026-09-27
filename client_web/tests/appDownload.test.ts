import './helpers/nodeLocalStorage';
import { describe, it, expect, beforeEach } from 'vitest';
import {
  APP_DOWNLOAD_PATH,
  appDownloadUrl,
  isAppleMobile,
  markPromoSeen,
  promoSeen,
  shouldOfferApp,
} from '../src/lib/net/appDownload';

/*
  THE ANDROID APP OFFER. Offered in a browser that could use it, never inside
  the app and never on an iPhone/iPad, and the popup is seen once.
*/

const ANDROID = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/128.0 Mobile Safari/537.36';
const DESKTOP = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128.0 Safari/537.36';
const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148';
const MAC = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Version/18.0 Safari/605.1.15';

describe('app download offer', () => {
  beforeEach(() => localStorage.clear());

  it('is offered in an Android or desktop browser', () => {
    expect(shouldOfferApp(false, ANDROID, 5)).toBe(true);
    expect(shouldOfferApp(false, DESKTOP, 0)).toBe(true);
    expect(shouldOfferApp(false, MAC, 0)).toBe(true);
  });

  it('is not offered inside the app', () => {
    expect(shouldOfferApp(true, ANDROID, 5)).toBe(false);
  });

  it('is not offered on an iPhone, or an iPad posing as a Mac', () => {
    expect(isAppleMobile(IPHONE, 5)).toBe(true);
    expect(isAppleMobile(MAC, 5)).toBe(true);
    expect(shouldOfferApp(false, IPHONE, 5)).toBe(false);
    expect(shouldOfferApp(false, MAC, 5)).toBe(false);
  });

  it('builds the full address from the page origin', () => {
    expect(appDownloadUrl('https://folkidle.duckdns.org')).toBe('https://folkidle.duckdns.org/download/folkidle.apk');
    expect(APP_DOWNLOAD_PATH).toBe('/download/folkidle.apk');
  });

  it('remembers the popup was seen', () => {
    expect(promoSeen()).toBe(false);
    markPromoSeen();
    expect(promoSeen()).toBe(true);
  });
});
