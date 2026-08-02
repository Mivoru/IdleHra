# Web Client Port Plan

Status: **Decision gate TAKEN 2026-08-02 - the web client is the direction.**
Phases 0-5 and 7 built. **Phase 8 (protocol-gap audit) complete, 2026-08-02.**
Phase 6 is now packaging only. **Unity is abandoned, not merely frozen.**

## Phase 8 - the protocol-gap audit (2026-08-02, DONE)

A field-level audit against the server, not against the phase list. It found
the web client could send **35 of 60 opcodes** and call **30 of 44 endpoints**,
with 44 of 156 state fields reaching no screen. Whole systems were unreachable:
mailbox, world boss, consumables, chrono bank, guild depot and war, tool
upgrades, mentor slots, market limit orders, account erasure.

All of it is now built: **21 screens**, every opcode a player can legitimately
send, every endpoint that has a UI answer. `scripts/smoke-screens.mjs` opens
each screen in a real browser and fails on any console error or blank page.

### Wire traps found by reading validators rather than guessing

- **`LogicEpochCounter` carries two different quantities.** Every command
  echoes the save-generation counter, but `ActivateChronoBoost` and
  `ConsumeTimeWarpCore` are exempt from `ValidateEpochSynchronization` and are
  measured against the WALL CLOCK within five seconds. `GameConnection.send`
  stamps the counter on everything, so those two override it.
- **Guild logistics and storefront force-disconnect on ANY query string.** A
  reflexive `?t=${Date.now()}` cache-buster drops the player out of the game.
  Verified live: 200 without, 403 with.
- **Guild application approve/reject take `applicationId` in camelCase** while
  neighbouring endpoints use PascalCase. Verified live: 200 vs 400.
- **The world boss silently rolls back an attack when the larder is empty.**
  Accepted, no damage, nothing reported.
- **`Success: false` arrives with HTTP 200** on guild application actions.
- The GDPR interlock hash is a **wrapping uint32 multiply**; the server now
  publishes known-good vectors through `--dump-protocol` and the TypeScript
  port is tested against them, including epochs that overflow.

### Server-side defects found, NOT fixed (web was the scope)

- **`GlobalEventType.MasterArtisan` (event 3) has no effect anywhere.** The
  weekly rotation schedules it like any other, so for a quarter of every cycle
  the game announces an event that does nothing. Verified by searching every
  comparison against the id.
- **`/api/v1/codex/regions` reports ten regions; the game has five.** It groups
  by `GetMonsterRegionTier`, and RegionTier is not the canonical region - the
  five real ones are monsters 91-115. Phantom regions 6-10 show 0/1000 kills
  that can never be earned. Filtered client-side for now.
- **`LootLuckBonusPct` is written as `isCompleted ? 1 : 0`**, so it cannot
  answer "what is finishing this region worth".
- **`Test_BreedingPair_GrantedRacePairCanBreedAndSameSexIsRefused` is flaky** -
  order- or parallelism-dependent. Passes in isolation and on a re-run of the
  full suite; failed once mid-session.

### Deliberately not shipped

- **`SubmitShardAttack`.** The server refuses an attack aimed at any match
  other than the one you are committed to, and refuses it by disconnecting -
  but `ActiveCrossShardMatchId` lives only in the server's tick state and is on
  no packet or endpoint the client can read. The screen says so.
- **Real-money purchase.** Needs a platform store SDK. Storefront prices are
  shown as information with the buy path disabled.

Target: a browser-first client (Svelte + TypeScript), packaged for Android and
iOS with Capacitor later. The existing Unity client stays untouched and
working throughout.

---

## 1. Verdict first

This is viable and the server needs almost no changes. But the honest number
is that **the Unity client is 104 scripts, 49 panels and windows, roughly
18,000 lines of UI code**, against 32 REST endpoints and 64 command opcodes.
Reaching parity is months of work, not a weekend.

So the plan below is deliberately NOT a port-everything plan. It is a vertical
slice first, a decision gate, and only then a staged build-out. The failure
mode to avoid is a second client that reaches 60 percent, stops, and becomes
maintenance drag on every future feature.

**The single most important rule in this document:** every feature must exist
in exactly one place per layer. This codebase's dominant bug class, evidenced
repeatedly during the 2026-08-01 audits, is two sources of truth drifting
apart - diamonds in two stores, gold credited to a consumer that might not
exist, affix ordering as an unwritten wire contract. A second client is the
largest possible instance of that pattern. It is only safe if the shared
contract is generated, never hand-maintained.

---

## 2. What exists today (measured, not estimated)

| Surface | Count |
|---|---|
| UI scripts | 104 |
| Panels and windows | 49 |
| UI code | ~18,000 lines |
| REST endpoints | 32 |
| Command opcodes | 64 |
| `StateUpdatePacket` fields | 151 |
| Client caches and engines | 38 |

### 2.1 Screens, grouped by subsystem

