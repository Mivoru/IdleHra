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

Today the wire is fixed-layout C# structs: `StateUpdatePacket` at 695 bytes
with 151 fields, `ClientCommandPacket` at 359. It works because both ends are
C# and `Marshal.SizeOf` verifies both sides agree in one test.

A TypeScript client cannot share that. Three options:

1. **Hand-write a DataView parser.** Rejected. 151 fields of manual offsets,
   maintained in parallel with the C# struct. This is precisely the drift that
   has produced this project's worst bugs.
2. **Generate the TypeScript parser from the C# structs** at build time.
   Workable, keeps the binary format, but needs a code generator and a CI step
   to prove they still match.
3. **Add a JSON WebSocket mode server-side.** Recommended.

**Recommendation: option 3.** The binary format exists to save bandwidth, but
the actual load is one player receiving roughly 10 packets per second. 695
bytes versus perhaps 2 KB of JSON is irrelevant over a browser WebSocket.

Crucially it is **additive**: the Unity path keeps the binary format untouched,
and the server gains a per-connection mode flag chosen at handshake. No shared
code, no dual maintenance of a parser, and the JSON shape can be generated
from the same struct by serialisation rather than by hand.

The broadcast dirty-checking already in `SimulationEngine` (`ShouldDispatchStateUpdate`)
applies unchanged - it decides *whether* to send, not *how* to encode.

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

### Phase 0 - Enabler (server only, no client work)

1. Add a JSON mode to the WebSocket handshake. Per-connection flag; binary
   remains the default so Unity is unaffected.
2. Serialise `StateUpdatePacket` and accept `ClientCommandPacket` as JSON on
   that connection.
3. Add a contract test asserting the JSON shape carries every field the binary
   struct does, so a new field cannot land in one and not the other. This test
   is the whole defence against the drift this document warns about.

Deliverable: `wscat` can log in and receive readable state. Nothing visual.

### Phase 1 - Vertical slice (the decision gate)

Target: **login, pick a monster, fight it, see HP, loot and progress.** Four
screens out of 49.

1. Vite + Svelte + TypeScript project under `web/`.
2. Auth: register, login, token storage. Endpoints already exist.
3. WebSocket client with reconnect, feeding a `playerState` store.
4. Combat screen: monster list, selection, HP bars, floating damage, loot log
   with rarity in brackets.
5. Rarity palette as CSS variables plus a glow animation.

**Stop here and decide.** The question this answers is not "does it work" - it
will - but "is iterating on this materially faster than Unity". If the honest
answer is no, stop and keep Unity. Nothing is lost.

### Phase 2 - Core loop

Inventory, equipment slots, character stats, larder and auto-eat, activity
status, gathering, offline summary.

### Phase 3 - Economy

Market browse/buy/sell/cancel, bank vault deposit and withdraw, crafting tree,
forge crafting and fusion, affix reroll including auto-reroll.

### Phase 4 - Social

Chat with all three channels plus announcements and the congratulate button,
friends, guild create/join/directory/roster/applications, guild war, raids,
mentorship.

### Phase 5 - Meta and progression

Achievements, statistics, leaderboards, codex, season pass, login bonus, race
mastery, skill tree, village and breeding.

### Phase 6 - Monetisation and packaging

Store, legacy shop, chrono bank, billing verification. Then Capacitor for
Android, then iOS - which needs a Mac either way, exactly as Unity does.

### Phase 7 - Parity close-out

Tutorial, localisation, audio, accessibility, telemetry.

**Ordering rationale:** each phase is independently playable. Phase 1 alone is
a real game loop; Phase 3 makes it an economy. Nothing here requires a big-bang
switchover, and the Unity client remains the shipping client until a phase
boundary where the web version is genuinely ahead.

---

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
