# Building FolkIdle for Android and iOS

The mobile app is the **same web build** inside a native WebView. There is no
second client and no separate codebase - Capacitor wraps `dist/`.

## What is already done

- `@capacitor/core`, `@capacitor/cli`, `@capacitor/android`, `@capacitor/ios`
  installed; `capacitor.config.json` points at the Vite output.
- The client detects a native shell (`src/lib/net/platform.ts`) and changes the
  two things that actually differ - see "Why native differs" below.
- npm scripts: `sync`, `build:android`, `open:android`, `open:ios`.

## What you still need (and nobody can do for you)

- **Android:** a JDK and the Android SDK. `cap add android` generates the
  project; `cap build android` needs Gradle to be able to run.
- **iOS:** a Mac with Xcode. This is the same hard requirement Unity had - it
  is a platform rule, not a limitation of this stack.
- **A reachable server over HTTPS.** See below.

## First run

```bash
npm install
npm run cap:add:android      # once, generates android/
npm run build:android        # build web -> sync -> gradle
npm run open:android         # opens Android Studio for signing and running
```

`npm run sync` alone rebuilds the web app and copies it into the native
project. Run it after every web change; the native project does not watch.

## Why native differs, in exactly three places

Everything else is byte-identical to the browser build. These three are not,
and each one fails **silently** if ignored - which is why they are code and
comments rather than a note in a wiki.

### 1. The server address

`localhost` on a phone means *the phone*. A native build left pointing at the
development default reaches nothing, and it fails as a connection timeout,
which reads like the server being down.

```bash
VITE_FOLKIDLE_SERVER=https://api.example.com npm run build:android
```

`configurationProblem()` in `src/lib/net/config.ts` detects both this and the
next one, and the login screen says so plainly instead of appearing to hang.

### 2. HTTPS, therefore WSS

Capacitor serves the page from `https://localhost` (Android) or
`capacitor://localhost` (iOS). A page on a secure origin **cannot open a plain
`ws://` socket** - the WebView blocks it as mixed content, and blocks it
without an error the page can catch.

`WS_URL` is derived from `HTTP_BASE`, so an `https://` server address produces
`wss://` automatically. The server needs a real certificate; there is nothing
to configure on this side.

### 3. CORS

The server's allow-list is exact-match. Add the origin for the platform you are
building, or every request fails before the player sees anything:

```bash
FOLKIDLE_WEB_ORIGINS="https://localhost,capacitor://localhost,https://play.example.com"
```

Both Capacitor origins are exported as `CAPACITOR_ORIGINS` from
`src/lib/net/platform.ts` so the values live beside their explanation.

## Token storage changes on native, deliberately

The browser build keeps the JWT in `sessionStorage` because "dies with the tab"
is a sensible lifetime for a session the player chose to close. A phone does
not work that way - the OS suspends and kills apps on its own schedule - so the
same rule would sign the player out at moments they did not cause and cannot
predict. Native builds use `localStorage` instead.

Signing out clears **both**, so switching platforms cannot leave a token behind
in the store that is no longer being read.

## Not built yet

- **Push notifications.** `@capacitor/push-notifications` would give a native
  FCM/APNs token that fits the `RegisterPushToken` opcode's 64-byte field, and
  the server already has `PushNotificationTriggerEngine`. Nothing is wired.
- **In-app purchases.** The store screen shows the personalised storefront and
  its prices as information, with the buy path disabled, because completing a
  purchase needs a platform store SDK and then
  `/api/v1/billing/verify-receipt`. That endpoint exists and has never been
  called by any client - worth knowing before shipping a paid build.
- **Icons, splash screens, app IDs and signing keys.** `appId` in
  `capacitor.config.json` is a placeholder (`com.folkidle.game`).

## Not verified

The web build has never been run on a real phone or on a touch screen. Layouts
are responsive grids and should reflow, but "should" is not "was tried". Treat
the first device run as a testing session, not a formality.