- **Auth and shell**: LoginWindow, AccountPanel, SettingsPanel, LanguagePicker, HamburgerMenu, TabGroup, SectionHeaderRow, SaveTrustIndicator
- **Combat**: CombatArena, CombatLocationPanel, CombatMonsterRow, ActionBar, ActivityStatusPanel, ActivityHaltBanner, AutoEatThresholdPanel, LarderPanel, FloatingDamageText, AttackProjectile, CombatVfxPool, SkillTreeWindow
- **Character**: CharacterStatsPanel, EquipmentSlotsPanel, RosterPanel, GatheringMasteryPanel, RaceMasteryPanel
- **Items and economy**: InventoryPanel/EntryRow, BankVaultWindow/EntryRow, BankDepositCandidateRow, EquipmentRerollPanel, ForgeCraftingPanel, ForgeFusionPanel, ForgeItemViewer, ForgeRecipeRow, ForgeEquipmentRow, ForgeFusionCandidateRow, CraftingTreePanel, CraftingRecipeRow
- **Market**: MarketBrowserWindow, MarketBuyOrderPanel, MarketSellPanel, MarketListingRow, MarketSellCandidateRow, MarketDataBinder
- **Social**: ChatWindow, ChatMessageRow, ChatMinimizePanel, FriendsWindow, FriendEntryRow, GuildCreatePanel, GuildDirectoryPanel/Row, GuildRosterPanel/Row, GuildApplicationsPanel/Row, GuildLogisticsPanel, GuildWarPanel, GuildRaidPanel, MentorshipContractPanel
- **Village**: VillageOverviewWindow/Panel, VillageBuildingRow, BreedingLabWindow, BreedingRosterRow, GeneVectorRenderer
- **Progression and meta**: AchievementsPanel/Row, StatisticsPanel, LeaderboardWindow/EntryRow, SeasonPassWindow/MilestoneRow, LoginBonusPanel, CodexRegionsWindow, CodexListBinder/Row, CodexRegionRow, Codex3DViewer, CodexBonusBinder, MonsterCodexEntryView
- **Monetisation**: StoreWindow/EntryRow, LegacyShopPanel, ChronoBankPanel
- **Mail and misc**: MailboxWindow/EntryRow, OfflineSummaryWindow, WorldBossDataBinder, EventCountdownBinder, CommandResultToast, RaceUnlockToast
- **Tutorial**: TutorialController, TutorialHighlight, TutorialInteractionGate
- **Infrastructure**: UIComponentPool, RarityPalette, RarityGlow, ButtonClickSfx, LocalizationMatrix, FastStringCache

### 2.2 Client engines and caches

38 total. They fall into three groups, and each maps to a different web
answer:

1. **REST caches** (24 of them: LeaderboardCache, MarketBrowserCache,
   MailboxCache, BankVaultCache, GuildRosterCache, MonsterLootCache, ...).
   These exist only because Unity has no data-fetching library.
2. **Live state** (VisualSyncProxy, ChatRelay, CombatSessionTracker).
   Genuinely stateful, must be reimplemented.
3. **Platform plumbing** (AssetManager, PartialContentDownloader,
   AssetLifecycleCoordinator, ThermalOptimizationBroker, SfxPoolEngine,
   GameAudioDirector, AmbientAudioEngine, MotionUiEasingEngine,
   AssetRegistry, GameInitializer). Mostly deleted, not ported - the browser
   already provides these.

---

## 3. Architecture decisions

### 3.1 Stack

| Concern | Choice | Why |
|---|---|---|
| Framework | **Svelte 5 + TypeScript** | Compiles away; no virtual DOM overhead on a screen that updates 10x/sec. Less boilerplate than React for a solo dev. React is an acceptable substitute if familiarity wins. |
| Build | **Vite** | Instant HMR, which is the entire point of this exercise. |
| Routing | **Svelte routing or a simple screen store** | 49 screens are modal panels, not URLs. A `currentScreen` store is closer to the existing design than a router. |
| Server state | **TanStack Query** | Replaces 24 hand-written caches outright: caching, deduplication, invalidation, retry, stale time. |
| Live state | **Svelte stores** | One store per packet domain, fed by the WebSocket. |
| Styling | **Tailwind + CSS custom properties** | Rarity colours become CSS variables; glow becomes a keyframe animation rather than a TMP shader. |
| Lists | **svelte-virtual-list or TanStack Virtual** | Replaces `UIComponentPool` entirely. |
| Audio | **Howler.js** | Replaces GameAudioDirector, SfxPoolEngine and AmbientAudioEngine. |
| Packaging | **Capacitor** | Last step, not first. |
| Testing | **Vitest + Playwright** | Playwright matters: it can drive the real UI, which Unity never allowed without the MCP harness. |

### 3.2 The protocol decision - this is the critical one

The wire is fixed-layout C# structs. **There are six, not two** - an earlier
draft of this document covered only the first two, which would have produced a
JSON mode that silently omitted chat and loot:

| Packet | Bytes | Direction | Role |
|---|---|---|---|
| `AuthHandshakePacket` | 530 | client to server | Session establishment |
| `ClientCommandPacket` | 359 | client to server | All 64 opcodes |
| `StateUpdatePacket` | 695 | server to client | 151 fields, ~10/sec |
| `RequestChatMessagePacket` | 139 | client to server | Outgoing chat |
| `ResponseChatMessagePacket` | 147 | server to client | Incoming chat and announcements |
| `ResponseLootDropPacket` | 22 | server to client | Individual loot events |

The client distinguishes them **by exact byte length** in its receive loop.
That is workable in C# where `Marshal.SizeOf` proves both sides agree, but it
is a trap for any other language: two packets of equal size would be
indistinguishable, and `NetworkPacketLayoutGuard` exists specifically to
assert no two sizes collide.

Three options for a TypeScript client:

1. **Hand-write a DataView parser.** Rejected. Six structs, 151 fields in the
   largest, maintained in parallel with C#. This is exactly the drift that
   produced this project's worst bugs.
2. **Generate the TypeScript parser from the C# structs.** Workable, keeps the
   binary format, needs a generator plus a CI step proving they still match.
3. **Add a JSON WebSocket mode server-side.** Recommended.

**Recommendation: option 3**, covering all six packet types with an explicit
`type` discriminator field rather than relying on length. The binary format
exists to save bandwidth, but the real load is one player receiving roughly 10
packets per second - 695 bytes versus perhaps 2 KB of JSON is irrelevant over
a browser WebSocket.

