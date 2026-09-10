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
- **The hardware back button** (2026-09-10) and **a no-network state that says
  something** (2026-09-10). See their own sections below.
- npm scripts: `sync`, `sync:web`, `build:android`, `build:android:web`,
  `open:android`, `open:ios`.

## What you still need (and nobody can do for you)

- **Android:** a JDK and the Android SDK. `cap add android` generates the
  project; `cap build android` needs Gradle to be able to run.
- **iOS:** a Mac with Xcode. This is the same hard requirement Unity had - it
  is a platform rule, not a limitation of this stack.
- **A reachable server over HTTPS.** See below.

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

**`build:web` deliberately does not run `svelte-check`, and `npm run build`
does - which is why `npm run build` currently fails.** There is a known
baseline of four `svelte-check` errors (the hidden Guild War handlers in
`GuildOps.svelte`; see the repo's CLAUDE.md), `svelte-check` exits 1 whenever
there is any error at all, and so every script that chains it - `build`, and
therefore `sync` and `build:android` - stops before Vite ever runs. Production
deploys sidestep it by calling `npx vite build` directly. Packaging an app is
not the place to discover a months-old type-check baseline, so `build:web`
does not chain it. Type-checking is `npm run check`, and CI enforces the
baseline as a ratchet that fails only if the count grows.

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

Four Playwright checkers cover the geometry a phone exposes. None of them needs
a device; all of them need a running local stack, and all sign in as the dev
fixture, so **do not aim them at production**.

```bash
npm run check:mobile     # horizontal overflow at 320 / 360 / 414
npm run check:clipping   # content cut off, 26 screens x 3 widths
npm run check:overlap    # controls buried under other controls
npm run check:touch      # controls too small for a thumb, 44px floor at 390px
```

`check:touch` is the newest (2026-09-09) and the one written specifically for
this app. The floor is 44px from Apple's HIG and WCAG 2.5.5, measured on the
control's own box rather than on the text inside it, and it also fails a
control sitting too close to the bottom edge to be pressed. It found **221**
undersized targets on its first run - every screen's shared chrome sat at 37px.

**It is at 5, not 0**, measured 2026-09-10. The chrome fix took it from 221 to a
handful and the "now 0" claim that stood here was written before this run. What
is left is three controls on Chest and one on Wiki that are large enough but sit
in the bottom band of the viewport (where a gesture-navigation bar takes the
press), and one text input on Auto-Eat at 32px tall - the browser hit-tests an
input's border box, so padding cannot fix that one either. All five predate
PHASE B; they are tracked as D6.

## Not built yet

- **In-app purchases: one store adapter.** Everything else is built.
  `src/lib/net/billing.ts` runs the purchase, and the signed receipt goes over
  REST to `/api/v1/billing/verify-receipt`, which verifies the store signature
  and is idempotent on the transaction id. It deliberately does **not** use
  opcode 39, which grants diamonds on an unsigned transaction id and a hashed
  product id and would be a "type any string, receive diamonds" path in a
  shipped build. `purchaseUnavailableReason()` disables the Buy buttons and
  says why.

  What is missing is an implementation of the `StoreAdapter` interface already
  declared in that file (`listProducts` and `purchase`, returning the store's
  signed receipt unmodified), registered at startup - plus the products
  themselves created in Play Console and App Store Connect with ids matching
  `GameBalanceConfig.json`'s `IapProductPrices` keys. Choosing the vendor
  (RevenueCat, cordova-plugin-purchase, a first-party bridge) is a commercial
  decision about fees, not a technical one, which is why the file does not make
  it.

  **`/api/v1/billing/verify-receipt` has never been called by any client.**
  Worth knowing before shipping a paid build: test the refund and cancellation
  paths, not only the happy one.
- **A Firebase project, and the iOS half of push.** The client, the transport
  and the server are wired (see "Notifications" below), but the send path needs
  `FCM_PROJECT_ID`, `FCM_CLIENT_EMAIL` and `FCM_PRIVATE_KEY` in the server's
  environment or it returns without sending. iOS additionally needs the
  Firebase iOS SDK: FCM v1 addresses a device through an FCM registration
  token, and what Capacitor hands over on iOS is the **raw APNs token**, which
  FCM will not accept. Android works end to end once the project exists.
- **Icons, splash screens, app IDs and signing keys.** `appId` in
  `capacitor.config.json` is a placeholder (`com.folkidle.game`). Decide the
  real bundle id before the first store upload - it cannot be changed
  afterwards on either platform.

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

## Not verified

**The web build has never been run on a real phone.** That is still the single
biggest gap, and everything estimated below a device session is a guess.

What is now answered mechanically, without a device:

- Touch target size and bottom-edge reachability - `check:touch`, **5
  failures** on 3 screens, all pre-existing. See "Checking the phone build".
- Content clipped at phone widths - `check:clipping`.
- Controls buried under other controls - `check:overlap`.
- Horizontal overflow at 320/360/414 - `check:mobile`.
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
