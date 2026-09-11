# Building FolkIdle for Android and iOS

The mobile app is the **same web build** inside a native WebView. There is no
second client and no separate codebase - Capacitor wraps `dist/`.

## What is already done

- `@capacitor/core`, `@capacitor/cli`, `@capacitor/android`, `@capacitor/ios`
  installed; `capacitor.config.json` points at the Vite output. Both `android/`
  and `ios/` are generated and committed.
- The client detects a native shell (`src/lib/net/platform.ts`) and changes the
  two things that actually differ - see "Why native differs" below.
- **App-resume handling** (`src/lib/net/lifecycle.ts`, 2026-09-09). A phone
  freezes the WebView; the socket dies in the player's pocket and the reconnect
  loop in `connection.ts` was written for a server that went away, not for a
  client that stopped existing. `watchAppLifecycle()` listens on three sources
  (`visibilitychange`, Capacitor's `appStateChange`, `pageshow`) because no one
  of them fires on every platform, and debounces so all three firing for one
  resume is one resume. It is started from `stores/game.ts`.
- **Touch targets are measured** (`npm run check:touch`, 2026-09-09). See
  "Checking the phone build" below.
- **Push notifications, end to end** (2026-09-10) - permission, token, delivery
  and tap-to-screen. See "Notifications" below for what still needs a Firebase
  project.
- **A session that survives the night** (2026-09-10). A rotating refresh token;
  see "Session length" below.
- **Real icons and a splash** (2026-09-10), generated from two SVGs - see "App
  identity". What shipped before was Capacitor's blue placeholder.
- **A store adapter and a receipt path that could actually verify a receipt**
  (2026-09-10). See "In-app purchases"; the second half was a genuine defect.
- **Release signing wired from a gitignored key file** (2026-09-10).
- **Onboarding remembered per ACCOUNT** rather than per device (2026-09-10).
- **A deliberate suspend when the app is backgrounded** (2026-09-10).
- **`npm run check:perf`** (2026-09-10), which measures the client on a
  4x-throttled main thread and asserts budgets.
- **The hardware back button** (2026-09-10) and **a no-network state that says
  something** (2026-09-10). See their own sections below.
- **The native plugins are actually installed** (2026-09-11). They were not,
  and three modules had been reading plugins that were never going to be there
  — see "The plugins were missing" below. This is the one that mattered.
- **The status bar no longer sits on top of the game** (2026-09-11), plus
  `npm run check:safearea`. See "Edge-to-edge" below.
- npm scripts: `sync`, `sync:web`, `build:android`, `build:android:web`,
  `open:android`, `open:ios`, `normalize:native`.

## What you still need (and nobody can do for you)

- **Android:** a JDK and the Android SDK. `cap add android` generates the
  project; `cap build android` needs Gradle to be able to run.
- **iOS:** a Mac with Xcode. This is the same hard requirement Unity had - it
  is a platform rule, not a limitation of this stack.
- **A reachable server over HTTPS.** See below.

## The app updates itself

The shape modern games use: the install is a SHELL, and the content arrives over
the air. Here the shell is the APK — Java, the Capacitor plugins, the
permissions — and the content is everything in `dist/`. So a gameplay change
reaches a player when they open the app, not when they remember to reinstall.

`@capgo/capacitor-updater` does the download and the swap (MPL-2.0, self-hosted
— `statsUrl` and `channelUrl` are set to `""` so nothing is reported to anyone
else). `autoUpdate` is `onLaunch`: it checks when the app is brought up from a
killed state and applies immediately, which is the moment a player expects to
wait a second.

**What it cannot carry: anything native.** A new plugin, a new permission, a
Gradle change — none of that is in `dist/`, so none of it travels this way. Those
still need a real APK.

### `notifyAppReady` is the most important line in the feature

Over-the-air updates introduce a failure mode nothing else here has: **a bundle
that cannot boot**. A syntax error, a bad import, a Svelte snippet rendered with
component-tag syntax — this repo has shipped that last one, and it took the whole
screen down at runtime while `svelte-check` passed. In an APK the fix is another
APK. Over the air, without a rollback, every phone that took the update is
bricked until the player uninstalls — and the update system is *inside* the
broken bundle, so it cannot heal them.

The plugin's answer is a dead-man's switch: after applying a bundle it waits for
`notifyAppReady()`, and if the app never calls it the previous bundle is restored
on the next launch, permanently.

So the call is the assertion "I booted", and it is made **after the Svelte mount**
(`src/main.ts`), never at import time — an import-time call would confirm a bundle
whose entire UI throws. It deliberately does not wait for the server: a phone in a
tunnel is not a broken bundle.

### How a release reaches a phone

1. `docker compose up -d --build` with `FOLKIDLE_BUNDLE_VERSION` set — see
   `ops/oracle/README.md`. The web image builds `dist` once and zips the same
   `dist` into `/srv/updates/<version>.zip`, so the site and the bundle can
   never be different builds.
2. Caddy serves `/updates/*` as immutable static files.
3. `POST /api/v1/app/bundle` on the server answers with `{version, url}`.
4. The phone downloads it on next cold start and applies it.

**The endpoint fails closed.** With `FOLKIDLE_BUNDLE_VERSION` or
`FOLKIDLE_BUNDLE_URL` unset it answers `{"message":"no bundle configured"}` and
nothing updates. A wrong answer to an update check is not a failed request — it
is every phone applying something broken — so silence is the only safe default.

### Known and deliberate: the whole bundle travels

A typical update is ~17 MB, of which about 16.9 MB is artwork that did not
change; the app code is ~720 KB. The plugin *does* support differential
downloads — `getLatest` may return a `manifest` of
`{file_name, file_hash, download_url}` and it fetches only what differs, which
would cut an update by roughly 25x.

It is not used yet because the hash format has to match the plugin's own byte
for byte, and getting it wrong does not fail loudly: it silently re-downloads
everything, or decides nothing changed. **That needs a device to verify and
there has not been one.** Correct and slow first.

### Not verified on hardware

Everything above is reasoned from the plugin's source and tested at the seams
(the endpoint answers correctly in both states; the plugin is linked into both
native projects). **No update has ever been watched landing on a real phone.**
The first device session should install an old APK, deploy, and confirm the app
picks the bundle up on a cold start — and then deliberately ship a broken bundle
to a test device to confirm the rollback works, because a rollback nobody has
seen is a rollback nobody has.

## Getting it onto your own phone, today

Two routes. The first needs nothing installed.

### 1. Take the APK CI already built (Android, no toolchain)

Every push to `main` assembles a debug APK and keeps it for 14 days.

1. Open the repository's **Actions** tab, click the newest green
   *Build, Test, and Deploy* run.
2. Scroll to **Artifacts** and download **`folkidle-debug-apk`**. It arrives as
   a zip; unzip it to get the `.apk`.
3. Put the file on the phone (USB, Drive, or email it to yourself).
4. Tap it. Android will say the app came from an unknown source — allow the
   installing app (your browser or Files) to install unknown apps, then tap
   Install again.

**It points at `https://folkidle.duckdns.org`**, the live server, so it plays
the real game against your real account. That was only true from 2026-09-11: the
step that builds the bundle had no `VITE_FOLKIDLE_SERVER`, so every APK before
that inlined `http://localhost:8080` — which on a phone means the phone — and
could not reach anything. It failed *politely*, saying what was wrong on the
login screen, which is why the artefact looked fine until somebody tried to
sign in.

It is a **debug** build: unsigned for the store, slightly slower, and Android
marks it as such. That is the right trade for a test install and the wrong one
for anybody else.

### 2. Build it yourself (Android)

Needs **JDK 21** — not 17. `capacitor.build.gradle` sets
`sourceCompatibility JavaVersion.VERSION_21`, so Gradle on 17 fails with a
class-version error that does not mention the JDK — and the Android SDK, which
Android Studio installs.

```bash
VITE_FOLKIDLE_SERVER=https://folkidle.duckdns.org npm run build:android:web
```

The APK lands in `android/app/build/outputs/apk/`. Omit that environment
variable and you get the localhost build described above.

### iOS

A Mac with Xcode, and an Apple Developer account to put it on a physical
device. That is a platform rule, not a limitation of this stack. Run
`npm run sync:web` then `npm run open:ios` there.

## First run

```bash
npm install
npm run cap:add:android      # only if android/ is missing; it is committed
npm run build:android:web    # build web -> sync -> gradle
npm run open:android         # opens Android Studio for signing and running
```

`npm run sync:web` alone rebuilds the web app and copies it into the native
project. Run it after every web change; the native project does not watch.

## Which sync to run: `sync` or `sync:web`

They differ in exactly one thing - whether the build asks the C# server what
the wire looks like.

| Script | Regenerates the protocol | Needs the .NET SDK + server source |
|---|---|---|
| `npm run sync`, `npm run build:android` | yes (`generate:protocol`) | **yes** |
| `npm run sync:web`, `npm run build:android:web` | no | no |

**Use `sync:web` on any machine that is not a full development checkout** - in
practice, the Mac doing the iOS build. `npm run build` shells out to the
server's `--dump-protocol` to regenerate `src/lib/net/protocol.generated.ts`,
and on a machine with no working .NET server that fails as a `dotnet` error in
the middle of what otherwise looks like a Capacitor problem.

`sync:web` instead runs `generate-protocol.mjs --assume-committed`, which
checks that the committed generated file is present and is really generator
output, warns if the working copy has been modified since the commit, and
contacts nothing.

**This does not weaken drift protection, and it cannot.** Detecting drift means
asking the server for its own struct layout, which is precisely what the
machine running `sync:web` cannot do. `node scripts/generate-protocol.mjs
--check` is still the gate, it still runs in CI on every push, and a packet
struct changed without regenerating still fails the pipeline there. What
`sync:web` buys is a *build* on a machine without the toolchain - not a
different contract.

Sprite generation (`generate:sprites`) reads only the repository - the WebP
tree under `client/Assets/Images/SpritesWeb` and `server/GameData/items.json` -
so it needs no .NET and `sync:web` keeps it.

**`npm run build` works again as of 2026-09-10, and it had not for months.**

There is a known baseline of four `svelte-check` errors - the hidden Guild War
handlers in `GuildOps.svelte`, see the repo's CLAUDE.md - and `svelte-check`
exits 1 whenever there is any error at all. So every script that chained it
stopped before Vite ever ran: `build`, and therefore `sync` and
`build:android`, which are the two commands this document tells you to use.

Nobody noticed because production calls `npx vite build` directly and CI
reimplemented the "fail only if the count grows" ratchet as a shell block
inside `deploy.yml` - one rule, written twice, in the one place a developer
cannot run it. It now lives in `scripts/typecheck-ratchet.mjs` and both call it.

`build:web` still does not chain a type-check, deliberately: packaging an app is
not the place to discover a months-old baseline, and the machine running
`sync:web` is usually not a full checkout. Type-checking is `npm run check`
(raw, exits 1 on the baseline) or `npm run check:ratchet` (the gate).

## The plugins were missing, and that is why none of this could have worked

Found 2026-09-11, by reading `node_modules` instead of the source.

`push.ts`, `lifecycle.ts` and `backButton.ts` all read their plugin off the
runtime-injected `Capacitor.Plugins` object rather than importing it. Each one
says why, at length, and each reason is correct: an import would drag a
native-only module into every browser bundle.

**But `@capacitor/app` and `@capacitor/push-notifications` were not installed at
all.** Only `core`, `preferences`, `android`, `ios` and the Cordova purchase
plugin were, and `android/capacitor.settings.gradle` linked exactly what
package.json declared — which is to say, neither of them. So on a real device:

- `Capacitor.Plugins.App` was `undefined`, and every access is guarded, so it
  degraded silently. **The hardware back button fell through to Capacitor's
  default — exit the app from any screen** — which is precisely the bug
  `backButton.ts` was written to fix, with its fix inert. `appStateChange`
  never fired either; the deliberate suspend ran on `visibilitychange` alone.
- `Capacitor.Plugins.PushNotifications` was `undefined`, so push was dead on
  arrival regardless of Firebase. "Not built yet: a Firebase project" understated
  it by one whole layer.

**The rule, because the shape recurs: reading a plugin off the global is about
the WEB BUNDLE, not about installation.** `cap sync` links what package.json
declares, and the native shell injects only what was linked. "Not imported" and
"not installed" look identical in a browser and are opposites on a phone.
`tests/nativeProjects.test.ts` now pins every plugin the client reads to a
dependency and to both native projects.

Two things came with them:

- **`POST_NOTIFICATIONS` is declared in the app manifest.** The push plugin
  requests it via `@Permission` but contributes only its `MessagingService` to
  the manifest merge, and from Android 13 the platform refuses a runtime request
  for an undeclared permission *silently* — no dialog, an immediate "denied",
  indistinguishable from a player saying no. Settings only gets one ask.
- **`android:allowBackup` is now `false`.** Capacitor's template says true,
  which copies the WebView's `localStorage` — and therefore the 60-day refresh
  token — into Google Drive and restores it onto any device the account signs
  into. Nothing is lost by declining: the simulation is on the server, so a
  restored install needs one sign-in and nothing else.

## `cap sync` on Windows writes a Package.swift that Swift cannot parse

Also 2026-09-11, and the reason CI's native lane was going to fail next.

The Capacitor CLI interpolates a `path.relative` result straight into a Swift
string literal. On Windows that is `..\..\..\node_modules\@capacitor\app`, and
a backslash in a Swift literal is an **escape introducer**: `\.` and `\@` are
invalid escapes and `\n` is a newline. The manifest does not compile, so the
committed iOS project could not be built on a Mac — invisible here, because
this project's only Apple toolchain is hypothetical.

`npm run sync` and `npm run sync:web` now run `scripts/normalize-native.mjs`
after `cap sync`, so the separator is right by construction rather than by
somebody remembering. `tests/nativeProjects.test.ts` is the guard for a sync run
without it.

`ITSAppUsesNonExemptEncryption` is in `Info.plist` too, answered `false` — the
app's only cryptography is the HTTPS it talks to its own server over. Without
the key App Store Connect stops every build to ask a human.

## Edge-to-edge: the status bar was sitting on top of the game

`index.html` asks for `viewport-fit=cover`, and Capacitor's `SystemBars` reads
that as consent: on a WebView from version 140 it stops padding the view and
passes the insets to CSS instead, so the page draws **under the status bar and
the gesture bar**. Android 15 removed the opt-out for anything targeting SDK 35
and this app targets 36, so it is not a mode the app can decline.

The client handled one of the four insets. The header — which carries the menu
button, the most-pressed control in the game — was going to render under the
clock on every modern phone.

`app.css` now defines `--sa-top/right/bottom/left` as
`var(--safe-area-inset-*, env(safe-area-inset-*, 0px))`. That chain is correct
on all three platforms without asking which one it is on: **Android's WebView
does not implement `env()` reliably, so Capacitor injects those four custom
properties itself** (as zeroes in the branch where it padded the view), while
iOS injects nothing and WKWebView implements `env()` properly.

Two things fell out of fixing it:

- **The phone block was replacing the chat-dock clearance, not adding to it.**
  `body { padding-bottom: max(8px, env(safe-area-inset-bottom)) }` sat below
  `body { padding-bottom: 4.5rem }` at the same specificity, so under 40rem the
  4.5rem simply stopped existing — at the width where the dock is most in the
  way. It is one rule now, `calc(4.5rem + var(--sa-bottom))`.
- **ChatDock was setting `:global(body) { padding-bottom: 4.25rem }` as well**,
  a third copy of the same number in a third place. Removed.

The four bottom-anchored fixed overlays (chat dock, toasts, achievement toasts,
onboarding coach) carry `var(--sa-bottom)` in their own offsets, because a
fixed element is positioned against the viewport and body's padding never
reaches it.

**This is checkable without a phone**, which is the whole point of routing it
through a custom property: `npm run check:safearea` sets those four properties
exactly as the native layer does and measures what moves.

## Why native differs, in exactly three places

Everything else is byte-identical to the browser build. These three are not,
and each one fails **silently** if ignored - which is why they are code and
comments rather than a note in a wiki.

### 1. The server address

`localhost` on a phone means *the phone*. A native build left pointing at the
development default reaches nothing, and it fails as a connection timeout,
which reads like the server being down.

```bash
VITE_FOLKIDLE_SERVER=https://api.example.com npm run build:android:web
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

`localStorage` and not Capacitor Preferences, even though Preferences is
installed: `storedToken()` is synchronous and is called on every request, and
Preferences is async. The reasoning is written out at the top of
`src/lib/net/auth.ts`.

Signing out clears **both**, so switching platforms cannot leave a token behind
in the store that is no longer being read.

## Checking the phone build

Six Playwright checkers cover what a phone exposes. None of them needs a
device; all of them need a running local stack, and all sign in as the dev
fixture, so **do not aim them at production**.

```bash
npm run check:mobile     # horizontal overflow at 320 / 360 / 414
npm run check:clipping   # content cut off, 26 screens x 3 widths
npm run check:overlap    # controls buried under other controls
npm run check:touch      # controls too small for a thumb, 44px floor at 390px
npm run check:perf       # main-thread cost with the CPU throttled 4x
npm run check:safearea   # anything under the status bar or the gesture bar
```

`check:safearea` is the newest (2026-09-11) and the only one that measures the
page against hardware rather than against its own viewport. It walks all 26
screens in **both orientations** — 52 pairs — with a representative notch
(48/24 portrait, 44/21 sides in landscape) applied the way the native layer
applies it.

Three rules it encodes, each learned by getting it wrong on the first run, which
reported **45 failures on a correct layout**:

- **The top band is judged at rest.** Content sliding under the status bar as
  you scroll is what edge-to-edge means; every native app does it. What must
  never be under the clock is what greets somebody opening the game.
- **The bottom band is judged only where scrolling cannot cure it** — something
  genuinely pinned, or the very end of the document. This is the same
  conclusion `check:touch` reached, for the same reason.
- **Pinned is decided by scrolling, not by `position: sticky`.** The Wiki's
  sidebar is declared sticky and at 390px the layout stacks so it never sticks;
  reading the computed style produced the last two false findings. The checker
  scrolls the page and sees what moved. `check:touch`'s note says the same
  thing, and it still had to be learned twice.

It also asserts the layout **responds** — the header must move down by the full
inset — because every other assertion in the file passes trivially on a page
that ignores the properties entirely.

`check:perf` is the newest (2026-09-10) and the only one that is not about
geometry. It slows the main thread to a quarter and watches long tasks, total
blocking time and heap growth for twelve seconds per screen, then **asserts
budgets** - a measurement a script only prints is decoration. It reports `n/a`
rather than `+0.0MB` where `performance.memory` is absent, so a missing API
cannot masquerade as a passing measurement.

`check:touch` is the newest (2026-09-09) and the one written specifically for
this app. The floor is 44px from Apple's HIG and WCAG 2.5.5, measured on the
control's own box rather than on the text inside it, and it also fails a
control sitting too close to the bottom edge to be pressed. It found **221**
undersized targets on its first run - every screen's shared chrome sat at 37px.

It read 5 on 2026-09-10, and **four of those five were the checker's own fault**
- fixed the same day, along with two things that made it measure less than it
claimed:

- It only ever measured the **first viewport**. Anything below the fold was
  skipped, so on a 3147px Wiki it checked the top 844px and reported as though
  it had checked the screen. It now measures in overlapping passes down the
  page, which immediately found a real failure on Settings that had never been
  visible.
- **Crowding is decided by scrolling now**, not by computed style. A control
  counts as sitting on the bottom edge only if it did so in every pass that saw
  it - `position: sticky` was tried as the test and is not one, because the
  Wiki's sidebar is sticky and at 390px the layout stacks so it never sticks.

The two genuine failures were both `input[type="range"]` at 32px tall, on
Auto-Eat and Settings. Ranges had been excluded from the 44px floor beside
checkbox and radio, which is backwards: a slider is the one control on a phone
that is nothing but a thumb target. **Now 0 across all 26 screens.**

## Not built yet

Two things, and neither is code.

- **A Firebase project.** Push is wired end to end (see "Notifications") and
  cannot send a byte without `FCM_PROJECT_ID`, `FCM_CLIENT_EMAIL` and
  `FCM_PRIVATE_KEY` in the server's environment. The server now says so at
  start-up rather than failing silently:

  ```
  Push: NOT CONFIGURED - missing FCM_PROJECT_ID, FCM_CLIENT_EMAIL, FCM_PRIVATE_KEY.
  Device tokens will be stored and triggers scheduled, but NOTHING WILL BE SENT.
  ```

  iOS needs strictly more: FCM v1 addresses a device by an FCM registration
  token issued by the Firebase iOS SDK, and what Capacitor hands over on iOS is
  the raw APNs token, which FCM will not accept. Android works once the project
  exists.

- **An upload keystore, and the store consoles.** See "Signing" below. Creating
  products in Play Console and App Store Connect, filling in Data Safety and the
  privacy nutrition labels, and getting an age rating are all console work.

## App identity, icons and the splash

The bundle id is **`com.folkidle.game`** on both platforms. It cannot be changed
after the first store upload, so it is decided rather than pending.

`resources/icon.svg` and `resources/splash.svg` are the SOURCES; every PNG the
two platforms want is rasterised from them:

```bash
npm run generate:icons     # 30 files, Android mipmaps + iOS + both splashes
```

The outputs are committed, because CI runs `cap sync` and then fails on a dirty
tree, and a build machine should not need a browser to produce an icon. Run it
when the artwork changes and commit what it writes.

The icon is drawn for the **mask**: Android crops an adaptive icon to whatever
shape the launcher fancies and only the middle 66% survives, so everything that
carries meaning sits inside that circle. `ic_launcher_background.xml` is
`#100D0A` rather than Capacitor's white - a circular mask on a white background
draws a white ring around dark artwork.

It does NOT use `@capacitor/assets`, which would pull in `sharp`, a native
binary that has to match the platform. Playwright is already here.

## Signing

`android/app/build.gradle` reads a release key from `keystore.properties` in
`android/` (gitignored), or failing that from `FOLKIDLE_KEYSTORE_PATH`,
`FOLKIDLE_KEYSTORE_PASSWORD`, `FOLKIDLE_KEY_ALIAS` and `FOLKIDLE_KEY_PASSWORD`.

With nothing configured it produces an **unsigned** release build rather than
failing - so every machine without the key still builds, and an unsigned
artefact announces itself the moment anybody tries to upload it.

Making the key is yours, because it needs a password nobody should type into a
script:

```bash
keytool -genkey -v -keystore folkidle-upload.jks -keyalg RSA -keysize 2048 \
        -validity 10000 -alias folkidle
```

**Back it up somewhere that is not the laptop it was made on.** Play signs every
release with this key and it cannot be replaced. Lose it and the listing can
never be updated again, by anybody; the only remedy is a new listing with no
installs and no reviews.

## Store listing assets

```bash
npm run screenshots:store   # 18 shots, dev box only
```

Writes `resources/store-screenshots/` at the sizes the stores actually demand:
1080x1920 for Play, and Apple's 6.7" (1290x2796) and 6.5" (1242x2688), which are
the two sets a new submission is rejected without.

Two things are pinned in that script and both were wrong on the first run:
`colorScheme: 'dark'`, because the client follows the system and Playwright
defaults to light - producing parchment screenshots beside a dark app icon; and
`locale: 'en-GB'`, because `initLanguage` reads `navigator.language` and a Czech
dev box produced an English UI with three Czech words in the header.

**`public/privacy.html`** and **`public/delete-account.html`** are served as real
files by Caddy's static handle. Play requires the deletion route to work
**without the app**, for somebody who has already uninstalled - an in-app path
alone is a rejection.

Both pages quote **folkidle.support@gmail.com** (set 2026-09-11). Until then
they carried the literal string `CONTACT_EMAIL`, on the one page Play requires
to work for somebody who has already uninstalled — which made it decorative.
**That mailbox has to exist and be read**; a deletion request sent into nothing
is the same rejection with extra steps. `tests/storeCompliance.test.ts` fails
if the placeholder returns or if the two pages ever disagree.

## In-app purchases

The vendor is **cordova-plugin-purchase** - no revenue share beyond the stores'
own cut, one API for both platforms. It is read off the injected global, exactly
as `push.ts` reads its plugin, so it never enters the web bundle.

`storeAdapter.ts` implements the `StoreAdapter` interface `billing.ts` has always
declared, and `storeRegistration.ts` registers it with the product ids from
`/api/v1/store/catalog` - the same `IapProductPrices` the server pays out
against, so the mapping is not written down twice.

**The receipt path could never have verified a real receipt, and that is the
bigger half of this.** The server checked a bespoke `{provider, payload,
signature}` envelope that no store produces and no client could manufacture -
signing it needs a key a client must never hold. It was satisfiable only by the
test that invented it, and nothing noticed because
`/api/v1/billing/verify-receipt` had never been called.

The client now sends what the store actually gave it, and the server ASKS the
store:

```
Google Play : { provider, productId, transactionId, purchaseToken }
App Store   : { provider, productId, transactionId }
```

No signature - the absence of one is the server's discriminator between the new
scheme and the legacy envelope. `StoreApiReceiptVerifier` fails closed: an
envelope naming a store with no credentials configured is refused, never passed
to a validator that would evaluate a signature that was never there.

Still needed: the products in both consoles, and
`FOLKIDLE_IAP_GOOGLE_SERVICE_ACCOUNT_PATH`,
`FOLKIDLE_IAP_APPLE_PRIVATE_KEY_PATH`, `FOLKIDLE_IAP_APPLE_KEY_ID`,
`FOLKIDLE_IAP_APPLE_ISSUER_ID` in the server's environment. Test the refund and
cancellation paths, not only the happy one.

## What the app does when you put it away

It **puts the socket down on purpose**, on native only.

Nothing used to. `lifecycle.ts` listened for the app coming back and had no
notion of it going away, so the socket survived until the OS froze the WebView -
minutes on Android - and for those minutes a pocketed phone went on receiving
and decoding a 10 Hz packet stream.

Closing is free because the **simulation is on the server**: a disconnected
client is one that is not watching, and offline catch-up pays for the gap - the
same mechanism somebody who closes the app entirely already relies on.

A hidden **desktop** tab is deliberately left alone. It is a legitimate way to
leave an idle game running, and closing there would cost the live view and any
chat in it for no battery saved on a machine that is plugged in. The platform
separates the two, not the visibility state, which is identical in both.

## Onboarding is per-account now, not per-device

The seen-set used to be `localStorage` keyed by player id, so a phone re-taught
everything a browser already taught. `PlayerRecord.OnboardingSeenIds` is the
truth now, behind `GET/PUT /api/v1/player/onboarding-seen`, with `localStorage`
kept in front as a cache - which cannot be removed, because the cue is derived
on every packet and must answer synchronously.

**Null is not an empty set.** Absent means "never baselined anywhere", which is
the signal to mark everything already true as read rather than queueing
seventeen explanations at somebody who has played for weeks. Empty-and-present
means the opposite.

## Notifications

The one argument for an app rather than a bookmark, and it is wired now.

**The token goes over REST, not over opcode 33.** `ClientCommandPacket
.DeviceTokenBytes` is a fixed `byte[64]` and `RegisterDeviceAsync` refused
anything that was not exactly that length. An FCM registration token is roughly
160 characters, so Android push could never have worked through that path and
iOS push fitted it with nothing to spare. Nothing had ever sent one, so nobody
found out. `POST /api/v1/player/push-token` takes `{ Token, Platform }` and
answers 400/401/503/500 rather than dropping a bad token silently - the Settings
screen repeats the answer, because a push feature that never fires is
indistinguishable from one the player turned off.

**Permission is asked for from a control the player pressed**, in Settings, and
nowhere else. The OS remembers a refusal permanently; an app that prompts on
first launch, before it has shown anything worth being notified about, spends
its one ask and cannot get another. Declining leaves the game fully playable and
nothing asks again.

**The message carries a `notification` block, not just `data`.** It used to
carry data alone, which FCM delivers to a running app and drops on the floor of
a backgrounded one - so the single moment the feature exists for arrived as
silence. `PushMessageShapeTests` fails if that ever comes back.

**Tapping opens the screen the notification is about**, and the destination
comes from the server in `data.screen`. The client does not map trigger codes to
screens; that would be the server's table written down twice in two languages,
which is how `KNOWN_AFFIX_IDS` drifted into ten wrong entries out of twelve.

`src/lib/net/push.ts` reads the plugin off the injected `Capacitor.Plugins`
object rather than importing it, exactly as `lifecycle.ts` does, so
`@capacitor/push-notifications` never becomes a dependency of the web bundle.

## Session length: the app opens straight into the game

The JWT lives 24 hours. In a browser tab that is a mild annoyance; on a phone it
was a password prompt every morning, in a game whose whole proposition is that
it runs while you are gone.

The server now issues a **refresh token** beside the JWT: 32 bytes of CSPRNG
output, stored as a SHA-256 hash, good for 60 idle days, rotated on every use.
`POST /api/v1/auth/refresh` trades one for a fresh session; `POST
/api/v1/auth/revoke` ends one; a password reset revokes every token the account
holds, for the same reason it already clears the remembered `DeviceId`.

**`TokenLifetimeSeconds` is deliberately unchanged.** Lengthening the JWT was
the cheap option and it is the one not taken: a JWT is a bearer credential this
server does not store, so a stolen 60-day one is valid for 60 days and nothing
anybody does - not a password change, not a sign-out, not support - shortens
that by a second. A refresh token is a row, and a row can be revoked.

Two client-side rules matter, and `tests/sessionRefresh.test.ts` pins both:

- **A refused token is discarded.** The server treats a second presentation of a
  spent token as theft and revokes the whole account, so a client that keeps a
  refused token signs the player out of every device they own on the next
  launch.
- **An unreachable server's token is kept.** A phone in a tunnel holds a
  perfectly good token; discarding it there would make a lost signal
  indistinguishable from an expired session.

On the web the refresh token lands in `sessionStorage` and dies with the tab, so
a browser's session length does not change at all. Only the native build gets a
persistent login, which is the case this exists for.

## The Android back button

It used to exit the app from any screen - a player two taps deep in the Chest
pressed the button their thumb has pressed a thousand times and the game closed.

`src/lib/net/backButton.ts` holds the decision as a **pure function**, so the
ordering can be tested without a device (`tests/backButton.test.ts`). The order
is the paint order of the layers, topmost first: exit prompt, death card,
victory card, offline summary, chat dock, nav menu, then the screen history,
then the map, then the exit confirmation. Get it wrong and back appears to skip
a layer - it closes something behind whatever is covering the screen and the
player sees nothing happen.

**Registering a listener is what disables Capacitor's own exit**, so from that
moment this handler owes the player a way out. That is what the confirmation
dialog is for, and why it is rendered outside the signed-in branch - it has to
work on the login screen too.

## Losing signal says something

A phone loses signal several times an hour and a spinner is not an answer. The
one-line "reconnecting (attempt 4)" banner has been replaced by
`ConnectionNotice`, which says whose fault it is, that the character is still
earning while the socket is down, and what the player can do - and clears itself
when the phase returns to live. The reconnect loop it reports on is untouched;
this is presentation only.

## Known, diagnosed, not fixed

**The Chest row does not fit a 320px screen.** `npm run check:mobile` fails one
of its 78 checks: `button.tiny-btn` overflows by 11px on Chest at 320px. It is
not a CSS slip — measured, the row has already collapsed the item NAME and the
rarity label to **zero width** and still holds five 44px buttons (Equip, Reroll,
Lock, Sell, Bin) that cannot shrink, because the 44px touch floor is deliberate.

Fixing it properly means deciding which actions deserve a phone row, and the
obvious move — wrapping — is barred: `.row`'s height is a **contract** with
`VirtualList` (`rowHeight={34}`), and a row that renders taller overlaps its
neighbour instead of pushing it down. That is a product decision about the
Chest, not a cleanup, so it is written down rather than guessed at.

360px and 414px are clean, as are all 26 screens under the other five checkers.
320px is iPhone-SE-2016 class hardware.

## Not verified

**The web build has never been run on a real phone.** That is still the single
biggest gap, and everything estimated below a device session is a guess.

It is worth saying what changed on 2026-09-11: three of the items on the device
checklist below — the back button, resume/suspend, and push — could not have
passed on any device before that date, because the plugins behind them were not
installed. The checklist was measuring a build that had no native half.

What is now answered mechanically, without a device:

- Touch target size and bottom-edge reachability - `check:touch`, 0 failures,
  now measuring the whole page rather than the first viewport.
- Main-thread cost at a quarter speed - `check:perf`. The 10 Hz stream produced
  ZERO long tasks on Combat, Chest and Village; scrolling the chest was the only
  thing with any cost (19 tasks, longest 129ms).
- That the Android project still COMPILES - CI assembles a debug APK on every
  push and keeps it as an artefact.
- Content clipped at phone widths - `check:clipping`.
- Controls buried under other controls - `check:overlap`.
- Horizontal overflow at 320/360/414 - `check:mobile` (one known failure, above).
- Nothing under the status bar or the gesture bar, in either orientation -
  `check:safearea`, 0 findings across 52 screen/orientation pairs.
- That every plugin the client reads off `Capacitor.Plugins` is installed and
  linked into both native projects - `tests/nativeProjects.test.ts`.
- That the privacy and deletion pages name a real, matching contact address -
  `tests/storeCompliance.test.ts`.
- What one back press does in every layer combination -
  `tests/backButton.test.ts`, against the pure resolver.
- That a push message is displayable and tappable, and names a real screen -
  `PushMessageShapeTests`. That a device token of a realistic length is accepted
  at all - `PushTokenBoundsTests`.
- That a refresh token is single-use, rotates, expires, and that a replay or a
  password reset ends every session - `RefreshTokenTests`. That the client
  discards a refused token and keeps an unreachable server's -
  `tests/sessionRefresh.test.ts`.

What only a device can answer, and what the first session should therefore be
run against as a checklist:

- Suspend and resume after 30 seconds, 5 minutes and 1 hour. Does the game come
  back, and how fast? This is the one that settles the zombie-socket question:
  `resumeFromBackground`'s stale check is defensive and unproven, because a
  Chromium tab cannot reproduce an OS-frozen WebView.
- Lock the screen mid-combat. Does offline catch-up credit correctly on return?
- Airplane mode on, then off. What does the player see in between?
  `ConnectionNotice` is written for this and has only ever been seen in a
  browser with the socket killed by hand.
- **A real notification.** Everything up to the send is tested, and the send
  itself has never run - it needs a Firebase project. Does one arrive on a
  locked phone, does tapping it open the right screen, and does declining
  permission leave the game playable and silent?
- **The overnight test, which is the point of the refresh token.** Sign in,
  leave the phone for 26 hours - past the JWT's life, well inside the refresh
  token's - and open the app. It should reach the game without a password, and
  without the login form flashing on the way.
- Rotation, and the soft keyboard over a text input (chat, market price, guild
  donation) - does the input stay visible?
- The hardware back button on every screen. The ordering is tested; what a
  device adds is the gesture-navigation variant, and whether the exit
  confirmation is reachable at all on a phone that uses an edge swipe.
- An hour of play: battery, heat, and whether the 10 Hz packet stream is
  survivable on a mid-range phone.

Record the answers here, with the device and OS version named. Treat the first
device run as a testing session, not a formality.