Crucially it is **additive**: the Unity path keeps binary untouched, and the
server gains a per-connection mode chosen at handshake. The broadcast
dirty-checking in `SimulationEngine.ShouldDispatchStateUpdate` applies
unchanged, since it decides *whether* to send, not *how* to encode.

### 3.2b The rest of the network layer

`Network/` holds 17 scripts. Beyond the six packets:

| Script | Lines | Disposition |
|---|---|---|
| `WebSocketClient` | 1398 | **Reimplement.** The largest single piece of client infrastructure: connect, reconnect, auth, packet dispatch, all 64 send methods, challenge responses. |
| `ClientContentRegistry` | 502 | **Port.** Loads monsters/items/skills/gathering JSON from StreamingAssets. Web serves the same files over HTTP. |
| `ClientAffixRegistry` | 242 | **Port.** Display mirror of the server affix registry, including the ordered id list that is a wire contract. |
| `UnsafePacketParser` | - | **Delete.** Superseded by JSON parsing. |
| `NetworkPacketLayoutGuard` | - | **Delete client-side**, keep server-side. |
| `ObfuscatedValues` | - | **Delete.** See 3.3. |
| `FlightRecorder` | - | **Optional.** Diagnostic ring buffer; browsers have devtools. Port only if the telemetry burst command is kept. |
| `ClientInputProxy` | - | **Delete.** Unity Input System; the DOM handles this. |
| `ProductIdHasher` | - | **Port** if store purchases ship; needed for receipt validation. |
| `PushDeviceTokenProvider` | - | **Replace.** See 3.2c. |

### 3.2c Push notifications - a platform feature the first draft missed entirely

The server has `PushNotificationTriggerEngine` and the client has
`PushDeviceTokenProvider`, wired through the `RegisterPushToken` command
(opcode 33). This matters more for an idle game than for most genres: the
whole retention loop is "come back when something finished", and the server
already schedules triggers for the world boss window opening and the daily
reset.

Web is not a straight swap:

- **Browser**: Web Push via a service worker and VAPID keys. Requires HTTPS,
  a permission prompt, and a push subscription rather than an FCM token. iOS
  Safari supports it only for installed PWAs, and only since 16.4.
- **Capacitor**: `@capacitor/push-notifications` gives native FCM and APNs, so
  the token shape matches what the server already expects.

**Plan**: skip push entirely until Phase 6. The `RegisterPushToken` opcode
carries an opaque 64-byte token, so a Web Push subscription can be encoded
into it without a protocol change - but the server's sender would need a Web
Push implementation alongside FCM. Treat that as its own scoped task, not a
line item.

### 3.3 What NOT to port

Being explicit here saves weeks:

- **`ObfuscatedValue` / memory anti-cheat.** In a browser the source is
  visible and memory is inspectable by design. Client-side obfuscation there
  is security theatre. The server is already authoritative; keep it that way
  and delete this layer rather than reimplementing it.
- **Zero-allocation discipline.** `SetCharArray`, char buffers, `FastStringCache`,
  `UIComponentPool`. These exist to avoid Unity's GC spikes. JS engines and
  virtual lists make them unnecessary, and porting them would be cargo cult.
- **Addressables, `PartialContentDownloader`, `AssetLifecycleCoordinator`.**
  Replaced by HTTP, a CDN and the browser cache. This also removes the class
  of bug where chat rendered nothing because Addressables silently failed.
- **`ThermalOptimizationBroker`, `MotionUiEasingEngine`.** Browser and CSS
  handle these.
- **The two render-texture viewers.** `UiCodex3DViewer` AND `UiForgeItemViewer`
  - the second was missed by the first two drafts of this document, which is
  worth recording because it sits in the Forge flow scheduled for Phase 3, not
  in the optional codex.

  Both render a loaded prefab into an isolated RenderTexture on a dedicated
  layer with their own Camera. **But no 3D model assets exist in the project**
  - no `.fbx`, `.obj` or `.blend` anywhere. They are rendering sprite-based
  prefabs through a 3D pipeline to get an isolated preview viewport, which in
  the browser is simply an `<img>` in a styled container.

  So neither needs three.js. Both collapse to a plain image preview, and the
  entire `UI_3D_Preview` layer, RenderTexture and Camera machinery disappears.
  This is a simplification the first draft got wrong in the pessimistic
  direction.
- **Scene builder (`MainSceneBuilder`, ~8,000 lines).** Has no equivalent and
  needs none - components ARE the scene description. This is the single
  largest deletion and a good measure of why the web is a better fit here.

---

## 4. Phased plan

Estimates are in focused working days for one developer already familiar with
the domain. They assume the server is not changing underneath, and they
exclude art, copy and balancing.

### Phase 0 - Enabler (server only, ~3-5 days) - **DONE**

No client work. Nothing visual.

1. ~~CORS allow-list.~~ Done, commit `8a793bb`. `FOLKIDLE_WEB_ORIGINS`,
   comma separated, failing closed when unset.
2. ~~Add a `mode` field to the auth handshake: `binary` (default, Unity) or
   `json`. Per connection, never global.~~ Done. The real switch is the
   **frame type of the handshake** - a Binary first frame is the byte
   protocol, a Text first frame is JSON - because a frame's type is
   unforgeable and already carried by every WebSocket implementation, so
   there is no negotiation round-trip and no way for the two sides to
   disagree. The JSON handshake also carries an explicit `"mode":"json"`,
   which is validated rather than inferred, so the intent is legible in a
   packet capture.
3. ~~JSON serialisation for all six packet types, each carrying an explicit
   `type` discriminator so the client never dispatches on byte length.~~
   Done - `Network/PacketJsonCodec.cs`.
