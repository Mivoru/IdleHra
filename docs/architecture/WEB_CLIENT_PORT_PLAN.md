# Web Client Port Plan

Status: PROPOSAL, not started. Written 2026-08-02.

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
- **`Codex3DViewer`.** Decide deliberately: either drop it, or use three.js.
  Do not port it during the slice.
- **Scene builder (`MainSceneBuilder`, ~8,000 lines).** Has no equivalent and
  needs none - components ARE the scene description. This is the single
  largest deletion and a good measure of why the web is a better fit here.

---

## 4. Phased plan

Estimates are in focused working days for one developer already familiar with
the domain. They assume the server is not changing underneath, and they
exclude art, copy and balancing.

### Phase 0 - Enabler (server only, ~3-5 days)

No client work. Nothing visual.

1. Add a `mode` field to the auth handshake: `binary` (default, Unity) or
   `json`. Per connection, never global.
2. JSON serialisation for all six packet types, each carrying an explicit
   `type` discriminator so the client never dispatches on byte length.
3. A contract test asserting the JSON shape carries every field the binary
   struct declares, for all six. **This test is the entire defence** against
   the drift this document warns about - without it, the port becomes the
   project's fourth two-sources-of-truth bug and the largest one.
4. Serve `StreamingAssets/GameData/*.json` over HTTP so the web client can
   load the same content files.

Exit: `wscat` logs in and receives readable state.

### Phase 1 - Vertical slice and DECISION GATE (~8-12 days)

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

### Phase 3 - Economy (~15-20 days)

Market browse/buy/sell/cancel, bank vault deposit and withdraw, crafting tree
(104 recipes), forge crafting and fusion, affix reroll including auto-reroll
and its stop conditions.

`UiEquipmentRerollPanel` is 607 lines and among the most intricate screens in
the project - three operations, two currencies, escalating costs, stop
conditions. Budget for it accordingly.

**Feature freeze on the Unity client starts here.** Past this point, dual
maintenance is the main cost driver, and only bug fixes should land in Unity.

### Phase 4 - Social (~12-18 days)

Chat across all three channels plus announcements and the congratulate button,
friends, guild create/join/directory/roster/applications, guild war, raids,
mentorship.

`UiChatWindow` is 627 lines with pooled rows, three channels and history. In
the web version most of that shrinks to a virtual list plus a store.

### Phase 5 - Meta and progression (~15-20 days)

Achievements, statistics, leaderboards, codex (excluding the 3D viewer), season
pass, login bonus, race mastery, skill tree, village overview and buildings,
breeding lab and gene vectors.

Largest phase by screen count, but the screens are mostly read-only lists -
fast per screen.

### Phase 6 - Monetisation and packaging (~10-15 days)

Store, legacy shop, chrono bank, billing verification and receipt validation.
Then Capacitor for Android; then iOS, which needs a Mac exactly as Unity does.
Push notifications land here (see 3.2c).

### Phase 7 - Parity close-out (~10-15 days)

Tutorial (`TutorialStateMachine` plus highlight and interaction gate),
localisation (`LocalizationMatrix`, 239 lines, plus the existing
`localizations.json`), audio via Howler with the ten generated WAVs,
accessibility, telemetry.

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

---

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