4. ~~A contract test asserting the JSON shape carries every field the binary
   struct declares, for all six.~~ Done -
   `FolkIdle.Server.Tests/WebClientJsonProtocolTests.cs`, 31 tests.
5. ~~Serve `StreamingAssets/GameData/*.json` over HTTP.~~ Done -
   `GET /gamedata` (manifest) and `GET /gamedata/<name>.json`.

Exit criterion met: a Node client using the browser `WebSocket` API logs in,
receives readable `StateUpdate`, changes activity, chats, and receives loot -
all as JSON - while a binary session on the same server still receives
695-byte frames.

#### How the drift defence actually works

Worth stating precisely, because "we wrote a contract test" understates it.

`PacketJsonCodec` **never writes the field list down**. It reflects over the
struct, then asserts the derived plan covers every byte of that struct
contiguously from offset 0 to `Unsafe.SizeOf<T>()`. A field added to
`StateUpdatePacket` therefore appears in the JSON automatically, and a field
the codec somehow failed to see is not a silently-missing property - it is a
hole in the byte coverage, which throws at first use. Drift is not caught by
a test someone has to remember to update; it is close to unrepresentable.

The contract test is the second layer, and it deliberately derives its
expected field list **by reflecting over the packet structs itself**, never
by asking the codec what it thinks the fields are - a test that asked the
codec to agree with itself would pass forever while the JSON quietly dropped
whatever the codec missed. It was verified by mutation: deleting one field
(`Gold`) from the codec's writer fails two tests, naming the field and its
byte offset.

#### Two undocumented wire obligations, found by building a client

Neither is in any document; both were found by writing a non-Unity client and
watching it die. **Both failure modes look identical**: WebSocket close code
1008 "Violent termination", with no server log line of any kind. That shared,
silent, undiagnosable failure is the reason this section exists.

Both are measured, not read off the source.

**1. Every `ClientCommand` must echo `LogicEpochCounter` from the most recent
`StateUpdate`.** Otherwise `ValidateEpochSynchronization`'s epoch interception
gate calls `TerminateSessionForSecurity`. A fresh account survives briefly by
luck - its counter is still 0, which is what an unaware client sends - so this
bug hides until the account has played a little. A level-40 account
(`LogicEpochCounter = 23`) is killed on its **first** command.

**2. The server issues an anti-cheat challenge on the broadcast path
(`ActiveChallengeSeed` on `StateUpdate`) and the client must answer it with
opcode 31 (`AntiCheatChallengeResponse`).** The window is 15 s
(`ChallengeResponseWindowMs`) and 4 consecutive misses
(`ConsecutiveChallengeMissLimit`) quarantines the account - measured at
**about 60 seconds** for a client that never answers. A 25 s session survives
and looks perfectly healthy, which is precisely how this gets missed. The
account is left with `IsQuarantined = true` persisted, so it stays broken
after reconnecting until the flag is cleared.

The answer is computable client-side; the client needs
`AntiCheatTelemetryEngine.ComputeChallengeHash`, over uint32 arithmetic:

```text
xorshift32(v): v ^= v<<13; v ^= v>>17; v ^= v<<5; return v == 0 ? 0x6D2B79F5 : v

hash = seed
hash ^= (uint32)playerId
hash  = xorshift32(hash)
hash ^= (uint32)(playerId >> 32)
hash  = xorshift32(hash + (uint32)logicEpochCounter)
hash ^= 0xC2B2AE35
hash  = xorshift32(hash)
```

Reply with `ChallengeId = ActiveChallengeSeed` and
`ChallengeVerificationHash = hash`, using the `LogicEpochCounter` **from the
same `StateUpdate` that carried the seed** - the server judges against the
epoch the challenge was issued under, not the live one. The response packet
must additionally leave `TargetId`, `SecondaryId`, `TertiaryId`, `LimitPrice`,
`IsBuy`, `QualityTier`, both Guids and the eight `*Id`/`*Index` fields at
zero, or it is rejected as a malformed answer.

**Correction to an earlier draft of this section:** it claimed the
`--seed-dev` fixture account is force-disconnected on login by
`ValidateLoginTime`. That was wrong. Its `LastLogoutTimestamp` is in the past,
not the future, so that validator passes. The fixture disconnects for
obligation 1 alone, immediately, because it is a played-in account with a
non-zero epoch counter. With the epoch echoed correctly it runs indefinitely -
verified at 2934 XP and a continuous stream of loot drops. **There is exactly
one root cause here, not two, and the fixture is not broken.**

#### Notes on the JSON encoding

- Property names are the C# field names verbatim (PascalCase). No camelCase
  translation layer, because a translation layer is one more place the two
  sides can disagree. `type` is lowercase and so cannot collide.
- `fixed byte X[N]` buffers are **base64 of the full fixed capacity**. Base64
  rather than the decoded text, even though all four such buffers happen to
  carry text today: it round-trips byte for byte with no assumption about
  content, and it keeps the buffer and its paired length field (e.g.
  `JwtToken`/`JwtTokenLength`) independent rather than teaching the codec
  which pairs with which - that pairing knowledge would be hand-maintained.
- Absent properties leave their field at default, so a client can send three
  fields of `ClientCommand`'s fifty. A property that is *present but
  malformed* is a hard rejection.
- Non-finite floats travel as `"NaN"`/`"Infinity"`/`"-Infinity"`, so a
  divide-by-zero in combat math cannot throw on the broadcast path.
- `GameBalanceConfig.json` is **not** served: it is server balance data and
  is not part of the client's StreamingAssets mirror. Same reasoning as
  `/api/v1/monsters/loot` being an endpoint rather than a shipped file.

### Phase 1 - Vertical slice and DECISION GATE - **BUILT, gate not yet taken**

Built in `client_web/` (Vite + Svelte 5 + TypeScript). Login, live WebSocket
session, all 25 canonical monsters across 5 regions, combat with interpolated
health bars, drop preview, loot feed with rarity colours, halt-reason banner,
reconnect UI. 62 KB of JavaScript, 23 KB gzipped.

**The protocol types are generated, not written.** `--dump-protocol` emits the
server's own reflected field plan; `client_web/scripts/generate-protocol.mjs`
turns it into `protocol.generated.ts` (6 packets, 232 fields, 61 opcodes).
`--check` mode fails CI if a struct changed without regenerating. This is the
port plan's central rule made mechanical rather than aspirational.

Three things the build found that no amount of reading would have:

1. **The "~10/sec" figure in this document is wrong**, and it broke the
   interpolation. 10 Hz is the tick rate; `SimulationEngine` dirty-checks
   before dispatching. Measured in a real browser with a `MutationObserver` on
   the health bar, gaps between monster-HP changes were 2183, 1100, 2183,
   1084, 2182, 1100 ms - mean **1637 ms**. A fixed 100 ms render delay pinned
   the lerp factor at 1 so nothing interpolated at all, and a fixed 1000 ms
   "reconnect" threshold then classified ordinary combat as a disconnect and
   discarded the previous snapshot anyway. The unit tests passed and the bar
   stepped. The delay is now estimated from observed arrivals; measured after
   the fix, 122 distinct bar widths over 4 seconds against 3 before.

   **This is the concrete argument for the Playwright line in 3.1.** The bug
   was invisible to every test that did not render.

2. **A quarantined account silently suppresses loot.** Any client run that
   never answers an anti-cheat challenge sets `IsQuarantined` *persistently*,
   and the account then produces no drops with no error anywhere. It cost real
   time to diagnose, so the integration test now asserts the flag and says so.

3. Drop-preview rows for equipment carry `ItemId = 0` and are identified by
   `BaseItemId` alone, which rendered four of five rows as "Item #0".

Floating damage text and the offline "welcome back" flow are now in as well.
Two more things the build surfaced:

4. **The wire carries no damage event.** There is no "you hit for N" packet
   anywhere in this protocol - only `CurrentMonsterHp` on a snapshot - so
   every number shown is inferred from a difference between two snapshots,
   and the inference has to reject three lies: a monster change (6 -> 3500 is
   a new monster, not a heal), a respawn at full health, and a reconnect gap
   that collapses thirty hits into one difference. It is fed from the
   AUTHORITATIVE snapshot, never the interpolated one, which would turn one
   hit into a blizzard of fictional tiny ones.

5. **`OfflineSummaryTick` does not mean "you earned something".**
   `OfflineSimulationEngine` increments it for any elapsed window at all, so
   an idle character produces a "welcome back" with `+0 +0 +0` - measured at
   39 minutes away on the dev fixture. Presenting that as a rewards panel is
   a dialog whose only purpose is to be dismissed, but suppressing it is also
   wrong: in an idle game, earning nothing over 39 minutes is the single most
   useful thing to tell the player, because the cause is a character they
   never deployed. So a zero-earning catch-up still surfaces, phrased as the
   problem it is, once the window is long enough not to be a page refresh.

#### The gate was taken on 2026-08-02: continue on web

The owner's call, made after Phases 1 and 2 were playable. Consequences, which
are the whole reason the gate exists:

- **The Unity client enters feature freeze now, not at the end of Phase 3.**
  The plan put the freeze at the start of Phase 3 precisely because dual
  maintenance is the dominant cost from here on. Only bug fixes land in
  `client/` from this point.
- The web client is not yet ahead of Unity in coverage - nine screens of
  forty-nine - so **Unity remains the shipping client** until a phase boundary
  where the web build genuinely surpasses it. Freezing features is not the
  same as switching over.
- Every server change stays additive. Nothing in the port may break the Unity
  client while it is still what players run.

#### Original plan for this phase

Target: log in, pick a monster, fight it, watch HP, loot and progress.
Four screens of 49.

1. Vite + Svelte 5 + TypeScript under `web/`.
2. Auth: register, login, token persistence. Endpoints already exist.
3. WebSocket client with reconnect and a `playerState` store - the reduced
   descendant of `WebSocketClient` (1398 lines) and `VisualSyncProxy`
   (1100 lines). Expect roughly 400 lines of TypeScript for the subset the
   slice needs.
4. Snapshot interpolation. `VisualSyncProxy` lerps between two server
   snapshots so bars move smoothly at 10 Hz. **Do not skip this** - without
   it the UI visibly steps and feels worse than Unity, which would poison the
   decision this phase exists to inform.
5. Combat screen: monster list, selection, HP bars, floating damage, loot log
   with rarity in brackets, and the drop preview from
   `/api/v1/monsters/loot`.
6. Rarity palette as CSS custom properties; glow as a keyframe animation.

**STOP. Decide.** The question is not "does it work" - it will. It is
"is iterating on this materially faster than Unity". If the honest answer is
no, stop here and keep Unity. Roughly two weeks spent, nothing lost, and the
Phase 0 JSON mode remains useful for tooling and tests regardless.

### Phase 2 - Core loop (~10-15 days)

Inventory, equipment slots, character stats, larder and auto-eat thresholds,
activity status and halt reasons, gathering, offline summary, roster.

Dependency: none beyond Phase 1. This is the point at which the web client
becomes genuinely playable rather than a demo.

### Phase 3 - Economy (~15-20 days) - **BUILT**

Market browse/buy/sell, bank vault deposit and withdraw, crafting tree
(103 recipes), forge crafting and fusion, affix reroll including auto-reroll
and its stop conditions.

**Correction: there is no market CANCEL.** An earlier draft listed it, but no
opcode, engine method or Unity screen for it exists anywhere - the capability
has never been in this game. Listing it as port work invented a gap.

Two things this phase found that no amount of reading the endpoint list would
have:

- **The server DISCONNECTS on an invalid economy command** rather than
  answering with a rejection code - `TerminateSessionForSecurity`, which the
  player sees as close 1008 with no explanation. A mis-typed price ends the
  session. Hence `client_web/src/lib/net/commands.ts`: every economy command
  checks the server's own precondition and refuses to send. The sharpest case
  is `ValidateFusionCommand`, which disconnects when *any two of the three item
  ids match* - exactly what three dropdowns defaulting to the same item produce.
- **There are TWO recipe tables and two commands, named the opposite way round
  from what you would guess.** `ContentRegistry.Recipes` (the 103-recipe tree,
  `/api/v1/crafting/recipes`) is driven by `InitializeCrafting` with the RESULT
  item id on `TargetId`. `CraftingReceptuary` (the Forge's own,
  `/api/v1/forge/inventory`) is driven by `CraftItem` with a RECIPE id on
  `TargetRecipeId`. So `CraftItem` does not craft from the crafting tree.
  Wiring the tree to it disconnects on ids the Forge does not know and
  *silently crafts the wrong recipe* where the two id spaces overlap.

`UiEquipmentRerollPanel` is 607 lines and among the most intricate screens in
the project - three operations, two currencies, escalating costs, stop
conditions. Budget for it accordingly.

**Feature freeze on the Unity client starts here.** Past this point, dual
maintenance is the main cost driver, and only bug fixes should land in Unity.

### Phase 4 - Social (~12-18 days) - **BUILT**

Chat across all three channels, friends with block/unblock, guild
create/join/directory/roster/applications, guild war scoreboard and supply
contribution, raids, guild treasury, logistics depot, and mentorship.

Congratulate is in, and finding where it belonged exposed a real gap:
**announcements are a FOURTH channel type (3), not global messages with special
text.** ChatEngine gives them their own byte precisely so a client can tell
them apart without parsing - and a client that only knows 0/1/2, as this one
originally did, silently dropped every announcement on the floor. The button
itself is not a dedicated command: it sends the literal string "gz!" on the
global channel, inheriting the ordinary rate limit and profanity path.

Two things worth carrying forward:

- **This wire identifies players NUMERICALLY.** `ResponseChatMessagePacket` has
  no room for a name and neither does the guild roster response, so every
  social surface would read "Player #1042" without resolving names separately.
  `/api/v1/players/names` batches deliberately: a chat log issues ONE request
  for every id on screen, not one per row.
- Guild create and join are **HTTP POSTs, not WebSocket commands**, because a
  guild name is a variable-length string and `ClientCommandPacket` has no field
  for one - the same reason email/password auth uses HTTP. The body field is
  `guildName`; getting it wrong is a bare 400 with no indication of which field
  was at fault.

`UiChatWindow` is 627 lines with pooled rows, three channels and history. In
the web version most of that shrinks to a virtual list plus a store.

### Phase 5 - Meta and progression (~15-20 days) - **MOSTLY BUILT**

Built: achievements with claiming, statistics, player AND guild leaderboards,
monster codex with region completion, daily login bonus, race mastery, skill
tree, village buildings and villagers.

Season pass and the breeding lab are in too, so the phase is complete apart
from the codex 3D viewer, which the plan already excludes.

One thing the season pass cannot do, and it is a wire limitation rather than a
shortcut: **`ClaimedMilestonesBitmask` was removed from `StateUpdatePacket` and
nothing replaced it**, so which milestones a player has already claimed is not
readable by any client. Milestones are therefore claimed by index, and a repeat
is the server's to refuse - a checked list would have to invent the checkmarks.

Two findings, both from the same root cause - assuming a shape instead of
reading it:

- **The guild leaderboard does NOT reuse the player leaderboard's shape**,
  despite this document asserting it did. It returns
  `{ Rank, GuildId, Name, GuildTier, GuildMMR }` - no `DisplayName`, no `Xp` -
  and reading it as a player row crashes on undefined. Nobody had ever seen the
  response, because it is one of the nine endpoints no Unity screen calls,
  which is exactly why the wrong assumption survived in this plan.
- **Only ONE of the four achievements could actually be claimed.** The claim
  processor handled the monster-kill id under a comment reading "Other
  achievements mapped here in future...", while the snapshot endpoint reported
  all four with real progress and rewards. Every threshold and payout was
  already authored in `AchievementMilestones`, and `GetDiamondsForTiersCrossed`
  existed to total them - so the mapping was completed generically, and a fifth
  achievement now needs no change there at all.

### Phase 6 - Monetisation and packaging (~10-15 days) - **PARTIALLY BUILT**

Built: the diamond catalogue, the legacy shop's three prestige perks read off
`LegacyPerksBitmask`, and the chrono bank's speed toggle and core consumption.

**Deliberately NOT built: actual purchasing.** `/api/v1/store/catalog` carries
a product id and a diamond amount and *no price* - real-money pricing lives in
the storefront, behind a platform store SDK, with `/api/v1/billing/verify-receipt`
closing the loop. A "Buy" button that cannot take money would be worse than
none, so the screen says so plainly instead.

Still outstanding: Capacitor packaging for Android and then iOS, receipt
validation, and push notifications (see 3.2c). Receipt verification is one of
the nine endpoints the Unity client never called, and the plan already records
it as a real revenue risk rather than cosmetics.

### Phase 7 - Parity close-out (~10-15 days) - **BUILT**

Tutorial, localisation, audio and a Settings screen carrying the accessibility
notes. Telemetry is not wired - `ReportUiContextSwitch` and
`ReportTelemetryBurst` exist, but nothing consumes their output that this port
needs, so adding calls would be motion without a reader.

- **Audio is served, not copied.** The ten WAVs are LINKED out of
  `client/Assets/Resources/Audio` by the server csproj - the same technique
  that shares `TutorialStateMachine.cs` with the test project - and served from
  `/audio`. Both clients play the same bytes and there is no second copy to
  drift. Plain Web Audio rather than Howler: ten one-shot clips need decode,
  gain and play, and a dependency whose value is format fallbacks we do not use
  is not worth 30 kB.
- **The tutorial is a port, and that makes it a second source of truth.**
  `TutorialStateMachine.cs` is pure C# precisely so the server's xUnit suite
  can compile it verbatim; the TypeScript version mirrors its rules and is
  covered by tests that mirror the server's own, including the ones that matter
  most - out-of-order signals are DROPPED not queued, and Settings is never
  blocked so a tutorial cannot trap a player away from sign-out.
- **The language index and the wire id are different numbers.**
  `LocalizationMatrix` indexes 0-3 (En, Cs, De, Pl) while
  `SwitchLanguage`'s `TargetLanguageId` is 1-4, and 0 is rejected outright.
- **Localisation covers 28 keys**, so most of this client's text is not
  translated at all. The Settings screen says so rather than letting a language
  picker imply full coverage.

### Totals and honesty about them

Phases 1 through 7 land somewhere around **80-115 focused days** for one
developer. That is a real number, not a discouraging one - but it is months,
and it is why the decision gate exists after roughly two weeks rather than
after six months.

**Ordering rationale**: every phase is independently playable, so there is
never a big-bang switchover. Unity remains the shipping client until a phase
boundary where the web build is genuinely ahead. If work stops at any phase
boundary, what exists is coherent rather than half-wired.

## 4b. Server capability the Unity client never used

Found by diffing the endpoints the server exposes against the ones the client
calls. **Nine endpoints exist server-side with no client caller.** These are
not port work - they are work the Unity client never did, and a web client
either inherits the same gap or closes it.

| Endpoint | Status | Decision for the web client |
|---|---|---|
| `/api/v1/auth/oauth-link` | Never called | **Close it.** Google and Apple sign-in is close to mandatory on mobile storefronts, and Apple requires it if any other social login ships. Phase 1 or 6. |
| `/api/v1/billing/verify`, `/verify-receipt` | Never called | **Close it.** A store exists client-side but purchases are never verified. Real revenue risk, not cosmetics. Phase 6. |
| `/api/v1/leaderboard/guilds` | Never called | **Close it.** The guild leaderboard is implemented and fixed server-side but no screen reads it. Cheap - the player leaderboard shape already exists. Phase 5. |
| `/api/v1/guild/logistics/snapshot` | Never called, **despite `UiGuildLogisticsPanel` existing** | **Close it.** The panel is built and shows nothing real. Same "built but never wired" pattern documented as section 16 in the architecture notes. Phase 4. |
| `/api/v1/storefront/listings` | Never called | Decide: either the store uses it or it is dead. Phase 6. |
| `/api/v1/support/tickets/create` | Never called | Low priority, but a support path is expected by app stores. Phase 7. |
| `/api/v1/assets/handshake` | Never called | **Likely delete.** It exists for Addressables/CDN negotiation, which the web replaces with plain HTTP and cache headers. |
| `/api/v1/billing/refund-webhook` | Server to server | Not client work. |

Also present and not client-facing: `/admin/liveops`, `/healthz`,
`/health/liveness`, `/health/readiness`, `/metrics`. Leave alone.

## 4c. Assets and content - smaller than feared

Verified rather than assumed:

- **Sprites**: 206 transparent PNGs, already generated by
  `ops/tools/generate_sprites.py`. Serve over HTTP unchanged. No conversion.
- **Fonts**: 14 found, **all TextMesh Pro sample fonts** - Anton, Bangers,
  Oswald and similar. Nothing custom, nothing licensed for this project. The
  web client picks its own webfont freely, which also resolves the open "font
  restyle" item that was blocked on needing a TTF.
- **Shaders and materials**: 40 found, **all TextMesh Pro built-ins**. No
  custom shaders exist, so nothing to reimplement. The rarity glow used TMP's
  built-in glow, which becomes CSS `text-shadow` plus a keyframe.
- **Addressables**: a single "Default Local Group". Nothing is remotely
  bundled, so there is no CDN content pipeline to recreate - just static files.
- **Prefabs**: 26, all UI rows. Become components.
- **Localisation**: `localizations.json` holds **28 keys across four languages
  (En, Cs, De, Pl)**. Small enough to port in an afternoon, and small enough
  that most UI strings are currently hardcoded - meaning a web port is the
  natural moment to decide whether localisation is actually a goal.

The practical consequence: **the asset layer is nearly free**. What looked
like the riskiest part of a port is 206 PNGs and a JSON file.

## 4d. Things that do not exist yet and will be needed

Neither client has these; they surface as soon as a browser is the target.

| Concern | Why it appears | Phase |
|---|---|---|
| **Session resume across tab suspend** | A backgrounded tab is throttled or frozen. The server treats it as a disconnect, which is already handled - but the client must reconnect cleanly and reconcile offline progress via `OfflineSimulationEngine`. Unity on mobile has the same issue but the OS suspends the whole app, which is simpler. | 1 |
| **Server time synchronisation** | Cooldowns and event windows are epoch-based. A browser clock can be arbitrarily wrong, and unlike a mobile app there is no platform time guarantee. Compute an offset at handshake and never trust `Date.now()` directly. | 1 |
| **HTTPS and secure WebSocket** | Web Push, service workers and installed PWAs all require HTTPS. Local development can use plain HTTP, but the deployment cannot. | 6 |
| **CORS** | The server currently answers a same-origin Unity client. A browser on a different origin needs explicit headers. Small but blocking. | 0 |
| **Token storage** | Unity uses PlayerPrefs. A browser needs a deliberate choice - `localStorage` is XSS-readable; an httpOnly cookie is safer but complicates the WebSocket handshake. Decide before Phase 1 ships. | 1 |
| **Input focus and keyboard** | Chat and search fields must not swallow game hotkeys, and mobile keyboards resize the viewport. Unity handled this badly; the web should handle it deliberately. | 2 |
| **Offline/reconnect UI** | Unity shows connection state weakly. A browser tab can go offline mid-session and the player deserves to be told plainly - this was already logged as a gap when registration failed with no backend running. | 1 |

## 5. System-by-system mapping

| Unity concept | Web equivalent | Notes |
|---|---|---|
| `MainSceneBuilder` | Svelte components | Largest deletion. ~8,000 lines vanish. |
| `UIComponentPool` | Virtual list | Pooling is automatic. |
| `VisualSyncProxy` | Svelte store + `requestAnimationFrame` lerp | The interpolation between two snapshots must be kept - it is what makes bars smooth at 10 Hz. |
| 24 REST caches | TanStack Query | Roughly 2,000 lines deleted. |
| `Image.Type.Filled` | `<div style="width: X%">` | Removes the null-sprite class of bug entirely. |
| TMP `SetCharArray` | Reactive text | Zero-alloc buffers unnecessary. |
| `UiRarityPalette` | CSS custom properties | One variable per rarity. |
| `UiRarityGlow` (TMP shader) | CSS `@keyframes` + `text-shadow` | Simpler and no per-instance material leak. |
| Addressables | HTTP + CDN | `FOLKIDLE_CDN_BASE_URL` already exists. |
| `GameAudioDirector` etc. | Howler.js | The ten generated WAVs port as-is. |
| `LocalizationMatrix` | i18n library or a keyed JSON store | Existing localisation JSON is reusable. |
| `TutorialStateMachine` | Port the logic, redo the highlight | Logic is stack-independent. |
| `ObfuscatedValue` | Delete | See section 3.3. |
| `PlayerNameCache` | TanStack Query with a batch fetcher | Endpoint already batches. |
| Prefabs | Components | Removes the fileID churn in git. |
| RenderTexture previews (`UiCodex3DViewer`, `UiForgeItemViewer`) | `<img>` in a styled container | No 3D assets exist; the 3D pipeline was only providing an isolated viewport. |
| `ParticleSystem`, `AndroidJavaObject` | Deleted with `ThermalOptimizationBroker` | Their only users. |

---

## 5b. Verification performed on this document

Three passes, each finding material gaps the previous had missed. Recorded so
the confidence level is legible rather than implied.

| Pass | Method | What it found |
|---|---|---|
| 1 | File census - names, counts, endpoints, opcodes | The baseline scope numbers |
| 2 | Folder-by-folder audit | Four missing packet types, push notifications, the whole `Network/` disposition |
| 3 | Endpoint diff, asset survey, Unity-API sweep | Nine unused server endpoints, the asset layer being trivial, `UiForgeItemViewer` |

**Unity-API sweep results** (the check most likely to invalidate the plan):

- `Mesh` appears in 34 files and is **TextMeshPro in all 34** - no 3D geometry.
- `ParticleSystem` and `AndroidJavaObject` appear in exactly one file each,
  both `ThermalOptimizationBroker`, already marked for deletion.
- `Camera` appears in two files, both render-texture previews - see 3.3.
- No `Rigidbody`, no `Physics`, no `Animator`, no `LineRenderer` anywhere.
- One coroutine in the entire client.

**What has still NOT been done:** reading the 18,000 lines of UI behaviour.
The structure is verified - every folder, packet, endpoint, asset type and
Unity dependency. What is not verified is what each of the 49 screens does in
detail. That level of specification is worth writing per phase, when the phase
is about to be built, not up front for screens that may never be ported.

## 6. Risks

| Risk | Severity | Handling |
|---|---|---|
| Two clients drift | **High** | The Phase 0 contract test. Never hand-maintain the protocol on two sides. |
| Web client stalls half-built | **High** | Phase gates with explicit stop points. A half-built client that is never shipped must be deleted, not kept. |
| Feature work doubles during the transition | Medium | Freeze Unity client features once Phase 3 starts; only bug fixes. |
| Art pipeline | Low | 206 sprites are already transparent PNGs and serve over HTTP unchanged. |
| Offline and background behaviour | Medium | Idle games accumulate offline progress; the server already computes it via `OfflineSimulationEngine`. A browser tab suspending is equivalent to a disconnect, which is already handled. |
| iOS build | Low | Needs a Mac, identically to Unity. Not a regression. |
| LFS quota | Low | Serve art from CDN rather than the repo, which also resolves backlog item 32. |

---

## 7. What this does not change

The server is untouched apart from the additive JSON mode. Tick engine,
economy, affixes, anti-cheat, persistence, guild wars, world boss - all of it
is stack-independent and stays exactly as it is. That is the reason this port
is even worth considering: **the expensive, hard-won part of this project is
not in the client.**
