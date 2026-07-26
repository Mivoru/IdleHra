using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.UI;
using FolkIdle.Client.Network;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.Editor
{
    // Modul: Unity UI & Network Automation - Main Scene. Programmatic
    // hierarchy builder for the always-on gameplay screens (Login gate +
    // main HUD: character stats, action bar, equipment slots, combat
    // arena) plus the chat window (reusing ChatSceneBuilder's own row-
    // prefab and chat-window builders rather than duplicating them).
    // Editor-only by construction (Assets/Scripts/Editor/), never ships.
    public static class MainSceneBuilder
    {
        private const string PrefabDirectory = "Assets/Prefabs/UI";
        private const string DamageTextPrefabPath = PrefabDirectory + "/UiFloatingDamageText.prefab";
        private const string ProjectilePrefabPath = PrefabDirectory + "/UiAttackProjectile.prefab";
        private const string GuildRosterRowPrefabPath = PrefabDirectory + "/UiGuildRosterEntryRow.prefab";
        private const string MarketListingRowPrefabPath = PrefabDirectory + "/UiMarketListingRow.prefab";
        private const string MarketSellRowPrefabPath = PrefabDirectory + "/UiMarketSellCandidateRow.prefab";
        private const string ForgeFusionRowPrefabPath = PrefabDirectory + "/UiForgeFusionCandidateRow.prefab";
        private const string BankVaultRowPrefabPath = PrefabDirectory + "/UiBankVaultEntryRow.prefab";
        private const string BankDepositRowPrefabPath = PrefabDirectory + "/UiBankDepositCandidateRow.prefab";
        private const string AchievementRowPrefabPath = PrefabDirectory + "/UiAchievementRow.prefab";
        private const string LeaderboardRowPrefabPath = PrefabDirectory + "/UiLeaderboardEntryRow.prefab";
        private const string MailboxRowPrefabPath = PrefabDirectory + "/UiMailboxEntryRow.prefab";
        private const string StoreRowPrefabPath = PrefabDirectory + "/UiStoreEntryRow.prefab";
        private const string SeasonPassRowPrefabPath = PrefabDirectory + "/UiSeasonPassMilestoneRow.prefab";
        private const string CombatMonsterRowPrefabPath = PrefabDirectory + "/UiCombatMonsterRow.prefab";
        private const string InventoryRowPrefabPath = PrefabDirectory + "/UiInventoryEntryRow.prefab";
        private const string SectionHeaderRowPrefabPath = PrefabDirectory + "/UiSectionHeaderRow.prefab";
        private const string CraftingRecipeRowPrefabPath = PrefabDirectory + "/UiCraftingRecipeRow.prefab";
        private const string ForgeRecipeRowPrefabPath = PrefabDirectory + "/UiForgeRecipeRow.prefab";
        private const string ForgeEquipmentRowPrefabPath = PrefabDirectory + "/UiForgeEquipmentRow.prefab";
        private const string AssetRegistryAssetPath = PrefabDirectory + "/AssetRegistry.asset";
        private const string CodexListRowPrefabPath = PrefabDirectory + "/UiCodexListRow.prefab";
        private const string CodexRegionRowPrefabPath = PrefabDirectory + "/UiCodexRegionRow.prefab";
        private const string BreedingRosterRowPrefabPath = PrefabDirectory + "/UiBreedingRosterRow.prefab";
        private const string FriendRowPrefabPath = PrefabDirectory + "/UiFriendEntryRow.prefab";
        private const string GuildApplicationRowPrefabPath = PrefabDirectory + "/UiGuildApplicationEntryRow.prefab";

        [MenuItem("FolkIdle/Build Main Scene (Login + HUD + Chat)")]
        public static void BuildMainScene()
        {
            // Modul: Full-Game UI Architecture, Part 3. Idempotency - this
            // menu item is meant to be re-run every time the builder script
            // changes (see the file header comment), but every Build*
            // method below unconditionally does `new GameObject(...)` with
            // no "find and reuse" check. Without clearing the previous
            // pass's output first, re-running this against an already-built
            // scene would leave two overlapping Canvases and two
            // WebSocketClient/VisualSyncProxy instances fighting over the
            // same connection instead of replacing the old hierarchy.
            // "Main Camera" is deliberately left untouched - it is never
            // something this builder owns.
            ClearPreviousGeneratedHierarchy();

            // Modul: UI audit follow-up. UiCodex3DViewer's PreviewCamera
            // culling mask (and the model instance's own layer) has always
            // depended on a "UI_3D_Preview" Layer existing in this project's
            // Tags & Layers settings - referenced only by name in code,
            // which cannot itself create the engine-level Layer slot.
            // Without it LayerMask.NameToLayer returns -1, the camera's
            // cullingMask becomes 0, and the viewer renders nothing even
            // once a monster prefab is assigned - a structural gap, not an
            // art one. Ensured here (idempotent) rather than requiring a
            // manual one-time Project Settings edit. The two Forge item
            // preview viewports get their own distinct layers rather than
            // sharing this one: both viewers' rigs live under Managers (see
            // BuildManagers), which - like CodexPreviewRig - keeps
            // rendering even while its own screen/tab is inactive, so a
            // shared culling mask would let one viewer's leftover model
            // bleed into the other's render texture.
            EnsureLayerExists("UI_3D_Preview");
            EnsureLayerExists("UI_3D_Preview_ForgeCraft");
            EnsureLayerExists("UI_3D_Preview_ForgeReroll");

            ChatSceneBuilder.EnsureEventSystem();
            Canvas canvas = ChatSceneBuilder.BuildCanvas();

            GameObject managers = BuildManagers();
            WebSocketClient networkClient = managers.GetComponent<WebSocketClient>();
            VisualSyncProxy syncProxy = managers.GetComponent<VisualSyncProxy>();
            EquipmentInventoryCache inventoryCache = managers.GetComponent<EquipmentInventoryCache>();
            SfxPoolEngine sfxEngine = managers.GetComponent<SfxPoolEngine>();
            AssetLifecycleCoordinator assetCoordinator = managers.GetComponent<AssetLifecycleCoordinator>();
            AssetRegistry assetRegistry = EnsureAssetRegistryAsset();

            GameObject rowPrefabAsset = ChatSceneBuilder.BuildAndSaveRowPrefab();
            ChatSceneBuilder.RegisterRowPrefabAsAddressable(rowPrefabAsset);

            // Modul: Full-Game UI Architecture, Part 3. Every always-on HUD
            // panel now lives under one HudGroup root instead of parenting
            // straight to the Canvas, so the top-level screen switcher can
            // show/hide the whole HUD as a single unit alongside every
            // other screen it toggles against. HudGroup is reached only via
            // the Combat Selection panel's Deploy button now (Map Hub, Part
            // 3 below), not a direct nav button - it is no longer the
            // default/home screen (MainMapHub is).
            GameObject hudGroup = new GameObject("HudGroup", typeof(RectTransform));
            hudGroup.transform.SetParent(canvas.transform, false);
            StretchFull((RectTransform)hudGroup.transform);

            BuildCharacterStatsPanel(hudGroup.transform, syncProxy);
            BuildActionBar(hudGroup.transform, networkClient, syncProxy);
            Image equipmentSlotsBackground = BuildEquipmentSlotsPanel(hudGroup.transform, syncProxy, inventoryCache, networkClient);
            Image playerPortraitImage = BuildCombatArena(hudGroup.transform, syncProxy);
            BuildVillageResourceStrip(hudGroup.transform, syncProxy);

            GameObject guildWindowObject = BuildGuildWindow(canvas.transform, syncProxy, networkClient, sfxEngine);
            GameObject marketBankWindowObject = BuildMarketBankWindow(canvas.transform, syncProxy, networkClient, inventoryCache);

            // Modul: Full-Game UI Architecture, Part 5. Forge (Craft/Reroll
            // sub-tabs), Skill Tree, and Village windows - the last batch of
            // previously-orphaned network-wired scripts from the UI survey.
            GameObject forgeWindowObject = BuildForgeWindow(canvas.transform, inventoryCache, networkClient, syncProxy, assetRegistry, assetCoordinator, managers.transform);
            GameObject skillTreeWindowObject = BuildSkillTreeWindow(canvas.transform, networkClient, syncProxy);
            GameObject villageWindowObject = BuildVillageWindow(canvas.transform, syncProxy, networkClient);
            GameObject codexWindowObject = BuildCodexWindow(canvas.transform, assetRegistry, assetCoordinator, managers.transform);
            GameObject breedingLabWindowObject = BuildBreedingLabWindow(canvas.transform, networkClient);

            // Modul: Full-Game UI Architecture, Part 4. Simple list-style
            // screens - Achievements, Leaderboard, Mailbox, Store, Season
            // Pass. All real, network-wired scripts.
            GameObject achievementsWindowObject = BuildAchievementsWindow(canvas.transform, networkClient);
            GameObject leaderboardWindowObject = BuildLeaderboardWindow(canvas.transform);
            GameObject mailboxWindowObject = BuildMailboxWindow(canvas.transform, syncProxy, networkClient);
            GameObject storeWindowObject = BuildStoreWindow(canvas.transform, syncProxy, networkClient);
            GameObject seasonPassWindowObject = BuildSeasonPassWindow(canvas.transform, syncProxy, networkClient);

            // Modul: UI audit follow-up. UiRaceMasteryPanel/RaceMasteryCache
            // were a complete, real, network-wired feature with no
            // GameObject anywhere in the scene - see BuildRaceMasteryWindow's
            // own comment.
            GameObject raceMasteryWindowObject = BuildRaceMasteryWindow(canvas.transform);
            GameObject chronoBankWindowObject = BuildChronoBankWindow(canvas.transform, syncProxy, networkClient);
            GameObject legacyShopWindowObject = BuildLegacyShopWindow(canvas.transform, syncProxy, networkClient);
            GameObject mentorshipContractWindowObject = BuildMentorshipContractWindow(canvas.transform, syncProxy, networkClient);

            // Modul: Map Hub, Part 2. Honest static placeholders - Friends,
            // Statistics, and Login Bonus have no corresponding
            // engine/network code anywhere server-side (confirmed via
            // project-wide search), so unlike every other screen in this
            // file they are not wired to any real cache; they are plain
            // shells reachable from the hamburger menu, ready for real
            // content once that server-side support exists. Settings gets
            // a real (if minimal) Profile section - see BuildSettingsWindow
            // - since it hosts the one real, load-bearing action this pass
            // adds: Log Off. Friends is no longer a placeholder - see
            // BuildFriendsWindow's own comment.
            (GameObject settingsPanelObject, Button logOffButton) = BuildSettingsWindow(canvas.transform, syncProxy, networkClient);
            GameObject friendsPanelObject = BuildFriendsWindow(canvas.transform, networkClient);
            GameObject statisticsPanelObject = BuildStatisticsWindow(canvas.transform);
            GameObject loginBonusPanelObject = BuildLoginBonusWindow(canvas.transform);

            // Modul: UI rework. Account screen - identity plus the two
            // account-level actions. Log Off already existed (buried in
            // Settings); account deletion did not have a UI anywhere despite
            // CommandType.TriggerGdprPurge being real and validated
            // server-side. See UiAccountPanel's own header comment.
            GameObject accountPanelObject = BuildAccountWindow(canvas.transform, networkClient);

            // Modul: Inventory and Crafting Tree. Two brand new screens
            // closing the two largest remaining content gaps - see
            // UiInventoryPanel and UiCraftingTreePanel for what was missing.
            GameObject larderPanelObject = BuildLarderWindow(canvas.transform, networkClient, syncProxy);

            GameObject inventoryPanelObject = BuildInventoryWindow(canvas.transform, assetRegistry, syncProxy, networkClient, inventoryCache);
            GameObject craftingPanelObject = BuildCraftingTreeWindow(canvas.transform, networkClient, assetRegistry);

            // Modul: Map Hub, Part 3. Combat Selection (real region/
            // monster/character data, see UiCombatLocationPanel) and Boss
            // World (real HP/attack plus the real global leaderboard, see
            // BuildBossWorldPanel) - the two new full-screen panels reached
            // from the map's Combat and Boss zones.
            (GameObject combatPanelObject, UiCombatLocationPanel combatPanelComponent) = BuildCombatSelectionPanel(canvas.transform, networkClient, syncProxy, assetRegistry);
            GameObject bossWorldPanelObject = BuildBossWorldPanel(canvas.transform, syncProxy, sfxEngine, networkClient);

            // Modul: Map Hub, Part 4. The medieval map field itself - 5
            // clickable zone buttons (Combat, Village, Guild, Market,
            // Boss), now the default/home screen.
            (GameObject mainMapHubObject, Button combatZoneButton, Button villageZoneButton, Button guildZoneButton, Button marketZoneButton, Button bossZoneButton) = BuildMainMapHub(canvas.transform);

            // Modul: UI rework. World chat is a child of the map hub, not a
            // Canvas-level overlay - see BuildChatPanel's header comment for
            // why the old always-on overlay both overlapped every screen and
            // never worked at all.
            BuildWorldChatOverlay(mainMapHubObject.transform, networkClient);

            // Modul: Map Hub, Part 5. Hamburger sliding menu - folds every
            // screen not represented as one of the 5 map zones (per user
            // direction: Bestiary reuses the existing Codex window rather
            // than duplicating it under a second name).
            //
            // Modul: UI rework. Grouped under section headers and keyed by
            // label rather than by array position. The old flat string[] had
            // to stay index-aligned by hand with two further arrays 40 lines
            // below (screens[] / screenButtons[]), so reordering or
            // inserting a single entry silently pointed menu items at the
            // wrong screens. "Places" is new: the five map zones were
            // previously reachable only by going back to the map and finding
            // the right region of it.
            (GameObject hamburgerBlocker, UiHamburgerMenuPanel hamburgerComponent, Dictionary<string, Button> menu) = BuildHamburgerPanel(canvas.transform, new[]
            {
                ("Places", new[] { "World Map", "Combat", "Village", "Guild", "Market & Bank", "World Boss" }),
                ("Character", new[] { "Inventory", "Larder", "Forge", "Crafting", "Skills", "Bestiary", "Breeding Lab", "Race Mastery", "Mentorship" }),
                ("Progress", new[] { "Achievements", "Season Pass", "Login Bonus", "Statistics", "Leaderboard" }),
                ("Social", new[] { "Friends", "Mailbox" }),
                ("Economy", new[] { "Store", "Time Bank", "Legacy Shop" }),
                ("System", new[] { "Account", "Settings" })
            });

            // Modul: Map Hub, Part 6. Persistent top-left (hamburger toggle
            // + Home/Map button), top-right (real Gold/Gems currency), and
            // bottom (Season Pass banner) bars - stay visible across every
            // screen per the map-hub spec's UI persistence requirement.
            (Button hamburgerToggleButton, Button homeButton, Button battlePassBannerButton) = BuildPersistentBars(canvas.transform, syncProxy, assetRegistry);

            BuildActivityHaltBanner(canvas.transform, syncProxy);

            // Modul: Map Hub, Part 7. One screen switcher for every
            // top-level screen - replaces the old flat scrollable nav-tab
            // strip. Index 0 (MainMapHub) is the default/home screen.
            GameObject screenManagerObject = new GameObject("ScreenManager", typeof(RectTransform));
            screenManagerObject.transform.SetParent(canvas.transform, false);

            GameObject[] screens =
            {
                mainMapHubObject, hudGroup, combatPanelObject, villageWindowObject, guildWindowObject,
                marketBankWindowObject, bossWorldPanelObject, forgeWindowObject, skillTreeWindowObject,
                codexWindowObject, breedingLabWindowObject, achievementsWindowObject, leaderboardWindowObject,
                mailboxWindowObject, storeWindowObject, seasonPassWindowObject, settingsPanelObject,
                friendsPanelObject, statisticsPanelObject, loginBonusPanelObject, raceMasteryWindowObject,
                chronoBankWindowObject, legacyShopWindowObject, mentorshipContractWindowObject,
                accountPanelObject, inventoryPanelObject, craftingPanelObject, larderPanelObject
            };

            // Index-aligned with screens[] above. UiTabGroup supports at most
            // one Buttons[] entry per screen; the map zones therefore keep
            // their zone button here and their duplicate hamburger entry is
            // wired separately as a ShowIndex persistent listener below.
            Button[] screenButtons =
            {
                null, null, combatZoneButton, villageZoneButton, guildZoneButton,
                marketZoneButton, bossZoneButton, menu["Forge"], menu["Skills"],
                menu["Bestiary"], menu["Breeding Lab"], menu["Achievements"], menu["Leaderboard"],
                menu["Mailbox"], menu["Store"], menu["Season Pass"], menu["Settings"],
                menu["Friends"], menu["Statistics"], menu["Login Bonus"], menu["Race Mastery"],
                menu["Time Bank"], menu["Legacy Shop"], menu["Mentorship"],
                menu["Account"], menu["Inventory"], menu["Crafting"], menu["Larder"]
            };

            const int HudGroupScreenIndex = 1;
            const int SeasonPassScreenIndex = 15;

            for (int screenIndex = 1; screenIndex < screens.Length; screenIndex++)
            {
                screens[screenIndex].SetActive(false);
            }

            UiTabGroup screenTabGroup = screenManagerObject.AddComponent<UiTabGroup>();
            screenTabGroup.Groups = screens;
            screenTabGroup.Buttons = screenButtons;

            combatPanelComponent.ScreenTabGroup = screenTabGroup;
            combatPanelComponent.CharacterScreenIndex = HudGroupScreenIndex;
            combatPanelComponent.NetworkClient = networkClient;

            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(homeButton.onClick, screenTabGroup.ShowIndex, 0);
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(battlePassBannerButton.onClick, screenTabGroup.ShowIndex, SeasonPassScreenIndex);

            // Modul: UI rework. The "Places" section duplicates the five map
            // zones (plus the map itself) as menu entries. They cannot go in
            // screenButtons[] - that array is one button per screen - so
            // each is wired directly to UiTabGroup.ShowIndex with its own
            // screen index, the same mechanism homeButton already uses.
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(menu["World Map"].onClick, screenTabGroup.ShowIndex, 0);
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(menu["Combat"].onClick, screenTabGroup.ShowIndex, 2);
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(menu["Village"].onClick, screenTabGroup.ShowIndex, 3);
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(menu["Guild"].onClick, screenTabGroup.ShowIndex, 4);
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(menu["Market & Bank"].onClick, screenTabGroup.ShowIndex, 5);
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(menu["World Boss"].onClick, screenTabGroup.ShowIndex, 6);

            // Modul: plain field assignment, not a persistent listener -
            // UiHamburgerMenuPanel already self-wires ToggleButton.onClick
            // inside its own Awake() (established codebase convention),
            // it just couldn't be assigned inside BuildHamburgerPanel
            // itself since hamburgerToggleButton (built by
            // BuildPersistentBars) does not exist yet at that point.
            hamburgerComponent.ToggleButton = hamburgerToggleButton;

            // Modul: every hamburger menu button both switches screens (via
            // the index-aligned Buttons[] entry above, or the explicit
            // ShowIndex listener for the Places section) and closes the
            // sliding panel afterward - two independent persistent listeners
            // on the same onClick.
            foreach (Button menuButton in menu.Values)
            {
                UnityEditor.Events.UnityEventTools.AddPersistentListener(menuButton.onClick, hamburgerComponent.Close);
            }

            // Modul: Full-Game UI Architecture, Part 4. Persistent global
            // overlays - always visible regardless of the active screen,
            // matching every one of these scripts' own "isolated sub-
            // canvas" design intent.
            BuildGlobalOverlays(canvas.transform, syncProxy);

            // Modul: Full-Game UI Architecture, Part 6 (final). FTUE
            // tutorial - CTA highlights on the Inventory HUD panel/Forge
            // menu button/Arena, a step-instruction overlay with a Skip
            // button, and interaction gates on the buttons the closed
            // TutorialUiElement enum can distinguish. Forge/Skills now live
            // in the hamburger menu; Market/Guild are map zones; Chat is a
            // persistent overlay with no button left to gate, so its gate
            // is skipped (BuildTutorialInteractionGate already no-ops on a
            // null button).
            UiTutorialController tutorialController = BuildTutorialSystem(
                canvas.transform, syncProxy, equipmentSlotsBackground, playerPortraitImage,
                forgeButton: menu["Forge"], marketButton: marketZoneButton, guildButton: guildZoneButton,
                skillTreeButton: menu["Skills"], chatButton: null);

            // Modul: Map Hub. Built LAST, deliberately - LoginWindow's
            // BlockingPanel must always render (and raycast-block) on top
            // of literally everything else while unauthenticated. Building
            // it early (as before the map hub existed) left it at the
            // bottom of the sibling stack, so the map hub/hamburger/
            // persistent bars all drew and received clicks over top of it
            // once a real screen switcher and always-on overlay bars
            // existed - previously harmless only because nothing else was
            // ever both persistent and interactive at the same time.
            UiLoginWindow loginWindow = BuildLoginWindow(canvas.transform, networkClient);
            loginWindow.TutorialController = tutorialController;

            // Modul: Email/Password Auth. Settings/Profile's Log Off button
            // was built long before UiLoginWindow existed (LoginWindow is
            // deliberately built last for z-order - see its own comment
            // above), so it can only be wired now, as a post-pass
            // persistent listener onto the now-real LogOff() method -
            // exactly the same pattern already used for homeButton/
            // battlePassBannerButton above.
            UnityEditor.Events.UnityEventTools.AddPersistentListener(logOffButton.onClick, loginWindow.LogOff);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();

            Debug.Log("MainSceneBuilder: main scene hierarchy built successfully.");
        }

        private static void ClearPreviousGeneratedHierarchy()
        {
            DestroyRootIfExists("Managers");
            DestroyRootIfExists("Canvas");
            DestroyRootIfExists("EventSystem");
        }

        private static void DestroyRootIfExists(string rootObjectName)
        {
            GameObject existing = GameObject.Find(rootObjectName);
            if (existing != null && existing.transform.parent == null)
            {
                Object.DestroyImmediate(existing);
            }
        }

        // Modul: UI audit follow-up. Layers can only be registered by
        // editing ProjectSettings/TagManager.asset (no runtime API exists to
        // create one) - user layers occupy slots 8-31. Idempotent: a no-op
        // if the name is already registered anywhere in the table.
        private static void EnsureLayerExists(string layerName)
        {
            Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (tagManagerAssets == null || tagManagerAssets.Length == 0)
            {
                Debug.LogWarning("MainSceneBuilder: could not load ProjectSettings/TagManager.asset to register layer '" + layerName + "'.");
                return;
            }

            SerializedObject tagManager = new SerializedObject(tagManagerAssets[0]);
            SerializedProperty layersProp = tagManager.FindProperty("layers");
            if (layersProp == null) return;

            for (int i = 0; i < layersProp.arraySize; i++)
            {
                if (layersProp.GetArrayElementAtIndex(i).stringValue == layerName) return;
            }

            for (int i = 8; i < layersProp.arraySize; i++)
            {
                SerializedProperty slot = layersProp.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = layerName;
                    tagManager.ApplyModifiedProperties();
                    return;
                }
            }

            Debug.LogWarning("MainSceneBuilder: no free user layer slot (8-31) available to register '" + layerName + "'.");
        }

        // Modul: one root GameObject holding every singleton-style
        // dependency the HUD panels share (WebSocketClient, AssetManager,
        // VisualSyncProxy, EquipmentInventoryCache, SfxPoolEngine) - built
        // once, referenced by every panel below instead of each panel
        // resolving its own dependency via a runtime lookup.
        private static GameObject BuildManagers()
        {
            GameObject managers = new GameObject("Managers");

            WebSocketClient networkClient = managers.AddComponent<WebSocketClient>();
            managers.AddComponent<AssetManager>();

            VisualSyncProxy syncProxy = managers.AddComponent<VisualSyncProxy>();
            syncProxy.NetworkClient = networkClient;

            managers.AddComponent<EquipmentInventoryCache>();
            managers.AddComponent<SfxPoolEngine>();
            managers.AddComponent<AssetLifecycleCoordinator>();

            // Modul: UI rework. Single drain point for the inbound chat
            // stream, feeding all three chat windows (World/Guild/Whisper).
            // Lives here rather than on any one window so a message still
            // reaches a channel whose screen is currently hidden - see
            // ChatRelay's own header comment.
            ChatRelay chatRelay = managers.AddComponent<ChatRelay>();
            chatRelay.NetworkClient = networkClient;

            // Modul: UI rework. Baselines gold/XP/kill counts at deploy time
            // so the Combat screen can show what this session actually
            // farmed. On Managers, not on the Combat screen, so the tally
            // keeps accruing while the player is looking elsewhere.
            CombatSessionTracker combatSessionTracker = managers.AddComponent<CombatSessionTracker>();
            combatSessionTracker.SyncProxy = syncProxy;
            // Modul: Loot Event Feed. The tracker drains
            // WebSocketClient.LootDropQueue itself, so it needs the socket -
            // without this the drop list stays permanently empty even though
            // the server is publishing drops correctly.
            combatSessionTracker.NetworkClient = networkClient;

            return managers;
        }

        // ------------------------------------------------------------
        // Login window
        // ------------------------------------------------------------
        // Modul: Email/Password Auth. Choice (Login vs Register) / Login /
        // Register-Step1 (email) / Register-Step2 (username+password)
        // screens, all centered in the same BlockingPanel and shown/hidden
        // exclusively by UiLoginWindow's own logic at runtime (Start()
        // hides all four before deciding which one, if any, to reveal - a
        // remembered-device hit skips them entirely). StatusText is shared
        // across every screen, pinned above them.
        private static UiLoginWindow BuildLoginWindow(Transform canvasTransform, WebSocketClient networkClient)
        {
            GameObject windowObject = new GameObject("LoginWindow", typeof(RectTransform));
            windowObject.transform.SetParent(canvasTransform, false);
            StretchFull((RectTransform)windowObject.transform);

            UiLoginWindow loginWindow = windowObject.AddComponent<UiLoginWindow>();
            loginWindow.NetworkClient = networkClient;

            GameObject blockingPanel = new GameObject("BlockingPanel", typeof(RectTransform));
            blockingPanel.transform.SetParent(windowObject.transform, false);
            StretchFull((RectTransform)blockingPanel.transform);
            blockingPanel.AddComponent<Image>().color = new Color(0.05f, 0.05f, 0.08f, 0.96f);

            TMP_Text statusText = CreateText(blockingPanel.transform, "StatusText", string.Empty, 18f, TextAlignmentOptions.Center);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0.5f, 0.68f);
            statusRect.anchorMax = new Vector2(0.5f, 0.68f);
            statusRect.sizeDelta = new Vector2(600f, 50f);
            statusRect.anchoredPosition = Vector2.zero;

            GameObject choiceRoot = BuildAuthScreenRoot(blockingPanel.transform, "ChoiceRoot", 140f);
            Button showLoginButton = BuildAuthButton(choiceRoot.transform, "ShowLoginButton", "Login");
            Button showRegisterButton = BuildAuthButton(choiceRoot.transform, "ShowRegisterButton", "Register");

            GameObject loginRoot = BuildAuthScreenRoot(blockingPanel.transform, "LoginRoot", 250f);
            TMP_InputField loginEmailField = BuildAuthInputField(loginRoot.transform, "LoginEmailField", "Email", false);
            TMP_InputField loginPasswordField = BuildAuthInputField(loginRoot.transform, "LoginPasswordField", "Password", true);
            Button loginSubmitButton = BuildAuthButton(loginRoot.transform, "LoginSubmitButton", "Log In");
            Button loginBackButton = BuildAuthButton(loginRoot.transform, "LoginBackButton", "Back");

            GameObject registerStep1Root = BuildAuthScreenRoot(blockingPanel.transform, "RegisterStep1Root", 195f);
            TMP_InputField registerEmailField = BuildAuthInputField(registerStep1Root.transform, "RegisterEmailField", "Email", false);
            Button registerNextButton = BuildAuthButton(registerStep1Root.transform, "RegisterNextButton", "Next");
            Button registerStep1BackButton = BuildAuthButton(registerStep1Root.transform, "RegisterStep1BackButton", "Back");

            GameObject registerStep2Root = BuildAuthScreenRoot(blockingPanel.transform, "RegisterStep2Root", 400f);
            TMP_Text registerStep2EmailLabel = CreateText(registerStep2Root.transform, "RegisterStep2EmailLabel", string.Empty, 16f, TextAlignmentOptions.Center);
            SetFixedLayoutHeight(registerStep2EmailLabel.gameObject, 26f);
            TMP_InputField registerUsernameField = BuildAuthInputField(registerStep2Root.transform, "RegisterUsernameField", "Username", false);
            TMP_InputField registerPasswordField = BuildAuthInputField(registerStep2Root.transform, "RegisterPasswordField", "Password", true);
            TMP_InputField registerConfirmPasswordField = BuildAuthInputField(registerStep2Root.transform, "RegisterConfirmPasswordField", "Confirm Password", true);
            Button registerSubmitButton = BuildAuthButton(registerStep2Root.transform, "RegisterSubmitButton", "Create Account");
            Button registerStep2BackButton = BuildAuthButton(registerStep2Root.transform, "RegisterStep2BackButton", "Back");

            loginWindow.BlockingPanelRoot = blockingPanel;
            loginWindow.StatusText = statusText;

            loginWindow.ChoiceRoot = choiceRoot;
            loginWindow.ShowLoginButton = showLoginButton;
            loginWindow.ShowRegisterButton = showRegisterButton;

            loginWindow.LoginRoot = loginRoot;
            loginWindow.LoginEmailField = loginEmailField;
            loginWindow.LoginPasswordField = loginPasswordField;
            loginWindow.LoginSubmitButton = loginSubmitButton;
            loginWindow.LoginBackButton = loginBackButton;

            loginWindow.RegisterStep1Root = registerStep1Root;
            loginWindow.RegisterEmailField = registerEmailField;
            loginWindow.RegisterNextButton = registerNextButton;
            loginWindow.RegisterStep1BackButton = registerStep1BackButton;

            loginWindow.RegisterStep2Root = registerStep2Root;
            loginWindow.RegisterStep2EmailLabel = registerStep2EmailLabel;
            loginWindow.RegisterUsernameField = registerUsernameField;
            loginWindow.RegisterPasswordField = registerPasswordField;
            loginWindow.RegisterConfirmPasswordField = registerConfirmPasswordField;
            loginWindow.RegisterSubmitButton = registerSubmitButton;
            loginWindow.RegisterStep2BackButton = registerStep2BackButton;

            return loginWindow;
        }

        // A centered, fixed-width, vertically-stacking container shared by
        // every auth screen (Choice/Login/Register step 1/Register step 2)
        // - UiLoginWindow.HideAllScreens()/Start() decide which one (if
        // any) is actually active at runtime.
        private static GameObject BuildAuthScreenRoot(Transform parent, string name, float height)
        {
            GameObject root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)root.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(520f, height);
            rect.anchoredPosition = new Vector2(0f, -20f);

            VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 14f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            return root;
        }

        private static TMP_InputField BuildAuthInputField(Transform parent, string name, string placeholder, bool isPassword)
        {
            TMP_InputField field = CreateInputField(parent, name, placeholder);
            SetFixedLayoutHeight(field.gameObject, 50f);
            if (isPassword)
            {
                field.contentType = TMP_InputField.ContentType.Password;
            }
            return field;
        }

        private static Button BuildAuthButton(Transform parent, string name, string label)
        {
            Button button = CreateButton(parent, name, label, out TextMeshProUGUI _);
            SetFixedLayoutHeight(button.gameObject, 54f);
            return button;
        }

        // ------------------------------------------------------------
        // Character stats panel - top-left HUD corner
        // ------------------------------------------------------------
        private static void BuildCharacterStatsPanel(Transform canvasTransform, VisualSyncProxy syncProxy)
        {
            GameObject panelObject = new GameObject("CharacterStatsPanel", typeof(RectTransform));
            panelObject.transform.SetParent(canvasTransform, false);
            RectTransform panelRect = (RectTransform)panelObject.transform;
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            // Modul: Map Hub. Shifted down from the literal top-left corner
            // to make room for the new persistent Hamburger/Map button row
            // (top-left, y -16 to -62) which sits above every screen.
            panelRect.anchoredPosition = new Vector2(20f, -72f);
            panelRect.sizeDelta = new Vector2(260f, 220f);

            panelObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            VerticalLayoutGroup layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            UiCharacterStatsPanel statsPanel = panelObject.AddComponent<UiCharacterStatsPanel>();
            statsPanel.SyncProxy = syncProxy;

            statsPanel.StrText = CreateStatRow(panelObject.transform, "STR: 0");
            statsPanel.DexText = CreateStatRow(panelObject.transform, "DEX: 0");
            statsPanel.ConText = CreateStatRow(panelObject.transform, "CON: 0");
            statsPanel.LckText = CreateStatRow(panelObject.transform, "LCK: 0");
            statsPanel.MeleeDamageText = CreateStatRow(panelObject.transform, "Melee: 0");
            statsPanel.RangedDamageText = CreateStatRow(panelObject.transform, "Ranged: 0");
            statsPanel.CritChanceText = CreateStatRow(panelObject.transform, "Crit: 0.0%");
            statsPanel.MaxHpText = CreateStatRow(panelObject.transform, "Max HP: 0");
        }

        private static TextMeshProUGUI CreateStatRow(Transform parent, string placeholderText)
        {
            TextMeshProUGUI text = CreateText(parent, "Stat_" + placeholderText, placeholderText, 16f, TextAlignmentOptions.MidlineLeft);
            SetFixedLayoutHeight(text.gameObject, 22f);
            // Every caller's VerticalLayoutGroup sets childControlHeight = false
            // (so rows keep a fixed height instead of stretching to fill leftover
            // space), which means preferredHeight above is only used for the
            // layout group's spacing math - it is never applied to the actual
            // RectTransform. Left alone, the row silently kept TextMeshProUGUI's
            // own default RectTransform height (50) instead of 22, so every stat
            // row rendered over twice as tall as its allocated slot and bled into
            // whatever sat below it (confirmed live: CodexBonusPanel's third row
            // rendering directly on top of CurrencyDisplay's Gold row). Set the
            // real height explicitly so it always matches what layout assumes.
            Vector2 sizeDelta = text.rectTransform.sizeDelta;
            text.rectTransform.sizeDelta = new Vector2(sizeDelta.x, 22f);
            return text;
        }

        // ------------------------------------------------------------
        // Action bar - 4 skill slots, bottom-center HUD
        // ------------------------------------------------------------
        private static void BuildActionBar(Transform canvasTransform, WebSocketClient networkClient, VisualSyncProxy syncProxy)
        {
            GameObject barObject = new GameObject("ActionBar", typeof(RectTransform));
            barObject.transform.SetParent(canvasTransform, false);
            RectTransform barRect = (RectTransform)barObject.transform;
            barRect.anchorMin = new Vector2(0.5f, 0f);
            barRect.anchorMax = new Vector2(0.5f, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.anchoredPosition = new Vector2(0f, 20f);
            barRect.sizeDelta = new Vector2(4f * 90f + 3f * 10f, 90f);

            HorizontalLayoutGroup layout = barObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            UiActionBar actionBar = barObject.AddComponent<UiActionBar>();
            actionBar.NetworkClient = networkClient;
            actionBar.SyncProxy = syncProxy;

            (Button b1, Image cd1, Image fl1, TMP_Text mc1) = BuildActionSlot(barObject.transform, "Slot1");
            (Button b2, Image cd2, Image fl2, TMP_Text mc2) = BuildActionSlot(barObject.transform, "Slot2");
            (Button b3, Image cd3, Image fl3, TMP_Text mc3) = BuildActionSlot(barObject.transform, "Slot3");
            (Button b4, Image cd4, Image fl4, TMP_Text mc4) = BuildActionSlot(barObject.transform, "Slot4");

            actionBar.CastButton1 = b1; actionBar.CooldownOverlay1 = cd1; actionBar.FlashOverlay1 = fl1; actionBar.ManaCostText1 = mc1;
            actionBar.CastButton2 = b2; actionBar.CooldownOverlay2 = cd2; actionBar.FlashOverlay2 = fl2; actionBar.ManaCostText2 = mc2;
            actionBar.CastButton3 = b3; actionBar.CooldownOverlay3 = cd3; actionBar.FlashOverlay3 = fl3; actionBar.ManaCostText3 = mc3;
            actionBar.CastButton4 = b4; actionBar.CooldownOverlay4 = cd4; actionBar.FlashOverlay4 = fl4; actionBar.ManaCostText4 = mc4;
        }

        // One skill slot: base Button, a radial-fill cooldown Image overlay,
        // a flash Image overlay (success/fail feedback), and a mana-cost
        // label pinned to the bottom edge.
        private static (Button button, Image cooldown, Image flash, TMP_Text manaCost) BuildActionSlot(Transform parent, string slotName)
        {
            GameObject slotRoot = new GameObject(slotName, typeof(RectTransform));
            slotRoot.transform.SetParent(parent, false);
            RectTransform slotRect = (RectTransform)slotRoot.transform;
            slotRect.sizeDelta = new Vector2(80f, 80f);

            Button button = CreateButton(slotRoot.transform, "CastButton", string.Empty, out TextMeshProUGUI _);
            StretchFull((RectTransform)button.transform);

            GameObject cooldownObject = new GameObject("CooldownOverlay", typeof(RectTransform));
            cooldownObject.transform.SetParent(slotRoot.transform, false);
            StretchFull((RectTransform)cooldownObject.transform);
            Image cooldownImage = cooldownObject.AddComponent<Image>();
            cooldownImage.color = new Color(0f, 0f, 0f, 0.6f);
            cooldownImage.type = Image.Type.Filled;
            cooldownImage.fillMethod = Image.FillMethod.Radial360;
            cooldownImage.fillAmount = 1f;

            GameObject flashObject = new GameObject("FlashOverlay", typeof(RectTransform));
            flashObject.transform.SetParent(slotRoot.transform, false);
            StretchFull((RectTransform)flashObject.transform);
            Image flashImage = flashObject.AddComponent<Image>();
            flashImage.color = new Color(1f, 1f, 1f, 0f);
            flashImage.raycastTarget = false;

            TMP_Text manaCostText = CreateText(slotRoot.transform, "ManaCostText", "0", 14f, TextAlignmentOptions.BottomRight);
            RectTransform manaRect = (RectTransform)manaCostText.transform;
            manaRect.anchorMin = Vector2.zero;
            manaRect.anchorMax = Vector2.one;
            manaRect.offsetMin = new Vector2(0f, 2f);
            manaRect.offsetMax = new Vector2(-4f, 0f);

            return (button, cooldownImage, flashImage, manaCostText);
        }

        // ------------------------------------------------------------
        // Equipment slots panel - top-right HUD corner
        // ------------------------------------------------------------
        private static Image BuildEquipmentSlotsPanel(Transform canvasTransform, VisualSyncProxy syncProxy, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient)
        {
            GameObject panelObject = new GameObject("EquipmentSlotsPanel", typeof(RectTransform));
            panelObject.transform.SetParent(canvasTransform, false);
            RectTransform panelRect = (RectTransform)panelObject.transform;
            panelRect.anchorMin = new Vector2(1f, 1f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 1f);
            // Modul: Full-Game UI Architecture, Part 4. Shifted down from
            // the literal top-right corner (-20,-20) to make room for
            // UiCodexBonusBinder, which hard-codes that exact corner
            // position inside its own Awake() (CodexPanelRect.
            // anchoredPosition = (-20,-20), not something this builder can
            // override) - the two would otherwise overlap now that both
            // panels exist in the same scene for the first time.
            // Modul: Map Hub. Shifted further down to also clear the new
            // persistent top-right CurrencyDisplay (y -120 to -166).
            panelRect.anchoredPosition = new Vector2(-20f, -176f);
            panelRect.sizeDelta = new Vector2(280f, 140f);

            Image panelBackground = panelObject.AddComponent<Image>();
            panelBackground.color = new Color(0f, 0f, 0f, 0.35f);

            VerticalLayoutGroup layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            UiEquipmentSlotsPanel equipmentPanel = panelObject.AddComponent<UiEquipmentSlotsPanel>();
            equipmentPanel.SyncProxy = syncProxy;
            equipmentPanel.InventoryCache = inventoryCache;
            equipmentPanel.NetworkClient = networkClient;

            (TextMeshProUGUI weaponText, Button weaponButton, GameObject weaponEmpty) = BuildEquipmentSlotRow(panelObject.transform, "WeaponSlot", "Weapon: (empty)");
            (TextMeshProUGUI armorText, Button armorButton, GameObject armorEmpty) = BuildEquipmentSlotRow(panelObject.transform, "ArmorSlot", "Armor: (empty)");

            equipmentPanel.WeaponSlotText = weaponText;
            equipmentPanel.UnequipWeaponButton = weaponButton;
            equipmentPanel.WeaponEmptyIndicator = weaponEmpty;
            equipmentPanel.ArmorSlotText = armorText;
            equipmentPanel.UnequipArmorButton = armorButton;
            equipmentPanel.ArmorEmptyIndicator = armorEmpty;

            return panelBackground;
        }

        private static (TextMeshProUGUI slotText, Button unequipButton, GameObject emptyIndicator) BuildEquipmentSlotRow(Transform parent, string rowName, string placeholderText)
        {
            GameObject rowObject = new GameObject(rowName, typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);
            SetFixedLayoutHeight(rowObject, 44f);

            HorizontalLayoutGroup rowLayoutGroup = rowObject.AddComponent<HorizontalLayoutGroup>();
            rowLayoutGroup.spacing = 6f;
            rowLayoutGroup.childControlWidth = true;
            rowLayoutGroup.childForceExpandWidth = false;
            rowLayoutGroup.childControlHeight = true;
            rowLayoutGroup.childForceExpandHeight = true;

            TextMeshProUGUI slotText = CreateText(rowObject.transform, "SlotText", placeholderText, 15f, TextAlignmentOptions.MidlineLeft);
            LayoutElement slotTextLayout = slotText.gameObject.AddComponent<LayoutElement>();
            slotTextLayout.flexibleWidth = 1f;

            Button unequipButton = CreateButton(rowObject.transform, "UnequipButton", "X", out TextMeshProUGUI _);
            LayoutElement unequipLayout = unequipButton.gameObject.AddComponent<LayoutElement>();
            unequipLayout.preferredWidth = 32f;
            unequipButton.gameObject.SetActive(false);

            GameObject emptyIndicator = new GameObject("EmptyIndicator", typeof(RectTransform));
            emptyIndicator.transform.SetParent(rowObject.transform, false);
            LayoutElement emptyLayout = emptyIndicator.AddComponent<LayoutElement>();
            emptyLayout.preferredWidth = 32f;
            Image emptyImage = emptyIndicator.AddComponent<Image>();
            emptyImage.color = new Color(1f, 1f, 1f, 0.15f);

            return (slotText, unequipButton, emptyIndicator);
        }

        // ------------------------------------------------------------
        // Combat arena - centered player/enemy visuals + VFX pool
        // ------------------------------------------------------------
        private static Image BuildCombatArena(Transform canvasTransform, VisualSyncProxy syncProxy)
        {
            GameObject arenaRoot = new GameObject("CombatArena", typeof(RectTransform));
            arenaRoot.transform.SetParent(canvasTransform, false);
            RectTransform arenaRect = (RectTransform)arenaRoot.transform;
            arenaRect.anchorMin = new Vector2(0.5f, 0.5f);
            arenaRect.anchorMax = new Vector2(0.5f, 0.5f);
            arenaRect.pivot = new Vector2(0.5f, 0.5f);
            arenaRect.anchoredPosition = new Vector2(0f, 40f);
            arenaRect.sizeDelta = new Vector2(900f, 400f);

            UiCombatArena combatArena = arenaRoot.AddComponent<UiCombatArena>();
            combatArena.SyncProxy = syncProxy;
            combatArena.ArenaRoot = arenaRoot;

            // Player target - left side.
            (RectTransform playerAnchor, Image playerHealthFill, TMP_Text playerHealthText) = BuildCombatTarget(arenaRoot.transform, "PlayerTarget", new Vector2(0.18f, 0.5f), out GameObject _, out Image playerPortrait);
            combatArena.PlayerAnchor = playerAnchor;
            combatArena.PlayerHealthBarFill = playerHealthFill;
            combatArena.PlayerHealthText = playerHealthText;

            // Enemy target - right side, starts hidden (UiCombatArena.Awake
            // deactivates it and only re-activates while a combat/world-boss
            // audio track is active).
            (RectTransform enemyAnchor, Image enemyHealthFill, TMP_Text enemyHealthText) = BuildCombatTarget(arenaRoot.transform, "EnemyTarget", new Vector2(0.82f, 0.5f), out GameObject enemyVisualRoot, out Image enemyPortrait);
            combatArena.EnemyVisualRoot = enemyVisualRoot;
            combatArena.EnemyAnchor = enemyAnchor;
            combatArena.EnemyHealthBarFill = enemyHealthFill;
            combatArena.EnemyHealthText = enemyHealthText;
            combatArena.EnemyPortraitIcon = enemyPortrait;

            // VFX pool + its two prefabs/containers.
            GameObject damageTextPrefabAsset = BuildAndSaveDamageTextPrefab();
            GameObject projectilePrefabAsset = BuildAndSaveProjectilePrefab();

            GameObject vfxContainerObject = new GameObject("VfxContainers", typeof(RectTransform));
            vfxContainerObject.transform.SetParent(arenaRoot.transform, false);
            StretchFull((RectTransform)vfxContainerObject.transform);

            GameObject damageTextContainer = new GameObject("DamageTextContainer", typeof(RectTransform));
            damageTextContainer.transform.SetParent(vfxContainerObject.transform, false);
            StretchFull((RectTransform)damageTextContainer.transform);

            GameObject projectileContainer = new GameObject("ProjectileContainer", typeof(RectTransform));
            projectileContainer.transform.SetParent(vfxContainerObject.transform, false);
            StretchFull((RectTransform)projectileContainer.transform);

            CombatVfxPool vfxPool = arenaRoot.AddComponent<CombatVfxPool>();
            vfxPool.DamageTextPrefab = damageTextPrefabAsset.GetComponent<UiFloatingDamageText>();
            vfxPool.DamageTextContainer = damageTextContainer.transform;
            vfxPool.ProjectilePrefab = projectilePrefabAsset.GetComponent<UiAttackProjectile>();
            vfxPool.ProjectileContainer = projectileContainer.transform;

            combatArena.VfxPool = vfxPool;

            return playerPortrait;
        }

        private static (RectTransform anchor, Image healthFill, TMP_Text healthText) BuildCombatTarget(Transform parent, string targetName, Vector2 anchorPosition, out GameObject visualRoot, out Image portraitIcon)
        {
            GameObject targetObject = new GameObject(targetName, typeof(RectTransform));
            targetObject.transform.SetParent(parent, false);
            RectTransform targetRect = (RectTransform)targetObject.transform;
            targetRect.anchorMin = anchorPosition;
            targetRect.anchorMax = anchorPosition;
            targetRect.pivot = new Vector2(0.5f, 0.5f);
            targetRect.sizeDelta = new Vector2(160f, 220f);
            targetRect.anchoredPosition = Vector2.zero;

            GameObject portraitObject = new GameObject("Portrait", typeof(RectTransform));
            portraitObject.transform.SetParent(targetObject.transform, false);
            RectTransform portraitRect = (RectTransform)portraitObject.transform;
            portraitRect.anchorMin = new Vector2(0.5f, 1f);
            portraitRect.anchorMax = new Vector2(0.5f, 1f);
            portraitRect.pivot = new Vector2(0.5f, 1f);
            portraitRect.sizeDelta = new Vector2(120f, 120f);
            portraitRect.anchoredPosition = Vector2.zero;
            Image portraitImage = portraitObject.AddComponent<Image>();
            portraitImage.color = new Color(1f, 1f, 1f, 0.9f);

            GameObject healthBarBackground = new GameObject("HealthBarBackground", typeof(RectTransform));
            healthBarBackground.transform.SetParent(targetObject.transform, false);
            RectTransform healthBgRect = (RectTransform)healthBarBackground.transform;
            healthBgRect.anchorMin = new Vector2(0.5f, 0f);
            healthBgRect.anchorMax = new Vector2(0.5f, 0f);
            healthBgRect.pivot = new Vector2(0.5f, 0f);
            healthBgRect.sizeDelta = new Vector2(160f, 20f);
            healthBgRect.anchoredPosition = new Vector2(0f, 30f);
            healthBarBackground.AddComponent<Image>().color = new Color(0.2f, 0f, 0f, 0.8f);

            GameObject healthBarFillObject = new GameObject("HealthBarFill", typeof(RectTransform));
            healthBarFillObject.transform.SetParent(healthBarBackground.transform, false);
            StretchFull((RectTransform)healthBarFillObject.transform);
            Image healthFillImage = healthBarFillObject.AddComponent<Image>();
            healthFillImage.color = new Color(0.2f, 0.85f, 0.2f, 1f);
            healthFillImage.type = Image.Type.Filled;
            healthFillImage.fillMethod = Image.FillMethod.Horizontal;
            healthFillImage.fillAmount = 1f;

            TMP_Text healthText = CreateText(targetObject.transform, "HealthText", "0 / 0", 14f, TextAlignmentOptions.Center);
            RectTransform healthTextRect = (RectTransform)healthText.transform;
            healthTextRect.anchorMin = new Vector2(0.5f, 0f);
            healthTextRect.anchorMax = new Vector2(0.5f, 0f);
            healthTextRect.pivot = new Vector2(0.5f, 0f);
            healthTextRect.sizeDelta = new Vector2(160f, 20f);
            healthTextRect.anchoredPosition = new Vector2(0f, 30f);

            visualRoot = targetObject;
            portraitIcon = portraitImage;
            return ((RectTransform)targetObject.transform, healthFillImage, healthText);
        }

        // Modul: staging-instance-then-SaveAsPrefabAsset-then-DestroyImmediate,
        // matching ChatSceneBuilder.BuildAndSaveRowPrefab's exact pattern.
        private static GameObject BuildAndSaveDamageTextPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiFloatingDamageText", typeof(RectTransform));
            RectTransform rootRect = (RectTransform)root.transform;
            rootRect.sizeDelta = new Vector2(120f, 40f);

            TextMeshProUGUI text = CreateText(root.transform, "Text", "0", 24f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)text.transform);

            UiFloatingDamageText damageText = root.AddComponent<UiFloatingDamageText>();
            damageText.DamageText = text;
            damageText.SelfRectTransform = rootRect;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, DamageTextPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiFloatingDamageText prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveProjectilePrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiAttackProjectile", typeof(RectTransform));
            RectTransform rootRect = (RectTransform)root.transform;
            rootRect.sizeDelta = new Vector2(20f, 20f);
            root.AddComponent<Image>().color = Color.yellow;

            UiAttackProjectile projectile = root.AddComponent<UiAttackProjectile>();
            projectile.SelfRectTransform = rootRect;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, ProjectilePrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiAttackProjectile prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        // ------------------------------------------------------------
        // Guild window - four sub-tabs (Roster, Logistics, Raid, War),
        // switched via UiTabGroup instead of stacking all four vertically -
        // on a portrait phone width there is no room to show a member
        // list, a donation bar, a raid boss bar, and a full war scoreboard
        // at once. Roster also carries Create Guild / Invite Player.
        // GuildManagementEngine.CreateGuildAsync/JoinGuildAsync exist
        // server-side but have no HTTP route or WebSocket CommandType
        // wired to them yet, so those two actions are built as visible,
        // wired-up UI that logs a clear warning instead of dispatching an
        // invented packet - see UiGuildCreatePanel's header comment for
        // the full reasoning. Logistics/Raid/War reuse the pre-existing,
        // already network-wired UiGuildLogisticsPanel/UiGuildRaidPanel/
        // UiGuildWarPanel scripts, which had no scene GameObject anywhere
        // before this pass.
        // ------------------------------------------------------------
        private static GameObject BuildGuildWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient, SfxPoolEngine sfxEngine)
        {
            GameObject windowObject = new GameObject("GuildWindow", typeof(RectTransform));
            windowObject.transform.SetParent(canvasTransform, false);
            RectTransform windowRect = (RectTransform)windowObject.transform;
            // Modul: Map Hub. Fixed-pixel top/bottom insets instead of pure
            // percentage anchors - percentage margins compress along with
            // canvas height on any aspect ratio shorter than the 1080x1920
            // portrait reference, which let this window's own top content
            // (title/sub-tab header) collide with the persistent overlay
            // bars (Menu/Map buttons, Codex Bonus, Gold/Gems currency) and
            // the bottom Season Pass banner. Left/right stay percentage
            // since width scaling is already consistent (CanvasScaler
            // match-width).
            windowRect.anchorMin = new Vector2(0.04f, 0f);
            windowRect.anchorMax = new Vector2(0.96f, 1f);
            windowRect.offsetMin = new Vector2(0f, 70f);
            windowRect.offsetMax = new Vector2(0f, -180f);

            windowObject.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.96f);

            GameObject subTabHeaderObject = new GameObject("SubTabHeader", typeof(RectTransform));
            subTabHeaderObject.transform.SetParent(windowRect, false);
            RectTransform subTabHeaderRect = (RectTransform)subTabHeaderObject.transform;
            subTabHeaderRect.anchorMin = new Vector2(0f, 1f);
            subTabHeaderRect.anchorMax = new Vector2(1f, 1f);
            subTabHeaderRect.pivot = new Vector2(0.5f, 1f);
            subTabHeaderRect.sizeDelta = new Vector2(0f, 44f);
            subTabHeaderRect.anchoredPosition = new Vector2(0f, -12f);

            Button[] subTabButtons = BuildSubTabButtons(subTabHeaderRect, new[] { "Roster", "Chat", "Logistics", "Raid", "War", "Applications" });

            GameObject contentAreaObject = new GameObject("ContentArea", typeof(RectTransform));
            contentAreaObject.transform.SetParent(windowRect, false);
            RectTransform contentAreaRect = (RectTransform)contentAreaObject.transform;
            contentAreaRect.anchorMin = Vector2.zero;
            contentAreaRect.anchorMax = Vector2.one;
            contentAreaRect.offsetMin = new Vector2(20f, 20f);
            contentAreaRect.offsetMax = new Vector2(-20f, -64f);

            GameObject rosterGroup = BuildGuildRosterGroup(contentAreaRect, syncProxy, networkClient);

            // Modul: UI rework. Guild chat gets its own sub-tab here rather
            // than sharing the map hub's world-chat log - the server has
            // always had a real, separate guild channel (ChatEngine.
            // GuildChannelType, routed strictly to the sender's own guild
            // using the server's cached GuildId, never a client-supplied
            // one) that nothing client-side ever sent to or displayed.
            GameObject guildChatGroup = new GameObject("ChatGroup", typeof(RectTransform));
            guildChatGroup.transform.SetParent(contentAreaRect, false);
            StretchFull((RectTransform)guildChatGroup.transform);
            BuildChatPanel(guildChatGroup.transform, "GuildChatPanel", "Guild Chat", ChatChannelType.Guild, networkClient, withMinimizeToggle: false);

            GameObject logisticsGroup = BuildGuildLogisticsGroup(contentAreaRect, syncProxy, networkClient);
            GameObject raidGroup = BuildGuildRaidGroup(contentAreaRect, syncProxy, networkClient, sfxEngine);
            GameObject warGroup = BuildGuildWarGroup(contentAreaRect, syncProxy, networkClient);
            GameObject applicationsGroup = BuildGuildApplicationsGroup(contentAreaRect);

            guildChatGroup.SetActive(false);
            logisticsGroup.SetActive(false);
            raidGroup.SetActive(false);
            warGroup.SetActive(false);
            applicationsGroup.SetActive(false);

            UiTabGroup tabGroup = windowObject.AddComponent<UiTabGroup>();
            tabGroup.Groups = new[] { rosterGroup, guildChatGroup, logisticsGroup, raidGroup, warGroup, applicationsGroup };
            tabGroup.Buttons = subTabButtons;

            return windowObject;
        }

        // Modul: Play Mode audit fix. JoinGuildAsync has always filed a
        // GuildApplication row for Application-Required guilds, but
        // nothing anywhere ever reviewed one - see
        // UiGuildApplicationsPanel's own header comment. Leader-only in
        // practice (the backing endpoint returns an empty list for
        // non-Leaders), so this tab is harmlessly empty for most players.
        private static GameObject BuildGuildApplicationsGroup(Transform parent)
        {
            GameObject groupObject = new GameObject("ApplicationsGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            TextMeshProUGUI headerText = CreateText(groupObject.transform, "HeaderText", "Pending Applications", 22f, TextAlignmentOptions.Center);
            RectTransform headerRect = (RectTransform)headerText.transform;
            headerRect.anchorMin = new Vector2(0f, 1f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.pivot = new Vector2(0.5f, 1f);
            headerRect.sizeDelta = new Vector2(0f, 36f);
            headerRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI statusText = CreateText(groupObject.transform, "StatusText", string.Empty, 14f, TextAlignmentOptions.Center);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = new Vector2(0f, 24f);
            statusRect.anchoredPosition = new Vector2(0f, -36f);

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(groupObject.transform, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = Vector2.zero;
            scrollAreaRect.offsetMax = new Vector2(0f, -64f);

            (ScrollRect _, RectTransform applicationsContent) = ChatSceneBuilder.BuildScrollView(scrollAreaRect);

            GameObject applicationRowPrefabAsset = BuildAndSaveGuildApplicationRowPrefab();

            UiGuildApplicationsPanel panel = groupObject.AddComponent<UiGuildApplicationsPanel>();
            panel.RowContainer = applicationsContent;
            panel.RowPrefab = applicationRowPrefabAsset.GetComponent<UiGuildApplicationEntryRow>();
            panel.HeaderText = headerText;
            panel.StatusText = statusText;

            return groupObject;
        }

        // Roster list (real, network-backed UiGuildRosterPanel) plus
        // Create Guild / Invite Player controls stacked underneath.
        private static GameObject BuildGuildRosterGroup(Transform parent, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject rosterAreaObject = new GameObject("RosterGroup", typeof(RectTransform));
            rosterAreaObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)rosterAreaObject.transform);

            GameObject rosterListAreaObject = new GameObject("RosterListArea", typeof(RectTransform));
            rosterListAreaObject.transform.SetParent(rosterAreaObject.transform, false);
            RectTransform rosterListAreaRect = (RectTransform)rosterListAreaObject.transform;
            rosterListAreaRect.anchorMin = new Vector2(0f, 0.34f);
            rosterListAreaRect.anchorMax = new Vector2(1f, 1f);
            rosterListAreaRect.offsetMin = Vector2.zero;
            rosterListAreaRect.offsetMax = Vector2.zero;

            TextMeshProUGUI headerText = CreateText(rosterListAreaRect, "HeaderText", "Guild", 22f, TextAlignmentOptions.Center);
            RectTransform headerRect = (RectTransform)headerText.transform;
            headerRect.anchorMin = new Vector2(0f, 1f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.pivot = new Vector2(0.5f, 1f);
            headerRect.sizeDelta = new Vector2(0f, 36f);
            headerRect.anchoredPosition = Vector2.zero;

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(rosterListAreaRect, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = Vector2.zero;
            scrollAreaRect.offsetMax = new Vector2(0f, -40f);

            (ScrollRect _, RectTransform rosterContent) = ChatSceneBuilder.BuildScrollView(scrollAreaRect);

            GameObject rosterRowPrefabAsset = BuildAndSaveGuildRosterRowPrefab();

            UiGuildRosterPanel rosterPanel = rosterListAreaObject.AddComponent<UiGuildRosterPanel>();
            rosterPanel.SyncProxy = syncProxy;
            rosterPanel.RowContainer = rosterContent;
            rosterPanel.RowPrefab = rosterRowPrefabAsset.GetComponent<UiGuildRosterEntryRow>();
            rosterPanel.HeaderText = headerText;

            // Create Guild / Invite Player controls - lower ~34%.
            GameObject actionsAreaObject = new GameObject("GuildActionsPanel", typeof(RectTransform));
            actionsAreaObject.transform.SetParent(rosterAreaObject.transform, false);
            RectTransform actionsAreaRect = (RectTransform)actionsAreaObject.transform;
            actionsAreaRect.anchorMin = new Vector2(0f, 0f);
            actionsAreaRect.anchorMax = new Vector2(1f, 0.34f);
            actionsAreaRect.offsetMin = new Vector2(0f, 0f);
            actionsAreaRect.offsetMax = new Vector2(0f, -20f);

            VerticalLayoutGroup actionsLayout = actionsAreaObject.AddComponent<VerticalLayoutGroup>();
            actionsLayout.spacing = 10f;
            actionsLayout.childControlWidth = true;
            actionsLayout.childForceExpandWidth = true;
            actionsLayout.childControlHeight = false;
            actionsLayout.childForceExpandHeight = false;

            UiGuildCreatePanel createPanel = actionsAreaObject.AddComponent<UiGuildCreatePanel>();

            (TMP_InputField createInput, Button createButton) = BuildLabeledInputRow(actionsAreaObject.transform, "CreateGuildRow", "Guild Name", "Create Guild");
            createPanel.CreateGuildNameInputField = createInput;
            createPanel.CreateGuildButton = createButton;

            // Modul: UI audit follow-up. Renamed from "Invite Player" -
            // JoinGuildAsync is a self-service join-by-name (no player-to-
            // player invite mechanism exists server-side), so this field
            // takes a guild name, not a player name. See UiGuildCreatePanel's
            // header comment.
            (TMP_InputField inviteInput, Button inviteButton) = BuildLabeledInputRow(actionsAreaObject.transform, "JoinGuildRow", "Guild Name", "Join Guild");
            createPanel.InvitePlayerInputField = inviteInput;
            createPanel.InvitePlayerButton = inviteButton;

            TextMeshProUGUI guildActionsStatusText = CreateText(actionsAreaObject.transform, "GuildActionsStatusText", string.Empty, 14f, TextAlignmentOptions.Center);
            SetFixedLayoutHeight(guildActionsStatusText.gameObject, 24f);
            createPanel.StatusText = guildActionsStatusText;

            return rosterAreaObject;
        }

        // Guild Logistics Depot donation panel - real, network-wired
        // UiGuildLogisticsPanel (CommandType.DepositGuildMaterial).
        private static GameObject BuildGuildLogisticsGroup(Transform parent, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject groupObject = new GameObject("LogisticsGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            VerticalLayoutGroup layout = groupObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            CreateGroupSectionLabel(groupObject.transform, "SUPPLY DEPOT");
            CreateHelpText(groupObject.transform, "DepotHelpText",
                "The depot is the guild-wide material pool. Every deposit fills the bar toward the next depot level, and each level raises the passive production bonus every member receives. Progress is shared, so partial deposits from many members still count.",
                58f);

            TextMeshProUGUI levelText = CreateStatRow(groupObject.transform, "Lv. 0");
            TextMeshProUGUI contributionText = CreateStatRow(groupObject.transform, "0 / 0");

            (GameObject barBackground, RectTransform barFill) = BuildAnchoredProgressBar(groupObject.transform, new Color(0.3f, 0.7f, 1f, 1f));
            SetFixedLayoutHeight(barBackground, 24f);

            Button donateButton = CreateButton(groupObject.transform, "DonateButton", "Deposit 10 Copper Ore", out TextMeshProUGUI _);
            SetFixedLayoutHeight(donateButton.gameObject, 44f);

            // The panel's TargetMaterialId/DonateQuantity are fixed
            // designer constants (1 = copper_ore, 10 per tap - see
            // UiGuildLogisticsPanel's own header comment on why they are not
            // resolved from inventory), so the button can state the real cost
            // outright instead of just saying "Donate".
            CreateHelpText(groupObject.transform, "DepotCostHelpText",
                "Cost: 10 Copper Ore per deposit, taken from your backpack then your stash. The server checks the balance, so a deposit you cannot afford simply does nothing.",
                40f);

            // Modul: Play Mode audit fix. Monolith contribution (drives
            // GuildRecords Mining/Woodcutting Monolith levels) and Treasury
            // gold contribution (drives GuildRecords.CurrentTier +
            // GuildMembers.ContributionPoints) both used to share
            // CommandType.ContributeToGuild with the Depot deposit above
            // in a shadowed if/else chain server-side and had no UI at all
            // - see UiGuildLogisticsPanel's own header comments.
            CreateGroupSectionLabel(groupObject.transform, "MONOLITHS");
            CreateHelpText(groupObject.transform, "MonolithHelpText",
                "Monoliths are permanent guild upgrades: the Mining Monolith raises ore yield and the Woodcutting Monolith raises log yield for every member. Material ids are 1 Copper Ore, 2 Raw Log, 3 Iron Ore, 4 Oak Log, 5 Gold Ore, 6 Magic Log.",
                58f);

            TextMeshProUGUI monolithLevelsText = CreateStatRow(groupObject.transform, "Mining Lv 0  Woodcutting Lv 0");

            GameObject monolithRowObject = new GameObject("MonolithContributionRow", typeof(RectTransform));
            monolithRowObject.transform.SetParent(groupObject.transform, false);
            SetFixedLayoutHeight(monolithRowObject, 44f);

            HorizontalLayoutGroup monolithRowLayoutGroup = monolithRowObject.AddComponent<HorizontalLayoutGroup>();
            monolithRowLayoutGroup.spacing = 6f;
            monolithRowLayoutGroup.childControlWidth = true;
            monolithRowLayoutGroup.childForceExpandWidth = false;
            monolithRowLayoutGroup.childControlHeight = true;
            monolithRowLayoutGroup.childForceExpandHeight = true;

            TMP_InputField monolithMaterialIdField = CreateInputField(monolithRowObject.transform, "MonolithMaterialIdField", "Item#");
            LayoutElement monolithMaterialIdLayout = monolithMaterialIdField.gameObject.AddComponent<LayoutElement>();
            monolithMaterialIdLayout.flexibleWidth = 1f;

            TMP_InputField monolithQuantityField = CreateInputField(monolithRowObject.transform, "MonolithQuantityField", "Qty");
            LayoutElement monolithQuantityLayout = monolithQuantityField.gameObject.AddComponent<LayoutElement>();
            monolithQuantityLayout.flexibleWidth = 1f;

            Button contributeMonolithButton = CreateButton(monolithRowObject.transform, "ContributeMonolithButton", "Contribute", out TextMeshProUGUI _);
            LayoutElement contributeMonolithButtonLayout = contributeMonolithButton.gameObject.AddComponent<LayoutElement>();
            contributeMonolithButtonLayout.preferredWidth = 140f;

            GameObject treasuryRowObject = new GameObject("TreasuryContributionRow", typeof(RectTransform));
            treasuryRowObject.transform.SetParent(groupObject.transform, false);
            SetFixedLayoutHeight(treasuryRowObject, 44f);

            HorizontalLayoutGroup treasuryRowLayoutGroup = treasuryRowObject.AddComponent<HorizontalLayoutGroup>();
            treasuryRowLayoutGroup.spacing = 6f;
            treasuryRowLayoutGroup.childControlWidth = true;
            treasuryRowLayoutGroup.childForceExpandWidth = false;
            treasuryRowLayoutGroup.childControlHeight = true;
            treasuryRowLayoutGroup.childForceExpandHeight = true;

            TMP_InputField treasuryGoldAmountField = CreateInputField(treasuryRowObject.transform, "TreasuryGoldAmountField", "Gold");
            LayoutElement treasuryGoldAmountLayout = treasuryGoldAmountField.gameObject.AddComponent<LayoutElement>();
            treasuryGoldAmountLayout.flexibleWidth = 1f;

            Button donateGoldButton = CreateButton(treasuryRowObject.transform, "DonateGoldButton", "Donate to Treasury", out TextMeshProUGUI _);
            LayoutElement donateGoldButtonLayout = donateGoldButton.gameObject.AddComponent<LayoutElement>();
            donateGoldButtonLayout.preferredWidth = 180f;

            UiGuildLogisticsPanel panel = groupObject.AddComponent<UiGuildLogisticsPanel>();
            panel.SyncProxy = syncProxy;
            panel.NetworkClient = networkClient;
            panel.LogisticsLevelText = levelText;
            panel.ContributionText = contributionText;
            panel.ProgressBarFill = barFill;
            panel.DonateButton = donateButton;
            panel.MonolithLevelsText = monolithLevelsText;
            panel.MonolithMaterialIdField = monolithMaterialIdField;
            panel.MonolithQuantityField = monolithQuantityField;
            panel.ContributeMonolithButton = contributeMonolithButton;
            panel.TreasuryGoldAmountField = treasuryGoldAmountField;
            panel.DonateGoldButton = donateGoldButton;

            return groupObject;
        }

        // Guild Raid boss panel - real, network-wired UiGuildRaidPanel
        // (CommandType.LaunchGuildRaid).
        private static GameObject BuildGuildRaidGroup(Transform parent, VisualSyncProxy syncProxy, WebSocketClient networkClient, SfxPoolEngine sfxEngine)
        {
            GameObject groupObject = new GameObject("RaidGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            VerticalLayoutGroup layout = groupObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            CreateGroupSectionLabel(groupObject.transform, "GUILD RAID BOSS");
            CreateHelpText(groupObject.transform, "RaidMechanicsHelpText",
                "A raid boss is fought by the whole guild at once. Once launched it takes damage automatically on a five second server tick - nobody has to click. The fight resolves entirely server-side, so members who are offline still contribute their share.",
                58f);

            TextMeshProUGUI tierText = CreateStatRow(groupObject.transform, "Tier 0");
            TextMeshProUGUI hpText = CreateStatRow(groupObject.transform, "0 / 0");

            (GameObject barBackground, RectTransform barFill) = BuildAnchoredProgressBar(groupObject.transform, new Color(0.85f, 0.2f, 0.2f, 1f));
            SetFixedLayoutHeight(barBackground, 24f);

            CreateGroupSectionLabel(groupObject.transform, "SCALING AND REWARDS");
            CreateHelpText(groupObject.transform, "RaidScalingHelpText",
                "Boss health is 1,000,000 multiplied by the raid tier, so each tier is a strictly harder fight than the last. Guild damage per tick is 10 per member level summed across the roster, which means recruiting and levelling members is the only way to raise raid DPS. Victory awards 100 guild contribution points.",
                72f);

            Button launchButton = CreateButton(groupObject.transform, "LaunchRaidButton", "Launch Raid", out TextMeshProUGUI _);
            ((Image)launchButton.targetGraphic).color = new Color(0.62f, 0.24f, 0.20f, 1f);
            SetFixedLayoutHeight(launchButton.gameObject, 44f);

            CreateHelpText(groupObject.transform, "RaidEntryHelpText",
                "Entry requirements: you must be in a guild, and only one raid can be active per guild at a time. Launching again while a boss is already up does nothing - finish or lose the current one first.",
                48f);

            UiGuildRaidPanel panel = groupObject.AddComponent<UiGuildRaidPanel>();
            panel.SyncProxy = syncProxy;
            panel.NetworkClient = networkClient;
            panel.SfxEngine = sfxEngine;
            panel.RaidTierText = tierText;
            panel.BossHpText = hpText;
            panel.HpBarFill = barFill;
            panel.LaunchRaidButton = launchButton;

            return groupObject;
        }

        // Guild War scoreboard - real, network-wired UiGuildWarPanel. Was
        // read-only until the Play Mode audit found RegisterGuildDefense
        // had a working zero-alloc sender with no button anywhere ever
        // calling it - see UiGuildWarPanel's DefendButton doc comment.
        private static GameObject BuildGuildWarGroup(Transform parent, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject groupObject = new GameObject("WarGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            VerticalLayoutGroup layout = groupObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            CreateGroupSectionLabel(groupObject.transform, "GUILD WAR");
            CreateHelpText(groupObject.transform, "WarScoringHelpText",
                "A war is scored across three independent point tracks, and the guild with the higher combined total wins. Every track is earned by normal play - there is no separate war activity to grind.",
                48f);
            CreateHelpText(groupObject.transform, "WarVanguardHelpText",
                "Vanguard: 10 points per monster your members kill, and 500 for a regional boss. This is the track that rewards simply staying in combat.",
                40f);
            CreateHelpText(groupObject.transform, "WarLogisticsHelpText",
                "Logistics: 50 points per region tier for each crafted item of region tier 5 or higher. Only endgame crafts score, so this track rewards a guild with deep crafting progression.",
                48f);
            CreateHelpText(groupObject.transform, "WarSupplyHelpText",
                "Supply: 100 points per 1,000 units of material burned through the contribution box below. Materials are consumed permanently, so this track converts stockpiles directly into score.",
                48f);

            TextMeshProUGUI statusText = CreateStatRow(groupObject.transform, "War Status");

            // Modul: Guild War scoreboard sync. An explicit banner rather
            // than an empty region - with no war running the panel used to
            // show only a bare countdown line above a hidden scoreboard,
            // which read as a screen that had failed to load.
            GameObject noActiveWarRoot = new GameObject("NoActiveWarRoot", typeof(RectTransform));
            noActiveWarRoot.transform.SetParent(groupObject.transform, false);
            SetFixedLayoutHeight(noActiveWarRoot, 96f);
            noActiveWarRoot.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            TextMeshProUGUI noWarBannerText = CreateText(noActiveWarRoot.transform, "NoActiveWarBannerText", "No Active Guild War", 20f, TextAlignmentOptions.Center);
            RectTransform noWarBannerRect = (RectTransform)noWarBannerText.transform;
            noWarBannerRect.anchorMin = new Vector2(0f, 1f);
            noWarBannerRect.anchorMax = new Vector2(1f, 1f);
            noWarBannerRect.pivot = new Vector2(0.5f, 1f);
            noWarBannerRect.sizeDelta = new Vector2(0f, 30f);
            noWarBannerRect.anchoredPosition = new Vector2(0f, -8f);

            TextMeshProUGUI noWarHelpText = CreateText(noActiveWarRoot.transform, "NoActiveWarHelpText", "Matchmaking runs weekly. Your guild is paired automatically - there is nothing to queue for.", 12f, TextAlignmentOptions.Center);
            noWarHelpText.color = new Color(1f, 1f, 1f, 0.55f);
            RectTransform noWarHelpRect = (RectTransform)noWarHelpText.transform;
            noWarHelpRect.anchorMin = new Vector2(0f, 1f);
            noWarHelpRect.anchorMax = new Vector2(1f, 1f);
            noWarHelpRect.pivot = new Vector2(0.5f, 1f);
            noWarHelpRect.sizeDelta = new Vector2(-16f, 32f);
            noWarHelpRect.anchoredPosition = new Vector2(0f, -38f);

            TextMeshProUGUI countdownText = CreateText(noActiveWarRoot.transform, "MatchmakingCountdownText", string.Empty, 14f, TextAlignmentOptions.Center);
            RectTransform countdownRect = (RectTransform)countdownText.transform;
            countdownRect.anchorMin = new Vector2(0f, 0f);
            countdownRect.anchorMax = new Vector2(1f, 0f);
            countdownRect.pivot = new Vector2(0.5f, 0f);
            countdownRect.sizeDelta = new Vector2(0f, 24f);
            countdownRect.anchoredPosition = new Vector2(0f, 6f);

            GameObject activeWarRoot = new GameObject("ActiveWarRoot", typeof(RectTransform));
            activeWarRoot.transform.SetParent(groupObject.transform, false);
            // Sized for the scoreboard, the freshness note and the supply-cost
            // explanation below; a VerticalLayoutGroup slot that is too short
            // silently clips its own tail rows.
            SetFixedLayoutHeight(activeWarRoot, 488f);

            VerticalLayoutGroup activeWarLayoutGroup = activeWarRoot.AddComponent<VerticalLayoutGroup>();
            activeWarLayoutGroup.spacing = 4f;
            activeWarLayoutGroup.childControlWidth = true;
            activeWarLayoutGroup.childForceExpandWidth = true;
            activeWarLayoutGroup.childControlHeight = false;
            activeWarLayoutGroup.childForceExpandHeight = false;

            TextMeshProUGUI activeMatchText = CreateStatRow(activeWarRoot.transform, "Match 0");
            TextMeshProUGUI turnCounterText = CreateStatRow(activeWarRoot.transform, "Turn 0");
            TextMeshProUGUI lastDamageText = CreateStatRow(activeWarRoot.transform, "Last Damage 0");
            TextMeshProUGUI combatPointsText = CreateStatRow(activeWarRoot.transform, "Vanguard: 0");
            TextMeshProUGUI logisticsPointsText = CreateStatRow(activeWarRoot.transform, "Logistics: 0");
            TextMeshProUGUI supplyPointsText = CreateStatRow(activeWarRoot.transform, "Supply: 0");
            TextMeshProUGUI enemyCombatPointsText = CreateStatRow(activeWarRoot.transform, "Enemy Vanguard: 0");
            TextMeshProUGUI enemyLogisticsPointsText = CreateStatRow(activeWarRoot.transform, "Enemy Logistics: 0");
            TextMeshProUGUI enemySupplyPointsText = CreateStatRow(activeWarRoot.transform, "Enemy Supply: 0");
            TextMeshProUGUI multiplierText = CreateStatRow(activeWarRoot.transform, "x100");

            // Modul: Guild War scoreboard sync. The caveat that used to sit
            // here ("live totals are not pushed to clients yet") is gone
            // because that is no longer true - GuildWarEngine.RunScoreboardSyncLoopAsync
            // now pushes real GuildWarMatches totals into every online
            // member's payload every five seconds.
            CreateHelpText(activeWarRoot.transform, "WarScoreboardFreshnessText",
                "Scoreboard totals refresh every five seconds from the authoritative match record.",
                28f);

            Button defendButton = CreateButton(activeWarRoot.transform, "DefendButton", "Defend", out TextMeshProUGUI _);
            SetFixedLayoutHeight(defendButton.gameObject, 46f);

            Button attackButton = CreateButton(activeWarRoot.transform, "AttackButton", "Attack", out TextMeshProUGUI _);
            SetFixedLayoutHeight(attackButton.gameObject, 46f);

            GameObject contributeRowObject = new GameObject("ContributeSupplyRow", typeof(RectTransform));
            contributeRowObject.transform.SetParent(activeWarRoot.transform, false);
            SetFixedLayoutHeight(contributeRowObject, 44f);

            HorizontalLayoutGroup contributeRowLayoutGroup = contributeRowObject.AddComponent<HorizontalLayoutGroup>();
            contributeRowLayoutGroup.spacing = 6f;
            contributeRowLayoutGroup.childControlWidth = true;
            contributeRowLayoutGroup.childForceExpandWidth = false;
            contributeRowLayoutGroup.childControlHeight = true;
            contributeRowLayoutGroup.childForceExpandHeight = true;

            CreateHelpText(activeWarRoot.transform, "WarSupplyCostHelpText",
                "Material ids are 1 Copper Ore, 2 Raw Log, 3 Iron Ore, 4 Oak Log, 5 Gold Ore, 6 Magic Log. Quantities below 1,000 are consumed but score no points, so contribute in full thousands.",
                44f);

            TMP_InputField contributeCommodityIdField = CreateInputField(contributeRowObject.transform, "ContributeCommodityIdField", "Item#");
            LayoutElement contributeCommodityIdLayout = contributeCommodityIdField.gameObject.AddComponent<LayoutElement>();
            contributeCommodityIdLayout.flexibleWidth = 1f;

            TMP_InputField contributeQuantityField = CreateInputField(contributeRowObject.transform, "ContributeQuantityField", "Qty");
            LayoutElement contributeQuantityLayout = contributeQuantityField.gameObject.AddComponent<LayoutElement>();
            contributeQuantityLayout.flexibleWidth = 1f;

            Button contributeSupplyButton = CreateButton(contributeRowObject.transform, "ContributeSupplyButton", "Contribute Supply", out TextMeshProUGUI _);
            LayoutElement contributeSupplyButtonLayout = contributeSupplyButton.gameObject.AddComponent<LayoutElement>();
            contributeSupplyButtonLayout.preferredWidth = 160f;

            UiGuildWarPanel panel = groupObject.AddComponent<UiGuildWarPanel>();
            panel.SyncProxy = syncProxy;
            panel.NetworkClient = networkClient;
            panel.DefendButton = defendButton;
            panel.AttackButton = attackButton;
            panel.WarStatusText = statusText;
            panel.NoActiveWarRoot = noActiveWarRoot;
            panel.ActiveWarRoot = activeWarRoot;
            panel.ActiveMatchText = activeMatchText;
            panel.TurnCounterText = turnCounterText;
            panel.LastDamageDeltaText = lastDamageText;
            panel.CombatVanguardPointsText = combatPointsText;
            panel.ProductionLogisticsPointsText = logisticsPointsText;
            panel.GatheringSupplyChainPointsText = supplyPointsText;
            panel.EnemyCombatVanguardPointsText = enemyCombatPointsText;
            panel.EnemyProductionLogisticsPointsText = enemyLogisticsPointsText;
            panel.EnemyGatheringSupplyChainPointsText = enemySupplyPointsText;
            panel.WarMultiplierText = multiplierText;
            panel.MatchmakingCountdownText = countdownText;
            panel.ContributeCommodityIdField = contributeCommodityIdField;
            panel.ContributeQuantityField = contributeQuantityField;
            panel.ContributeSupplyButton = contributeSupplyButton;

            return groupObject;
        }

        // A horizontal row of N equal-width tab buttons filling the given
        // RectTransform - shared by every UiTabGroup instance in this file
        // (Guild's four sub-tabs, Market & Bank's two).
        // Modul: Guild sub-tab polish. A wrapped explanatory paragraph sized
        // for a VerticalLayoutGroup slot, matching the description lines
        // added to the Village rows and the World Boss panel. Non-interactive
        // and deliberately dim, so it reads as guidance rather than as data.
        // Modul: Guild sub-tab polish. The trap CreateStatRow documents,
        // factored out. Every guild group runs a VerticalLayoutGroup with
        // childControlHeight = false, which ignores LayoutElement.preferredHeight
        // entirely - so a button or bar created here kept its own default
        // RectTransform size and rendered enormously taller than the slot the
        // layout maths had reserved for it (a 44px Donate button drawing ~100px
        // tall, overlapping the row beneath). Setting both keeps the declared
        // height and the real height in agreement.
        private static LayoutElement SetFixedLayoutHeight(GameObject target, float height)
        {
            LayoutElement layoutElement = target.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = target.AddComponent<LayoutElement>();
            }
            layoutElement.preferredHeight = height;
            layoutElement.minHeight = height;

            // Modul: layout-trap sweep. Pinning a preferred height is not
            // enough to pin a height. If the target also carries a layout group
            // of its own with childForceExpandHeight = true - which any row
            // built as a Horizontal/VerticalLayoutGroup does, so its own
            // children fill it - that group reports flexibleHeight = 1, and an
            // UNSET LayoutElement.flexibleHeight (-1) means "no opinion" rather
            // than "zero", so it does not override. Unity then hands the row
            // every spare pixel in the parent.
            //
            // The Larder screen's three slot rows were laid out at 368px each
            // instead of 44: the parent's leftover 1106px split three ways.
            // Stating zero explicitly is what "fixed" has to mean, and it is
            // correct for every existing caller by definition of this helper.
            layoutElement.flexibleHeight = 0f;

            RectTransform rect = (RectTransform)target.transform;
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);
            return layoutElement;
        }

        private static TextMeshProUGUI CreateHelpText(Transform parent, string objectName, string body, float preferredHeight)
        {
            TextMeshProUGUI text = CreateText(parent, objectName, body, 12f, TextAlignmentOptions.TopLeft);
            text.color = new Color(1f, 1f, 1f, 0.55f);
            text.raycastTarget = false;
            SetFixedLayoutHeight(text.gameObject, preferredHeight);
            return text;
        }

        // Modul: Guild sub-tab polish. A gold section label inside one of the
        // guild groups, same visual language as the hamburger menu sections
        // and the Combat screen's roster header.
        private static TextMeshProUGUI CreateGroupSectionLabel(Transform parent, string title)
        {
            TextMeshProUGUI text = CreateText(parent, "Section_" + title, title, 13f, TextAlignmentOptions.MidlineLeft);
            text.color = new Color(0.85f, 0.72f, 0.45f, 1f);
            text.characterSpacing = 6f;
            text.raycastTarget = false;
            SetFixedLayoutHeight(text.gameObject, 24f);
            return text;
        }

        private static Button[] BuildSubTabButtons(RectTransform areaRect, string[] labels)
        {
            HorizontalLayoutGroup layout = areaRect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = true;

            Button[] buttons = new Button[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                buttons[i] = CreateButton(areaRect, "TabButton_" + labels[i], labels[i], out TextMeshProUGUI _);
            }
            return buttons;
        }

        // A background bar plus a left-anchored fill child whose
        // anchorMax.x a panel drives directly at runtime (0 = empty, 1 =
        // full) - matches UiGuildLogisticsPanel.ProgressBarFill/
        // UiGuildRaidPanel.HpBarFill's exact existing read pattern
        // (`RectTransform.anchorMax.x`), not an Image.fillAmount radial/
        // horizontal fill.
        private static (GameObject background, RectTransform fill) BuildAnchoredProgressBar(Transform parent, Color fillColor)
        {
            GameObject barBackground = new GameObject("ProgressBarBackground", typeof(RectTransform));
            barBackground.transform.SetParent(parent, false);
            barBackground.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            GameObject fillObject = new GameObject("ProgressBarFill", typeof(RectTransform));
            fillObject.transform.SetParent(barBackground.transform, false);
            RectTransform fillRect = (RectTransform)fillObject.transform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            fillObject.AddComponent<Image>().color = fillColor;

            return (barBackground, fillRect);
        }

        private static (TMP_InputField input, Button button) BuildLabeledInputRow(Transform parent, string rowName, string placeholder, string buttonLabel)
        {
            GameObject rowObject = new GameObject(rowName, typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);
            SetFixedLayoutHeight(rowObject, 44f);

            HorizontalLayoutGroup rowLayoutGroup = rowObject.AddComponent<HorizontalLayoutGroup>();
            rowLayoutGroup.spacing = 10f;
            rowLayoutGroup.childControlWidth = true;
            rowLayoutGroup.childForceExpandWidth = false;
            rowLayoutGroup.childControlHeight = true;
            rowLayoutGroup.childForceExpandHeight = true;

            TMP_InputField input = CreateInputField(rowObject.transform, "InputField", placeholder);
            LayoutElement inputLayout = input.gameObject.AddComponent<LayoutElement>();
            inputLayout.flexibleWidth = 1f;

            Button button = CreateButton(rowObject.transform, "ActionButton", buttonLabel, out TextMeshProUGUI _);
            LayoutElement buttonLayout = button.gameObject.AddComponent<LayoutElement>();
            buttonLayout.preferredWidth = 160f;

            return (input, button);
        }

        // ------------------------------------------------------------
        // Market & Bank window - two sub-tabs (Market, Bank) switched via
        // UiTabGroup instead of a side-by-side split. A left/right 50-50
        // split made sense at a 1920-wide landscape reference but leaves
        // barely 500px per side at a 1080-wide portrait reference -
        // nowhere near enough room for Market's own internal Buy/Sell
        // split plus a filter row and pagination. Each tab now gets the
        // full window width and height. Market reuses the real, wired
        // UiMarketBrowserWindow (buy) plus UiMarketSellPanel (sell,
        // dispatching the real CommandType.MarketListItem). Bank reuses
        // the real, wired UiBankVaultWindow - this codebase's bank is an
        // equipment vault, not a raw-gold vault (there is no gold-deposit
        // feature anywhere server-side), so "vault balance tracker" is the
        // vault's item list, matching what actually exists.
        // ------------------------------------------------------------
        private static GameObject BuildMarketBankWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient, EquipmentInventoryCache inventoryCache)
        {
            GameObject windowObject = new GameObject("MarketBankWindow", typeof(RectTransform));
            windowObject.transform.SetParent(canvasTransform, false);
            RectTransform windowRect = (RectTransform)windowObject.transform;
            // Modul: Map Hub. Fixed-pixel top/bottom insets instead of pure
            // percentage anchors - percentage margins compress along with
            // canvas height on any aspect ratio shorter than the 1080x1920
            // portrait reference, which let this window's own top content
            // (title/sub-tab header) collide with the persistent overlay
            // bars (Menu/Map buttons, Codex Bonus, Gold/Gems currency) and
            // the bottom Season Pass banner. Left/right stay percentage
            // since width scaling is already consistent (CanvasScaler
            // match-width).
            windowRect.anchorMin = new Vector2(0.04f, 0f);
            windowRect.anchorMax = new Vector2(0.96f, 1f);
            windowRect.offsetMin = new Vector2(0f, 70f);
            windowRect.offsetMax = new Vector2(0f, -180f);

            windowObject.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.96f);

            GameObject subTabHeaderObject = new GameObject("SubTabHeader", typeof(RectTransform));
            subTabHeaderObject.transform.SetParent(windowRect, false);
            RectTransform subTabHeaderRect = (RectTransform)subTabHeaderObject.transform;
            subTabHeaderRect.anchorMin = new Vector2(0f, 1f);
            subTabHeaderRect.anchorMax = new Vector2(1f, 1f);
            subTabHeaderRect.pivot = new Vector2(0.5f, 1f);
            subTabHeaderRect.sizeDelta = new Vector2(0f, 44f);
            subTabHeaderRect.anchoredPosition = new Vector2(0f, -12f);

            Button[] subTabButtons = BuildSubTabButtons(subTabHeaderRect, new[] { "Market", "Bank" });

            GameObject marketSide = new GameObject("MarketSide", typeof(RectTransform));
            marketSide.transform.SetParent(windowRect, false);
            RectTransform marketSideRect = (RectTransform)marketSide.transform;
            marketSideRect.anchorMin = Vector2.zero;
            marketSideRect.anchorMax = Vector2.one;
            marketSideRect.offsetMin = new Vector2(16f, 16f);
            marketSideRect.offsetMax = new Vector2(-16f, -64f);

            GameObject bankSide = new GameObject("BankSide", typeof(RectTransform));
            bankSide.transform.SetParent(windowRect, false);
            RectTransform bankSideRect = (RectTransform)bankSide.transform;
            bankSideRect.anchorMin = Vector2.zero;
            bankSideRect.anchorMax = Vector2.one;
            bankSideRect.offsetMin = new Vector2(16f, 16f);
            bankSideRect.offsetMax = new Vector2(-16f, -64f);

            BuildMarketSide(marketSideRect, networkClient, syncProxy, inventoryCache);
            BuildBankSide(bankSideRect, syncProxy, inventoryCache, networkClient);

            bankSide.SetActive(false);

            UiTabGroup tabGroup = windowObject.AddComponent<UiTabGroup>();
            tabGroup.Groups = new[] { marketSide, bankSide };
            tabGroup.Buttons = subTabButtons;

            return windowObject;
        }

        // Buy (top half - real UiMarketBrowserWindow) + Sell (bottom half
        // - new UiMarketSellPanel) plus a live gold/tax preview strip
        // (UiMarketDataBinder) pinned under the title.
        private static void BuildMarketSide(RectTransform parent, WebSocketClient networkClient, VisualSyncProxy syncProxy, EquipmentInventoryCache inventoryCache)
        {
            TMP_Text titleText = CreateText(parent, "MarketTitleText", "Market", 20f, TextAlignmentOptions.Center);
            RectTransform titleRect = (RectTransform)titleText.transform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 30f);
            titleRect.anchoredPosition = Vector2.zero;

            GameObject dataBinderObject = new GameObject("MarketDataBinder", typeof(RectTransform));
            dataBinderObject.transform.SetParent(parent, false);
            RectTransform dataBinderRect = (RectTransform)dataBinderObject.transform;
            dataBinderRect.anchorMin = new Vector2(0f, 1f);
            dataBinderRect.anchorMax = new Vector2(1f, 1f);
            dataBinderRect.pivot = new Vector2(0.5f, 1f);
            dataBinderRect.sizeDelta = new Vector2(0f, 24f);
            dataBinderRect.anchoredPosition = new Vector2(0f, -34f);

            TMP_Text taxSummaryText = CreateText(dataBinderRect, "TaxSummaryText", "Gold: 0  Tax: -", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform taxSummaryRect = (RectTransform)taxSummaryText.transform;
            taxSummaryRect.anchorMin = new Vector2(0f, 0f);
            taxSummaryRect.anchorMax = new Vector2(0.5f, 1f);
            taxSummaryRect.offsetMin = Vector2.zero;
            taxSummaryRect.offsetMax = Vector2.zero;

            TMP_Text netPayoutText = CreateText(dataBinderRect, "NetPayoutText", "Net Payout: 0g", 14f, TextAlignmentOptions.MidlineRight);
            RectTransform netPayoutRect = (RectTransform)netPayoutText.transform;
            netPayoutRect.anchorMin = new Vector2(0.5f, 0f);
            netPayoutRect.anchorMax = new Vector2(1f, 1f);
            netPayoutRect.offsetMin = Vector2.zero;
            netPayoutRect.offsetMax = Vector2.zero;

            UiMarketDataBinder dataBinder = dataBinderObject.AddComponent<UiMarketDataBinder>();
            dataBinder.SyncProxy = syncProxy;
            dataBinder.MarketTaxSummaryText = taxSummaryText;
            dataBinder.MarketNetPayoutText = netPayoutText;

            GameObject filterRowObject = new GameObject("FilterRow", typeof(RectTransform));
            filterRowObject.transform.SetParent(parent, false);
            RectTransform filterRowRect = (RectTransform)filterRowObject.transform;
            filterRowRect.anchorMin = new Vector2(0f, 1f);
            filterRowRect.anchorMax = new Vector2(1f, 1f);
            filterRowRect.pivot = new Vector2(0.5f, 1f);
            filterRowRect.sizeDelta = new Vector2(0f, 36f);
            filterRowRect.anchoredPosition = new Vector2(0f, -66f);

            HorizontalLayoutGroup filterLayout = filterRowObject.AddComponent<HorizontalLayoutGroup>();
            filterLayout.spacing = 6f;
            filterLayout.childControlWidth = true;
            filterLayout.childForceExpandWidth = false;
            filterLayout.childControlHeight = true;
            filterLayout.childForceExpandHeight = true;

            TMP_InputField baseItemIdInput = CreateInputField(filterRowRect, "BaseItemIdInputField", "Item Id");
            LayoutElement baseItemIdLayout = baseItemIdInput.gameObject.AddComponent<LayoutElement>();
            baseItemIdLayout.flexibleWidth = 1f;

            TMP_InputField qualityTierInput = CreateInputField(filterRowRect, "QualityTierInputField", "Tier");
            LayoutElement qualityTierLayout = qualityTierInput.gameObject.AddComponent<LayoutElement>();
            qualityTierLayout.preferredWidth = 70f;

            Button searchButton = CreateButton(filterRowRect, "SearchButton", "Search", out TextMeshProUGUI _);
            LayoutElement searchLayout = searchButton.gameObject.AddComponent<LayoutElement>();
            searchLayout.preferredWidth = 80f;

            TMP_Text taxLegendText = CreateText(parent, "TaxLegendText", string.Empty, 11f, TextAlignmentOptions.MidlineLeft);
            RectTransform taxLegendRect = (RectTransform)taxLegendText.transform;
            taxLegendRect.anchorMin = new Vector2(0f, 1f);
            taxLegendRect.anchorMax = new Vector2(1f, 1f);
            taxLegendRect.pivot = new Vector2(0.5f, 1f);
            taxLegendRect.sizeDelta = new Vector2(0f, 16f);
            taxLegendRect.anchoredPosition = new Vector2(0f, -104f);

            // Modul: Play Mode audit follow-up. PlaceLimitOrder's BUY side
            // (a standing order, distinct from this browser's instant
            // MarketBuyItem-against-an-existing-listing flow) had no UI -
            // see UiMarketBuyOrderPanel's own header comment. Uses a raw
            // numeric ContentRegistry item id, not the BaseItemIdInput
            // string search field above (different identifier scheme,
            // same seam PlaceLimitOrder's server-side fix addressed), so
            // it gets its own compact row rather than sharing that field.
            GameObject buyOrderRowObject = new GameObject("BuyOrderRow", typeof(RectTransform));
            buyOrderRowObject.transform.SetParent(parent, false);
            RectTransform buyOrderRowRect = (RectTransform)buyOrderRowObject.transform;
            buyOrderRowRect.anchorMin = new Vector2(0f, 1f);
            buyOrderRowRect.anchorMax = new Vector2(1f, 1f);
            buyOrderRowRect.pivot = new Vector2(0.5f, 1f);
            buyOrderRowRect.sizeDelta = new Vector2(0f, 34f);
            buyOrderRowRect.anchoredPosition = new Vector2(0f, -122f);

            HorizontalLayoutGroup buyOrderLayout = buyOrderRowObject.AddComponent<HorizontalLayoutGroup>();
            buyOrderLayout.spacing = 4f;
            buyOrderLayout.childControlWidth = true;
            buyOrderLayout.childForceExpandWidth = false;
            buyOrderLayout.childControlHeight = true;
            buyOrderLayout.childForceExpandHeight = true;

            TMP_InputField buyItemIdInput = CreateInputField(buyOrderRowRect, "BuyItemIdInputField", "Item#");
            LayoutElement buyItemIdLayout = buyItemIdInput.gameObject.AddComponent<LayoutElement>();
            buyItemIdLayout.preferredWidth = 60f;

            TMP_InputField buyQualityTierInput = CreateInputField(buyOrderRowRect, "BuyQualityTierInputField", "Tier");
            LayoutElement buyQualityTierLayout = buyQualityTierInput.gameObject.AddComponent<LayoutElement>();
            buyQualityTierLayout.preferredWidth = 50f;

            TMP_InputField buyPriceInput = CreateInputField(buyOrderRowRect, "BuyPriceInputField", "Price");
            LayoutElement buyPriceLayout = buyPriceInput.gameObject.AddComponent<LayoutElement>();
            buyPriceLayout.preferredWidth = 70f;

            Button placeBuyOrderButton = CreateButton(buyOrderRowRect, "PlaceBuyOrderButton", "Buy Order", out TextMeshProUGUI _);
            LayoutElement placeBuyOrderLayout = placeBuyOrderButton.gameObject.AddComponent<LayoutElement>();
            placeBuyOrderLayout.flexibleWidth = 1f;

            UiMarketBuyOrderPanel buyOrderPanel = buyOrderRowObject.AddComponent<UiMarketBuyOrderPanel>();
            buyOrderPanel.NetworkClient = networkClient;
            buyOrderPanel.ItemIdField = buyItemIdInput;
            buyOrderPanel.QualityTierField = buyQualityTierInput;
            buyOrderPanel.PriceField = buyPriceInput;
            buyOrderPanel.PlaceBuyOrderButton = placeBuyOrderButton;

            GameObject listingAreaObject = new GameObject("ListingArea", typeof(RectTransform));
            listingAreaObject.transform.SetParent(parent, false);
            RectTransform listingAreaRect = (RectTransform)listingAreaObject.transform;
            listingAreaRect.anchorMin = new Vector2(0f, 0.54f);
            listingAreaRect.anchorMax = new Vector2(1f, 1f);
            listingAreaRect.offsetMin = new Vector2(0f, 26f);
            listingAreaRect.offsetMax = new Vector2(0f, -160f);

            (ScrollRect listingScrollRect, RectTransform listingContent) = ChatSceneBuilder.BuildScrollView(listingAreaRect);

            GameObject pageRowObject = new GameObject("PageRow", typeof(RectTransform));
            pageRowObject.transform.SetParent(parent, false);
            RectTransform pageRowRect = (RectTransform)pageRowObject.transform;
            pageRowRect.anchorMin = new Vector2(0f, 0.54f);
            pageRowRect.anchorMax = new Vector2(1f, 0.54f);
            pageRowRect.pivot = new Vector2(0.5f, 0f);
            pageRowRect.sizeDelta = new Vector2(0f, 26f);
            pageRowRect.anchoredPosition = Vector2.zero;

            HorizontalLayoutGroup pageLayout = pageRowObject.AddComponent<HorizontalLayoutGroup>();
            pageLayout.spacing = 6f;
            pageLayout.childControlWidth = false;
            pageLayout.childForceExpandWidth = false;
            pageLayout.childControlHeight = true;
            pageLayout.childForceExpandHeight = true;
            pageLayout.childAlignment = TextAnchor.MiddleLeft;

            Button prevPageButton = CreateButton(pageRowRect, "PrevPageButton", "Prev", out TextMeshProUGUI _);
            LayoutElement prevLayout = prevPageButton.gameObject.AddComponent<LayoutElement>();
            prevLayout.preferredWidth = 60f;

            TMP_Text pageIndexText = CreateText(pageRowRect, "PageIndexText", "Page 1", 14f, TextAlignmentOptions.Center);
            LayoutElement pageIndexLayout = pageIndexText.gameObject.AddComponent<LayoutElement>();
            pageIndexLayout.preferredWidth = 80f;

            Button nextPageButton = CreateButton(pageRowRect, "NextPageButton", "Next", out TextMeshProUGUI _);
            LayoutElement nextLayout = nextPageButton.gameObject.AddComponent<LayoutElement>();
            nextLayout.preferredWidth = 60f;

            GameObject browserRowPrefabAsset = BuildAndSaveMarketListingRowPrefab();

            UiMarketBrowserWindow browserWindow = parent.gameObject.AddComponent<UiMarketBrowserWindow>();
            browserWindow.ListScrollRect = listingScrollRect;
            browserWindow.RowContainer = listingContent;
            browserWindow.RowPrefab = browserRowPrefabAsset.GetComponent<UiMarketListingRow>();
            browserWindow.BaseItemIdInput = baseItemIdInput;
            browserWindow.QualityTierInput = qualityTierInput;
            browserWindow.SearchButton = searchButton;
            browserWindow.NextPageButton = nextPageButton;
            browserWindow.PrevPageButton = prevPageButton;
            browserWindow.PageIndexText = pageIndexText;
            browserWindow.TaxLegendText = taxLegendText;
            browserWindow.NetworkClient = networkClient;

            // Sell - bottom half.
            TMP_Text sellTitleText = CreateText(parent, "SellTitleText", "Sell", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform sellTitleRect = (RectTransform)sellTitleText.transform;
            sellTitleRect.anchorMin = new Vector2(0f, 0.54f);
            sellTitleRect.anchorMax = new Vector2(1f, 0.54f);
            sellTitleRect.pivot = new Vector2(0.5f, 1f);
            sellTitleRect.sizeDelta = new Vector2(0f, 22f);
            sellTitleRect.anchoredPosition = Vector2.zero;

            GameObject sellAreaObject = new GameObject("SellArea", typeof(RectTransform));
            sellAreaObject.transform.SetParent(parent, false);
            RectTransform sellAreaRect = (RectTransform)sellAreaObject.transform;
            sellAreaRect.anchorMin = new Vector2(0f, 0f);
            sellAreaRect.anchorMax = new Vector2(1f, 0.54f);
            sellAreaRect.offsetMin = Vector2.zero;
            sellAreaRect.offsetMax = new Vector2(0f, -22f);

            (ScrollRect _, RectTransform sellContent) = ChatSceneBuilder.BuildScrollView(sellAreaRect);

            GameObject sellRowPrefabAsset = BuildAndSaveMarketSellRowPrefab();

            UiMarketSellPanel sellPanel = sellAreaObject.AddComponent<UiMarketSellPanel>();
            sellPanel.InventoryCache = inventoryCache;
            sellPanel.NetworkClient = networkClient;
            sellPanel.RowContainer = sellContent;
            sellPanel.RowPrefab = sellRowPrefabAsset.GetComponent<UiMarketSellCandidateRow>();
        }

        // Real, wired UiBankVaultWindow - vault (withdraw) list on top,
        // backpack (deposit) list on the bottom, exactly mirroring its own
        // established two-list layout.
        private static void BuildBankSide(RectTransform parent, VisualSyncProxy syncProxy, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient)
        {
            TextMeshProUGUI headerText = CreateText(parent, "HeaderText", "Bank", 20f, TextAlignmentOptions.Center);
            RectTransform headerRect = (RectTransform)headerText.transform;
            headerRect.anchorMin = new Vector2(0f, 1f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.pivot = new Vector2(0.5f, 1f);
            headerRect.sizeDelta = new Vector2(0f, 30f);
            headerRect.anchoredPosition = Vector2.zero;

            TMP_Text vaultLabelText = CreateText(parent, "VaultLabelText", "Vault (Withdraw)", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform vaultLabelRect = (RectTransform)vaultLabelText.transform;
            vaultLabelRect.anchorMin = new Vector2(0f, 1f);
            vaultLabelRect.anchorMax = new Vector2(1f, 1f);
            vaultLabelRect.pivot = new Vector2(0.5f, 1f);
            vaultLabelRect.sizeDelta = new Vector2(0f, 20f);
            vaultLabelRect.anchoredPosition = new Vector2(0f, -36f);

            GameObject vaultAreaObject = new GameObject("VaultArea", typeof(RectTransform));
            vaultAreaObject.transform.SetParent(parent, false);
            RectTransform vaultAreaRect = (RectTransform)vaultAreaObject.transform;
            vaultAreaRect.anchorMin = new Vector2(0f, 0.52f);
            vaultAreaRect.anchorMax = new Vector2(1f, 1f);
            vaultAreaRect.offsetMin = Vector2.zero;
            vaultAreaRect.offsetMax = new Vector2(0f, -58f);

            (ScrollRect _, RectTransform vaultContent) = ChatSceneBuilder.BuildScrollView(vaultAreaRect);

            TMP_Text backpackLabelText = CreateText(parent, "BackpackLabelText", "Backpack (Deposit)", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform backpackLabelRect = (RectTransform)backpackLabelText.transform;
            backpackLabelRect.anchorMin = new Vector2(0f, 0.52f);
            backpackLabelRect.anchorMax = new Vector2(1f, 0.52f);
            backpackLabelRect.pivot = new Vector2(0.5f, 1f);
            backpackLabelRect.sizeDelta = new Vector2(0f, 20f);
            backpackLabelRect.anchoredPosition = Vector2.zero;

            GameObject backpackAreaObject = new GameObject("BackpackArea", typeof(RectTransform));
            backpackAreaObject.transform.SetParent(parent, false);
            RectTransform backpackAreaRect = (RectTransform)backpackAreaObject.transform;
            backpackAreaRect.anchorMin = new Vector2(0f, 0f);
            backpackAreaRect.anchorMax = new Vector2(1f, 0.52f);
            backpackAreaRect.offsetMin = Vector2.zero;
            backpackAreaRect.offsetMax = new Vector2(0f, -22f);

            (ScrollRect _, RectTransform backpackContent) = ChatSceneBuilder.BuildScrollView(backpackAreaRect);

            GameObject vaultRowPrefabAsset = BuildAndSaveBankVaultRowPrefab();
            GameObject backpackRowPrefabAsset = BuildAndSaveBankDepositRowPrefab();

            UiBankVaultWindow bankWindow = parent.gameObject.AddComponent<UiBankVaultWindow>();
            bankWindow.SyncProxy = syncProxy;
            bankWindow.HeaderText = headerText;
            bankWindow.VaultRowContainer = vaultContent;
            bankWindow.VaultRowPrefab = vaultRowPrefabAsset.GetComponent<UiBankVaultEntryRow>();
            bankWindow.InventoryCache = inventoryCache;
            bankWindow.BackpackRowContainer = backpackContent;
            bankWindow.BackpackRowPrefab = backpackRowPrefabAsset.GetComponent<UiBankDepositCandidateRow>();
            bankWindow.NetworkClient = networkClient;
        }

        // ------------------------------------------------------------
        // Global overlays - always visible regardless of the active nav
        // tab. Every class here already carries its own "isolated sub-
        // canvas, self-anchors in Awake" design (World Boss, Event
        // Countdown, Codex Bonus panels all forcibly reposition themselves
        // via their own Awake() using hard-coded anchoredPosition values -
        // this builder cannot override those, only provide the initial
        // RectTransform they reposition), so none of these are wrapped in
        // their own Canvas here; the panels simply sit directly under the
        // main Canvas, on top of every nav tab's content by sibling order.
        // ------------------------------------------------------------
        private static void BuildGlobalOverlays(Transform canvasTransform, VisualSyncProxy syncProxy)
        {
            GameObject overlaysRoot = new GameObject("GlobalOverlays", typeof(RectTransform));
            overlaysRoot.transform.SetParent(canvasTransform, false);
            StretchFull((RectTransform)overlaysRoot.transform);

            BuildSaveTrustIndicator(overlaysRoot.transform, syncProxy);
            BuildEventCountdownOverlay(overlaysRoot.transform, syncProxy);
            // Modul: Map Hub. The small always-on World Boss mini panel
            // (HP bar + Attack button, top-center) is superseded by the
            // dedicated Boss World screen reachable from the map's Boss
            // zone - keeping both meant two HP/Attack displays for the
            // same boss fighting for the exact same top-center real
            // estate on every single screen (see BuildBossWorldPanel for
            // the real, network-wired replacement).
            BuildCodexBonusOverlay(overlaysRoot.transform, syncProxy);
            BuildCommandResultToast(overlaysRoot.transform, syncProxy);
            BuildOfflineSummaryModal(overlaysRoot.transform, syncProxy);
        }

        private static void BuildSaveTrustIndicator(Transform parent, VisualSyncProxy syncProxy)
        {
            TextMeshProUGUI text = CreateText(parent, "SaveTrustIndicator", "All progress saved", 13f, TextAlignmentOptions.TopLeft);
            RectTransform rect = (RectTransform)text.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.sizeDelta = new Vector2(240f, 20f);
            text.alignment = TextAlignmentOptions.MidlineRight;
            // Modul: UI rework. Moved from top-left (20, -300) to just
            // above the Season Pass banner on the right. At -300 it sat
            // 120px INSIDE every full-screen window (they all start at
            // -180) and printed "All progress saved" straight across
            // whatever that window was drawing - visible on the Combat and
            // Map screens alike. Bottom-right also keeps it clear of the
            // bottom-left world chat dock. Lifted 60px so it clears the
            // 54px-tall Season Pass banner underneath it.
            rect.anchoredPosition = new Vector2(-16f, 60f);

            UiSaveTrustIndicator indicator = text.gameObject.AddComponent<UiSaveTrustIndicator>();
            indicator.SyncProxy = syncProxy;
            indicator.SaveStatusText = text;
        }

        private static void BuildEventCountdownOverlay(Transform parent, VisualSyncProxy syncProxy)
        {
            GameObject panelObject = new GameObject("EventCountdownPanel", typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)panelObject.transform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(320f, 24f);
            rect.anchoredPosition = new Vector2(0f, -60f);

            TextMeshProUGUI text = CreateText(rect, "EventCountdownText", "No Active Event", 13f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)text.transform);

            UiEventCountdownBinder binder = panelObject.AddComponent<UiEventCountdownBinder>();
            binder.SyncProxy = syncProxy;
            binder.EventCountdownPanelRect = rect;
            binder.EventCountdownText = text;
        }

        private static void BuildCodexBonusOverlay(Transform parent, VisualSyncProxy syncProxy)
        {
            GameObject panelObject = new GameObject("CodexBonusPanel", typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)panelObject.transform;
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(140f, 90f);
            rect.anchoredPosition = new Vector2(-20f, -20f);

            VerticalLayoutGroup layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 2f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperRight;

            rect.sizeDelta = new Vector2(190f, 90f);

            TextMeshProUGUI humanText = CreateStatRow(panelObject.transform, "Human: +0%");
            TextMeshProUGUI vilaText = CreateStatRow(panelObject.transform, "Vila: +0%");
            TextMeshProUGUI draugrText = CreateStatRow(panelObject.transform, "Draugr: +0%");
            humanText.fontSize = 13f;
            vilaText.fontSize = 13f;
            draugrText.fontSize = 13f;
            humanText.alignment = TextAlignmentOptions.MidlineRight;
            vilaText.alignment = TextAlignmentOptions.MidlineRight;
            draugrText.alignment = TextAlignmentOptions.MidlineRight;

            UiCodexBonusBinder binder = panelObject.AddComponent<UiCodexBonusBinder>();
            binder.SyncProxy = syncProxy;
            binder.CodexPanelRect = rect;
            binder.HumanBonusText = humanText;
            binder.VilaBonusText = vilaText;
            binder.DraugrBonusText = draugrText;
        }

        private static void BuildCommandResultToast(Transform parent, VisualSyncProxy syncProxy)
        {
            GameObject toastRootObject = new GameObject("CommandResultToast", typeof(RectTransform));
            toastRootObject.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)toastRootObject.transform;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(500f, 50f);
            rect.anchoredPosition = new Vector2(0f, 110f);

            toastRootObject.AddComponent<Image>().color = new Color(0.6f, 0.1f, 0.1f, 0.9f);

            TextMeshProUGUI text = CreateText(rect, "ToastText", string.Empty, 16f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)text.transform);

            UiCommandResultToast toast = toastRootObject.AddComponent<UiCommandResultToast>();
            toast.SyncProxy = syncProxy;
            toast.ToastText = text;
            toast.ToastRoot = toastRootObject;
        }

        // Modul: the UiOfflineSummaryWindow component must live on a
        // GameObject that stays permanently active - its own Awake() calls
        // WindowRoot.SetActive(false), and if WindowRoot were the same
        // GameObject the component lives on, that call would disable the
        // component's own OnEnable before it ever subscribes to
        // OnOfflineSummaryAvailable, permanently breaking the modal
        // (nothing would ever be listening to re-activate it). WindowRoot
        // is therefore a child object, matching UiLoginWindow's
        // BlockingPanelRoot/UiGuildWarPanel's NoActiveWarRoot pattern.
        private static void BuildOfflineSummaryModal(Transform parent, VisualSyncProxy syncProxy)
        {
            GameObject controllerObject = new GameObject("OfflineSummaryModal", typeof(RectTransform));
            controllerObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)controllerObject.transform);

            GameObject windowRoot = new GameObject("ModalRoot", typeof(RectTransform));
            windowRoot.transform.SetParent(controllerObject.transform, false);
            StretchFull((RectTransform)windowRoot.transform);
            windowRoot.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);

            GameObject panelObject = new GameObject("Panel", typeof(RectTransform));
            panelObject.transform.SetParent(windowRoot.transform, false);
            RectTransform panelRect = (RectTransform)panelObject.transform;
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(560f, 420f);
            panelObject.AddComponent<Image>().color = new Color(0.1f, 0.1f, 0.13f, 0.98f);

            VerticalLayoutGroup layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 24, 24);
            layout.spacing = 14f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperCenter;

            TextMeshProUGUI headerText = CreateStatRow(panelObject.transform, "Welcome Back");
            headerText.fontSize = 24f;
            headerText.alignment = TextAlignmentOptions.Center;

            TextMeshProUGUI elapsedText = CreateStatRow(panelObject.transform, "Away for 0h 0m");
            TextMeshProUGUI goldText = CreateStatRow(panelObject.transform, "+0 Gold");
            TextMeshProUGUI xpText = CreateStatRow(panelObject.transform, "+0 XP");
            TextMeshProUGUI materialsText = CreateStatRow(panelObject.transform, "+0 Materials");

            Button dismissButton = CreateButton(panelObject.transform, "DismissButton", "OK", out TextMeshProUGUI _);
            SetFixedLayoutHeight(dismissButton.gameObject, 48f);

            UiOfflineSummaryWindow modal = controllerObject.AddComponent<UiOfflineSummaryWindow>();
            modal.SyncProxy = syncProxy;
            modal.WindowRoot = windowRoot;
            modal.DismissButton = dismissButton;
            modal.HeaderText = headerText;
            modal.ElapsedTimeText = elapsedText;
            modal.GoldEarnedText = goldText;
            modal.XpEarnedText = xpText;
            modal.MaterialDropsText = materialsText;
        }

        // ------------------------------------------------------------
        // FTUE tutorial - CTA pulse highlights on the three gateable
        // targets (Inventory HUD panel, Forge nav tab, Arena player
        // portrait), a step-instruction banner with a Skip button, and
        // interaction gates on the nav tab buttons the closed
        // TutorialUiElement enum can distinguish. UiTutorialController
        // itself is a plain non-visual component; the highlight
        // sub-objects are its children purely for organization (they own
        // no RectTransform - UiTutorialHighlight only needs Update() to
        // run while active, matching the "logic component with a Target
        // reference elsewhere in the hierarchy" pattern its own header
        // comment describes).
        // ------------------------------------------------------------
        private static UiTutorialController BuildTutorialSystem(
            Transform canvasTransform, VisualSyncProxy syncProxy, Image inventoryTarget, Image arenaTarget,
            Button forgeButton, Button marketButton, Button guildButton, Button skillTreeButton, Button chatButton)
        {
            GameObject controllerObject = new GameObject("TutorialController", typeof(RectTransform));
            controllerObject.transform.SetParent(canvasTransform, false);

            UiTutorialHighlight inventoryHighlight = BuildTutorialHighlight(controllerObject.transform, "InventoryHighlight", inventoryTarget);
            UiTutorialHighlight forgeHighlight = BuildTutorialHighlight(controllerObject.transform, "ForgeHighlight", forgeButton != null ? forgeButton.GetComponent<Image>() : null);
            UiTutorialHighlight arenaHighlight = BuildTutorialHighlight(controllerObject.transform, "ArenaHighlight", arenaTarget);

            GameObject overlayRoot = new GameObject("TutorialOverlay", typeof(RectTransform));
            overlayRoot.transform.SetParent(controllerObject.transform, false);
            RectTransform overlayRect = (RectTransform)overlayRoot.transform;
            overlayRect.anchorMin = new Vector2(0.5f, 0f);
            overlayRect.anchorMax = new Vector2(0.5f, 0f);
            overlayRect.pivot = new Vector2(0.5f, 0f);
            overlayRect.anchoredPosition = new Vector2(0f, 110f);
            overlayRect.sizeDelta = new Vector2(560f, 60f);
            overlayRoot.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);

            TextMeshProUGUI instructionText = CreateText(overlayRoot.transform, "InstructionLabel", string.Empty, 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform instructionRect = (RectTransform)instructionText.transform;
            instructionRect.anchorMin = Vector2.zero;
            instructionRect.anchorMax = Vector2.one;
            instructionRect.offsetMin = new Vector2(16f, 0f);
            instructionRect.offsetMax = new Vector2(-90f, 0f);

            Button skipButton = CreateButton(overlayRoot.transform, "SkipButton", "Skip", out TextMeshProUGUI _);
            RectTransform skipRect = (RectTransform)skipButton.transform;
            skipRect.anchorMin = new Vector2(1f, 0.15f);
            skipRect.anchorMax = new Vector2(1f, 0.85f);
            skipRect.pivot = new Vector2(1f, 0.5f);
            skipRect.sizeDelta = new Vector2(70f, 0f);
            skipRect.anchoredPosition = new Vector2(-10f, 0f);

            UiTutorialController controller = controllerObject.AddComponent<UiTutorialController>();
            controller.SyncProxy = syncProxy;
            controller.InventoryHighlight = inventoryHighlight;
            controller.ForgeHighlight = forgeHighlight;
            controller.ArenaHighlight = arenaHighlight;
            controller.TutorialOverlayRoot = overlayRoot;
            controller.InstructionLabel = instructionText;
            controller.SkipButton = skipButton;

            BuildTutorialInteractionGate(controllerObject.transform, controller, TutorialUiElement.Forge, forgeButton);
            BuildTutorialInteractionGate(controllerObject.transform, controller, TutorialUiElement.Market, marketButton);
            BuildTutorialInteractionGate(controllerObject.transform, controller, TutorialUiElement.Guild, guildButton);
            BuildTutorialInteractionGate(controllerObject.transform, controller, TutorialUiElement.SkillTree, skillTreeButton);
            BuildTutorialInteractionGate(controllerObject.transform, controller, TutorialUiElement.Chat, chatButton);

            return controller;
        }

        private static UiTutorialHighlight BuildTutorialHighlight(Transform parent, string name, Image target)
        {
            GameObject highlightObject = new GameObject(name);
            highlightObject.transform.SetParent(parent, false);
            highlightObject.SetActive(false);

            UiTutorialHighlight highlight = highlightObject.AddComponent<UiTutorialHighlight>();
            highlight.Target = target;
            return highlight;
        }

        private static void BuildTutorialInteractionGate(Transform parent, UiTutorialController controller, TutorialUiElement element, Button gatedButton)
        {
            if (gatedButton == null) return;

            GameObject gateObject = new GameObject("Gate_" + element);
            gateObject.transform.SetParent(parent, false);

            UiTutorialInteractionGate gate = gateObject.AddComponent<UiTutorialInteractionGate>();
            gate.Controller = controller;
            gate.Element = element;
            gate.GatedButton = gatedButton;
        }

        // ------------------------------------------------------------
        // Row prefabs for the Guild/Market/Bank list panels above -
        // mirrors BuildAndSaveDamageTextPrefab's exact staging-instance-
        // then-SaveAsPrefabAsset-then-DestroyImmediate pattern. None of
        // these are Addressable-loaded (unlike ChatMessageRow) - each
        // owning panel holds a direct RowPrefab object reference instead
        // of a string key, so a plain PrefabUtility.SaveAsPrefabAsset call
        // is all that's needed.
        // ------------------------------------------------------------
        private static GameObject BuildAndSaveGuildRosterRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiGuildRosterEntryRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 28f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Player", 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-26f, 0f);

            GameObject onlineIndicator = new GameObject("OnlineIndicator", typeof(RectTransform));
            onlineIndicator.transform.SetParent(root.transform, false);
            RectTransform onlineRect = (RectTransform)onlineIndicator.transform;
            onlineRect.anchorMin = new Vector2(1f, 0.5f);
            onlineRect.anchorMax = new Vector2(1f, 0.5f);
            onlineRect.pivot = new Vector2(1f, 0.5f);
            onlineRect.sizeDelta = new Vector2(16f, 16f);
            onlineRect.anchoredPosition = new Vector2(-6f, 0f);
            onlineIndicator.AddComponent<Image>().color = new Color(0.2f, 0.85f, 0.2f, 1f);

            GameObject offlineIndicator = new GameObject("OfflineIndicator", typeof(RectTransform));
            offlineIndicator.transform.SetParent(root.transform, false);
            RectTransform offlineRect = (RectTransform)offlineIndicator.transform;
            offlineRect.anchorMin = new Vector2(1f, 0.5f);
            offlineRect.anchorMax = new Vector2(1f, 0.5f);
            offlineRect.pivot = new Vector2(1f, 0.5f);
            offlineRect.sizeDelta = new Vector2(16f, 16f);
            offlineRect.anchoredPosition = new Vector2(-6f, 0f);
            offlineIndicator.AddComponent<Image>().color = new Color(0.5f, 0.5f, 0.5f, 1f);

            UiGuildRosterEntryRow rowComponent = root.AddComponent<UiGuildRosterEntryRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.OnlineIndicator = onlineIndicator;
            rowComponent.OfflineIndicator = offlineIndicator;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, GuildRosterRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiGuildRosterEntryRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveGuildApplicationRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiGuildApplicationEntryRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 40f);

            HorizontalLayoutGroup rowLayout = root.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 6f;
            rowLayout.padding = new RectOffset(6, 6, 4, 4);
            rowLayout.childControlWidth = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandHeight = true;

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Player  Lv 0", 15f, TextAlignmentOptions.MidlineLeft);
            LayoutElement rowTextLayout = rowText.gameObject.AddComponent<LayoutElement>();
            rowTextLayout.flexibleWidth = 1f;

            Button approveButton = CreateButton(root.transform, "ApproveButton", "Approve", out TextMeshProUGUI _);
            LayoutElement approveLayout = approveButton.gameObject.AddComponent<LayoutElement>();
            approveLayout.preferredWidth = 100f;

            Button rejectButton = CreateButton(root.transform, "RejectButton", "Reject", out TextMeshProUGUI _);
            LayoutElement rejectLayout = rejectButton.gameObject.AddComponent<LayoutElement>();
            rejectLayout.preferredWidth = 100f;

            UiGuildApplicationEntryRow rowComponent = root.AddComponent<UiGuildApplicationEntryRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.ApproveButton = approveButton;
            rowComponent.RejectButton = rejectButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, GuildApplicationRowPrefabPath, out bool applicationRowSuccess);
            if (!applicationRowSuccess)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiGuildApplicationEntryRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveMarketListingRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiMarketListingRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 30f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Item", 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-70f, 0f);

            Button buyButton = CreateButton(root.transform, "BuyButton", "Buy", out TextMeshProUGUI _);
            RectTransform buyRect = (RectTransform)buyButton.transform;
            buyRect.anchorMin = new Vector2(1f, 0.1f);
            buyRect.anchorMax = new Vector2(1f, 0.9f);
            buyRect.pivot = new Vector2(1f, 0.5f);
            buyRect.sizeDelta = new Vector2(60f, 0f);
            buyRect.anchoredPosition = new Vector2(-4f, 0f);

            UiMarketListingRow rowComponent = root.AddComponent<UiMarketListingRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.BuyButton = buyButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, MarketListingRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiMarketListingRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveMarketSellRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiMarketSellCandidateRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 34f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Item", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = new Vector2(0f, 0f);
            rowTextRect.anchorMax = new Vector2(0.45f, 1f);
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = Vector2.zero;

            TMP_InputField priceInput = CreateInputField(root.transform, "PriceInputField", "Price");
            RectTransform priceRect = (RectTransform)priceInput.transform;
            priceRect.anchorMin = new Vector2(0.45f, 0.1f);
            priceRect.anchorMax = new Vector2(0.75f, 0.9f);
            priceRect.offsetMin = Vector2.zero;
            priceRect.offsetMax = Vector2.zero;

            Button sellButton = CreateButton(root.transform, "SellButton", "Sell", out TextMeshProUGUI _);
            RectTransform sellRect = (RectTransform)sellButton.transform;
            sellRect.anchorMin = new Vector2(0.77f, 0.1f);
            sellRect.anchorMax = new Vector2(1f, 0.9f);
            sellRect.offsetMin = Vector2.zero;
            sellRect.offsetMax = Vector2.zero;

            UiMarketSellCandidateRow rowComponent = root.AddComponent<UiMarketSellCandidateRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.PriceInputField = priceInput;
            rowComponent.SellButton = sellButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, MarketSellRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiMarketSellCandidateRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveForgeFusionRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiForgeFusionCandidateRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 34f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Item", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-70f, 0f);

            Button selectButton = CreateButton(root.transform, "SelectButton", "Select", out TextMeshProUGUI _);
            RectTransform selectRect = (RectTransform)selectButton.transform;
            selectRect.anchorMin = new Vector2(1f, 0.1f);
            selectRect.anchorMax = new Vector2(1f, 0.9f);
            selectRect.pivot = new Vector2(1f, 0.5f);
            selectRect.sizeDelta = new Vector2(60f, 0f);
            selectRect.anchoredPosition = new Vector2(-4f, 0f);

            UiForgeFusionCandidateRow rowComponent = root.AddComponent<UiForgeFusionCandidateRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.SelectButton = selectButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, ForgeFusionRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiForgeFusionCandidateRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveBankVaultRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiBankVaultEntryRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 30f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Item", 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-80f, 0f);

            Button withdrawButton = CreateButton(root.transform, "WithdrawButton", "Withdraw", out TextMeshProUGUI _);
            RectTransform withdrawRect = (RectTransform)withdrawButton.transform;
            withdrawRect.anchorMin = new Vector2(1f, 0.1f);
            withdrawRect.anchorMax = new Vector2(1f, 0.9f);
            withdrawRect.pivot = new Vector2(1f, 0.5f);
            withdrawRect.sizeDelta = new Vector2(76f, 0f);
            withdrawRect.anchoredPosition = new Vector2(-4f, 0f);

            UiBankVaultEntryRow rowComponent = root.AddComponent<UiBankVaultEntryRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.WithdrawButton = withdrawButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, BankVaultRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiBankVaultEntryRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveBankDepositRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiBankDepositCandidateRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 30f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Item", 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-70f, 0f);

            Button depositButton = CreateButton(root.transform, "DepositButton", "Deposit", out TextMeshProUGUI _);
            RectTransform depositRect = (RectTransform)depositButton.transform;
            depositRect.anchorMin = new Vector2(1f, 0.1f);
            depositRect.anchorMax = new Vector2(1f, 0.9f);
            depositRect.pivot = new Vector2(1f, 0.5f);
            depositRect.sizeDelta = new Vector2(66f, 0f);
            depositRect.anchoredPosition = new Vector2(-4f, 0f);

            UiBankDepositCandidateRow rowComponent = root.AddComponent<UiBankDepositCandidateRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.DepositButton = depositButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, BankDepositRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiBankDepositCandidateRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        // ------------------------------------------------------------
        // Forge (Craft/Reroll sub-tabs), Skill Tree, Village - the last
        // batch of previously-orphaned network-wired scripts from the UI
        // survey. AssetRegistry is a shared, intentionally-empty
        // ScriptableObject (no art assets exist per this pass's "zero
        // visual asset creation" constraint) assigned to the two consumer
        // panels below via SerializedObject, since assetRegistry is a
        // private [SerializeField] on both with no public setter.
        // ------------------------------------------------------------
        private static AssetRegistry EnsureAssetRegistryAsset()
        {
            EnsureFolder(PrefabDirectory);

            AssetRegistry existing = AssetDatabase.LoadAssetAtPath<AssetRegistry>(AssetRegistryAssetPath);
            if (existing != null)
            {
                return existing;
            }

            AssetRegistry registry = ScriptableObject.CreateInstance<AssetRegistry>();
            AssetDatabase.CreateAsset(registry, AssetRegistryAssetPath);
            return registry;
        }

        private static void AssignAssetRegistry(Object component, AssetRegistry registry)
        {
            SerializedObject serializedObject = new SerializedObject(component);
            SerializedProperty property = serializedObject.FindProperty("assetRegistry");
            if (property != null)
            {
                property.objectReferenceValue = registry;
                serializedObject.ApplyModifiedProperties();
            }
        }

        private static GameObject BuildForgeWindow(Transform canvasTransform, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient, VisualSyncProxy syncProxy, AssetRegistry assetRegistry, AssetLifecycleCoordinator assetCoordinator, Transform riggingParent)
        {
            GameObject windowObject = new GameObject("ForgeWindow", typeof(RectTransform));
            windowObject.transform.SetParent(canvasTransform, false);
            RectTransform windowRect = (RectTransform)windowObject.transform;
            // Modul: Map Hub. Fixed-pixel top/bottom insets instead of pure
            // percentage anchors - percentage margins compress along with
            // canvas height on any aspect ratio shorter than the 1080x1920
            // portrait reference, which let this window's own top content
            // (title/sub-tab header) collide with the persistent overlay
            // bars (Menu/Map buttons, Codex Bonus, Gold/Gems currency) and
            // the bottom Season Pass banner. Left/right stay percentage
            // since width scaling is already consistent (CanvasScaler
            // match-width).
            windowRect.anchorMin = new Vector2(0.04f, 0f);
            windowRect.anchorMax = new Vector2(0.96f, 1f);
            windowRect.offsetMin = new Vector2(0f, 70f);
            windowRect.offsetMax = new Vector2(0f, -180f);

            windowObject.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.96f);

            GameObject subTabHeaderObject = new GameObject("SubTabHeader", typeof(RectTransform));
            subTabHeaderObject.transform.SetParent(windowRect, false);
            RectTransform subTabHeaderRect = (RectTransform)subTabHeaderObject.transform;
            subTabHeaderRect.anchorMin = new Vector2(0f, 1f);
            subTabHeaderRect.anchorMax = new Vector2(1f, 1f);
            subTabHeaderRect.pivot = new Vector2(0.5f, 1f);
            subTabHeaderRect.sizeDelta = new Vector2(0f, 44f);
            subTabHeaderRect.anchoredPosition = new Vector2(0f, -12f);

            Button[] subTabButtons = BuildSubTabButtons(subTabHeaderRect, new[] { "Craft", "Reroll", "Fusion" });

            GameObject contentAreaObject = new GameObject("ContentArea", typeof(RectTransform));
            contentAreaObject.transform.SetParent(windowRect, false);
            RectTransform contentAreaRect = (RectTransform)contentAreaObject.transform;
            contentAreaRect.anchorMin = Vector2.zero;
            contentAreaRect.anchorMax = Vector2.one;
            contentAreaRect.offsetMin = new Vector2(20f, 20f);
            contentAreaRect.offsetMax = new Vector2(-20f, -64f);

            GameObject craftingGroup = BuildForgeCraftingGroup(contentAreaRect, inventoryCache, networkClient, assetRegistry, assetCoordinator, riggingParent);
            GameObject rerollGroup = BuildEquipmentRerollGroup(contentAreaRect, inventoryCache, networkClient, syncProxy, assetRegistry, assetCoordinator, riggingParent);
            GameObject fusionGroup = BuildForgeFusionGroup(contentAreaRect, inventoryCache, networkClient);

            rerollGroup.SetActive(false);
            fusionGroup.SetActive(false);

            UiTabGroup tabGroup = windowObject.AddComponent<UiTabGroup>();
            tabGroup.Groups = new[] { craftingGroup, rerollGroup, fusionGroup };
            tabGroup.Buttons = subTabButtons;

            return windowObject;
        }

        // Top 58% recipe list (real UiForgeCraftingPanel), bottom 42%
        // detail panel - a compact 3D item preview (UiForgeItemViewer) to
        // the left, name/material text plus a Craft button to the right.
        private static GameObject BuildForgeCraftingGroup(Transform parent, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient, AssetRegistry assetRegistry, AssetLifecycleCoordinator assetCoordinator, Transform riggingParent)
        {
            GameObject groupObject = new GameObject("CraftingGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            GameObject listAreaObject = new GameObject("RecipeListArea", typeof(RectTransform));
            listAreaObject.transform.SetParent(groupObject.transform, false);
            RectTransform listAreaRect = (RectTransform)listAreaObject.transform;
            listAreaRect.anchorMin = new Vector2(0f, 0.42f);
            listAreaRect.anchorMax = new Vector2(1f, 1f);
            listAreaRect.offsetMin = Vector2.zero;
            listAreaRect.offsetMax = Vector2.zero;

            (ScrollRect _, RectTransform recipeContent) = ChatSceneBuilder.BuildScrollView(listAreaRect);

            GameObject recipeRowPrefabAsset = BuildAndSaveForgeRecipeRowPrefab();

            GameObject detailAreaObject = new GameObject("CraftDetailPanel", typeof(RectTransform));
            detailAreaObject.transform.SetParent(groupObject.transform, false);
            RectTransform detailAreaRect = (RectTransform)detailAreaObject.transform;
            detailAreaRect.anchorMin = new Vector2(0f, 0f);
            detailAreaRect.anchorMax = new Vector2(1f, 0.42f);
            detailAreaRect.offsetMin = Vector2.zero;
            detailAreaRect.offsetMax = new Vector2(0f, -12f);

            HorizontalLayoutGroup detailRowLayout = detailAreaObject.AddComponent<HorizontalLayoutGroup>();
            detailRowLayout.spacing = 10f;
            detailRowLayout.childControlWidth = true;
            detailRowLayout.childForceExpandWidth = false;
            detailRowLayout.childControlHeight = true;
            detailRowLayout.childForceExpandHeight = false;

            UiForgeItemViewer craftItemViewer = BuildForgeItemViewer(detailAreaObject.transform, riggingParent, assetCoordinator, "UI_3D_Preview_ForgeCraft", "ForgeCraftPreviewRig");

            GameObject textStackObject = new GameObject("DetailTextStack", typeof(RectTransform));
            textStackObject.transform.SetParent(detailAreaObject.transform, false);
            LayoutElement textStackLayout = textStackObject.AddComponent<LayoutElement>();
            textStackLayout.flexibleWidth = 1f;
            textStackLayout.flexibleHeight = 1f;

            VerticalLayoutGroup detailLayout = textStackObject.AddComponent<VerticalLayoutGroup>();
            detailLayout.spacing = 8f;
            detailLayout.childControlWidth = true;
            detailLayout.childForceExpandWidth = true;
            detailLayout.childControlHeight = false;
            detailLayout.childForceExpandHeight = false;

            TextMeshProUGUI selectedNameText = CreateStatRow(textStackObject.transform, "No Recipe Selected");
            TextMeshProUGUI requiredMaterialText = CreateStatRow(textStackObject.transform, "Materials: -");

            Button craftButton = CreateButton(textStackObject.transform, "CraftButton", "Craft", out TextMeshProUGUI _);
            SetFixedLayoutHeight(craftButton.gameObject, 44f);

            UiForgeCraftingPanel craftingPanel = groupObject.AddComponent<UiForgeCraftingPanel>();
            craftingPanel.InventoryCache = inventoryCache;
            craftingPanel.NetworkClient = networkClient;
            craftingPanel.RowContainer = recipeContent;
            craftingPanel.RowPrefab = recipeRowPrefabAsset.GetComponent<UiForgeRecipeRow>();
            craftingPanel.SelectedRecipeNameText = selectedNameText;
            craftingPanel.RequiredMaterialText = requiredMaterialText;
            craftingPanel.CraftButton = craftButton;
            craftingPanel.ItemViewer = craftItemViewer;
            craftingPanel.SufficientStockColor = Color.white;
            craftingPanel.InsufficientStockColor = new Color(1f, 0.35f, 0.35f, 1f);

            AssignAssetRegistry(craftingPanel, assetRegistry);

            return groupObject;
        }

        // Shared by BuildForgeCraftingGroup/BuildEquipmentRerollGroup - a
        // compact (90x90) render-texture item preview, mirroring
        // UiCodex3DViewer's approach at Forge detail-panel scale. Each
        // caller passes its own distinct layerName/rigName (see
        // EnsureLayerExists's call site comment for why Craft and Reroll
        // cannot share one).
        private static UiForgeItemViewer BuildForgeItemViewer(Transform parent, Transform riggingParent, AssetLifecycleCoordinator assetCoordinator, string layerName, string rigName)
        {
            GameObject viewerPanelObject = new GameObject("ItemViewerPanel", typeof(RectTransform));
            viewerPanelObject.transform.SetParent(parent, false);
            LayoutElement viewerLayout = viewerPanelObject.AddComponent<LayoutElement>();
            viewerLayout.preferredWidth = 90f;
            viewerLayout.preferredHeight = 90f;
            viewerPanelObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            GameObject previewImageObject = new GameObject("PreviewImage", typeof(RectTransform));
            previewImageObject.transform.SetParent(viewerPanelObject.transform, false);
            StretchFull((RectTransform)previewImageObject.transform);
            RawImage previewImage = previewImageObject.AddComponent<RawImage>();
            previewImage.color = Color.white;

            GameObject rigObject = new GameObject(rigName);
            rigObject.transform.SetParent(riggingParent, false);

            GameObject cameraObject = new GameObject(rigName + "Camera", typeof(Camera));
            cameraObject.transform.SetParent(rigObject.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -5f);
            Camera previewCamera = cameraObject.GetComponent<Camera>();

            GameObject modelAnchorObject = new GameObject("ModelAnchor");
            modelAnchorObject.transform.SetParent(rigObject.transform, false);

            UiForgeItemViewer viewer = viewerPanelObject.AddComponent<UiForgeItemViewer>();
            viewer.AssetCoordinator = assetCoordinator;
            viewer.PreviewLayerName = layerName;
            viewer.PreviewCamera = previewCamera;
            viewer.PreviewImage = previewImage;
            viewer.ModelAnchor = modelAnchorObject.transform;

            return viewer;
        }

        // Modul: Play Mode audit fix. Top ~55% owned-equipment candidate
        // list (real UiForgeFusionCandidateRow instances over
        // EquipmentInventoryCache), bottom ~45% three slot selectors
        // (Target + 2 Sacrifices) plus the Fuse button - see
        // UiForgeFusionPanel's own header comment for why ForgeSplicingEngine.
        // ExecuteFusionAsync had a working sender with no caller anywhere.
        private static GameObject BuildForgeFusionGroup(Transform parent, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient)
        {
            GameObject groupObject = new GameObject("FusionGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            GameObject listAreaObject = new GameObject("FusionCandidateListArea", typeof(RectTransform));
            listAreaObject.transform.SetParent(groupObject.transform, false);
            RectTransform listAreaRect = (RectTransform)listAreaObject.transform;
            listAreaRect.anchorMin = new Vector2(0f, 0.45f);
            listAreaRect.anchorMax = new Vector2(1f, 1f);
            listAreaRect.offsetMin = Vector2.zero;
            listAreaRect.offsetMax = Vector2.zero;

            (ScrollRect _, RectTransform rowContent) = ChatSceneBuilder.BuildScrollView(listAreaRect);

            GameObject rowPrefabAsset = BuildAndSaveForgeFusionRowPrefab();

            GameObject detailAreaObject = new GameObject("FusionDetailPanel", typeof(RectTransform));
            detailAreaObject.transform.SetParent(groupObject.transform, false);
            RectTransform detailAreaRect = (RectTransform)detailAreaObject.transform;
            detailAreaRect.anchorMin = new Vector2(0f, 0f);
            detailAreaRect.anchorMax = new Vector2(1f, 0.45f);
            detailAreaRect.offsetMin = Vector2.zero;
            detailAreaRect.offsetMax = new Vector2(0f, -12f);

            VerticalLayoutGroup detailLayout = detailAreaObject.AddComponent<VerticalLayoutGroup>();
            detailLayout.spacing = 6f;
            detailLayout.childControlWidth = true;
            detailLayout.childForceExpandWidth = true;
            detailLayout.childControlHeight = false;
            detailLayout.childForceExpandHeight = false;

            TextMeshProUGUI targetSlotText = CreateStatRow(detailAreaObject.transform, "Target: (none)");
            Button selectTargetButton = CreateButton(detailAreaObject.transform, "SelectTargetButton", "Select Target", out TextMeshProUGUI _);
            SetFixedLayoutHeight(selectTargetButton.gameObject, 36f);

            TextMeshProUGUI sac1SlotText = CreateStatRow(detailAreaObject.transform, "Sacrifice 1: (none)");
            Button selectSac1Button = CreateButton(detailAreaObject.transform, "SelectSacrifice1Button", "Select Sacrifice 1", out TextMeshProUGUI _);
            SetFixedLayoutHeight(selectSac1Button.gameObject, 36f);

            TextMeshProUGUI sac2SlotText = CreateStatRow(detailAreaObject.transform, "Sacrifice 2: (none)");
            Button selectSac2Button = CreateButton(detailAreaObject.transform, "SelectSacrifice2Button", "Select Sacrifice 2", out TextMeshProUGUI _);
            SetFixedLayoutHeight(selectSac2Button.gameObject, 36f);

            Button fuseButton = CreateButton(detailAreaObject.transform, "FuseButton", "Fuse", out TextMeshProUGUI _);
            SetFixedLayoutHeight(fuseButton.gameObject, 44f);

            TextMeshProUGUI statusText = CreateStatRow(detailAreaObject.transform, string.Empty);

            UiForgeFusionPanel fusionPanel = groupObject.AddComponent<UiForgeFusionPanel>();
            fusionPanel.InventoryCache = inventoryCache;
            fusionPanel.NetworkClient = networkClient;
            fusionPanel.RowContainer = rowContent;
            fusionPanel.RowPrefab = rowPrefabAsset.GetComponent<UiForgeFusionCandidateRow>();
            fusionPanel.TargetSlotText = targetSlotText;
            fusionPanel.Sacrifice1SlotText = sac1SlotText;
            fusionPanel.Sacrifice2SlotText = sac2SlotText;
            fusionPanel.SelectTargetButton = selectTargetButton;
            fusionPanel.SelectSacrifice1Button = selectSac1Button;
            fusionPanel.SelectSacrifice2Button = selectSac2Button;
            fusionPanel.FuseButton = fuseButton;
            fusionPanel.StatusText = statusText;

            return groupObject;
        }

        // Top 50% equipment list (real UiForgeEquipmentRow instances),
        // bottom 50% detail panel - a compact 3D item preview
        // (UiForgeItemViewer) to the left; selected item name, 4 fixed
        // affix slot rows (each: highlight bar + label + Select button),
        // reroll cost text, Reroll button to the right. Real,
        // network-wired UiEquipmentRerollPanel (CommandType via
        // SendRerollCommandZeroAlloc/SendEquipItemCommandZeroAlloc).
        private static GameObject BuildEquipmentRerollGroup(Transform parent, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient, VisualSyncProxy syncProxy, AssetRegistry assetRegistry, AssetLifecycleCoordinator assetCoordinator, Transform riggingParent)
        {
            GameObject groupObject = new GameObject("RerollGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            GameObject listAreaObject = new GameObject("EquipmentListArea", typeof(RectTransform));
            listAreaObject.transform.SetParent(groupObject.transform, false);
            RectTransform listAreaRect = (RectTransform)listAreaObject.transform;
            listAreaRect.anchorMin = new Vector2(0f, 0.5f);
            listAreaRect.anchorMax = new Vector2(1f, 1f);
            listAreaRect.offsetMin = Vector2.zero;
            listAreaRect.offsetMax = Vector2.zero;

            (ScrollRect _, RectTransform equipmentContent) = ChatSceneBuilder.BuildScrollView(listAreaRect);

            GameObject equipmentRowPrefabAsset = BuildAndSaveForgeEquipmentRowPrefab();

            GameObject detailAreaObject = new GameObject("RerollDetailPanel", typeof(RectTransform));
            detailAreaObject.transform.SetParent(groupObject.transform, false);
            RectTransform detailAreaRect = (RectTransform)detailAreaObject.transform;
            detailAreaRect.anchorMin = new Vector2(0f, 0f);
            detailAreaRect.anchorMax = new Vector2(1f, 0.5f);
            detailAreaRect.offsetMin = Vector2.zero;
            detailAreaRect.offsetMax = new Vector2(0f, -12f);

            HorizontalLayoutGroup detailRowLayout = detailAreaObject.AddComponent<HorizontalLayoutGroup>();
            detailRowLayout.spacing = 10f;
            detailRowLayout.childControlWidth = true;
            detailRowLayout.childForceExpandWidth = false;
            detailRowLayout.childControlHeight = true;
            detailRowLayout.childForceExpandHeight = false;

            UiForgeItemViewer rerollItemViewer = BuildForgeItemViewer(detailAreaObject.transform, riggingParent, assetCoordinator, "UI_3D_Preview_ForgeReroll", "ForgeRerollPreviewRig");

            GameObject textStackObject = new GameObject("DetailTextStack", typeof(RectTransform));
            textStackObject.transform.SetParent(detailAreaObject.transform, false);
            LayoutElement textStackLayout = textStackObject.AddComponent<LayoutElement>();
            textStackLayout.flexibleWidth = 1f;
            textStackLayout.flexibleHeight = 1f;

            VerticalLayoutGroup detailLayout = textStackObject.AddComponent<VerticalLayoutGroup>();
            detailLayout.spacing = 6f;
            detailLayout.childControlWidth = true;
            detailLayout.childForceExpandWidth = true;
            detailLayout.childControlHeight = false;
            detailLayout.childForceExpandHeight = false;

            TextMeshProUGUI selectedItemNameText = CreateStatRow(textStackObject.transform, "No Item Selected");

            TextMeshProUGUI[] affixTexts = new TextMeshProUGUI[4];
            Button[] affixButtons = new Button[4];
            GameObject[] affixHighlights = new GameObject[4];
            for (int i = 0; i < 4; i++)
            {
                GameObject affixRowObject = new GameObject("AffixSlotRow" + i, typeof(RectTransform));
                affixRowObject.transform.SetParent(textStackObject.transform, false);
                SetFixedLayoutHeight(affixRowObject, 30f);

                HorizontalLayoutGroup affixRowLayoutGroup = affixRowObject.AddComponent<HorizontalLayoutGroup>();
                affixRowLayoutGroup.spacing = 6f;
                affixRowLayoutGroup.childControlWidth = true;
                affixRowLayoutGroup.childForceExpandWidth = false;
                affixRowLayoutGroup.childControlHeight = true;
                affixRowLayoutGroup.childForceExpandHeight = true;

                GameObject highlightObject = new GameObject("SelectedHighlight", typeof(RectTransform));
                highlightObject.transform.SetParent(affixRowObject.transform, false);
                LayoutElement highlightLayout = highlightObject.AddComponent<LayoutElement>();
                highlightLayout.preferredWidth = 10f;
                highlightObject.AddComponent<Image>().color = new Color(0.3f, 0.7f, 1f, 1f);
                highlightObject.SetActive(false);

                TextMeshProUGUI affixText = CreateText(affixRowObject.transform, "AffixText", "Affix " + (i + 1) + ": -", 14f, TextAlignmentOptions.MidlineLeft);
                LayoutElement affixTextLayout = affixText.gameObject.AddComponent<LayoutElement>();
                affixTextLayout.flexibleWidth = 1f;

                Button selectButton = CreateButton(affixRowObject.transform, "SelectButton", "Select", out TextMeshProUGUI _);
                LayoutElement selectButtonLayout = selectButton.gameObject.AddComponent<LayoutElement>();
                selectButtonLayout.preferredWidth = 90f;

                affixTexts[i] = affixText;
                affixButtons[i] = selectButton;
                affixHighlights[i] = highlightObject;
            }

            TextMeshProUGUI rerollCostText = CreateStatRow(textStackObject.transform, "Cost: -");

            Button rerollButton = CreateButton(textStackObject.transform, "RerollButton", "Reroll", out TextMeshProUGUI _);
            SetFixedLayoutHeight(rerollButton.gameObject, 44f);

            // Modul: Affix System Unification. The rules were nowhere on
            // screen: nothing said how many affixes a rarity grants, what a
            // reroll costs, or that a reroll can only produce an affix legal
            // for that item's slot. All three are GDD-specified and all three
            // are things a player has to know before spending diamonds.
            CreateHelpText(textStackObject.transform, "RerollRulesText",
                "Rarity sets the affix count: Normal to Uncommon 1, Rare to Epic 2, Legendary to Relic 3, Ancient to Demonic 4, Godly and Transcendent 5.",
                46f);
            CreateHelpText(textStackObject.transform, "RerollCostRulesText",
                "A reroll costs floor(5 x 1.35 ^ (rarity - 1)) Diamonds, so 5 at Normal and 247 at Transcendent. It replaces one affix with another that is legal for this item's slot, and never returns the same affix.",
                58f);
            CreateHelpText(textStackObject.transform, "RerollLockRulesText",
                "An affix locked by a failed Forge fusion can never be rerolled again.",
                28f);

            UiEquipmentRerollPanel rerollPanel = groupObject.AddComponent<UiEquipmentRerollPanel>();
            rerollPanel.InventoryCache = inventoryCache;
            rerollPanel.NetworkClient = networkClient;
            rerollPanel.SyncProxy = syncProxy;
            rerollPanel.RowContainer = equipmentContent;
            rerollPanel.RowPrefab = equipmentRowPrefabAsset.GetComponent<UiForgeEquipmentRow>();
            rerollPanel.SelectedItemNameText = selectedItemNameText;
            rerollPanel.AffixSlotTexts = affixTexts;
            rerollPanel.AffixSlotButtons = affixButtons;
            rerollPanel.AffixSlotSelectedHighlights = affixHighlights;
            rerollPanel.RerollCostText = rerollCostText;
            rerollPanel.RerollButton = rerollButton;
            rerollPanel.ItemViewer = rerollItemViewer;
            rerollPanel.AffordableCostColor = Color.white;
            rerollPanel.UnaffordableCostColor = new Color(1f, 0.35f, 0.35f, 1f);

            AssignAssetRegistry(rerollPanel, assetRegistry);

            return groupObject;
        }

        private static GameObject BuildAndSaveForgeRecipeRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiForgeRecipeRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 32f);
            Image background = root.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.04f);
            Button rowButton = root.AddComponent<Button>();
            rowButton.targetGraphic = background;

            GameObject selectedHighlight = new GameObject("SelectedHighlight", typeof(RectTransform));
            selectedHighlight.transform.SetParent(root.transform, false);
            StretchFull((RectTransform)selectedHighlight.transform);
            Image highlightImage = selectedHighlight.AddComponent<Image>();
            highlightImage.color = new Color(0.3f, 0.7f, 1f, 0.3f);
            highlightImage.raycastTarget = false;
            selectedHighlight.SetActive(false);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Recipe", 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(8f, 0f);
            rowTextRect.offsetMax = new Vector2(-8f, 0f);

            UiForgeRecipeRow rowComponent = root.AddComponent<UiForgeRecipeRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.RowButton = rowButton;
            rowComponent.SelectedHighlight = selectedHighlight;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, ForgeRecipeRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiForgeRecipeRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveForgeEquipmentRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiForgeEquipmentRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 32f);
            Image background = root.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.04f);
            Button rowButton = root.AddComponent<Button>();
            rowButton.targetGraphic = background;

            GameObject selectedHighlight = new GameObject("SelectedHighlight", typeof(RectTransform));
            selectedHighlight.transform.SetParent(root.transform, false);
            StretchFull((RectTransform)selectedHighlight.transform);
            Image highlightImage = selectedHighlight.AddComponent<Image>();
            highlightImage.color = new Color(0.3f, 0.7f, 1f, 0.3f);
            highlightImage.raycastTarget = false;
            selectedHighlight.SetActive(false);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Item", 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(8f, 0f);
            rowTextRect.offsetMax = new Vector2(-96f, 0f);

            GameObject lockedIcon = new GameObject("LockedIcon", typeof(RectTransform));
            lockedIcon.transform.SetParent(root.transform, false);
            RectTransform lockedIconRect = (RectTransform)lockedIcon.transform;
            lockedIconRect.anchorMin = new Vector2(1f, 0.5f);
            lockedIconRect.anchorMax = new Vector2(1f, 0.5f);
            lockedIconRect.pivot = new Vector2(1f, 0.5f);
            lockedIconRect.sizeDelta = new Vector2(18f, 18f);
            lockedIconRect.anchoredPosition = new Vector2(-84f, 0f);
            Image lockedImage = lockedIcon.AddComponent<Image>();
            lockedImage.color = new Color(0.9f, 0.75f, 0.1f, 1f);
            lockedImage.raycastTarget = false;
            lockedIcon.SetActive(false);

            GameObject equippedIcon = new GameObject("EquippedIcon", typeof(RectTransform));
            equippedIcon.transform.SetParent(root.transform, false);
            RectTransform equippedIconRect = (RectTransform)equippedIcon.transform;
            equippedIconRect.anchorMin = new Vector2(1f, 0.5f);
            equippedIconRect.anchorMax = new Vector2(1f, 0.5f);
            equippedIconRect.pivot = new Vector2(1f, 0.5f);
            equippedIconRect.sizeDelta = new Vector2(18f, 18f);
            equippedIconRect.anchoredPosition = new Vector2(-60f, 0f);
            Image equippedImage = equippedIcon.AddComponent<Image>();
            equippedImage.color = new Color(0.2f, 0.85f, 0.2f, 1f);
            equippedImage.raycastTarget = false;
            equippedIcon.SetActive(false);

            Button equipButton = CreateButton(root.transform, "EquipButton", "Eq", out TextMeshProUGUI _);
            RectTransform equipButtonRect = (RectTransform)equipButton.transform;
            equipButtonRect.anchorMin = new Vector2(1f, 0.1f);
            equipButtonRect.anchorMax = new Vector2(1f, 0.9f);
            equipButtonRect.pivot = new Vector2(1f, 0.5f);
            equipButtonRect.sizeDelta = new Vector2(50f, 0f);
            equipButtonRect.anchoredPosition = new Vector2(-4f, 0f);

            UiForgeEquipmentRow rowComponent = root.AddComponent<UiForgeEquipmentRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.RowButton = rowButton;
            rowComponent.SelectedHighlight = selectedHighlight;
            rowComponent.LockedIcon = lockedIcon;
            rowComponent.EquipButton = equipButton;
            rowComponent.EquippedIcon = equippedIcon;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, ForgeEquipmentRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiForgeEquipmentRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        // ------------------------------------------------------------
        // Skill Tree - 4 fixed skill nodes (SkillId 1-4), real, network-
        // wired UiSkillTreeWindow (CommandType 57 = RequestUnlockSkill).
        // ------------------------------------------------------------
        private static GameObject BuildSkillTreeWindow(Transform canvasTransform, WebSocketClient networkClient, VisualSyncProxy syncProxy)
        {
            GameObject windowObject = BuildSimpleListWindowShell("SkillTreeWindow", canvasTransform, "Skill Tree", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI pointsText = CreateText(contentAreaRect, "AvailableSkillPointsText", "Skill Points: 0", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform pointsRect = (RectTransform)pointsText.transform;
            pointsRect.anchorMin = new Vector2(0f, 1f);
            pointsRect.anchorMax = new Vector2(1f, 1f);
            pointsRect.pivot = new Vector2(0.5f, 1f);
            pointsRect.sizeDelta = new Vector2(0f, 26f);
            pointsRect.anchoredPosition = Vector2.zero;

            GameObject nodesAreaObject = new GameObject("NodesArea", typeof(RectTransform));
            nodesAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform nodesAreaRect = (RectTransform)nodesAreaObject.transform;
            nodesAreaRect.anchorMin = Vector2.zero;
            nodesAreaRect.anchorMax = Vector2.one;
            nodesAreaRect.offsetMin = Vector2.zero;
            nodesAreaRect.offsetMax = new Vector2(0f, -34f);

            VerticalLayoutGroup nodesLayout = nodesAreaObject.AddComponent<VerticalLayoutGroup>();
            nodesLayout.spacing = 10f;
            nodesLayout.childControlWidth = true;
            nodesLayout.childForceExpandWidth = true;
            nodesLayout.childControlHeight = false;
            nodesLayout.childForceExpandHeight = false;

            (Button unlock1, TMP_Text text1, GameObject overlay1, Image icon1) = BuildSkillNode(nodesAreaObject.transform, "SkillNode1");
            (Button unlock2, TMP_Text text2, GameObject overlay2, Image icon2) = BuildSkillNode(nodesAreaObject.transform, "SkillNode2");
            (Button unlock3, TMP_Text text3, GameObject overlay3, Image icon3) = BuildSkillNode(nodesAreaObject.transform, "SkillNode3");
            (Button unlock4, TMP_Text text4, GameObject overlay4, Image icon4) = BuildSkillNode(nodesAreaObject.transform, "SkillNode4");

            UiSkillTreeWindow window = windowObject.AddComponent<UiSkillTreeWindow>();
            window.NetworkClient = networkClient;
            window.SyncProxy = syncProxy;
            window.AvailableSkillPointsText = pointsText;
            window.UnlockButton1 = unlock1; window.NodeText1 = text1; window.UnlockedOverlay1 = overlay1; window.NodeIcon1 = icon1;
            window.UnlockButton2 = unlock2; window.NodeText2 = text2; window.UnlockedOverlay2 = overlay2; window.NodeIcon2 = icon2;
            window.UnlockButton3 = unlock3; window.NodeText3 = text3; window.UnlockedOverlay3 = overlay3; window.NodeIcon3 = icon3;
            window.UnlockButton4 = unlock4; window.NodeText4 = text4; window.UnlockedOverlay4 = overlay4; window.NodeIcon4 = icon4;

            return windowObject;
        }

        private static (Button unlockButton, TMP_Text nodeText, GameObject unlockedOverlay, Image nodeIcon) BuildSkillNode(Transform parent, string nodeName)
        {
            GameObject nodeObject = new GameObject(nodeName, typeof(RectTransform));
            nodeObject.transform.SetParent(parent, false);
            SetFixedLayoutHeight(nodeObject, 90f);
            nodeObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);

            HorizontalLayoutGroup nodeLayoutGroup = nodeObject.AddComponent<HorizontalLayoutGroup>();
            nodeLayoutGroup.padding = new RectOffset(8, 8, 8, 8);
            nodeLayoutGroup.spacing = 10f;
            nodeLayoutGroup.childControlWidth = false;
            nodeLayoutGroup.childForceExpandWidth = false;
            nodeLayoutGroup.childControlHeight = true;
            nodeLayoutGroup.childForceExpandHeight = true;

            GameObject iconObject = new GameObject("NodeIcon", typeof(RectTransform));
            iconObject.transform.SetParent(nodeObject.transform, false);
            LayoutElement iconLayout = iconObject.AddComponent<LayoutElement>();
            iconLayout.preferredWidth = 64f;
            Image nodeIcon = iconObject.AddComponent<Image>();
            nodeIcon.color = new Color(1f, 1f, 1f, 0.8f);

            TMP_Text nodeText = CreateText(nodeObject.transform, "NodeText", "Skill", 13f, TextAlignmentOptions.MidlineLeft);
            LayoutElement nodeTextLayout = nodeText.gameObject.AddComponent<LayoutElement>();
            nodeTextLayout.flexibleWidth = 1f;

            Button unlockButton = CreateButton(nodeObject.transform, "UnlockButton", "Unlock", out TextMeshProUGUI _);
            LayoutElement unlockLayout = unlockButton.gameObject.AddComponent<LayoutElement>();
            unlockLayout.preferredWidth = 90f;

            // Modul: this overlay must be excluded from nodeObject's
            // HorizontalLayoutGroup (ignoreLayout = true) - otherwise the
            // layout group would treat it as a fourth item in the icon/
            // text/button row instead of a full-node stretch overlay.
            GameObject unlockedOverlay = new GameObject("UnlockedOverlay", typeof(RectTransform));
            unlockedOverlay.transform.SetParent(nodeObject.transform, false);
            LayoutElement overlayLayoutElement = unlockedOverlay.AddComponent<LayoutElement>();
            overlayLayoutElement.ignoreLayout = true;
            StretchFull((RectTransform)unlockedOverlay.transform);
            Image overlayImage = unlockedOverlay.AddComponent<Image>();
            overlayImage.color = new Color(0.2f, 0.85f, 0.2f, 0.2f);
            overlayImage.raycastTarget = false;
            unlockedOverlay.SetActive(false);

            return (unlockButton, nodeText, unlockedOverlay, nodeIcon);
        }

        // ------------------------------------------------------------
        // Village - resource strip (HUD overlay, top-left) plus a
        // separate building-list window with the 8 fixed building rows
        // and their timed-upgrade progress bars.
        // ------------------------------------------------------------
        private static void BuildVillageResourceStrip(Transform hudGroupTransform, VisualSyncProxy syncProxy)
        {
            GameObject panelObject = new GameObject("VillageResourceStrip", typeof(RectTransform));
            panelObject.transform.SetParent(hudGroupTransform, false);
            RectTransform panelRect = (RectTransform)panelObject.transform;
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            // Modul: below SaveTrustIndicator, which the builder now
            // positions at (20,-300) with a 20px-tall label (bottom edge at
            // y=-320) - this strip starts at -330 to leave a clean gap.
            panelRect.anchoredPosition = new Vector2(20f, -330f);
            panelRect.sizeDelta = new Vector2(260f, 76f);

            panelObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            VerticalLayoutGroup layout = panelObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.spacing = 2f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            UiVillageOverviewPanel panel = panelObject.AddComponent<UiVillageOverviewPanel>();
            panel.SyncProxy = syncProxy;
            panel.WoodStockText = CreateStatRow(panelObject.transform, "Wood: 0 / 0 (+0.0/s)");
            panel.StoneStockText = CreateStatRow(panelObject.transform, "Stone: 0 / 0 (+0.0/s)");
            panel.IronStockText = CreateStatRow(panelObject.transform, "Iron: 0 / 0 (+0.0/s)");
            panel.NormalStockColor = Color.white;
            panel.FullStockColor = new Color(1f, 0.35f, 0.35f, 1f);
        }

        private static GameObject BuildVillageWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("VillageWindow", canvasTransform, "Village", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(contentAreaRect);

            // Modul: UI rework. Every row now carries a one-line
            // description of what the building actually does, taken from
            // what VillageManagementEngine and the consuming engines really
            // read that level for - not marketing copy. Before this the
            // screen was ten identical name/level/button rows with no
            // indication of what any of them were for.
            UiVillageBuildingRow forgeRow = BuildVillageBuildingRow(content, "ForgeRow", 1, "Forge", "Unlocks higher equipment crafting and reroll tiers.");
            UiVillageBuildingRow innRow = BuildVillageBuildingRow(content, "InnRow", 2, "Inn", "Speeds up how fast newly bred children mature.");
            UiVillageBuildingRow breedingRow = BuildVillageBuildingRow(content, "BreedingGroundsRow", 3, "Breeding Grounds", "Raises your population cap and breeding quality.");
            UiVillageBuildingRow academyRow = BuildVillageBuildingRow(content, "MentorshipAcademyRow", 4, "Mentorship Academy", "Adds a mentor slot per level - each mentor boosts XP gain.");
            UiVillageBuildingRow lumberjackRow = BuildVillageBuildingRow(content, "LumberjackRow", 5, "Lumberjack", "Passive Wood income while you are away.");
            UiVillageBuildingRow quarryRow = BuildVillageBuildingRow(content, "QuarryRow", 6, "Quarry", "Passive Stone income while you are away.");
            UiVillageBuildingRow mineRow = BuildVillageBuildingRow(content, "MineRow", 7, "Mine", "Passive Ore income and better tool tiers.");
            UiVillageBuildingRow warehouseRow = BuildVillageBuildingRow(content, "WarehouseRow", 8, "Warehouse", "Raises how much passive income can bank up offline.");

            // Modul: Play Mode audit fix. Town Hall/Crafting Workshop (ids
            // 9/10) existed server-side with real upgrade logic (Town Hall
            // gates every other building's max level, the Workshop boosts
            // crafting rarity odds) but had no UI row at all, so every
            // other building was permanently stuck at the level-2 ceiling
            // with no way to raise it.
            UiVillageBuildingRow townHallRow = BuildVillageBuildingRow(content, "TownHallRow", 9, "Town Hall", "Raises the level cap of EVERY building to 2 + 2 per Town Hall level. Also boosts passive gold.");
            UiVillageBuildingRow craftingWorkshopRow = BuildVillageBuildingRow(content, "CraftingWorkshopRow", 10, "Crafting Workshop", "Improves the rarity odds on everything you craft.");

            UiVillageOverviewWindow window = windowObject.AddComponent<UiVillageOverviewWindow>();
            window.SyncProxy = syncProxy;
            window.NetworkClient = networkClient;
            window.ForgeRow = forgeRow;
            window.InnRow = innRow;
            window.BreedingGroundsRow = breedingRow;
            window.MentorshipAcademyRow = academyRow;
            window.LumberjackRow = lumberjackRow;
            window.QuarryRow = quarryRow;
            window.MineRow = mineRow;
            window.WarehouseRow = warehouseRow;
            window.TownHallRow = townHallRow;
            window.CraftingWorkshopRow = craftingWorkshopRow;

            return windowObject;
        }

        // Fixed, uniquely-named building row - not pooled, matching
        // UiVillageOverviewWindow's 8 named-field wiring convention (a
        // small fixed roster of building ids, not a data-driven list).
        // ProgressBarFill here is an Image (Type.Filled/FillMethod.
        // Horizontal, driven via .fillAmount) - a different construction
        // than BuildAnchoredProgressBar's RectTransform.anchorMax.x
        // pattern, matching UiVillageBuildingRow.ProgressBarFill's actual
        // field type (Image, not RectTransform).
        private static UiVillageBuildingRow BuildVillageBuildingRow(Transform parent, string rowName, int buildingId, string displayName, string description)
        {
            GameObject rowObject = new GameObject(rowName, typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);
            ((RectTransform)rowObject.transform).sizeDelta = new Vector2(0f, 104f);
            SetFixedLayoutHeight(rowObject, 104f);
            rowObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);

            VerticalLayoutGroup rowLayoutGroup = rowObject.AddComponent<VerticalLayoutGroup>();
            rowLayoutGroup.padding = new RectOffset(8, 8, 6, 6);
            rowLayoutGroup.spacing = 2f;
            rowLayoutGroup.childControlWidth = true;
            rowLayoutGroup.childForceExpandWidth = true;
            // Modul: UI rework. childControlHeight was false here, but every
            // child of this group is a bare GameObject created in this
            // method with a default (zero-height) RectTransform and a
            // LayoutElement.preferredHeight - which a group with
            // childControlHeight = false ignores entirely. The result was a
            // zero-height header row, so the Upgrade buttons kept their own
            // default size and spilled across two rows each. Every child
            // here declares a preferred height, so letting the group apply
            // it is both correct and what the call sites already intended.
            rowLayoutGroup.childControlHeight = true;
            rowLayoutGroup.childForceExpandHeight = false;

            GameObject headerRowObject = new GameObject("HeaderRow", typeof(RectTransform));
            headerRowObject.transform.SetParent(rowObject.transform, false);
            SetFixedLayoutHeight(headerRowObject, 26f);

            HorizontalLayoutGroup headerLayoutGroup = headerRowObject.AddComponent<HorizontalLayoutGroup>();
            headerLayoutGroup.spacing = 8f;
            headerLayoutGroup.childControlWidth = true;
            headerLayoutGroup.childForceExpandWidth = false;
            headerLayoutGroup.childControlHeight = true;
            headerLayoutGroup.childForceExpandHeight = true;

            TextMeshProUGUI nameText = CreateText(headerRowObject.transform, "BuildingNameText", displayName, 15f, TextAlignmentOptions.MidlineLeft);
            LayoutElement nameLayout = nameText.gameObject.AddComponent<LayoutElement>();
            nameLayout.flexibleWidth = 1f;

            TextMeshProUGUI levelText = CreateText(headerRowObject.transform, "LevelText", "Lv. 0", 14f, TextAlignmentOptions.MidlineRight);
            LayoutElement levelLayout = levelText.gameObject.AddComponent<LayoutElement>();
            levelLayout.preferredWidth = 60f;

            Button upgradeButton = CreateButton(headerRowObject.transform, "UpgradeButton", "Upgrade", out TextMeshProUGUI _);
            LayoutElement upgradeLayout = upgradeButton.gameObject.AddComponent<LayoutElement>();
            upgradeLayout.preferredWidth = 100f;

            GameObject progressBarRoot = new GameObject("ProgressBarRoot", typeof(RectTransform));
            progressBarRoot.transform.SetParent(rowObject.transform, false);
            SetFixedLayoutHeight(progressBarRoot, 20f);

            HorizontalLayoutGroup progressLayoutGroup = progressBarRoot.AddComponent<HorizontalLayoutGroup>();
            progressLayoutGroup.spacing = 6f;
            progressLayoutGroup.childControlWidth = true;
            progressLayoutGroup.childForceExpandWidth = false;
            progressLayoutGroup.childControlHeight = true;
            progressLayoutGroup.childForceExpandHeight = true;

            GameObject fillBackgroundObject = new GameObject("ProgressBarFillBackground", typeof(RectTransform));
            fillBackgroundObject.transform.SetParent(progressBarRoot.transform, false);
            LayoutElement fillBackgroundLayout = fillBackgroundObject.AddComponent<LayoutElement>();
            fillBackgroundLayout.flexibleWidth = 1f;
            fillBackgroundObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            GameObject fillImageObject = new GameObject("ProgressBarFill", typeof(RectTransform));
            fillImageObject.transform.SetParent(fillBackgroundObject.transform, false);
            StretchFull((RectTransform)fillImageObject.transform);
            Image fillImage = fillImageObject.AddComponent<Image>();
            fillImage.color = new Color(0.9f, 0.7f, 0.2f, 1f);
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillAmount = 0f;

            TextMeshProUGUI remainingText = CreateText(progressBarRoot.transform, "ProgressRemainingText", "0s", 12f, TextAlignmentOptions.MidlineRight);
            LayoutElement remainingLayout = remainingText.gameObject.AddComponent<LayoutElement>();
            remainingLayout.preferredWidth = 50f;

            progressBarRoot.SetActive(false);

            TextMeshProUGUI descriptionText = CreateText(rowObject.transform, "DescriptionText", description, 12f, TextAlignmentOptions.TopLeft);
            descriptionText.color = new Color(1f, 1f, 1f, 0.55f);
            SetFixedLayoutHeight(descriptionText.gameObject, 32f);

            TextMeshProUGUI costText = CreateText(rowObject.transform, "CostText", string.Empty, 12f, TextAlignmentOptions.MidlineLeft);
            costText.color = new Color(0.95f, 0.82f, 0.45f, 1f);
            SetFixedLayoutHeight(costText.gameObject, 20f);

            UiVillageBuildingRow rowComponent = rowObject.AddComponent<UiVillageBuildingRow>();
            rowComponent.BuildingId = buildingId;
            rowComponent.BuildingNameText = nameText;
            rowComponent.LevelText = levelText;
            rowComponent.UpgradeButton = upgradeButton;
            rowComponent.ProgressBarRoot = progressBarRoot;
            rowComponent.ProgressBarFill = fillImage;
            rowComponent.ProgressRemainingText = remainingText;
            rowComponent.DescriptionText = descriptionText;
            rowComponent.CostText = costText;

            return rowComponent;
        }

        // ------------------------------------------------------------
        // Codex - Monsters (list + isolated 3D preview viewport) and
        // Regions (kill-completion milestones) sub-tabs. UiCodex3DViewer
        // forcibly re-centers its own ViewerPanelRect to anchor (0.5,0.5)
        // inside its own Awake() (matching the WorldBoss/EventCountdown/
        // CodexBonus self-positioning pattern already worked around
        // elsewhere in this file) - the panel below is built already
        // centered with a fixed sizeDelta inside its container so that
        // forced re-center is a no-op, not a layout-breaking surprise.
        // ------------------------------------------------------------
        private static GameObject BuildCodexWindow(Transform canvasTransform, AssetRegistry assetRegistry, AssetLifecycleCoordinator assetCoordinator, Transform riggingParent)
        {
            GameObject windowObject = new GameObject("CodexWindow", typeof(RectTransform));
            windowObject.transform.SetParent(canvasTransform, false);
            RectTransform windowRect = (RectTransform)windowObject.transform;
            // Modul: Map Hub. Fixed-pixel top/bottom insets instead of pure
            // percentage anchors - percentage margins compress along with
            // canvas height on any aspect ratio shorter than the 1080x1920
            // portrait reference, which let this window's own top content
            // (title/sub-tab header) collide with the persistent overlay
            // bars (Menu/Map buttons, Codex Bonus, Gold/Gems currency) and
            // the bottom Season Pass banner. Left/right stay percentage
            // since width scaling is already consistent (CanvasScaler
            // match-width).
            windowRect.anchorMin = new Vector2(0.04f, 0f);
            windowRect.anchorMax = new Vector2(0.96f, 1f);
            windowRect.offsetMin = new Vector2(0f, 70f);
            windowRect.offsetMax = new Vector2(0f, -180f);

            windowObject.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.96f);

            GameObject subTabHeaderObject = new GameObject("SubTabHeader", typeof(RectTransform));
            subTabHeaderObject.transform.SetParent(windowRect, false);
            RectTransform subTabHeaderRect = (RectTransform)subTabHeaderObject.transform;
            subTabHeaderRect.anchorMin = new Vector2(0f, 1f);
            subTabHeaderRect.anchorMax = new Vector2(1f, 1f);
            subTabHeaderRect.pivot = new Vector2(0.5f, 1f);
            subTabHeaderRect.sizeDelta = new Vector2(0f, 44f);
            subTabHeaderRect.anchoredPosition = new Vector2(0f, -12f);

            Button[] subTabButtons = BuildSubTabButtons(subTabHeaderRect, new[] { "Monsters", "Regions" });

            GameObject contentAreaObject = new GameObject("ContentArea", typeof(RectTransform));
            contentAreaObject.transform.SetParent(windowRect, false);
            RectTransform contentAreaRect = (RectTransform)contentAreaObject.transform;
            contentAreaRect.anchorMin = Vector2.zero;
            contentAreaRect.anchorMax = Vector2.one;
            contentAreaRect.offsetMin = new Vector2(20f, 20f);
            contentAreaRect.offsetMax = new Vector2(-20f, -64f);

            GameObject monstersGroup = BuildCodexMonstersGroup(contentAreaRect, assetRegistry, assetCoordinator, riggingParent);
            GameObject regionsGroup = BuildCodexRegionsGroup(contentAreaRect);

            regionsGroup.SetActive(false);

            UiTabGroup tabGroup = windowObject.AddComponent<UiTabGroup>();
            tabGroup.Groups = new[] { monstersGroup, regionsGroup };
            tabGroup.Buttons = subTabButtons;

            return windowObject;
        }

        // Left 55% - pooled monster list (real UiCodexListBinder, driving
        // UiCodex3DViewer.Instance.ShowMonster on row click). Right 45% -
        // a centered, fixed-size 3D preview viewport (real UiCodex3DViewer,
        // its own orthographic Camera rendering into a RenderTexture shown
        // via a RawImage). The preview camera/model anchor rig is parented
        // under Managers (riggingParent), not the Canvas, so it survives
        // ClearPreviousGeneratedHierarchy's "Managers" wipe/rebuild instead
        // of leaking a duplicate top-level scene root on every re-run.
        private static GameObject BuildCodexMonstersGroup(Transform parent, AssetRegistry assetRegistry, AssetLifecycleCoordinator assetCoordinator, Transform riggingParent)
        {
            GameObject groupObject = new GameObject("MonstersGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            GameObject listAreaObject = new GameObject("CodexListArea", typeof(RectTransform));
            listAreaObject.transform.SetParent(groupObject.transform, false);
            RectTransform listAreaRect = (RectTransform)listAreaObject.transform;
            listAreaRect.anchorMin = new Vector2(0f, 0f);
            listAreaRect.anchorMax = new Vector2(0.55f, 1f);
            listAreaRect.offsetMin = Vector2.zero;
            listAreaRect.offsetMax = new Vector2(-6f, 0f);

            (ScrollRect listScrollRect, RectTransform listContent) = ChatSceneBuilder.BuildScrollView(listAreaRect);

            GameObject listRowPrefabAsset = BuildAndSaveCodexListRowPrefab();

            UiCodexListBinder listBinder = listAreaObject.AddComponent<UiCodexListBinder>();
            listBinder.ListScrollRect = listScrollRect;
            listBinder.RowContainer = listContent;
            listBinder.RowPrefab = listRowPrefabAsset.GetComponent<UiCodexListRow>();
            AssignAssetRegistry(listBinder, assetRegistry);

            GameObject viewerContainerObject = new GameObject("Codex3DViewerContainer", typeof(RectTransform));
            viewerContainerObject.transform.SetParent(groupObject.transform, false);
            RectTransform viewerContainerRect = (RectTransform)viewerContainerObject.transform;
            viewerContainerRect.anchorMin = new Vector2(0.55f, 0f);
            viewerContainerRect.anchorMax = new Vector2(1f, 1f);
            viewerContainerRect.offsetMin = new Vector2(6f, 0f);
            viewerContainerRect.offsetMax = Vector2.zero;

            GameObject viewerPanelObject = new GameObject("Codex3DViewerPanel", typeof(RectTransform));
            viewerPanelObject.transform.SetParent(viewerContainerRect, false);
            RectTransform viewerPanelRect = (RectTransform)viewerPanelObject.transform;
            viewerPanelRect.anchorMin = new Vector2(0.5f, 0.5f);
            viewerPanelRect.anchorMax = new Vector2(0.5f, 0.5f);
            viewerPanelRect.pivot = new Vector2(0.5f, 0.5f);
            viewerPanelRect.anchoredPosition = Vector2.zero;
            viewerPanelRect.sizeDelta = new Vector2(280f, 280f);
            viewerPanelObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            GameObject previewImageObject = new GameObject("PreviewImage", typeof(RectTransform));
            previewImageObject.transform.SetParent(viewerPanelRect, false);
            StretchFull((RectTransform)previewImageObject.transform);
            RawImage previewImage = previewImageObject.AddComponent<RawImage>();
            previewImage.color = Color.white;

            GameObject rigObject = new GameObject("CodexPreviewRig");
            rigObject.transform.SetParent(riggingParent, false);

            GameObject cameraObject = new GameObject("CodexPreviewCamera", typeof(Camera));
            cameraObject.transform.SetParent(rigObject.transform, false);
            cameraObject.transform.localPosition = new Vector3(0f, 0f, -5f);
            Camera previewCamera = cameraObject.GetComponent<Camera>();

            GameObject modelAnchorObject = new GameObject("ModelAnchor");
            modelAnchorObject.transform.SetParent(rigObject.transform, false);

            UiCodex3DViewer viewer = viewerPanelObject.AddComponent<UiCodex3DViewer>();
            viewer.AssetCoordinator = assetCoordinator;
            viewer.ViewerPanelRect = viewerPanelRect;
            viewer.PreviewCamera = previewCamera;
            viewer.PreviewImage = previewImage;
            viewer.ModelAnchor = modelAnchorObject.transform;

            return groupObject;
        }

        private static GameObject BuildCodexRegionsGroup(Transform parent)
        {
            GameObject groupObject = new GameObject("RegionsGroup", typeof(RectTransform));
            groupObject.transform.SetParent(parent, false);
            StretchFull((RectTransform)groupObject.transform);

            (ScrollRect regionsScrollRect, RectTransform regionsContent) = ChatSceneBuilder.BuildScrollView((RectTransform)groupObject.transform);

            GameObject regionRowPrefabAsset = BuildAndSaveCodexRegionRowPrefab();

            UiCodexRegionsWindow regionsWindow = groupObject.AddComponent<UiCodexRegionsWindow>();
            regionsWindow.ListScrollRect = regionsScrollRect;
            regionsWindow.RowContainer = regionsContent;
            regionsWindow.RowPrefab = regionRowPrefabAsset.GetComponent<UiCodexRegionRow>();

            return groupObject;
        }

        private static GameObject BuildAndSaveCodexListRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiCodexListRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 30f);
            Image background = root.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.04f);
            Button rowButton = root.AddComponent<Button>();
            rowButton.targetGraphic = background;

            GameObject rowIconObject = new GameObject("RowIcon", typeof(RectTransform));
            rowIconObject.transform.SetParent(root.transform, false);
            RectTransform rowIconRect = (RectTransform)rowIconObject.transform;
            rowIconRect.anchorMin = new Vector2(0f, 0.5f);
            rowIconRect.anchorMax = new Vector2(0f, 0.5f);
            rowIconRect.pivot = new Vector2(0f, 0.5f);
            rowIconRect.anchoredPosition = new Vector2(4f, 0f);
            rowIconRect.sizeDelta = new Vector2(26f, 26f);
            Image rowIcon = rowIconObject.AddComponent<Image>();
            rowIcon.preserveAspect = true;
            rowIcon.enabled = false;

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Monster", 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(38f, 0f);
            rowTextRect.offsetMax = new Vector2(-8f, 0f);

            UiCodexListRow rowComponent = root.AddComponent<UiCodexListRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.RowButton = rowButton;
            rowComponent.RowIcon = rowIcon;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, CodexListRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiCodexListRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveCodexRegionRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiCodexRegionRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 64f);
            root.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);

            VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 4, 4);
            layout.spacing = 2f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            GameObject headerRow = new GameObject("HeaderRow", typeof(RectTransform));
            headerRow.transform.SetParent(root.transform, false);
            SetFixedLayoutHeight(headerRow, 18f);
            HorizontalLayoutGroup headerLayoutGroup = headerRow.AddComponent<HorizontalLayoutGroup>();
            headerLayoutGroup.childControlWidth = true;
            headerLayoutGroup.childForceExpandWidth = false;
            headerLayoutGroup.childControlHeight = true;
            headerLayoutGroup.childForceExpandHeight = true;

            TextMeshProUGUI regionText = CreateText(headerRow.transform, "RegionLabelText", "Region 0", 14f, TextAlignmentOptions.MidlineLeft);
            LayoutElement regionTextLayout = regionText.gameObject.AddComponent<LayoutElement>();
            regionTextLayout.flexibleWidth = 1f;

            GameObject completedBadge = new GameObject("CompletedBadge", typeof(RectTransform));
            completedBadge.transform.SetParent(headerRow.transform, false);
            LayoutElement completedBadgeLayout = completedBadge.AddComponent<LayoutElement>();
            completedBadgeLayout.preferredWidth = 60f;
            TextMeshProUGUI completedText = CreateText(completedBadge.transform, "CompletedText", "DONE", 12f, TextAlignmentOptions.MidlineRight);
            StretchFull((RectTransform)completedText.transform);
            completedText.color = new Color(0.2f, 0.85f, 0.2f, 1f);
            completedBadge.SetActive(false);

            GameObject progressRow = new GameObject("ProgressRow", typeof(RectTransform));
            progressRow.transform.SetParent(root.transform, false);
            SetFixedLayoutHeight(progressRow, 16f);
            HorizontalLayoutGroup progressRowLayoutGroup = progressRow.AddComponent<HorizontalLayoutGroup>();
            progressRowLayoutGroup.spacing = 6f;
            progressRowLayoutGroup.childControlWidth = true;
            progressRowLayoutGroup.childForceExpandWidth = false;
            progressRowLayoutGroup.childControlHeight = true;
            progressRowLayoutGroup.childForceExpandHeight = true;

            GameObject fillBackgroundObject = new GameObject("ProgressBarFillBackground", typeof(RectTransform));
            fillBackgroundObject.transform.SetParent(progressRow.transform, false);
            LayoutElement fillBackgroundLayout = fillBackgroundObject.AddComponent<LayoutElement>();
            fillBackgroundLayout.flexibleWidth = 1f;
            fillBackgroundObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            GameObject fillImageObject = new GameObject("ProgressBarFill", typeof(RectTransform));
            fillImageObject.transform.SetParent(fillBackgroundObject.transform, false);
            StretchFull((RectTransform)fillImageObject.transform);
            Image fillImage = fillImageObject.AddComponent<Image>();
            fillImage.color = new Color(0.4f, 0.8f, 1f, 1f);
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillAmount = 0f;

            TextMeshProUGUI progressText = CreateText(progressRow.transform, "ProgressLabelText", "0 / 0", 12f, TextAlignmentOptions.MidlineRight);
            LayoutElement progressTextLayout = progressText.gameObject.AddComponent<LayoutElement>();
            progressTextLayout.preferredWidth = 80f;

            TextMeshProUGUI bonusText = CreateText(root.transform, "BonusFlagText", string.Empty, 12f, TextAlignmentOptions.MidlineLeft);
            SetFixedLayoutHeight(bonusText.gameObject, 16f);
            bonusText.color = new Color(1f, 0.85f, 0.3f, 1f);
            bonusText.gameObject.SetActive(false);

            UiCodexRegionRow rowComponent = root.AddComponent<UiCodexRegionRow>();
            rowComponent.RegionLabelText = regionText;
            rowComponent.ProgressLabelText = progressText;
            rowComponent.ProgressBarFill = fillImage;
            rowComponent.CompletedBadge = completedBadge;
            rowComponent.BonusFlagText = bonusText;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, CodexRegionRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiCodexRegionRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        // ------------------------------------------------------------
        // Breeding Lab - roster list (top ~38%) plus a detail panel
        // (parent slot selection, 4 fixed gene-locus comparison rows,
        // eligibility/cost/inbreeding summary, Fuse Genes button).
        // ------------------------------------------------------------
        private static GameObject BuildBreedingLabWindow(Transform canvasTransform, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("BreedingLabWindow", canvasTransform, "Breeding Lab", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            GameObject rosterAreaObject = new GameObject("RosterArea", typeof(RectTransform));
            rosterAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform rosterAreaRect = (RectTransform)rosterAreaObject.transform;
            rosterAreaRect.anchorMin = new Vector2(0f, 0.62f);
            rosterAreaRect.anchorMax = new Vector2(1f, 1f);
            rosterAreaRect.offsetMin = Vector2.zero;
            rosterAreaRect.offsetMax = Vector2.zero;

            (ScrollRect rosterScrollRect, RectTransform rosterContent) = ChatSceneBuilder.BuildScrollView(rosterAreaRect);
            GameObject rosterRowPrefabAsset = BuildAndSaveBreedingRosterRowPrefab();

            GameObject detailAreaObject = new GameObject("BreedingDetailPanel", typeof(RectTransform));
            detailAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform detailAreaRect = (RectTransform)detailAreaObject.transform;
            detailAreaRect.anchorMin = Vector2.zero;
            detailAreaRect.anchorMax = new Vector2(1f, 0.62f);
            detailAreaRect.offsetMin = Vector2.zero;
            detailAreaRect.offsetMax = new Vector2(0f, -8f);

            VerticalLayoutGroup detailLayout = detailAreaObject.AddComponent<VerticalLayoutGroup>();
            detailLayout.spacing = 6f;
            detailLayout.childControlWidth = true;
            detailLayout.childForceExpandWidth = true;
            detailLayout.childControlHeight = false;
            detailLayout.childForceExpandHeight = false;

            GameObject slotRowObject = new GameObject("SlotRow", typeof(RectTransform));
            slotRowObject.transform.SetParent(detailAreaObject.transform, false);
            SetFixedLayoutHeight(slotRowObject, 34f);
            HorizontalLayoutGroup slotRowLayoutGroup = slotRowObject.AddComponent<HorizontalLayoutGroup>();
            slotRowLayoutGroup.spacing = 8f;
            slotRowLayoutGroup.childControlWidth = true;
            slotRowLayoutGroup.childForceExpandWidth = true;
            slotRowLayoutGroup.childControlHeight = true;
            slotRowLayoutGroup.childForceExpandHeight = true;

            Button selectAButton = CreateButton(slotRowObject.transform, "SelectParentAButton", string.Empty, out TextMeshProUGUI _);
            TextMeshProUGUI parentAText = CreateText(selectAButton.transform, "ParentASlotText", "Parent A: (none)", 13f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)parentAText.transform);

            Button selectBButton = CreateButton(slotRowObject.transform, "SelectParentBButton", string.Empty, out TextMeshProUGUI _);
            TextMeshProUGUI parentBText = CreateText(selectBButton.transform, "ParentBSlotText", "Parent B: (none)", 13f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)parentBText.transform);

            UiGeneVectorRenderer raceRenderer = BuildGeneVectorRow(detailAreaObject.transform, "RaceLocusRow");
            UiGeneVectorRenderer speedRenderer = BuildGeneVectorRow(detailAreaObject.transform, "SpeedLocusRow");
            UiGeneVectorRenderer critRenderer = BuildGeneVectorRow(detailAreaObject.transform, "CritLocusRow");
            UiGeneVectorRenderer yieldRenderer = BuildGeneVectorRow(detailAreaObject.transform, "YieldLocusRow");

            TextMeshProUGUI eligibilityText = CreateStatRow(detailAreaObject.transform, "Eligibility: -");
            TextMeshProUGUI costText = CreateStatRow(detailAreaObject.transform, "Cost: -");
            TextMeshProUGUI inbredRiskText = CreateStatRow(detailAreaObject.transform, string.Empty);

            Button fuseButton = CreateButton(detailAreaObject.transform, "FuseGenesButton", "Fuse Genes", out TextMeshProUGUI _);
            SetFixedLayoutHeight(fuseButton.gameObject, 44f);

            GameObject hatchingRoot = new GameObject("HatchingAnimationRoot", typeof(RectTransform));
            hatchingRoot.transform.SetParent(detailAreaObject.transform, false);
            SetFixedLayoutHeight(hatchingRoot, 20f);
            TextMeshProUGUI hatchingText = CreateText(hatchingRoot.transform, "HatchingText", "A new creature has been born!", 13f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)hatchingText.transform);

            UiBreedingLabWindow labWindow = windowObject.AddComponent<UiBreedingLabWindow>();
            labWindow.NetworkClient = networkClient;
            labWindow.RosterScrollRect = rosterScrollRect;
            labWindow.RosterRowContainer = rosterContent;
            labWindow.RosterRowPrefab = rosterRowPrefabAsset.GetComponent<UiBreedingRosterRow>();
            labWindow.ParentASlotText = parentAText;
            labWindow.ParentBSlotText = parentBText;
            labWindow.SelectParentAButton = selectAButton;
            labWindow.SelectParentBButton = selectBButton;
            labWindow.RaceLocusRenderer = raceRenderer;
            labWindow.SpeedLocusRenderer = speedRenderer;
            labWindow.CritLocusRenderer = critRenderer;
            labWindow.YieldLocusRenderer = yieldRenderer;
            labWindow.EligibilityText = eligibilityText;
            labWindow.CostText = costText;
            labWindow.InbredRiskText = inbredRiskText;
            labWindow.FuseGenesButton = fuseButton;
            labWindow.HatchingAnimationRoot = hatchingRoot;

            return windowObject;
        }

        private static UiGeneVectorRenderer BuildGeneVectorRow(Transform parent, string rowName)
        {
            GameObject rowObject = new GameObject(rowName, typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);
            SetFixedLayoutHeight(rowObject, 26f);

            HorizontalLayoutGroup rowLayoutGroup = rowObject.AddComponent<HorizontalLayoutGroup>();
            rowLayoutGroup.spacing = 4f;
            rowLayoutGroup.childControlWidth = true;
            rowLayoutGroup.childForceExpandWidth = false;
            rowLayoutGroup.childControlHeight = true;
            rowLayoutGroup.childForceExpandHeight = true;

            TextMeshProUGUI nameText = CreateText(rowObject.transform, "LocusNameText", "Locus", 12f, TextAlignmentOptions.MidlineLeft);
            LayoutElement nameLayout = nameText.gameObject.AddComponent<LayoutElement>();
            nameLayout.preferredWidth = 50f;

            Image paternalBar = BuildGeneBar(rowObject.transform, "PaternalAlleleBar", new Color(0.3f, 0.6f, 1f, 1f));
            Image maternalBar = BuildGeneBar(rowObject.transform, "MaternalAlleleBar", new Color(1f, 0.4f, 0.6f, 1f));
            Image predictedMinBar = BuildGeneBar(rowObject.transform, "PredictedMinBar", new Color(0.6f, 0.6f, 0.6f, 1f));
            Image predictedMaxBar = BuildGeneBar(rowObject.transform, "PredictedMaxBar", new Color(0.9f, 0.9f, 0.9f, 1f));

            TextMeshProUGUI rangeText = CreateText(rowObject.transform, "PredictedRangeText", "0 - 0", 11f, TextAlignmentOptions.MidlineRight);
            LayoutElement rangeLayout = rangeText.gameObject.AddComponent<LayoutElement>();
            rangeLayout.preferredWidth = 60f;

            TextMeshProUGUI mutationText = CreateText(rowObject.transform, "MutationChanceText", "0.0%", 11f, TextAlignmentOptions.MidlineRight);
            LayoutElement mutationLayout = mutationText.gameObject.AddComponent<LayoutElement>();
            mutationLayout.preferredWidth = 50f;

            UiGeneVectorRenderer renderer = rowObject.AddComponent<UiGeneVectorRenderer>();
            renderer.LocusNameText = nameText;
            renderer.PaternalAlleleBar = paternalBar;
            renderer.MaternalAlleleBar = maternalBar;
            renderer.PredictedMinBar = predictedMinBar;
            renderer.PredictedMaxBar = predictedMaxBar;
            renderer.PredictedRangeText = rangeText;
            renderer.MutationChanceText = mutationText;

            return renderer;
        }

        private static Image BuildGeneBar(Transform parent, string barName, Color fillColor)
        {
            GameObject barBackground = new GameObject(barName + "Background", typeof(RectTransform));
            barBackground.transform.SetParent(parent, false);
            LayoutElement barLayout = barBackground.AddComponent<LayoutElement>();
            barLayout.preferredWidth = 36f;
            barBackground.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            GameObject barFillObject = new GameObject(barName, typeof(RectTransform));
            barFillObject.transform.SetParent(barBackground.transform, false);
            StretchFull((RectTransform)barFillObject.transform);
            Image fillImage = barFillObject.AddComponent<Image>();
            fillImage.color = fillColor;
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillAmount = 0f;

            return fillImage;
        }

        private static GameObject BuildAndSaveBreedingRosterRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiBreedingRosterRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 30f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Character", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-90f, 0f);

            GameObject cooldownBadge = new GameObject("CooldownBadge", typeof(RectTransform));
            cooldownBadge.transform.SetParent(root.transform, false);
            RectTransform cooldownBadgeRect = (RectTransform)cooldownBadge.transform;
            cooldownBadgeRect.anchorMin = new Vector2(1f, 0.5f);
            cooldownBadgeRect.anchorMax = new Vector2(1f, 0.5f);
            cooldownBadgeRect.pivot = new Vector2(1f, 0.5f);
            cooldownBadgeRect.sizeDelta = new Vector2(70f, 18f);
            cooldownBadgeRect.anchoredPosition = new Vector2(-64f, 0f);
            TextMeshProUGUI cooldownText = CreateText(cooldownBadge.transform, "CooldownText", "Breeding...", 11f, TextAlignmentOptions.MidlineRight);
            StretchFull((RectTransform)cooldownText.transform);
            cooldownBadge.SetActive(false);

            Button selectButton = CreateButton(root.transform, "SelectButton", "Select", out TextMeshProUGUI _);
            RectTransform selectRect = (RectTransform)selectButton.transform;
            selectRect.anchorMin = new Vector2(1f, 0.1f);
            selectRect.anchorMax = new Vector2(1f, 0.9f);
            selectRect.pivot = new Vector2(1f, 0.5f);
            selectRect.sizeDelta = new Vector2(60f, 0f);
            selectRect.anchoredPosition = new Vector2(-4f, 0f);

            UiBreedingRosterRow rowComponent = root.AddComponent<UiBreedingRosterRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.SelectButton = selectButton;
            rowComponent.CooldownBadge = cooldownBadge;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, BreedingRosterRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiBreedingRosterRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        // ------------------------------------------------------------
        // Simple list-window nav tabs - Achievements, Leaderboard,
        // Mailbox, Store, Season Pass. All share one shell shape (title +
        // full-bleed content area) via BuildSimpleListWindowShell.
        // ------------------------------------------------------------
        private static GameObject BuildSimpleListWindowShell(string windowName, Transform canvasTransform, string title, out RectTransform contentAreaRect, out TextMeshProUGUI titleText)
        {
            GameObject windowObject = new GameObject(windowName, typeof(RectTransform));
            windowObject.transform.SetParent(canvasTransform, false);
            RectTransform windowRect = (RectTransform)windowObject.transform;
            // Modul: Map Hub. Fixed-pixel top/bottom insets instead of pure
            // percentage anchors - percentage margins compress along with
            // canvas height on any aspect ratio shorter than the 1080x1920
            // portrait reference, which let this window's own top content
            // (title/sub-tab header) collide with the persistent overlay
            // bars (Menu/Map buttons, Codex Bonus, Gold/Gems currency) and
            // the bottom Season Pass banner. Left/right stay percentage
            // since width scaling is already consistent (CanvasScaler
            // match-width).
            windowRect.anchorMin = new Vector2(0.04f, 0f);
            windowRect.anchorMax = new Vector2(0.96f, 1f);
            windowRect.offsetMin = new Vector2(0f, 70f);
            windowRect.offsetMax = new Vector2(0f, -180f);

            windowObject.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.96f);

            titleText = CreateText(windowRect, "TitleText", title, 22f, TextAlignmentOptions.Center);
            RectTransform titleRect = (RectTransform)titleText.transform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 40f);
            titleRect.anchoredPosition = new Vector2(0f, -12f);

            GameObject contentAreaObject = new GameObject("ContentArea", typeof(RectTransform));
            contentAreaObject.transform.SetParent(windowRect, false);
            contentAreaRect = (RectTransform)contentAreaObject.transform;
            contentAreaRect.anchorMin = Vector2.zero;
            contentAreaRect.anchorMax = Vector2.one;
            contentAreaRect.offsetMin = new Vector2(20f, 20f);
            contentAreaRect.offsetMax = new Vector2(-20f, -60f);

            return windowObject;
        }


        // Modul: larder. The auto-eat larder screen - three food slots plus the
        // threshold that decides when the character eats from them.
        //
        // Nothing could put food in those slots before this: the server read
        // them from four places and wrote them from none, so every larder was
        // empty and every combat activity stopped the first time the character
        // took damage. See UiLarderPanel's own header for the full account.
        //
        // Built as a vertical layout rather than the absolute anchoring the
        // Combat screen uses, because the three slot rows are identical and a
        // layout group keeps them so.
        private static GameObject BuildLarderWindow(Transform canvasTransform, WebSocketClient networkClient, VisualSyncProxy syncProxy)
        {
            GameObject windowObject = BuildSimpleListWindowShell("LarderWindow", canvasTransform, "Larder", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            VerticalLayoutGroup layout = contentAreaRect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            // childControlHeight must stay true: with it false, every
            // LayoutElement.preferredHeight below is ignored and the whole
            // screen collapses to zero-height rows - the exact failure that
            // left the hamburger menu looking empty.
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;

            CreateHelpText(contentAreaRect, "LarderHelpText",
                "Your character eats from these three slots automatically during combat. When all three run dry the character stops fighting. Cook food at the Crafting bench, then load it here.", 62f);

            UiLarderPanel panel = windowObject.AddComponent<UiLarderPanel>();
            panel.NetworkClient = networkClient;
            panel.SyncProxy = syncProxy;

            for (int slotIndex = 0; slotIndex < 3; slotIndex++)
            {
                CreateGroupSectionLabel(contentAreaRect, "SLOT " + (slotIndex + 1));

                TextMeshProUGUI contentsText = CreateText(contentAreaRect, "Slot" + slotIndex + "ContentsText", "Empty", 15f, TextAlignmentOptions.MidlineLeft);
                SetFixedLayoutHeight(contentsText.gameObject, 24f);
                panel.SlotContentsTexts[slotIndex] = contentsText;

                GameObject rowObject = new GameObject("Slot" + slotIndex + "Row", typeof(RectTransform));
                rowObject.transform.SetParent(contentAreaRect, false);
                SetFixedLayoutHeight(rowObject, 44f);

                HorizontalLayoutGroup rowLayout = rowObject.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = 6f;
                rowLayout.childControlWidth = true;
                rowLayout.childForceExpandWidth = true;
                rowLayout.childControlHeight = true;
                rowLayout.childForceExpandHeight = true;

                TMP_Dropdown dropdown = CreateTmpDropdown(rowObject.transform, "Slot" + slotIndex + "FoodDropdown");
                // Same re-tint as the Combat screen's consumable slots: the
                // stock template is light grey and reads as a glaring white bar
                // against this near-black panel.
                if (dropdown.targetGraphic is Image dropdownBackground)
                {
                    dropdownBackground.color = new Color(0.18f, 0.18f, 0.24f, 1f);
                }
                if (dropdown.captionText != null)
                {
                    dropdown.captionText.color = Color.white;
                }
                LayoutElement dropdownLayout = dropdown.gameObject.AddComponent<LayoutElement>();
                dropdownLayout.flexibleWidth = 3f;
                panel.SlotFoodDropdowns[slotIndex] = dropdown;

                TMP_InputField quantityField = CreateInputField(rowObject.transform, "Slot" + slotIndex + "QuantityField", "Qty");
                LayoutElement quantityLayout = quantityField.gameObject.GetComponent<LayoutElement>();
                if (quantityLayout == null) quantityLayout = quantityField.gameObject.AddComponent<LayoutElement>();
                quantityLayout.flexibleWidth = 1f;
                quantityLayout.minHeight = 0f;
                quantityLayout.preferredHeight = 0f;
                panel.SlotQuantityInputs[slotIndex] = quantityField;

                Button loadButton = CreateButton(rowObject.transform, "Slot" + slotIndex + "LoadButton", "Load", out TextMeshProUGUI _);
                ((Image)loadButton.targetGraphic).color = new Color(0.28f, 0.52f, 0.34f, 1f);
                LayoutElement loadLayout = loadButton.gameObject.GetComponent<LayoutElement>();
                if (loadLayout == null) loadLayout = loadButton.gameObject.AddComponent<LayoutElement>();
                loadLayout.flexibleWidth = 1f;
                loadLayout.minHeight = 0f;
                loadLayout.preferredHeight = 0f;
                panel.SlotLoadButtons[slotIndex] = loadButton;

                Button unloadButton = CreateButton(rowObject.transform, "Slot" + slotIndex + "UnloadButton", "Empty", out TextMeshProUGUI _);
                ((Image)unloadButton.targetGraphic).color = new Color(0.42f, 0.28f, 0.28f, 1f);
                LayoutElement unloadLayout = unloadButton.gameObject.GetComponent<LayoutElement>();
                if (unloadLayout == null) unloadLayout = unloadButton.gameObject.AddComponent<LayoutElement>();
                unloadLayout.flexibleWidth = 1f;
                unloadLayout.minHeight = 0f;
                unloadLayout.preferredHeight = 0f;
                panel.SlotUnloadButtons[slotIndex] = unloadButton;
            }

            CreateGroupSectionLabel(contentAreaRect, "AUTO-EAT THRESHOLD");

            TextMeshProUGUI thresholdValue = CreateText(contentAreaRect, "ThresholdValueText", "Eat when health drops below 30%", 15f, TextAlignmentOptions.MidlineLeft);
            SetFixedLayoutHeight(thresholdValue.gameObject, 24f);
            panel.ThresholdValueText = thresholdValue;

            Slider thresholdSlider = CreateHorizontalSlider(contentAreaRect, "ThresholdSlider");
            SetFixedLayoutHeight(thresholdSlider.gameObject, 32f);
            panel.ThresholdSlider = thresholdSlider;

            CreateHelpText(contentAreaRect, "ThresholdHelpText",
                "Higher means eating sooner and more often - safer, but food runs out faster. This setting is saved with your character.", 40f);

            TextMeshProUGUI sustainText = CreateText(contentAreaRect, "SustainEstimateText", string.Empty, 14f, TextAlignmentOptions.MidlineLeft);
            sustainText.color = new Color(0.75f, 0.9f, 1f, 0.85f);
            SetFixedLayoutHeight(sustainText.gameObject, 24f);
            panel.SustainEstimateText = sustainText;

            TextMeshProUGUI statusText = CreateText(contentAreaRect, "StatusText", string.Empty, 13f, TextAlignmentOptions.MidlineLeft);
            statusText.color = new Color(1f, 0.86f, 0.6f, 1f);
            SetFixedLayoutHeight(statusText.gameObject, 44f);
            panel.StatusText = statusText;

            return windowObject;
        }

        // Modul: larder. A plain uGUI horizontal slider. There was no slider
        // helper in this file because, until the auto-eat threshold got a
        // screen, no built panel had one - CommandType.UpdateAutoEatThreshold
        // was reachable from no UI at all.
        private static Slider CreateHorizontalSlider(Transform parent, string objectName)
        {
            GameObject sliderObject = new GameObject(objectName, typeof(RectTransform));
            sliderObject.transform.SetParent(parent, false);
            Slider slider = sliderObject.AddComponent<Slider>();

            GameObject backgroundObject = new GameObject("Background", typeof(RectTransform));
            backgroundObject.transform.SetParent(sliderObject.transform, false);
            RectTransform backgroundRect = (RectTransform)backgroundObject.transform;
            backgroundRect.anchorMin = new Vector2(0f, 0.28f);
            backgroundRect.anchorMax = new Vector2(1f, 0.72f);
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;
            backgroundObject.AddComponent<Image>().color = new Color(0.16f, 0.16f, 0.2f, 1f);

            GameObject fillAreaObject = new GameObject("Fill Area", typeof(RectTransform));
            fillAreaObject.transform.SetParent(sliderObject.transform, false);
            RectTransform fillAreaRect = (RectTransform)fillAreaObject.transform;
            fillAreaRect.anchorMin = new Vector2(0f, 0.28f);
            fillAreaRect.anchorMax = new Vector2(1f, 0.72f);
            fillAreaRect.offsetMin = new Vector2(8f, 0f);
            fillAreaRect.offsetMax = new Vector2(-8f, 0f);

            GameObject fillObject = new GameObject("Fill", typeof(RectTransform));
            fillObject.transform.SetParent(fillAreaObject.transform, false);
            RectTransform fillRect = (RectTransform)fillObject.transform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = new Vector2(16f, 0f);
            fillObject.AddComponent<Image>().color = new Color(0.85f, 0.72f, 0.45f, 1f);

            GameObject handleAreaObject = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleAreaObject.transform.SetParent(sliderObject.transform, false);
            RectTransform handleAreaRect = (RectTransform)handleAreaObject.transform;
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = new Vector2(8f, 0f);
            handleAreaRect.offsetMax = new Vector2(-8f, 0f);

            GameObject handleObject = new GameObject("Handle", typeof(RectTransform));
            handleObject.transform.SetParent(handleAreaObject.transform, false);
            RectTransform handleRect = (RectTransform)handleObject.transform;
            handleRect.anchorMin = new Vector2(0f, 0f);
            handleRect.anchorMax = new Vector2(0f, 1f);
            handleRect.sizeDelta = new Vector2(26f, 0f);
            handleObject.AddComponent<Image>().color = new Color(0.95f, 0.88f, 0.7f, 1f);

            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleObject.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;

            return slider;
        }

        // Modul: halt reasons. The banner that names why the character is not
        // earning. Parented to the persistent bar layer rather than to any one
        // screen: the player has to find out without going looking, and the
        // states it reports (empty larder, death, full backpack) are exactly
        // the ones that used to leave a character standing still with no
        // explanation anywhere in the game.
        private static void BuildActivityHaltBanner(Transform canvasTransform, VisualSyncProxy syncProxy)
        {
            // The component lives on a holder that is never deactivated, and
            // the banner it shows and hides is a CHILD of that holder. Putting
            // UiActivityHaltBanner on the same GameObject it toggles would be a
            // one-way trip: MonoBehaviour.Update does not run on an inactive
            // object, so the first time the banner hid itself it could never
            // notice a later halt and would stay hidden for the rest of the
            // session.
            GameObject holderObject = new GameObject("ActivityHaltBannerHolder", typeof(RectTransform));
            holderObject.transform.SetParent(canvasTransform, false);
            StretchFull((RectTransform)holderObject.transform);

            GameObject bannerObject = new GameObject("ActivityHaltBanner", typeof(RectTransform));
            bannerObject.transform.SetParent(holderObject.transform, false);
            RectTransform bannerRect = (RectTransform)bannerObject.transform;
            bannerRect.anchorMin = new Vector2(0.04f, 1f);
            bannerRect.anchorMax = new Vector2(0.96f, 1f);
            bannerRect.pivot = new Vector2(0.5f, 1f);
            bannerRect.sizeDelta = new Vector2(0f, 64f);
            bannerRect.anchoredPosition = new Vector2(0f, -112f);

            Image background = bannerObject.AddComponent<Image>();
            background.color = new Color(0.62f, 0.18f, 0.16f, 0.94f);

            TextMeshProUGUI message = CreateText(bannerRect, "MessageText", string.Empty, 14f, TextAlignmentOptions.Midline);
            RectTransform messageRect = (RectTransform)message.transform;
            messageRect.anchorMin = Vector2.zero;
            messageRect.anchorMax = Vector2.one;
            messageRect.offsetMin = new Vector2(12f, 6f);
            messageRect.offsetMax = new Vector2(-12f, -6f);
            message.raycastTarget = false;

            UiActivityHaltBanner banner = holderObject.AddComponent<UiActivityHaltBanner>();
            banner.SyncProxy = syncProxy;
            banner.BannerRoot = bannerObject;
            banner.MessageText = message;
            banner.BackgroundImage = background;

            bannerObject.SetActive(false);
        }

        private static GameObject BuildAchievementsWindow(Transform canvasTransform, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("AchievementsWindow", canvasTransform, "Achievements", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(contentAreaRect);

            GameObject rowPrefabAsset = BuildAndSaveAchievementRowPrefab();

            UiAchievementsPanel panel = windowObject.AddComponent<UiAchievementsPanel>();
            panel.RowContainer = content;
            panel.RowPrefab = rowPrefabAsset.GetComponent<UiAchievementRow>();
            panel.NetworkClient = networkClient;

            return windowObject;
        }

        // Modul: UI audit follow-up. UiRaceMasteryPanel + RaceMasteryCache
        // (backed by a real server endpoint, NetworkBroadcastSystem.
        // HandleMasterySnapshot) existed complete and fully wired but were
        // never instantiated anywhere - the exact "orphaned script, zero
        // GameObjects" shape the whole Map Hub effort was meant to fix.
        // Fixed 6-row layout (not a pooled ScrollView list) matches the
        // panel's own design: RaceRows is a fixed array, not a
        // dynamically-sized collection, since the race roster never
        // changes at runtime.
        private static readonly (int raceId, string displayName)[] RaceMasteryRoster =
        {
            (1, "Human"), (2, "Vila"), (3, "Draugr"), (4, "Kobold"), (5, "Vodnik"), (6, "Moosleute")
        };

        private static GameObject BuildRaceMasteryWindow(Transform canvasTransform)
        {
            GameObject windowObject = BuildSimpleListWindowShell("RaceMasteryWindow", canvasTransform, "Race Mastery", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            VerticalLayoutGroup listLayout = contentAreaRect.gameObject.AddComponent<VerticalLayoutGroup>();
            listLayout.spacing = 10f;
            listLayout.childControlWidth = true;
            listLayout.childForceExpandWidth = true;
            listLayout.childControlHeight = false;
            listLayout.childForceExpandHeight = false;

            UiRaceMasteryPanel panel = windowObject.AddComponent<UiRaceMasteryPanel>();
            panel.RaceRows = new RaceMasteryRowRefs[RaceMasteryRoster.Length];

            for (int i = 0; i < RaceMasteryRoster.Length; i++)
            {
                (int raceId, string displayName) = RaceMasteryRoster[i];
                panel.RaceRows[i] = BuildRaceMasteryRow(contentAreaRect, raceId, displayName);
            }

            return windowObject;
        }

        private static RaceMasteryRowRefs BuildRaceMasteryRow(Transform parent, int raceId, string displayName)
        {
            GameObject root = new GameObject(displayName + "Row", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            SetFixedLayoutHeight(root, 58f);
            root.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);

            VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            GameObject headerRow = new GameObject("HeaderRow", typeof(RectTransform));
            headerRow.transform.SetParent(root.transform, false);
            SetFixedLayoutHeight(headerRow, 20f);

            HorizontalLayoutGroup headerLayoutGroup = headerRow.AddComponent<HorizontalLayoutGroup>();
            headerLayoutGroup.childControlWidth = true;
            headerLayoutGroup.childForceExpandWidth = false;
            headerLayoutGroup.childControlHeight = true;
            headerLayoutGroup.childForceExpandHeight = true;

            TextMeshProUGUI nameText = CreateText(headerRow.transform, "NameText", displayName, 15f, TextAlignmentOptions.MidlineLeft);
            LayoutElement nameLayout = nameText.gameObject.AddComponent<LayoutElement>();
            nameLayout.flexibleWidth = 1f;

            TextMeshProUGUI levelText = CreateText(headerRow.transform, "LevelText", "Lv. 0", 15f, TextAlignmentOptions.MidlineRight);
            LayoutElement levelLayout = levelText.gameObject.AddComponent<LayoutElement>();
            levelLayout.preferredWidth = 90f;

            GameObject progressRow = new GameObject("ProgressRow", typeof(RectTransform));
            progressRow.transform.SetParent(root.transform, false);
            SetFixedLayoutHeight(progressRow, 16f);

            HorizontalLayoutGroup progressRowLayoutGroup = progressRow.AddComponent<HorizontalLayoutGroup>();
            progressRowLayoutGroup.spacing = 6f;
            progressRowLayoutGroup.childControlWidth = true;
            progressRowLayoutGroup.childForceExpandWidth = false;
            progressRowLayoutGroup.childControlHeight = true;
            progressRowLayoutGroup.childForceExpandHeight = true;

            (GameObject barBackground, RectTransform barFill) = BuildAnchoredProgressBar(progressRow.transform, new Color(0.6f, 0.4f, 0.9f, 1f));
            LayoutElement barLayout = barBackground.AddComponent<LayoutElement>();
            barLayout.flexibleWidth = 1f;

            TextMeshProUGUI experienceText = CreateText(progressRow.transform, "ExperienceText", "0 / 0", 12f, TextAlignmentOptions.MidlineRight);
            LayoutElement experienceTextLayout = experienceText.gameObject.AddComponent<LayoutElement>();
            experienceTextLayout.preferredWidth = 130f;

            return new RaceMasteryRowRefs
            {
                RaceId = raceId,
                LevelText = levelText,
                ExperienceText = experienceText,
                ProgressBarFill = barFill
            };
        }

        // Modul: UI audit follow-up. DailyLoginRewardEngine already grants a
        // real, server-authoritative streak reward on every login/register
        // (see LoginBonusCache's own comment) - it just had no UI. Fixed
        // 7-day layout (not a pooled list), matching BuildRaceMasteryWindow's
        // same reasoning: a week is always 7 days.
        private static GameObject BuildLoginBonusWindow(Transform canvasTransform)
        {
            GameObject windowObject = BuildSimpleListWindowShell("LoginBonusWindow", canvasTransform, "Login Bonus", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI streakText = CreateText(contentAreaRect, "StreakText", "Day 0 of 7", 20f, TextAlignmentOptions.Center);
            RectTransform streakRect = (RectTransform)streakText.transform;
            streakRect.anchorMin = new Vector2(0f, 1f);
            streakRect.anchorMax = new Vector2(1f, 1f);
            streakRect.pivot = new Vector2(0.5f, 1f);
            streakRect.sizeDelta = new Vector2(0f, 32f);
            streakRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI statusText = CreateText(contentAreaRect, "StatusText", string.Empty, 14f, TextAlignmentOptions.Center);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = new Vector2(0f, 24f);
            statusRect.anchoredPosition = new Vector2(0f, -36f);

            GameObject boxRowObject = new GameObject("DayBoxRow", typeof(RectTransform));
            boxRowObject.transform.SetParent(contentAreaRect, false);
            RectTransform boxRowRect = (RectTransform)boxRowObject.transform;
            boxRowRect.anchorMin = new Vector2(0f, 1f);
            boxRowRect.anchorMax = new Vector2(1f, 1f);
            boxRowRect.pivot = new Vector2(0.5f, 1f);
            boxRowRect.sizeDelta = new Vector2(0f, 120f);
            boxRowRect.anchoredPosition = new Vector2(0f, -76f);

            HorizontalLayoutGroup boxRowLayout = boxRowObject.AddComponent<HorizontalLayoutGroup>();
            boxRowLayout.spacing = 8f;
            boxRowLayout.childControlWidth = true;
            boxRowLayout.childForceExpandWidth = true;
            boxRowLayout.childControlHeight = true;
            boxRowLayout.childForceExpandHeight = true;

            UiLoginBonusPanel panel = windowObject.AddComponent<UiLoginBonusPanel>();
            panel.StreakText = streakText;
            panel.StatusText = statusText;
            panel.DayBoxes = new LoginBonusDayBoxRefs[7];

            for (int day = 1; day <= 7; day++)
            {
                panel.DayBoxes[day - 1] = BuildLoginBonusDayBox(boxRowObject.transform, day);
            }

            return windowObject;
        }

        private static LoginBonusDayBoxRefs BuildLoginBonusDayBox(Transform parent, int day)
        {
            GameObject boxObject = new GameObject("Day" + day + "Box", typeof(RectTransform));
            boxObject.transform.SetParent(parent, false);
            Image highlightBackground = boxObject.AddComponent<Image>();
            highlightBackground.color = new Color(1f, 1f, 1f, 0.05f);

            VerticalLayoutGroup layout = boxObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 8, 8);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            TextMeshProUGUI dayLabel = CreateText(boxObject.transform, "DayLabel", "Day " + day, 13f, TextAlignmentOptions.Center);
            SetFixedLayoutHeight(dayLabel.gameObject, 18f);

            TextMeshProUGUI rewardText = CreateText(boxObject.transform, "RewardText", "0g", 12f, TextAlignmentOptions.Center);
            SetFixedLayoutHeight(rewardText.gameObject, 32f);

            return new LoginBonusDayBoxRefs
            {
                Day = day,
                RewardText = rewardText,
                HighlightBackground = highlightBackground
            };
        }

        // Modul: UI audit follow-up. Statistics panel - see
        // UiStatisticsPanel/PlayerStatisticsCache's own comments. Plain
        // vertical stat-row list, mirroring BuildCharacterStatsPanel's
        // CreateStatRow usage.
        private static GameObject BuildStatisticsWindow(Transform canvasTransform)
        {
            GameObject windowObject = BuildSimpleListWindowShell("StatisticsWindow", canvasTransform, "Statistics", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            VerticalLayoutGroup layout = contentAreaRect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            UiStatisticsPanel panel = windowObject.AddComponent<UiStatisticsPanel>();
            panel.LevelText = CreateStatRow(contentAreaRect, "Level: 0");
            panel.XpText = CreateStatRow(contentAreaRect, "Experience: 0");
            panel.GoldText = CreateStatRow(contentAreaRect, "Gold: 0");
            panel.DiamondsText = CreateStatRow(contentAreaRect, "Diamonds: 0");
            panel.LoginStreakText = CreateStatRow(contentAreaRect, "Login Streak: 0");
            panel.AchievementsClaimedText = CreateStatRow(contentAreaRect, "Achievements Claimed: 0");
            panel.RegionsCompletedText = CreateStatRow(contentAreaRect, "Regions Completed: 0");
            panel.CharacterCountText = CreateStatRow(contentAreaRect, "Characters: 0");
            panel.SkillPointsText = CreateStatRow(contentAreaRect, "Unspent Skill Points: 0");
            panel.GuildText = CreateStatRow(contentAreaRect, "Guild: None");

            return windowObject;
        }

        // Modul: UI rework. Account screen. See UiAccountPanel's own header
        // comment for why account deletion lives here and why it is armed in
        // two steps rather than fired on a single tap.
        private static GameObject BuildAccountWindow(Transform canvasTransform, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("AccountWindow", canvasTransform, "Account", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI usernameText = CreateText(contentAreaRect, "UsernameText", "Signed in", 26f, TextAlignmentOptions.MidlineLeft);
            RectTransform usernameRect = (RectTransform)usernameText.transform;
            usernameRect.anchorMin = new Vector2(0f, 1f);
            usernameRect.anchorMax = new Vector2(1f, 1f);
            usernameRect.pivot = new Vector2(0.5f, 1f);
            usernameRect.sizeDelta = new Vector2(0f, 40f);
            usernameRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI playerIdText = CreateText(contentAreaRect, "PlayerIdText", "Player ID: (connecting...)", 15f, TextAlignmentOptions.MidlineLeft);
            playerIdText.color = new Color(1f, 1f, 1f, 0.6f);
            RectTransform playerIdRect = (RectTransform)playerIdText.transform;
            playerIdRect.anchorMin = new Vector2(0f, 1f);
            playerIdRect.anchorMax = new Vector2(1f, 1f);
            playerIdRect.pivot = new Vector2(0.5f, 1f);
            playerIdRect.sizeDelta = new Vector2(0f, 26f);
            playerIdRect.anchoredPosition = new Vector2(0f, -44f);

            TextMeshProUGUI levelText = CreateText(contentAreaRect, "LevelText", "Level -", 17f, TextAlignmentOptions.MidlineLeft);
            RectTransform levelRect = (RectTransform)levelText.transform;
            levelRect.anchorMin = new Vector2(0f, 1f);
            levelRect.anchorMax = new Vector2(1f, 1f);
            levelRect.pivot = new Vector2(0.5f, 1f);
            levelRect.sizeDelta = new Vector2(0f, 28f);
            levelRect.anchoredPosition = new Vector2(0f, -80f);

            TextMeshProUGUI guildText = CreateText(contentAreaRect, "GuildText", "Guild: -", 17f, TextAlignmentOptions.MidlineLeft);
            RectTransform guildRect = (RectTransform)guildText.transform;
            guildRect.anchorMin = new Vector2(0f, 1f);
            guildRect.anchorMax = new Vector2(1f, 1f);
            guildRect.pivot = new Vector2(0.5f, 1f);
            guildRect.sizeDelta = new Vector2(0f, 28f);
            guildRect.anchoredPosition = new Vector2(0f, -112f);

            TextMeshProUGUI dangerHeader = CreateText(contentAreaRect, "DangerHeader", "DANGER ZONE", 14f, TextAlignmentOptions.MidlineLeft);
            dangerHeader.color = new Color(0.9f, 0.45f, 0.4f, 1f);
            dangerHeader.characterSpacing = 6f;
            RectTransform dangerHeaderRect = (RectTransform)dangerHeader.transform;
            dangerHeaderRect.anchorMin = new Vector2(0f, 0f);
            dangerHeaderRect.anchorMax = new Vector2(1f, 0f);
            dangerHeaderRect.pivot = new Vector2(0.5f, 0f);
            dangerHeaderRect.sizeDelta = new Vector2(0f, 26f);
            dangerHeaderRect.anchoredPosition = new Vector2(0f, 108f);

            TextMeshProUGUI deleteWarningText = CreateText(contentAreaRect, "DeleteWarningText", "Permanently erases this account and all of its progress.", 13f, TextAlignmentOptions.MidlineLeft);
            deleteWarningText.color = new Color(1f, 1f, 1f, 0.6f);
            RectTransform deleteWarningRect = (RectTransform)deleteWarningText.transform;
            deleteWarningRect.anchorMin = new Vector2(0f, 0f);
            deleteWarningRect.anchorMax = new Vector2(1f, 0f);
            deleteWarningRect.pivot = new Vector2(0.5f, 0f);
            deleteWarningRect.sizeDelta = new Vector2(0f, 44f);
            deleteWarningRect.anchoredPosition = new Vector2(0f, 60f);

            Button deleteAccountButton = CreateButton(contentAreaRect, "DeleteAccountButton", "Delete Account", out TextMeshProUGUI deleteAccountLabel);
            ((Image)deleteAccountButton.targetGraphic).color = new Color(0.52f, 0.16f, 0.16f, 1f);
            RectTransform deleteAccountRect = (RectTransform)deleteAccountButton.transform;
            deleteAccountRect.anchorMin = new Vector2(0f, 0f);
            deleteAccountRect.anchorMax = new Vector2(1f, 0f);
            deleteAccountRect.pivot = new Vector2(0.5f, 0f);
            deleteAccountRect.sizeDelta = new Vector2(0f, 48f);
            deleteAccountRect.anchoredPosition = new Vector2(0f, 6f);

            UiAccountPanel panel = windowObject.AddComponent<UiAccountPanel>();
            panel.NetworkClient = networkClient;
            panel.UsernameText = usernameText;
            panel.PlayerIdText = playerIdText;
            panel.LevelText = levelText;
            panel.GuildText = guildText;
            panel.DeleteAccountButton = deleteAccountButton;
            panel.DeleteAccountButtonLabel = deleteAccountLabel;
            panel.DeleteWarningText = deleteWarningText;

            return windowObject;
        }

        // ------------------------------------------------------------
        // Inventory screen. Equipped gear, backpack (equipment instances and
        // carried material stacks) and the village stash, in one scrolling
        // list. See UiInventoryPanel for why none of this was visible before.
        // ------------------------------------------------------------
        private static GameObject BuildInventoryWindow(Transform canvasTransform, AssetRegistry assetRegistry, VisualSyncProxy syncProxy, WebSocketClient networkClient, EquipmentInventoryCache inventoryCache)
        {
            GameObject windowObject = BuildSimpleListWindowShell("InventoryWindow", canvasTransform, "Inventory", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI summaryText = CreateText(contentAreaRect, "SummaryText", "Loading inventory...", 13f, TextAlignmentOptions.MidlineLeft);
            summaryText.color = new Color(1f, 1f, 1f, 0.7f);
            RectTransform summaryRect = (RectTransform)summaryText.transform;
            summaryRect.anchorMin = new Vector2(0f, 1f);
            summaryRect.anchorMax = new Vector2(1f, 1f);
            summaryRect.pivot = new Vector2(0.5f, 1f);
            summaryRect.sizeDelta = new Vector2(-110f, 30f);
            summaryRect.anchoredPosition = new Vector2(-55f, 0f);

            Button refreshButton = CreateButton(contentAreaRect, "RefreshButton", "Refresh", out TextMeshProUGUI _);
            ((Image)refreshButton.targetGraphic).color = new Color(0.22f, 0.30f, 0.42f, 1f);
            RectTransform refreshRect = (RectTransform)refreshButton.transform;
            refreshRect.anchorMin = new Vector2(1f, 1f);
            refreshRect.anchorMax = new Vector2(1f, 1f);
            refreshRect.pivot = new Vector2(1f, 1f);
            refreshRect.sizeDelta = new Vector2(100f, 32f);
            refreshRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI statusText = CreateText(contentAreaRect, "StatusText", string.Empty, 12f, TextAlignmentOptions.MidlineLeft);
            statusText.color = new Color(1f, 0.86f, 0.6f, 1f);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = new Vector2(0f, 20f);
            statusRect.anchoredPosition = new Vector2(0f, -34f);

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = Vector2.zero;
            scrollAreaRect.offsetMax = new Vector2(0f, -58f);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(scrollAreaRect);
            StretchFull((RectTransform)content.parent.parent);

            UiInventoryPanel panel = windowObject.AddComponent<UiInventoryPanel>();
            panel.NetworkClient = networkClient;
            panel.InventoryCache = inventoryCache;
            panel.Registry = assetRegistry;
            panel.SyncProxy = syncProxy;
            panel.SummaryText = summaryText;
            panel.StatusText = statusText;
            panel.RefreshButton = refreshButton;
            panel.RowContainer = content;
            panel.RowPrefab = BuildAndSaveInventoryRowPrefab().GetComponent<UiInventoryEntryRow>();
            panel.SectionHeaderPrefab = BuildAndSaveSectionHeaderRowPrefab().GetComponent<UiSectionHeaderRow>();

            return windowObject;
        }

        // ------------------------------------------------------------
        // Crafting Tree screen. ContentRegistry's 103 recipes, grouped by
        // profession, with real per-material stock and one-click craft.
        // ------------------------------------------------------------
        private static GameObject BuildCraftingTreeWindow(Transform canvasTransform, WebSocketClient networkClient, AssetRegistry assetRegistry)
        {
            GameObject windowObject = BuildSimpleListWindowShell("CraftingWindow", canvasTransform, "Crafting", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI summaryText = CreateText(contentAreaRect, "SummaryText", "Loading recipes...", 13f, TextAlignmentOptions.MidlineLeft);
            summaryText.color = new Color(1f, 1f, 1f, 0.7f);
            RectTransform summaryRect = (RectTransform)summaryText.transform;
            summaryRect.anchorMin = new Vector2(0f, 1f);
            summaryRect.anchorMax = new Vector2(1f, 1f);
            summaryRect.pivot = new Vector2(0.5f, 1f);
            summaryRect.sizeDelta = new Vector2(-110f, 28f);
            summaryRect.anchoredPosition = new Vector2(-55f, 0f);

            Button refreshButton = CreateButton(contentAreaRect, "RefreshButton", "Refresh", out TextMeshProUGUI _);
            ((Image)refreshButton.targetGraphic).color = new Color(0.22f, 0.30f, 0.42f, 1f);
            RectTransform refreshRect = (RectTransform)refreshButton.transform;
            refreshRect.anchorMin = new Vector2(1f, 1f);
            refreshRect.anchorMax = new Vector2(1f, 1f);
            refreshRect.pivot = new Vector2(1f, 1f);
            refreshRect.sizeDelta = new Vector2(100f, 32f);
            refreshRect.anchoredPosition = Vector2.zero;

            // Profession filter strip. 103 recipes is not a browsable flat
            // list on a portrait phone, and the four professions are genuinely
            // separate progressions.
            GameObject filterRowObject = new GameObject("ProfessionFilterRow", typeof(RectTransform));
            filterRowObject.transform.SetParent(contentAreaRect, false);
            RectTransform filterRowRect = (RectTransform)filterRowObject.transform;
            filterRowRect.anchorMin = new Vector2(0f, 1f);
            filterRowRect.anchorMax = new Vector2(1f, 1f);
            filterRowRect.pivot = new Vector2(0.5f, 1f);
            filterRowRect.sizeDelta = new Vector2(0f, 38f);
            filterRowRect.anchoredPosition = new Vector2(0f, -32f);

            string[] filterLabels = { "Smelting", "Equipment", "Cooking", "Alchemy", "All" };
            int[] filterValues = { 2, 3, 4, 5, -1 };
            Button[] filterButtons = BuildSubTabButtons(filterRowRect, filterLabels);

            TextMeshProUGUI statusText = CreateText(contentAreaRect, "StatusText", string.Empty, 12f, TextAlignmentOptions.MidlineLeft);
            statusText.color = new Color(1f, 0.86f, 0.6f, 1f);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = new Vector2(0f, 20f);
            statusRect.anchoredPosition = new Vector2(0f, -72f);

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = Vector2.zero;
            scrollAreaRect.offsetMax = new Vector2(0f, -96f);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(scrollAreaRect);
            StretchFull((RectTransform)content.parent.parent);

            UiCraftingTreePanel panel = windowObject.AddComponent<UiCraftingTreePanel>();
            panel.NetworkClient = networkClient;
            panel.Registry = assetRegistry;
            panel.SummaryText = summaryText;
            panel.StatusText = statusText;
            panel.RefreshButton = refreshButton;
            panel.ProfessionFilterButtons = filterButtons;
            panel.ProfessionFilterValues = filterValues;
            panel.RowContainer = content;
            panel.RowPrefab = BuildAndSaveCraftingRecipeRowPrefab().GetComponent<UiCraftingRecipeRow>();
            panel.SectionHeaderPrefab = BuildAndSaveSectionHeaderRowPrefab().GetComponent<UiSectionHeaderRow>();

            return windowObject;
        }

        private static GameObject BuildAndSaveInventoryRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiInventoryEntryRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 56f);
            root.AddComponent<Image>().color = new Color(0.17f, 0.17f, 0.22f, 1f);

            Image icon = new GameObject("Icon", typeof(RectTransform)).AddComponent<Image>();
            icon.transform.SetParent(root.transform, false);
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            RectTransform iconRect = (RectTransform)icon.transform;
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.sizeDelta = new Vector2(48f, 48f);
            iconRect.anchoredPosition = new Vector2(4f, 0f);

            Image equippedMarker = new GameObject("EquippedMarker", typeof(RectTransform)).AddComponent<Image>();
            equippedMarker.transform.SetParent(root.transform, false);
            equippedMarker.color = new Color(0.45f, 0.85f, 0.5f, 1f);
            equippedMarker.raycastTarget = false;
            RectTransform markerRect = (RectTransform)equippedMarker.transform;
            markerRect.anchorMin = new Vector2(0f, 0f);
            markerRect.anchorMax = new Vector2(0f, 1f);
            markerRect.pivot = new Vector2(0f, 0.5f);
            markerRect.sizeDelta = new Vector2(4f, 0f);
            markerRect.anchoredPosition = Vector2.zero;
            equippedMarker.gameObject.SetActive(false);

            TextMeshProUGUI nameText = CreateText(root.transform, "NameText", "Item", 15f, TextAlignmentOptions.BottomLeft);
            nameText.raycastTarget = false;
            RectTransform nameRect = (RectTransform)nameText.transform;
            nameRect.anchorMin = new Vector2(0f, 0.5f);
            nameRect.anchorMax = new Vector2(1f, 1f);
            nameRect.offsetMin = new Vector2(58f, 0f);
            nameRect.offsetMax = new Vector2(-220f, -4f);

            TextMeshProUGUI detailText = CreateText(root.transform, "DetailText", string.Empty, 12f, TextAlignmentOptions.TopLeft);
            detailText.color = new Color(1f, 1f, 1f, 0.65f);
            detailText.raycastTarget = false;
            RectTransform detailRect = (RectTransform)detailText.transform;
            detailRect.anchorMin = new Vector2(0f, 0f);
            detailRect.anchorMax = new Vector2(1f, 0.5f);
            detailRect.offsetMin = new Vector2(58f, 4f);
            detailRect.offsetMax = new Vector2(-220f, 0f);

            TextMeshProUGUI quantityText = CreateText(root.transform, "QuantityText", string.Empty, 13f, TextAlignmentOptions.MidlineRight);
            quantityText.raycastTarget = false;
            RectTransform quantityRect = (RectTransform)quantityText.transform;
            quantityRect.anchorMin = new Vector2(1f, 0f);
            quantityRect.anchorMax = new Vector2(1f, 1f);
            quantityRect.pivot = new Vector2(1f, 0.5f);
            quantityRect.sizeDelta = new Vector2(124f, 0f);
            quantityRect.anchoredPosition = new Vector2(-98f, 0f);

            // Modul: interactive inventory. Hidden by default and revealed
            // per-bind, since only unequipped equipment rows have anything
            // to do here (see UiInventoryEntryRow.BindWithAction).
            Button actionButton = CreateButton(root.transform, "ActionButton", "Equip", out TextMeshProUGUI actionButtonLabel);
            ((Image)actionButton.targetGraphic).color = new Color(0.28f, 0.52f, 0.34f, 1f);
            RectTransform actionRect = (RectTransform)actionButton.transform;
            actionRect.anchorMin = new Vector2(1f, 0.5f);
            actionRect.anchorMax = new Vector2(1f, 0.5f);
            actionRect.pivot = new Vector2(1f, 0.5f);
            actionRect.sizeDelta = new Vector2(86f, 40f);
            actionRect.anchoredPosition = new Vector2(-6f, 0f);
            actionButton.gameObject.SetActive(false);

            UiInventoryEntryRow rowComponent = root.AddComponent<UiInventoryEntryRow>();
            rowComponent.IconImage = icon;
            rowComponent.EquippedMarker = equippedMarker;
            rowComponent.NameText = nameText;
            rowComponent.DetailText = detailText;
            rowComponent.QuantityText = quantityText;
            rowComponent.ActionButton = actionButton;
            rowComponent.ActionButtonLabel = actionButtonLabel;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, InventoryRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiInventoryEntryRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveSectionHeaderRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiSectionHeaderRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 30f);

            TextMeshProUGUI titleText = CreateText(root.transform, "TitleText", "SECTION", 13f, TextAlignmentOptions.MidlineLeft);
            titleText.color = new Color(0.85f, 0.72f, 0.45f, 1f);
            titleText.characterSpacing = 6f;
            titleText.raycastTarget = false;
            RectTransform titleRect = (RectTransform)titleText.transform;
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(4f, 0f);
            titleRect.offsetMax = new Vector2(-4f, 0f);

            UiSectionHeaderRow rowComponent = root.AddComponent<UiSectionHeaderRow>();
            rowComponent.TitleText = titleText;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, SectionHeaderRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiSectionHeaderRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveCraftingRecipeRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiCraftingRecipeRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 64f);
            root.AddComponent<Image>().color = new Color(0.17f, 0.17f, 0.22f, 1f);

            Image icon = new GameObject("Icon", typeof(RectTransform)).AddComponent<Image>();
            icon.transform.SetParent(root.transform, false);
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            RectTransform iconRect = (RectTransform)icon.transform;
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.sizeDelta = new Vector2(48f, 48f);
            iconRect.anchoredPosition = new Vector2(4f, 0f);

            TextMeshProUGUI nameText = CreateText(root.transform, "NameText", "Recipe", 15f, TextAlignmentOptions.BottomLeft);
            nameText.raycastTarget = false;
            RectTransform nameRect = (RectTransform)nameText.transform;
            nameRect.anchorMin = new Vector2(0f, 0.52f);
            nameRect.anchorMax = new Vector2(1f, 1f);
            nameRect.offsetMin = new Vector2(58f, 0f);
            nameRect.offsetMax = new Vector2(-124f, -4f);

            TextMeshProUGUI requirementText = CreateText(root.transform, "RequirementText", string.Empty, 12f, TextAlignmentOptions.TopLeft);
            requirementText.raycastTarget = false;
            RectTransform requirementRect = (RectTransform)requirementText.transform;
            requirementRect.anchorMin = new Vector2(0f, 0.2f);
            requirementRect.anchorMax = new Vector2(1f, 0.52f);
            requirementRect.offsetMin = new Vector2(58f, 0f);
            requirementRect.offsetMax = new Vector2(-124f, 0f);

            TextMeshProUGUI gateText = CreateText(root.transform, "GateText", string.Empty, 11f, TextAlignmentOptions.TopLeft);
            gateText.color = new Color(0.95f, 0.60f, 0.55f, 1f);
            gateText.raycastTarget = false;
            RectTransform gateRect = (RectTransform)gateText.transform;
            gateRect.anchorMin = new Vector2(0f, 0f);
            gateRect.anchorMax = new Vector2(1f, 0.2f);
            gateRect.offsetMin = new Vector2(58f, 2f);
            gateRect.offsetMax = new Vector2(-124f, 0f);

            Button craftButton = CreateButton(root.transform, "CraftButton", "Craft", out TextMeshProUGUI craftLabel);
            ((Image)craftButton.targetGraphic).color = new Color(0.28f, 0.52f, 0.34f, 1f);
            RectTransform craftRect = (RectTransform)craftButton.transform;
            craftRect.anchorMin = new Vector2(1f, 0.5f);
            craftRect.anchorMax = new Vector2(1f, 0.5f);
            craftRect.pivot = new Vector2(1f, 0.5f);
            craftRect.sizeDelta = new Vector2(112f, 44f);
            craftRect.anchoredPosition = new Vector2(-6f, 0f);

            UiCraftingRecipeRow rowComponent = root.AddComponent<UiCraftingRecipeRow>();
            rowComponent.IconImage = icon;
            rowComponent.NameText = nameText;
            rowComponent.RequirementText = requirementText;
            rowComponent.GateText = gateText;
            rowComponent.CraftButton = craftButton;
            rowComponent.CraftButtonLabel = craftLabel;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, CraftingRecipeRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiCraftingRecipeRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildLeaderboardWindow(Transform canvasTransform)
        {
            GameObject windowObject = BuildSimpleListWindowShell("LeaderboardWindow", canvasTransform, "Leaderboard", out RectTransform contentAreaRect, out TextMeshProUGUI _);
            BuildLeaderboardListInto(windowObject.transform, contentAreaRect);
            return windowObject;
        }

        // Modul: Map Hub, Part 2. Extracted so the real, network-backed
        // global leaderboard (LeaderboardCache/UiLeaderboardWindow) can be
        // embedded a second time inside the Boss World panel's "damage
        // leaderboard" section without duplicating the ~45-line scroll
        // view/paging/prefab wiring block. There is no boss-damage-specific
        // ranking on the server (LeaderboardCache is the one real global
        // rank-by-level/xp source) - the Boss panel labels this section
        // "Top Players" rather than falsely implying boss-specific data.
        private static UiLeaderboardWindow BuildLeaderboardListInto(Transform hostTransform, RectTransform contentAreaRect)
        {
            GameObject pageRowObject = new GameObject("PageRow", typeof(RectTransform));
            pageRowObject.transform.SetParent(contentAreaRect, false);
            RectTransform pageRowRect = (RectTransform)pageRowObject.transform;
            pageRowRect.anchorMin = new Vector2(0f, 1f);
            pageRowRect.anchorMax = new Vector2(1f, 1f);
            pageRowRect.pivot = new Vector2(0.5f, 1f);
            pageRowRect.sizeDelta = new Vector2(0f, 32f);
            pageRowRect.anchoredPosition = Vector2.zero;

            HorizontalLayoutGroup pageLayout = pageRowObject.AddComponent<HorizontalLayoutGroup>();
            pageLayout.spacing = 8f;
            pageLayout.childControlWidth = false;
            pageLayout.childForceExpandWidth = false;
            pageLayout.childControlHeight = true;
            pageLayout.childForceExpandHeight = true;

            Button prevButton = CreateButton(pageRowRect, "PrevPageButton", "Prev", out TextMeshProUGUI _);
            LayoutElement prevLayout = prevButton.gameObject.AddComponent<LayoutElement>();
            prevLayout.preferredWidth = 70f;

            TextMeshProUGUI pageLabelText = CreateText(pageRowRect, "PageLabelText", "Rank 1+", 14f, TextAlignmentOptions.Center);
            LayoutElement pageLabelLayout = pageLabelText.gameObject.AddComponent<LayoutElement>();
            pageLabelLayout.preferredWidth = 100f;

            Button nextButton = CreateButton(pageRowRect, "NextPageButton", "Next", out TextMeshProUGUI _);
            LayoutElement nextLayout = nextButton.gameObject.AddComponent<LayoutElement>();
            nextLayout.preferredWidth = 70f;

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = Vector2.zero;
            scrollAreaRect.offsetMax = new Vector2(0f, -40f);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(scrollAreaRect);

            GameObject rowPrefabAsset = BuildAndSaveLeaderboardRowPrefab();

            UiLeaderboardWindow window = hostTransform.gameObject.AddComponent<UiLeaderboardWindow>();
            window.RowContainer = content;
            window.RowPrefab = rowPrefabAsset.GetComponent<UiLeaderboardEntryRow>();
            window.NextPageButton = nextButton;
            window.PrevPageButton = prevButton;
            window.PageLabelText = pageLabelText;

            return window;
        }

        private static GameObject BuildMailboxWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("MailboxWindow", canvasTransform, string.Empty, out RectTransform contentAreaRect, out TextMeshProUGUI headerText);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(contentAreaRect);

            GameObject rowPrefabAsset = BuildAndSaveMailboxRowPrefab();

            UiMailboxWindow window = windowObject.AddComponent<UiMailboxWindow>();
            window.SyncProxy = syncProxy;
            window.RowContainer = content;
            window.RowPrefab = rowPrefabAsset.GetComponent<UiMailboxEntryRow>();
            window.HeaderText = headerText;
            window.NetworkClient = networkClient;

            return windowObject;
        }

        // Modul: UI audit follow-up. Friends roster - AddFriend/RemoveFriend/
        // BlockPlayer/UnblockPlayer (RelationshipEngine) already existed and
        // worked over the WebSocket wire, but there was no UI to see the
        // list or trigger them (see FriendsCache/UiFriendsWindow's own
        // comments). Replaces the old BuildPlaceholderWindow static shell.
        // Modul: UI rework. Two columns: the roster (add-friend field,
        // status line, pooled friend rows) on the left, a real private
        // whisper thread on the right. The whisper channel has existed on
        // the wire since the social layer shipped but nothing client-side
        // could ever pick a recipient, so it was unreachable - clicking a
        // roster row's Chat button now points the panel at that friend.
        private static GameObject BuildFriendsWindow(Transform canvasTransform, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("FriendsWindow", canvasTransform, "Friends", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            GameObject rosterColumnObject = new GameObject("RosterColumn", typeof(RectTransform));
            rosterColumnObject.transform.SetParent(contentAreaRect, false);
            RectTransform rosterColumnRect = (RectTransform)rosterColumnObject.transform;
            rosterColumnRect.anchorMin = new Vector2(0f, 0f);
            rosterColumnRect.anchorMax = new Vector2(1f, 1f);
            rosterColumnRect.offsetMin = Vector2.zero;
            rosterColumnRect.offsetMax = new Vector2(0f, 0f);
            // Portrait-first: the roster takes the top 52% and the chat the
            // rest, rather than a side-by-side split that would leave both
            // halves unusably narrow at the 1080-wide reference resolution.
            rosterColumnRect.anchorMin = new Vector2(0f, 0.48f);

            (TMP_InputField usernameInput, Button addButton) = BuildLabeledInputRow(rosterColumnRect, "AddFriendRow", "Username", "Add Friend");
            RectTransform addRowRect = (RectTransform)usernameInput.transform.parent;
            addRowRect.anchorMin = new Vector2(0f, 1f);
            addRowRect.anchorMax = new Vector2(1f, 1f);
            addRowRect.pivot = new Vector2(0.5f, 1f);
            addRowRect.sizeDelta = new Vector2(0f, 44f);
            addRowRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI statusText = CreateText(rosterColumnRect, "StatusText", "Add a friend by their exact username, then tap Chat to talk privately.", 13f, TextAlignmentOptions.MidlineLeft);
            statusText.color = new Color(1f, 1f, 1f, 0.6f);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = new Vector2(0f, 24f);
            statusRect.anchoredPosition = new Vector2(0f, -50f);

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(rosterColumnRect, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = new Vector2(0f, 6f);
            scrollAreaRect.offsetMax = new Vector2(0f, -80f);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(scrollAreaRect);
            StretchFull((RectTransform)content.parent.parent);

            GameObject chatColumnObject = new GameObject("PrivateChatColumn", typeof(RectTransform));
            chatColumnObject.transform.SetParent(contentAreaRect, false);
            RectTransform chatColumnRect = (RectTransform)chatColumnObject.transform;
            chatColumnRect.anchorMin = new Vector2(0f, 0f);
            chatColumnRect.anchorMax = new Vector2(1f, 0.46f);
            chatColumnRect.offsetMin = Vector2.zero;
            chatColumnRect.offsetMax = Vector2.zero;

            UiChatWindow whisperChat = BuildChatPanel(chatColumnRect, "PrivateChatPanel", "Private Chat", ChatChannelType.Whisper, networkClient, withMinimizeToggle: false);

            GameObject rowPrefabAsset = BuildAndSaveFriendRowPrefab();

            UiFriendsWindow window = windowObject.AddComponent<UiFriendsWindow>();
            window.NetworkClient = networkClient;
            window.RowContainer = content;
            window.RowPrefab = rowPrefabAsset.GetComponent<UiFriendEntryRow>();
            window.AddFriendUsernameField = usernameInput;
            window.AddFriendButton = addButton;
            window.StatusText = statusText;
            window.WhisperChatWindow = whisperChat;

            return windowObject;
        }

        private static GameObject BuildAndSaveFriendRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiFriendEntryRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 34f);

            TextMeshProUGUI nameText = CreateText(root.transform, "NameText", "Player", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform nameTextRect = (RectTransform)nameText.transform;
            nameTextRect.anchorMin = Vector2.zero;
            nameTextRect.anchorMax = Vector2.one;
            nameTextRect.offsetMin = new Vector2(6f, 0f);
            nameTextRect.offsetMax = new Vector2(-250f, 0f);

            Button removeButton = CreateButton(root.transform, "RemoveButton", "Remove", out TextMeshProUGUI _);
            RectTransform removeRect = (RectTransform)removeButton.transform;
            removeRect.anchorMin = new Vector2(1f, 0.1f);
            removeRect.anchorMax = new Vector2(1f, 0.9f);
            removeRect.pivot = new Vector2(1f, 0.5f);
            removeRect.sizeDelta = new Vector2(80f, 0f);
            removeRect.anchoredPosition = new Vector2(-4f, 0f);

            Button blockButton = CreateButton(root.transform, "BlockButton", "Block", out TextMeshProUGUI _);
            RectTransform blockRect = (RectTransform)blockButton.transform;
            blockRect.anchorMin = new Vector2(1f, 0.1f);
            blockRect.anchorMax = new Vector2(1f, 0.9f);
            blockRect.pivot = new Vector2(1f, 0.5f);
            blockRect.sizeDelta = new Vector2(70f, 0f);
            blockRect.anchoredPosition = new Vector2(-88f, 0f);

            Button chatButton = CreateButton(root.transform, "ChatButton", "Chat", out TextMeshProUGUI _);
            ((Image)chatButton.targetGraphic).color = new Color(0.24f, 0.58f, 0.42f, 1f);
            RectTransform chatRect = (RectTransform)chatButton.transform;
            chatRect.anchorMin = new Vector2(1f, 0.1f);
            chatRect.anchorMax = new Vector2(1f, 0.9f);
            chatRect.pivot = new Vector2(1f, 0.5f);
            chatRect.sizeDelta = new Vector2(66f, 0f);
            chatRect.anchoredPosition = new Vector2(-162f, 0f);

            Button unblockButton = CreateButton(root.transform, "UnblockButton", "Unblock", out TextMeshProUGUI _);
            RectTransform unblockRect = (RectTransform)unblockButton.transform;
            unblockRect.anchorMin = new Vector2(1f, 0.1f);
            unblockRect.anchorMax = new Vector2(1f, 0.9f);
            unblockRect.pivot = new Vector2(1f, 0.5f);
            unblockRect.sizeDelta = new Vector2(90f, 0f);
            unblockRect.anchoredPosition = new Vector2(-4f, 0f);

            UiFriendEntryRow rowComponent = root.AddComponent<UiFriendEntryRow>();
            rowComponent.NameText = nameText;
            rowComponent.RemoveButton = removeButton;
            rowComponent.BlockButton = blockButton;
            rowComponent.UnblockButton = unblockButton;
            rowComponent.ChatButton = chatButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, FriendRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiFriendEntryRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildStoreWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("StoreWindow", canvasTransform, string.Empty, out RectTransform contentAreaRect, out TextMeshProUGUI headerText);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(contentAreaRect);

            GameObject rowPrefabAsset = BuildAndSaveStoreRowPrefab();

            UiStoreWindow window = windowObject.AddComponent<UiStoreWindow>();
            window.SyncProxy = syncProxy;
            window.RowContainer = content;
            window.RowPrefab = rowPrefabAsset.GetComponent<UiStoreEntryRow>();
            window.HeaderText = headerText;
            window.NetworkClient = networkClient;

            return windowObject;
        }

        private static GameObject BuildSeasonPassWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("SeasonPassWindow", canvasTransform, string.Empty, out RectTransform contentAreaRect, out TextMeshProUGUI headerText);

            GameObject statsRowObject = new GameObject("StatsRow", typeof(RectTransform));
            statsRowObject.transform.SetParent(contentAreaRect, false);
            RectTransform statsRowRect = (RectTransform)statsRowObject.transform;
            statsRowRect.anchorMin = new Vector2(0f, 1f);
            statsRowRect.anchorMax = new Vector2(1f, 1f);
            statsRowRect.pivot = new Vector2(0.5f, 1f);
            statsRowRect.sizeDelta = new Vector2(0f, 28f);
            statsRowRect.anchoredPosition = Vector2.zero;

            HorizontalLayoutGroup statsLayout = statsRowObject.AddComponent<HorizontalLayoutGroup>();
            statsLayout.spacing = 10f;
            statsLayout.childControlWidth = true;
            statsLayout.childForceExpandWidth = true;
            statsLayout.childControlHeight = true;
            statsLayout.childForceExpandHeight = true;

            TextMeshProUGUI passLevelText = CreateText(statsRowRect, "PassLevelText", "Pass Level 0", 15f, TextAlignmentOptions.MidlineLeft);
            TextMeshProUGUI accumulatedXpText = CreateText(statsRowRect, "AccumulatedXpText", "0 XP", 15f, TextAlignmentOptions.MidlineRight);

            Button purchasePremiumButton = CreateButton(contentAreaRect, "PurchasePremiumButton", "Purchase Premium (950 Diamonds)", out TextMeshProUGUI _);
            RectTransform purchasePremiumRect = (RectTransform)purchasePremiumButton.transform;
            purchasePremiumRect.anchorMin = new Vector2(0f, 1f);
            purchasePremiumRect.anchorMax = new Vector2(1f, 1f);
            purchasePremiumRect.pivot = new Vector2(0.5f, 1f);
            purchasePremiumRect.sizeDelta = new Vector2(0f, 40f);
            purchasePremiumRect.anchoredPosition = new Vector2(0f, -34f);

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = Vector2.zero;
            scrollAreaRect.offsetMax = new Vector2(0f, -80f);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(scrollAreaRect);

            GameObject rowPrefabAsset = BuildAndSaveSeasonPassRowPrefab();

            UiSeasonPassWindow window = windowObject.AddComponent<UiSeasonPassWindow>();
            window.SyncProxy = syncProxy;
            window.NetworkClient = networkClient;
            window.RowContainer = content;
            window.RowPrefab = rowPrefabAsset.GetComponent<UiSeasonPassMilestoneRow>();
            window.PassLevelText = passLevelText;
            window.AccumulatedXpText = accumulatedXpText;
            window.HeaderText = headerText;
            window.PurchasePremiumButton = purchasePremiumButton;

            return windowObject;
        }

        private static GameObject BuildAndSaveAchievementRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiAchievementRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 78f);
            root.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);

            VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 4, 4);
            layout.spacing = 2f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            TextMeshProUGUI idText = CreateText(root.transform, "AchievementIdText", "Achievement 0", 14f, TextAlignmentOptions.MidlineLeft);
            SetFixedLayoutHeight(idText.gameObject, 18f);

            TextMeshProUGUI tierText = CreateText(root.transform, "TierText", "Tier None", 12f, TextAlignmentOptions.MidlineLeft);
            SetFixedLayoutHeight(tierText.gameObject, 14f);

            GameObject progressRow = new GameObject("ProgressRow", typeof(RectTransform));
            progressRow.transform.SetParent(root.transform, false);
            SetFixedLayoutHeight(progressRow, 16f);

            HorizontalLayoutGroup progressRowLayoutGroup = progressRow.AddComponent<HorizontalLayoutGroup>();
            progressRowLayoutGroup.spacing = 6f;
            progressRowLayoutGroup.childControlWidth = true;
            progressRowLayoutGroup.childForceExpandWidth = false;
            progressRowLayoutGroup.childControlHeight = true;
            progressRowLayoutGroup.childForceExpandHeight = true;

            (GameObject barBackground, RectTransform barFill) = BuildAnchoredProgressBar(progressRow.transform, new Color(0.4f, 0.8f, 1f, 1f));
            LayoutElement barLayout = barBackground.AddComponent<LayoutElement>();
            barLayout.flexibleWidth = 1f;

            TextMeshProUGUI progressText = CreateText(progressRow.transform, "ProgressText", "0 / 0", 12f, TextAlignmentOptions.MidlineRight);
            LayoutElement progressTextLayout = progressText.gameObject.AddComponent<LayoutElement>();
            progressTextLayout.preferredWidth = 90f;

            GameObject claimRow = new GameObject("ClaimRow", typeof(RectTransform));
            claimRow.transform.SetParent(root.transform, false);
            SetFixedLayoutHeight(claimRow, 22f);

            Button claimButton = CreateButton(claimRow.transform, "ClaimButton", "Claim", out TextMeshProUGUI _);
            RectTransform claimButtonRect = (RectTransform)claimButton.transform;
            claimButtonRect.anchorMin = new Vector2(1f, 0f);
            claimButtonRect.anchorMax = new Vector2(1f, 1f);
            claimButtonRect.pivot = new Vector2(1f, 0.5f);
            claimButtonRect.sizeDelta = new Vector2(80f, 0f);
            claimButtonRect.anchoredPosition = Vector2.zero;

            UiAchievementRow rowComponent = root.AddComponent<UiAchievementRow>();
            rowComponent.AchievementIdText = idText;
            rowComponent.TierText = tierText;
            rowComponent.ProgressText = progressText;
            rowComponent.ProgressBarFill = barFill;
            rowComponent.ClaimButton = claimButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, AchievementRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiAchievementRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveLeaderboardRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiLeaderboardEntryRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 26f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Row", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = Vector2.zero;

            UiLeaderboardEntryRow rowComponent = root.AddComponent<UiLeaderboardEntryRow>();
            rowComponent.RowLabelText = rowText;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, LeaderboardRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiLeaderboardEntryRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveMailboxRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiMailboxEntryRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 30f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Item", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-70f, 0f);

            Button claimButton = CreateButton(root.transform, "ClaimButton", "Claim", out TextMeshProUGUI _);
            RectTransform claimRect = (RectTransform)claimButton.transform;
            claimRect.anchorMin = new Vector2(1f, 0.1f);
            claimRect.anchorMax = new Vector2(1f, 0.9f);
            claimRect.pivot = new Vector2(1f, 0.5f);
            claimRect.sizeDelta = new Vector2(64f, 0f);
            claimRect.anchoredPosition = new Vector2(-4f, 0f);

            UiMailboxEntryRow rowComponent = root.AddComponent<UiMailboxEntryRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.ClaimButton = claimButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, MailboxRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiMailboxEntryRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveStoreRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiStoreEntryRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 34f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Product", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-90f, 0f);

            Button purchaseButton = CreateButton(root.transform, "PurchaseButton", "Buy", out TextMeshProUGUI _);
            RectTransform purchaseRect = (RectTransform)purchaseButton.transform;
            purchaseRect.anchorMin = new Vector2(1f, 0.1f);
            purchaseRect.anchorMax = new Vector2(1f, 0.9f);
            purchaseRect.pivot = new Vector2(1f, 0.5f);
            purchaseRect.sizeDelta = new Vector2(84f, 0f);
            purchaseRect.anchoredPosition = new Vector2(-4f, 0f);

            UiStoreEntryRow rowComponent = root.AddComponent<UiStoreEntryRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.PurchaseButton = purchaseButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, StoreRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiStoreEntryRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        private static GameObject BuildAndSaveSeasonPassRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiSeasonPassMilestoneRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 30f);

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Milestone", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(6f, 0f);
            rowTextRect.offsetMax = new Vector2(-64f, 0f);

            Button claimButton = CreateButton(root.transform, "ClaimButton", "Claim", out TextMeshProUGUI _);
            RectTransform claimRect = (RectTransform)claimButton.transform;
            claimRect.anchorMin = new Vector2(1f, 0.1f);
            claimRect.anchorMax = new Vector2(1f, 0.9f);
            claimRect.pivot = new Vector2(1f, 0.5f);
            claimRect.sizeDelta = new Vector2(58f, 0f);
            claimRect.anchoredPosition = new Vector2(-4f, 0f);

            UiSeasonPassMilestoneRow rowComponent = root.AddComponent<UiSeasonPassMilestoneRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.ClaimButton = claimButton;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, SeasonPassRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiSeasonPassMilestoneRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        // ------------------------------------------------------------
        // Map Hub - medieval-fantasy map field with 5 clickable zones
        // (Combat, Village, Guild, Market, Boss), replacing the old flat
        // scrollable nav-tab strip as the home screen. No art assets exist
        // (same "zero visual asset creation" constraint as the rest of
        // this file), so each zone is a plain colored bounding box with a
        // text label standing in for the sketch's hand-drawn region art.
        // ------------------------------------------------------------
        private static (GameObject hub, Button combatZone, Button villageZone, Button guildZone, Button marketZone, Button bossZone) BuildMainMapHub(Transform canvasTransform)
        {
            GameObject hubObject = new GameObject("MainMapHub", typeof(RectTransform));
            hubObject.transform.SetParent(canvasTransform, false);
            StretchFull((RectTransform)hubObject.transform);

            hubObject.AddComponent<Image>().color = new Color(0.10f, 0.14f, 0.09f, 1f);

            // Modul: Map Hub. MapFieldArea reserves a fixed-pixel top
            // margin (clears the persistent Menu/Map buttons and Gold/Gems
            // currency display - neither scales with canvas height the way
            // percentage anchors do) and a fixed-pixel bottom margin
            // (clears the Season Pass banner). Every zone below is
            // anchored as a fraction of THIS area, not the full screen, so
            // it stays clear of the persistent overlay on any aspect
            // ratio, not only the 1080x1920 portrait reference.
            GameObject mapFieldObject = new GameObject("MapFieldArea", typeof(RectTransform));
            mapFieldObject.transform.SetParent(hubObject.transform, false);
            RectTransform mapFieldRect = (RectTransform)mapFieldObject.transform;
            mapFieldRect.anchorMin = Vector2.zero;
            mapFieldRect.anchorMax = Vector2.one;
            mapFieldRect.offsetMin = new Vector2(0f, 70f);
            mapFieldRect.offsetMax = new Vector2(0f, -180f);

            TextMeshProUGUI titleText = CreateText(mapFieldRect, "MapTitleText", "Kingdom Map", 24f, TextAlignmentOptions.Center);
            RectTransform titleRect = (RectTransform)titleText.transform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(0f, 50f);
            titleRect.anchoredPosition = Vector2.zero;

            // Modul: UI audit follow-up. Zone rows previously spanned only
            // 0.06-0.80 of MapFieldArea, leaving a ~280px dead gap between
            // the title (top 50px) and the first row - the title's own
            // fixed-pixel height was never accounted for in the fractional
            // layout. Re-anchored to start just below the title (0.95) and
            // reach the bottom margin (0.06), with each row's height grown
            // proportionally to fill the reclaimed space rather than just
            // closing the gap.
            Button villageZone = BuildMapZone(mapFieldRect, "VillageZone", "Village", new Vector2(0.06f, 0.72f), new Vector2(0.48f, 0.95f), new Color(0.42f, 0.32f, 0.16f, 1f));
            Button guildZone = BuildMapZone(mapFieldRect, "GuildZone", "Guild Hall", new Vector2(0.52f, 0.72f), new Vector2(0.94f, 0.95f), new Color(0.30f, 0.24f, 0.42f, 1f));
            Button marketZone = BuildMapZone(mapFieldRect, "MarketZone", "Market", new Vector2(0.06f, 0.47f), new Vector2(0.48f, 0.69f), new Color(0.44f, 0.36f, 0.10f, 1f));
            Button bossZone = BuildMapZone(mapFieldRect, "BossZone", "World Boss", new Vector2(0.52f, 0.47f), new Vector2(0.94f, 0.69f), new Color(0.46f, 0.12f, 0.12f, 1f));
            // Modul: UI rework. Bottom raised from 0.06 to 0.20: the world
            // chat dock now lives in the bottom ~300px of the hub (see
            // BuildWorldChatOverlay), and at 0.06 the Combat zone ran
            // underneath it - the chat drew on top of a live button, so the
            // bottom third of the largest zone on the map was both visually
            // covered and unclickable.
            Button combatZone = BuildMapZone(mapFieldRect, "CombatZone", "Combat", new Vector2(0.06f, 0.20f), new Vector2(0.94f, 0.44f), new Color(0.18f, 0.34f, 0.18f, 1f));

            return (hubObject, combatZone, villageZone, guildZone, marketZone, bossZone);
        }

        private static Button BuildMapZone(Transform parent, string zoneName, string label, Vector2 anchorMin, Vector2 anchorMax, Color zoneColor)
        {
            GameObject zoneObject = new GameObject(zoneName, typeof(RectTransform));
            zoneObject.transform.SetParent(parent, false);
            RectTransform zoneRect = (RectTransform)zoneObject.transform;
            zoneRect.anchorMin = anchorMin;
            zoneRect.anchorMax = anchorMax;
            zoneRect.offsetMin = Vector2.zero;
            zoneRect.offsetMax = Vector2.zero;

            Image zoneImage = zoneObject.AddComponent<Image>();
            zoneImage.color = zoneColor;
            Button zoneButton = zoneObject.AddComponent<Button>();
            zoneButton.targetGraphic = zoneImage;

            TextMeshProUGUI zoneLabel = CreateText(zoneRect, "ZoneLabel", label, 18f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)zoneLabel.transform);

            return zoneButton;
        }

        // ------------------------------------------------------------
        // Combat screen.
        //
        // Modul: UI rework. Replaces five identical "Region N (0 / 1000)"
        // rows, each with a dropdown that listed the player's ENTIRE
        // discovered monster codex regardless of which region the row
        // claimed to be (the client mirror of monsters.json did not carry
        // RegionTier, so it genuinely could not tell which monsters belonged
        // where), and which showed no art, no monster stats, no target
        // health and no feedback of any kind.
        //
        // The new layout, top to bottom - see UiCombatLocationPanel's own
        // header comment for what backs each part with real data:
        //   [<]  big location / target art  [>]
        //        location name + clear progress bar
        //        selected target name + live HP bar
        //   monster roster for this location, boss last
        //   your stats + the four character slots
        //   food and potion slots
        //   this session's tally
        //   [ Fight ]  [ Watch the fight ]
        // ------------------------------------------------------------
        private static (GameObject panel, UiCombatLocationPanel component) BuildCombatSelectionPanel(
            Transform canvasTransform, WebSocketClient networkClient, VisualSyncProxy syncProxy, AssetRegistry assetRegistry)
        {
            GameObject panelObject = BuildSimpleListWindowShell("CombatPanel", canvasTransform, "Choose Your Hunt", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            UiCombatLocationPanel panel = panelObject.AddComponent<UiCombatLocationPanel>();
            panel.NetworkClient = networkClient;
            panel.SyncProxy = syncProxy;
            panel.Registry = assetRegistry;

            // ---- Feature image + location arrows (top block) ----
            GameObject featureBlockObject = new GameObject("FeatureBlock", typeof(RectTransform));
            featureBlockObject.transform.SetParent(contentAreaRect, false);
            RectTransform featureBlockRect = (RectTransform)featureBlockObject.transform;
            featureBlockRect.anchorMin = new Vector2(0f, 1f);
            featureBlockRect.anchorMax = new Vector2(1f, 1f);
            featureBlockRect.pivot = new Vector2(0.5f, 1f);
            featureBlockRect.sizeDelta = new Vector2(0f, 300f);
            featureBlockRect.anchoredPosition = Vector2.zero;

            Image featureImage = new GameObject("FeatureImage", typeof(RectTransform)).AddComponent<Image>();
            featureImage.transform.SetParent(featureBlockRect, false);
            featureImage.preserveAspect = true;
            RectTransform featureImageRect = (RectTransform)featureImage.transform;
            featureImageRect.anchorMin = new Vector2(0f, 0f);
            featureImageRect.anchorMax = new Vector2(1f, 1f);
            featureImageRect.offsetMin = new Vector2(72f, 0f);
            featureImageRect.offsetMax = new Vector2(-72f, 0f);
            panel.FeatureImage = featureImage;

            TextMeshProUGUI featureCaption = CreateText(featureImageRect, "FeatureCaption", string.Empty, 20f, TextAlignmentOptions.Center);
            featureCaption.color = new Color(1f, 1f, 1f, 0.5f);
            featureCaption.fontStyle = FontStyles.Italic;
            StretchFull((RectTransform)featureCaption.transform);
            panel.FeatureCaptionText = featureCaption;

            Button previousButton = CreateButton(featureBlockRect, "PreviousLocationButton", "<", out TextMeshProUGUI previousLabel);
            previousLabel.fontSize = 34f;
            RectTransform previousRect = (RectTransform)previousButton.transform;
            previousRect.anchorMin = new Vector2(0f, 0.5f);
            previousRect.anchorMax = new Vector2(0f, 0.5f);
            previousRect.pivot = new Vector2(0f, 0.5f);
            previousRect.sizeDelta = new Vector2(62f, 96f);
            previousRect.anchoredPosition = Vector2.zero;
            panel.PreviousLocationButton = previousButton;

            Button nextButton = CreateButton(featureBlockRect, "NextLocationButton", ">", out TextMeshProUGUI nextLabel);
            nextLabel.fontSize = 34f;
            RectTransform nextRect = (RectTransform)nextButton.transform;
            nextRect.anchorMin = new Vector2(1f, 0.5f);
            nextRect.anchorMax = new Vector2(1f, 0.5f);
            nextRect.pivot = new Vector2(1f, 0.5f);
            nextRect.sizeDelta = new Vector2(62f, 96f);
            nextRect.anchoredPosition = Vector2.zero;
            panel.NextLocationButton = nextButton;

            // ---- Location name + clear progress ----
            TextMeshProUGUI locationName = CreateText(contentAreaRect, "LocationNameText", "Location", 26f, TextAlignmentOptions.Center);
            RectTransform locationNameRect = (RectTransform)locationName.transform;
            locationNameRect.anchorMin = new Vector2(0f, 1f);
            locationNameRect.anchorMax = new Vector2(1f, 1f);
            locationNameRect.pivot = new Vector2(0.5f, 1f);
            locationNameRect.sizeDelta = new Vector2(0f, 34f);
            locationNameRect.anchoredPosition = new Vector2(0f, -302f);
            panel.LocationNameText = locationName;

            TextMeshProUGUI locationProgress = CreateText(contentAreaRect, "LocationProgressText", string.Empty, 14f, TextAlignmentOptions.Center);
            locationProgress.color = new Color(1f, 1f, 1f, 0.65f);
            RectTransform locationProgressRect = (RectTransform)locationProgress.transform;
            locationProgressRect.anchorMin = new Vector2(0f, 1f);
            locationProgressRect.anchorMax = new Vector2(1f, 1f);
            locationProgressRect.pivot = new Vector2(0.5f, 1f);
            locationProgressRect.sizeDelta = new Vector2(0f, 22f);
            locationProgressRect.anchoredPosition = new Vector2(0f, -336f);
            panel.LocationProgressText = locationProgress;

            GameObject locationBarHost = new GameObject("LocationProgressBar", typeof(RectTransform));
            locationBarHost.transform.SetParent(contentAreaRect, false);
            RectTransform locationBarHostRect = (RectTransform)locationBarHost.transform;
            locationBarHostRect.anchorMin = new Vector2(0f, 1f);
            locationBarHostRect.anchorMax = new Vector2(1f, 1f);
            locationBarHostRect.pivot = new Vector2(0.5f, 1f);
            locationBarHostRect.sizeDelta = new Vector2(0f, 8f);
            locationBarHostRect.anchoredPosition = new Vector2(0f, -360f);
            (GameObject locationBarBackground, RectTransform locationFill) = BuildAnchoredProgressBar(locationBarHostRect, new Color(0.45f, 0.72f, 0.35f, 1f));
            // BuildAnchoredProgressBar leaves its background on a default
            // RectTransform because every existing caller drops it into a
            // LayoutGroup that sizes it. These two do not, so they stretch
            // it to the host explicitly rather than rendering a stray
            // default-sized box.
            StretchFull((RectTransform)locationBarBackground.transform);
            panel.LocationProgressFill = locationFill;

            // ---- Selected target: name + live HP bar ----
            GameObject targetRootObject = new GameObject("TargetHealthRoot", typeof(RectTransform));
            targetRootObject.transform.SetParent(contentAreaRect, false);
            RectTransform targetRootRect = (RectTransform)targetRootObject.transform;
            targetRootRect.anchorMin = new Vector2(0f, 1f);
            targetRootRect.anchorMax = new Vector2(1f, 1f);
            targetRootRect.pivot = new Vector2(0.5f, 1f);
            targetRootRect.sizeDelta = new Vector2(0f, 62f);
            targetRootRect.anchoredPosition = new Vector2(0f, -376f);
            panel.TargetHealthRoot = targetRootObject;

            TextMeshProUGUI targetName = CreateText(targetRootRect, "TargetNameText", string.Empty, 20f, TextAlignmentOptions.Center);
            RectTransform targetNameRect = (RectTransform)targetName.transform;
            targetNameRect.anchorMin = new Vector2(0f, 1f);
            targetNameRect.anchorMax = new Vector2(1f, 1f);
            targetNameRect.pivot = new Vector2(0.5f, 1f);
            targetNameRect.sizeDelta = new Vector2(0f, 26f);
            targetNameRect.anchoredPosition = Vector2.zero;
            panel.TargetNameText = targetName;

            GameObject targetBarHost = new GameObject("TargetHealthBar", typeof(RectTransform));
            targetBarHost.transform.SetParent(targetRootRect, false);
            RectTransform targetBarHostRect = (RectTransform)targetBarHost.transform;
            targetBarHostRect.anchorMin = new Vector2(0f, 1f);
            targetBarHostRect.anchorMax = new Vector2(1f, 1f);
            targetBarHostRect.pivot = new Vector2(0.5f, 1f);
            targetBarHostRect.sizeDelta = new Vector2(0f, 20f);
            targetBarHostRect.anchoredPosition = new Vector2(0f, -28f);
            (GameObject targetBarBackground, RectTransform targetFill) = BuildAnchoredProgressBar(targetBarHostRect, new Color(0.78f, 0.24f, 0.22f, 1f));
            StretchFull((RectTransform)targetBarBackground.transform);
            panel.TargetHealthFill = targetFill;

            TextMeshProUGUI targetHealth = CreateText(targetBarHostRect, "TargetHealthText", string.Empty, 13f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)targetHealth.transform);
            panel.TargetHealthText = targetHealth;

            // ---- Monster roster ----
            TextMeshProUGUI rosterHeader = CreateText(contentAreaRect, "RosterHeader", "CREATURES OF THIS LOCATION", 13f, TextAlignmentOptions.MidlineLeft);
            rosterHeader.color = new Color(0.85f, 0.72f, 0.45f, 1f);
            rosterHeader.characterSpacing = 6f;
            RectTransform rosterHeaderRect = (RectTransform)rosterHeader.transform;
            rosterHeaderRect.anchorMin = new Vector2(0f, 1f);
            rosterHeaderRect.anchorMax = new Vector2(1f, 1f);
            rosterHeaderRect.pivot = new Vector2(0.5f, 1f);
            rosterHeaderRect.sizeDelta = new Vector2(0f, 24f);
            rosterHeaderRect.anchoredPosition = new Vector2(0f, -444f);

            GameObject rosterAreaObject = new GameObject("RosterArea", typeof(RectTransform));
            rosterAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform rosterAreaRect = (RectTransform)rosterAreaObject.transform;
            rosterAreaRect.anchorMin = new Vector2(0f, 0f);
            rosterAreaRect.anchorMax = new Vector2(1f, 1f);
            rosterAreaRect.offsetMin = new Vector2(0f, 560f);
            rosterAreaRect.offsetMax = new Vector2(0f, -470f);

            (ScrollRect _, RectTransform rosterContent) = ChatSceneBuilder.BuildScrollView(rosterAreaRect);
            StretchFull((RectTransform)rosterContent.parent.parent);
            panel.MonsterRowContainer = rosterContent;
            panel.MonsterRowPrefab = BuildAndSaveCombatMonsterRowPrefab().GetComponent<UiCombatMonsterRow>();

            // ---- Character stats + slots ----
            TextMeshProUGUI characterStats = CreateText(contentAreaRect, "CharacterStatsText", string.Empty, 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform characterStatsRect = (RectTransform)characterStats.transform;
            characterStatsRect.anchorMin = new Vector2(0f, 0f);
            characterStatsRect.anchorMax = new Vector2(0.62f, 0f);
            characterStatsRect.pivot = new Vector2(0f, 0f);
            characterStatsRect.sizeDelta = new Vector2(0f, 54f);
            characterStatsRect.anchoredPosition = new Vector2(0f, 484f);
            panel.CharacterStatsText = characterStats;

            TextMeshProUGUI characterHealth = CreateText(contentAreaRect, "CharacterHealthText", string.Empty, 15f, TextAlignmentOptions.MidlineRight);
            RectTransform characterHealthRect = (RectTransform)characterHealth.transform;
            characterHealthRect.anchorMin = new Vector2(0.62f, 0f);
            characterHealthRect.anchorMax = new Vector2(1f, 0f);
            characterHealthRect.pivot = new Vector2(1f, 0f);
            characterHealthRect.sizeDelta = new Vector2(0f, 54f);
            characterHealthRect.anchoredPosition = new Vector2(0f, 484f);
            panel.CharacterHealthText = characterHealth;

            GameObject slotRowObject = new GameObject("CharacterSlotRow", typeof(RectTransform));
            slotRowObject.transform.SetParent(contentAreaRect, false);
            RectTransform slotRowRect = (RectTransform)slotRowObject.transform;
            slotRowRect.anchorMin = new Vector2(0f, 0f);
            slotRowRect.anchorMax = new Vector2(1f, 0f);
            slotRowRect.pivot = new Vector2(0.5f, 0f);
            slotRowRect.sizeDelta = new Vector2(0f, 60f);
            slotRowRect.anchoredPosition = new Vector2(0f, 418f);

            HorizontalLayoutGroup slotLayout = slotRowObject.AddComponent<HorizontalLayoutGroup>();
            slotLayout.spacing = 8f;
            slotLayout.childControlWidth = true;
            slotLayout.childForceExpandWidth = true;
            slotLayout.childControlHeight = true;
            slotLayout.childForceExpandHeight = true;

            for (int slotIndex = 0; slotIndex < 4; slotIndex++)
            {
                Button slotButton = CreateButton(slotRowRect, "CharacterSlot" + (slotIndex + 1), string.Empty, out TextMeshProUGUI _);
                ((Image)slotButton.targetGraphic).color = new Color(0.18f, 0.18f, 0.24f, 1f);

                TextMeshProUGUI slotText = CreateText(slotButton.transform, "SlotText", "empty", 13f, TextAlignmentOptions.Center);
                StretchFull((RectTransform)slotText.transform);

                GameObject highlight = new GameObject("SelectedHighlight", typeof(RectTransform));
                highlight.transform.SetParent(slotButton.transform, false);
                StretchFull((RectTransform)highlight.transform);
                Image highlightImage = highlight.AddComponent<Image>();
                highlightImage.color = new Color(0.95f, 0.78f, 0.35f, 0.28f);
                highlightImage.raycastTarget = false;
                highlight.SetActive(false);

                panel.CharacterSlotButtons[slotIndex] = slotButton;
                panel.CharacterSlotTexts[slotIndex] = slotText;
                panel.CharacterSlotSelectedHighlights[slotIndex] = highlight;
            }

            // ---- Food + potion slots ----
            (TMP_Dropdown foodDropdown, Button useFoodButton) = BuildConsumableSlot(contentAreaRect, "FoodSlot", "Food", 348f);
            panel.FoodDropdown = foodDropdown;
            panel.UseFoodButton = useFoodButton;

            (TMP_Dropdown potionDropdown, Button usePotionButton) = BuildConsumableSlot(contentAreaRect, "PotionSlot", "Potion", 296f);
            panel.PotionDropdown = potionDropdown;
            panel.UsePotionButton = usePotionButton;

            TextMeshProUGUI activeBuff = CreateText(contentAreaRect, "ActiveBuffText", "No active potion.", 13f, TextAlignmentOptions.MidlineLeft);
            activeBuff.color = new Color(0.75f, 0.9f, 1f, 0.85f);
            RectTransform activeBuffRect = (RectTransform)activeBuff.transform;
            activeBuffRect.anchorMin = new Vector2(0f, 0f);
            activeBuffRect.anchorMax = new Vector2(1f, 0f);
            activeBuffRect.pivot = new Vector2(0.5f, 0f);
            activeBuffRect.sizeDelta = new Vector2(0f, 22f);
            activeBuffRect.anchoredPosition = new Vector2(0f, 268f);
            panel.ActiveBuffText = activeBuff;

            // ---- Session tally ----
            TextMeshProUGUI sessionHeader = CreateText(contentAreaRect, "SessionHeader", "THIS SESSION", 13f, TextAlignmentOptions.MidlineLeft);
            sessionHeader.color = new Color(0.85f, 0.72f, 0.45f, 1f);
            sessionHeader.characterSpacing = 6f;
            RectTransform sessionHeaderRect = (RectTransform)sessionHeader.transform;
            sessionHeaderRect.anchorMin = new Vector2(0f, 0f);
            sessionHeaderRect.anchorMax = new Vector2(1f, 0f);
            sessionHeaderRect.pivot = new Vector2(0.5f, 0f);
            sessionHeaderRect.sizeDelta = new Vector2(0f, 22f);
            sessionHeaderRect.anchoredPosition = new Vector2(0f, 240f);

            TextMeshProUGUI sessionSummary = CreateText(contentAreaRect, "SessionSummaryText", "Send a character out to start tracking this session.", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform sessionSummaryRect = (RectTransform)sessionSummary.transform;
            sessionSummaryRect.anchorMin = new Vector2(0f, 0f);
            sessionSummaryRect.anchorMax = new Vector2(1f, 0f);
            sessionSummaryRect.pivot = new Vector2(0.5f, 0f);
            sessionSummaryRect.sizeDelta = new Vector2(0f, 24f);
            sessionSummaryRect.anchoredPosition = new Vector2(0f, 214f);
            panel.SessionSummaryText = sessionSummary;

            TextMeshProUGUI sessionKills = CreateText(contentAreaRect, "SessionKillListText", string.Empty, 13f, TextAlignmentOptions.TopLeft);
            sessionKills.color = new Color(1f, 1f, 1f, 0.72f);
            RectTransform sessionKillsRect = (RectTransform)sessionKills.transform;
            sessionKillsRect.anchorMin = new Vector2(0f, 0f);
            sessionKillsRect.anchorMax = new Vector2(1f, 0f);
            sessionKillsRect.pivot = new Vector2(0.5f, 0f);
            sessionKillsRect.sizeDelta = new Vector2(0f, 120f);
            sessionKillsRect.anchoredPosition = new Vector2(0f, 92f);
            panel.SessionKillListText = sessionKills;

            // ---- Status + actions ----
            TextMeshProUGUI statusText = CreateText(contentAreaRect, "StatusText", string.Empty, 13f, TextAlignmentOptions.Center);
            statusText.color = new Color(1f, 0.86f, 0.6f, 1f);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0f, 0f);
            statusRect.anchorMax = new Vector2(1f, 0f);
            statusRect.pivot = new Vector2(0.5f, 0f);
            statusRect.sizeDelta = new Vector2(0f, 22f);
            statusRect.anchoredPosition = new Vector2(0f, 66f);
            panel.StatusText = statusText;

            Button deployButton = CreateButton(contentAreaRect, "DeployButton", "Fight", out TextMeshProUGUI deployLabel);
            ((Image)deployButton.targetGraphic).color = new Color(0.62f, 0.24f, 0.20f, 1f);
            deployLabel.fontSize = 20f;
            RectTransform deployRect = (RectTransform)deployButton.transform;
            deployRect.anchorMin = new Vector2(0f, 0f);
            deployRect.anchorMax = new Vector2(0.58f, 0f);
            deployRect.pivot = new Vector2(0f, 0f);
            deployRect.sizeDelta = new Vector2(-6f, 56f);
            deployRect.anchoredPosition = Vector2.zero;
            panel.DeployButton = deployButton;
            panel.DeployButtonLabel = deployLabel;

            Button watchButton = CreateButton(contentAreaRect, "WatchFightButton", "Watch the fight", out TextMeshProUGUI _);
            ((Image)watchButton.targetGraphic).color = new Color(0.22f, 0.30f, 0.42f, 1f);
            RectTransform watchRect = (RectTransform)watchButton.transform;
            watchRect.anchorMin = new Vector2(0.58f, 0f);
            watchRect.anchorMax = new Vector2(1f, 0f);
            watchRect.pivot = new Vector2(1f, 0f);
            watchRect.sizeDelta = new Vector2(0f, 56f);
            watchRect.anchoredPosition = Vector2.zero;

            // Modul: the old Deploy button switched screens unconditionally,
            // even in the (common) case where it had silently discarded both
            // selections and dispatched nothing at all. Splitting the screen
            // switch onto its own button means a failed Fight now stays put
            // and says why, instead of looking like it worked.
            UnityEditor.Events.UnityEventTools.AddPersistentListener(watchButton.onClick, panel.OpenCharacterScreen);

            return (panelObject, panel);
        }

        private static (TMP_Dropdown dropdown, Button useButton) BuildConsumableSlot(RectTransform parent, string slotName, string label, float anchoredY)
        {
            GameObject rowObject = new GameObject(slotName, typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);
            RectTransform rowRect = (RectTransform)rowObject.transform;
            rowRect.anchorMin = new Vector2(0f, 0f);
            rowRect.anchorMax = new Vector2(1f, 0f);
            rowRect.pivot = new Vector2(0.5f, 0f);
            rowRect.sizeDelta = new Vector2(0f, 44f);
            rowRect.anchoredPosition = new Vector2(0f, anchoredY);

            TextMeshProUGUI slotLabel = CreateText(rowRect, "SlotLabel", label, 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform slotLabelRect = (RectTransform)slotLabel.transform;
            slotLabelRect.anchorMin = new Vector2(0f, 0f);
            slotLabelRect.anchorMax = new Vector2(0f, 1f);
            slotLabelRect.pivot = new Vector2(0f, 0.5f);
            slotLabelRect.sizeDelta = new Vector2(90f, 0f);
            slotLabelRect.anchoredPosition = Vector2.zero;

            TMP_Dropdown dropdown = CreateTmpDropdown(rowRect, slotName + "Dropdown");
            RectTransform dropdownRect = (RectTransform)dropdown.transform;
            dropdownRect.anchorMin = new Vector2(0f, 0f);
            dropdownRect.anchorMax = new Vector2(1f, 1f);
            dropdownRect.offsetMin = new Vector2(94f, 2f);
            dropdownRect.offsetMax = new Vector2(-104f, -2f);

            // CreateTmpDropdown mirrors Unity's stock light-grey dropdown
            // template. Two of those against this screen's near-black panel
            // read as two glaring white bars, so the closed control is
            // re-tinted to match everything around it. The open template
            // list keeps its light styling - it draws over the page and
            // needs the contrast.
            if (dropdown.targetGraphic is Image dropdownBackground)
            {
                dropdownBackground.color = new Color(0.18f, 0.18f, 0.24f, 1f);
            }
            if (dropdown.captionText != null)
            {
                dropdown.captionText.color = Color.white;
            }

            Button useButton = CreateButton(rowRect, slotName + "UseButton", "Use", out TextMeshProUGUI _);
            ((Image)useButton.targetGraphic).color = new Color(0.28f, 0.52f, 0.34f, 1f);
            RectTransform useRect = (RectTransform)useButton.transform;
            useRect.anchorMin = new Vector2(1f, 0f);
            useRect.anchorMax = new Vector2(1f, 1f);
            useRect.pivot = new Vector2(1f, 0.5f);
            useRect.sizeDelta = new Vector2(96f, -4f);
            useRect.anchoredPosition = Vector2.zero;

            return (dropdown, useButton);
        }

        private static GameObject BuildAndSaveCombatMonsterRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiCombatMonsterRow", typeof(RectTransform));
            RectTransform rootRect = (RectTransform)root.transform;
            rootRect.sizeDelta = new Vector2(0f, 64f);

            Image background = root.AddComponent<Image>();
            background.color = new Color(0.17f, 0.17f, 0.22f, 1f);
            Button selectButton = root.AddComponent<Button>();
            selectButton.targetGraphic = background;

            GameObject highlightObject = new GameObject("SelectedHighlight", typeof(RectTransform));
            highlightObject.transform.SetParent(rootRect, false);
            StretchFull((RectTransform)highlightObject.transform);
            Image highlightImage = highlightObject.AddComponent<Image>();
            highlightImage.color = new Color(0.95f, 0.78f, 0.35f, 0.30f);
            highlightImage.raycastTarget = false;
            highlightObject.SetActive(false);

            Image icon = new GameObject("Icon", typeof(RectTransform)).AddComponent<Image>();
            icon.transform.SetParent(rootRect, false);
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            RectTransform iconRect = (RectTransform)icon.transform;
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.sizeDelta = new Vector2(56f, 56f);
            iconRect.anchoredPosition = new Vector2(4f, 0f);

            TextMeshProUGUI nameText = CreateText(rootRect, "NameText", "Monster", 16f, TextAlignmentOptions.BottomLeft);
            nameText.raycastTarget = false;
            RectTransform nameRect = (RectTransform)nameText.transform;
            nameRect.anchorMin = new Vector2(0f, 0.5f);
            nameRect.anchorMax = new Vector2(1f, 1f);
            nameRect.offsetMin = new Vector2(66f, 0f);
            nameRect.offsetMax = new Vector2(-110f, -6f);

            TextMeshProUGUI statsText = CreateText(rootRect, "StatsText", string.Empty, 13f, TextAlignmentOptions.TopLeft);
            statsText.color = new Color(1f, 1f, 1f, 0.7f);
            statsText.raycastTarget = false;
            RectTransform statsRect = (RectTransform)statsText.transform;
            statsRect.anchorMin = new Vector2(0f, 0f);
            statsRect.anchorMax = new Vector2(1f, 0.5f);
            statsRect.offsetMin = new Vector2(66f, 6f);
            statsRect.offsetMax = new Vector2(-110f, 0f);

            TextMeshProUGUI killsText = CreateText(rootRect, "KillsText", string.Empty, 12f, TextAlignmentOptions.MidlineRight);
            killsText.raycastTarget = false;
            RectTransform killsRect = (RectTransform)killsText.transform;
            killsRect.anchorMin = new Vector2(1f, 0f);
            killsRect.anchorMax = new Vector2(1f, 1f);
            killsRect.pivot = new Vector2(1f, 0.5f);
            killsRect.sizeDelta = new Vector2(104f, 0f);
            killsRect.anchoredPosition = new Vector2(-6f, 0f);

            Image bossBadge = new GameObject("BossBadge", typeof(RectTransform)).AddComponent<Image>();
            bossBadge.transform.SetParent(rootRect, false);
            bossBadge.color = new Color(0.85f, 0.25f, 0.22f, 1f);
            bossBadge.raycastTarget = false;
            RectTransform bossBadgeRect = (RectTransform)bossBadge.transform;
            bossBadgeRect.anchorMin = new Vector2(0f, 0f);
            bossBadgeRect.anchorMax = new Vector2(0f, 1f);
            bossBadgeRect.pivot = new Vector2(0f, 0.5f);
            bossBadgeRect.sizeDelta = new Vector2(4f, 0f);
            bossBadgeRect.anchoredPosition = Vector2.zero;

            UiCombatMonsterRow rowComponent = root.AddComponent<UiCombatMonsterRow>();
            rowComponent.SelectButton = selectButton;
            rowComponent.IconImage = icon;
            rowComponent.SelectedHighlight = highlightImage;
            rowComponent.BossBadge = bossBadge;
            rowComponent.NameText = nameText;
            rowComponent.StatsText = statsText;
            rowComponent.KillsText = killsText;

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, CombatMonsterRowPrefabPath, out bool success);
            if (!success)
            {
                Debug.LogError("MainSceneBuilder: failed to save UiCombatMonsterRow prefab asset.");
            }
            Object.DestroyImmediate(root);
            return prefabAsset;
        }

        // Modul: hand-built TMP_Dropdown hierarchy (Label + Template >
        // Viewport > Content > Item [Background/Checkmark/Label]) mirroring
        // Unity's own default TMP_Dropdown prefab structure, since no
        // prefab asset exists to instantiate from in this "zero visual
        // asset creation" build. Template starts inactive - TMP_Dropdown
        // instantiates a clone of it into a runtime popup on Show().
        private static TMP_Dropdown CreateTmpDropdown(Transform parent, string objectName)
        {
            GameObject dropdownObject = new GameObject(objectName, typeof(RectTransform));
            dropdownObject.transform.SetParent(parent, false);
            Image dropdownBackground = dropdownObject.AddComponent<Image>();
            dropdownBackground.color = Color.white;
            TMP_Dropdown dropdown = dropdownObject.AddComponent<TMP_Dropdown>();

            TextMeshProUGUI labelText = CreateText(dropdownObject.transform, "Label", "Select Monster", 14f, TextAlignmentOptions.MidlineLeft);
            labelText.color = Color.black;
            RectTransform labelRect = (RectTransform)labelText.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(10f, 2f);
            labelRect.offsetMax = new Vector2(-10f, -2f);

            GameObject templateObject = new GameObject("Template", typeof(RectTransform));
            templateObject.transform.SetParent(dropdownObject.transform, false);
            RectTransform templateRect = (RectTransform)templateObject.transform;
            templateRect.anchorMin = new Vector2(0f, 0f);
            templateRect.anchorMax = new Vector2(1f, 0f);
            templateRect.pivot = new Vector2(0.5f, 1f);
            templateRect.anchoredPosition = new Vector2(0f, 2f);
            templateRect.sizeDelta = new Vector2(0f, 150f);
            templateObject.AddComponent<Image>().color = Color.white;
            ScrollRect templateScrollRect = templateObject.AddComponent<ScrollRect>();
            templateScrollRect.horizontal = false;
            templateScrollRect.vertical = true;
            templateScrollRect.movementType = ScrollRect.MovementType.Clamped;

            GameObject viewportObject = new GameObject("Viewport", typeof(RectTransform));
            viewportObject.transform.SetParent(templateRect, false);
            RectTransform viewportRect = (RectTransform)viewportObject.transform;
            StretchFull(viewportRect);
            // Modul: layout-trap sweep. This was an Image tinted Color.clear
            // plus a stencil Mask - the exact combination that made every
            // ScrollView in the game render as nothing (see
            // ChatSceneBuilder.BuildScrollView, fixed the same way). A Mask
            // whose own graphic is transparent makes Unity compile
            // UNITY_UI_ALPHACLIP, and the alpha test then discards every
            // fragment, so nothing is ever written to the stencil buffer and
            // the entire masked subtree is clipped away. showMaskGraphic =
            // false does not help: it suppresses the colour write, not the
            // alpha clip.
            //
            // The visible symptom was that opening ANY dropdown in the game -
            // the Combat screen's food and potion pickers, the Larder's three
            // food slots, every filter control - showed an empty list. The
            // options were there and selectable by keyboard; they just did not
            // draw.
            //
            // RectMask2D clips by rectangle, needs no graphic at all, and
            // allocates no per-mask material variant. A dropdown list is a
            // plain rectangle, so nothing is lost.
            viewportObject.AddComponent<RectMask2D>();

            GameObject contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewportRect, false);
            RectTransform contentRect = (RectTransform)contentObject.transform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 28f);
            contentRect.anchoredPosition = Vector2.zero;

            VerticalLayoutGroup contentLayout = contentObject.AddComponent<VerticalLayoutGroup>();
            contentLayout.childControlWidth = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childControlHeight = false;
            contentLayout.childForceExpandHeight = false;
            ContentSizeFitter contentSizeFitter = contentObject.AddComponent<ContentSizeFitter>();
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject itemObject = new GameObject("Item", typeof(RectTransform));
            itemObject.transform.SetParent(contentRect, false);
            RectTransform itemRect = (RectTransform)itemObject.transform;
            itemRect.anchorMin = new Vector2(0f, 0.5f);
            itemRect.anchorMax = new Vector2(1f, 0.5f);
            itemRect.sizeDelta = new Vector2(0f, 28f);
            itemRect.anchoredPosition = Vector2.zero;

            Toggle itemToggle = itemObject.AddComponent<Toggle>();

            GameObject itemBackgroundObject = new GameObject("Item Background", typeof(RectTransform));
            itemBackgroundObject.transform.SetParent(itemRect, false);
            StretchFull((RectTransform)itemBackgroundObject.transform);
            Image itemBackgroundImage = itemBackgroundObject.AddComponent<Image>();
            itemBackgroundImage.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            itemToggle.targetGraphic = itemBackgroundImage;

            GameObject itemCheckmarkObject = new GameObject("Item Checkmark", typeof(RectTransform));
            itemCheckmarkObject.transform.SetParent(itemRect, false);
            RectTransform itemCheckmarkRect = (RectTransform)itemCheckmarkObject.transform;
            itemCheckmarkRect.anchorMin = new Vector2(0f, 0.5f);
            itemCheckmarkRect.anchorMax = new Vector2(0f, 0.5f);
            itemCheckmarkRect.sizeDelta = new Vector2(16f, 16f);
            itemCheckmarkRect.anchoredPosition = new Vector2(12f, 0f);
            Image itemCheckmarkImage = itemCheckmarkObject.AddComponent<Image>();
            itemCheckmarkImage.color = new Color(0.2f, 0.5f, 0.9f, 1f);
            itemToggle.graphic = itemCheckmarkImage;

            TextMeshProUGUI itemLabel = CreateText(itemRect, "Item Label", "Option", 13f, TextAlignmentOptions.MidlineLeft);
            itemLabel.color = Color.black;
            RectTransform itemLabelRect = (RectTransform)itemLabel.transform;
            itemLabelRect.anchorMin = Vector2.zero;
            itemLabelRect.anchorMax = Vector2.one;
            itemLabelRect.offsetMin = new Vector2(28f, 1f);
            itemLabelRect.offsetMax = new Vector2(-10f, -2f);

            templateScrollRect.viewport = viewportRect;
            templateScrollRect.content = contentRect;

            dropdown.captionText = labelText;
            dropdown.template = templateRect;
            dropdown.itemText = itemLabel;

            templateObject.SetActive(false);

            return dropdown;
        }

        // ------------------------------------------------------------
        // Boss World panel (map hub "Boss" zone) - a full-size real HP/
        // attack display mirroring BuildWorldBossOverlay's HUD-corner
        // pattern at a larger scale, plus the real global leaderboard
        // reused via BuildLeaderboardListInto ("Top Players" - see that
        // method's header comment on why it is not labeled as a
        // boss-specific damage ranking).
        // ------------------------------------------------------------
        private static GameObject BuildBossWorldPanel(Transform canvasTransform, VisualSyncProxy syncProxy, SfxPoolEngine sfxEngine, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("BossWorldPanel", canvasTransform, "World Boss", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            // Modul: UI audit follow-up. Previously reserved 38% of the
            // window's height (anchorMin.y = 0.62) but only ever populated
            // its top ~74px (HP bar + text) and bottom ~50px (Runs text +
            // Attack button, bottom-anchored) - leaving a large blank gap
            // in the middle on any real portrait canvas. Fixed-pixel height
            // sized to what the content actually needs (matches
            // BuildSimpleListWindowShell's own fixed-pixel-inset
            // convention), with Runs/Attack moved to sit directly below the
            // HP text instead of anchored to the bottom of an oversized
            // section.
            GameObject hpSectionObject = new GameObject("BossHpSection", typeof(RectTransform));
            hpSectionObject.transform.SetParent(contentAreaRect, false);
            RectTransform hpSectionRect = (RectTransform)hpSectionObject.transform;
            hpSectionRect.anchorMin = new Vector2(0f, 1f);
            hpSectionRect.anchorMax = new Vector2(1f, 1f);
            hpSectionRect.pivot = new Vector2(0.5f, 1f);
            hpSectionRect.sizeDelta = new Vector2(0f, 236f);
            hpSectionRect.anchoredPosition = Vector2.zero;
            hpSectionObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);

            // Modul: UI rework. The section used to open straight onto an
            // unlabelled red bar reading "0 / 0" with an Attack button that
            // was silently non-interactable for roughly 23 days a month
            // (the boss event only runs in a scheduled window) and no text
            // anywhere saying so. Name, live event state and an explanation
            // of the run budget added, all from fields the state packet
            // already carries.
            TextMeshProUGUI bossNameText = CreateText(hpSectionRect, "BossNameText", "The World Boss", 22f, TextAlignmentOptions.Center);
            RectTransform bossNameRect = (RectTransform)bossNameText.transform;
            bossNameRect.anchorMin = new Vector2(0f, 1f);
            bossNameRect.anchorMax = new Vector2(1f, 1f);
            bossNameRect.pivot = new Vector2(0.5f, 1f);
            bossNameRect.sizeDelta = new Vector2(0f, 30f);
            bossNameRect.anchoredPosition = new Vector2(0f, -8f);

            TextMeshProUGUI eventStateText = CreateText(hpSectionRect, "EventStateText", string.Empty, 14f, TextAlignmentOptions.Center);
            RectTransform eventStateRect = (RectTransform)eventStateText.transform;
            eventStateRect.anchorMin = new Vector2(0f, 1f);
            eventStateRect.anchorMax = new Vector2(1f, 1f);
            eventStateRect.pivot = new Vector2(0.5f, 1f);
            eventStateRect.sizeDelta = new Vector2(0f, 22f);
            eventStateRect.anchoredPosition = new Vector2(0f, -40f);

            (GameObject hpBackground, RectTransform hpFill) = BuildAnchoredProgressBar(hpSectionRect, new Color(0.8f, 0.1f, 0.1f, 1f));
            RectTransform hpBgRect = (RectTransform)hpBackground.transform;
            hpBgRect.anchorMin = new Vector2(0f, 1f);
            hpBgRect.anchorMax = new Vector2(1f, 1f);
            hpBgRect.pivot = new Vector2(0.5f, 1f);
            hpBgRect.sizeDelta = new Vector2(-20f, 28f);
            hpBgRect.anchoredPosition = new Vector2(0f, -74f);

            TextMeshProUGUI hpText = CreateText(hpSectionRect, "BossHpText", "0 / 0", 16f, TextAlignmentOptions.Center);
            RectTransform hpTextRect = (RectTransform)hpText.transform;
            hpTextRect.anchorMin = new Vector2(0f, 1f);
            hpTextRect.anchorMax = new Vector2(1f, 1f);
            hpTextRect.pivot = new Vector2(0.5f, 1f);
            hpTextRect.sizeDelta = new Vector2(0f, 24f);
            hpTextRect.anchoredPosition = new Vector2(0f, -108f);

            TextMeshProUGUI runsText = CreateText(hpSectionRect, "WorldBossRunsText", "Runs: 0", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform runsTextRect = (RectTransform)runsText.transform;
            runsTextRect.anchorMin = new Vector2(0f, 1f);
            runsTextRect.anchorMax = new Vector2(0.5f, 1f);
            runsTextRect.pivot = new Vector2(0f, 1f);
            runsTextRect.sizeDelta = new Vector2(0f, 40f);
            runsTextRect.anchoredPosition = new Vector2(14f, -140f);

            Button attackButton = CreateButton(hpSectionRect, "WorldBossAttackButton", "Attack", out TextMeshProUGUI _);
            ((Image)attackButton.targetGraphic).color = new Color(0.62f, 0.24f, 0.20f, 1f);
            RectTransform attackRect = (RectTransform)attackButton.transform;
            attackRect.anchorMin = new Vector2(0.5f, 1f);
            attackRect.anchorMax = new Vector2(1f, 1f);
            attackRect.pivot = new Vector2(1f, 1f);
            attackRect.sizeDelta = new Vector2(-14f, 44f);
            attackRect.anchoredPosition = new Vector2(-14f, -138f);

            TextMeshProUGUI bossHelpText = CreateText(hpSectionRect, "BossHelpText", "Everyone on the server fights the same boss. You get 3 attacks per event window; damage is ranked below.", 12f, TextAlignmentOptions.TopLeft);
            bossHelpText.color = new Color(1f, 1f, 1f, 0.55f);
            RectTransform bossHelpRect = (RectTransform)bossHelpText.transform;
            bossHelpRect.anchorMin = new Vector2(0f, 1f);
            bossHelpRect.anchorMax = new Vector2(1f, 1f);
            bossHelpRect.pivot = new Vector2(0.5f, 1f);
            bossHelpRect.sizeDelta = new Vector2(-28f, 40f);
            bossHelpRect.anchoredPosition = new Vector2(0f, -190f);

            UiCommandDispatcher dispatcher = hpSectionObject.AddComponent<UiCommandDispatcher>();
            dispatcher.NetworkClient = networkClient;
            UnityEditor.Events.UnityEventTools.AddPersistentListener(attackButton.onClick, dispatcher.DispatchAttackWorldBoss);

            UiWorldBossDataBinder binder = hpSectionObject.AddComponent<UiWorldBossDataBinder>();
            binder.SyncProxy = syncProxy;
            binder.WorldBossPanelRect = hpSectionRect;
            binder.BossHpFillRect = hpFill;
            binder.BossHpText = hpText;
            binder.WorldBossRunsText = runsText;
            binder.WorldBossAttackButton = attackButton;
            binder.EventStateText = eventStateText;
            binder.SoundEngine = sfxEngine;

            // Modul: UI audit follow-up. Previously capped at anchorMax.y =
            // 0.6, leaving everything between it and the (also oversized)
            // HP section above empty. Now fills all remaining space below
            // the HP section's fixed 150px, rather than an arbitrary
            // fraction of the window.
            GameObject leaderboardSectionObject = new GameObject("TopPlayersSection", typeof(RectTransform));
            leaderboardSectionObject.transform.SetParent(contentAreaRect, false);
            RectTransform leaderboardSectionRect = (RectTransform)leaderboardSectionObject.transform;
            leaderboardSectionRect.anchorMin = Vector2.zero;
            leaderboardSectionRect.anchorMax = Vector2.one;
            leaderboardSectionRect.offsetMin = Vector2.zero;
            // Kept in step with hpSectionRect's height above (236f) - these
            // two are a matched pair and drifting them apart overlaps the
            // Top Players header onto the Attack row.
            leaderboardSectionRect.offsetMax = new Vector2(0f, -244f);

            TextMeshProUGUI leaderboardTitleText = CreateText(leaderboardSectionRect, "TopPlayersTitleText", "Top Players", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform leaderboardTitleRect = (RectTransform)leaderboardTitleText.transform;
            leaderboardTitleRect.anchorMin = new Vector2(0f, 1f);
            leaderboardTitleRect.anchorMax = new Vector2(1f, 1f);
            leaderboardTitleRect.pivot = new Vector2(0.5f, 1f);
            leaderboardTitleRect.sizeDelta = new Vector2(0f, 26f);
            leaderboardTitleRect.anchoredPosition = Vector2.zero;

            GameObject leaderboardContentAreaObject = new GameObject("ContentArea", typeof(RectTransform));
            leaderboardContentAreaObject.transform.SetParent(leaderboardSectionRect, false);
            RectTransform leaderboardContentAreaRect = (RectTransform)leaderboardContentAreaObject.transform;
            leaderboardContentAreaRect.anchorMin = Vector2.zero;
            leaderboardContentAreaRect.anchorMax = Vector2.one;
            leaderboardContentAreaRect.offsetMin = Vector2.zero;
            leaderboardContentAreaRect.offsetMax = new Vector2(0f, -30f);

            BuildLeaderboardListInto(leaderboardSectionObject.transform, leaderboardContentAreaRect);

            return windowObject;
        }

        // ------------------------------------------------------------
        // Settings - currently just a Profile section with the one real,
        // load-bearing action this pass adds: Log Off (see
        // UiLoginWindow.LogOff - forgets the remembered device and returns
        // to the Login/Register Choice screen without restarting the app).
        // The returned Button is wired to LogOff itself as a post-pass
        // persistent listener back in BuildMainScene, once UiLoginWindow
        // actually exists (it is deliberately built last - see its own
        // comment there).
        // ------------------------------------------------------------
        private static (GameObject panel, Button logOffButton) BuildSettingsWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("SettingsPanel", canvasTransform, "Settings", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI profileHeaderText = CreateText(contentAreaRect, "ProfileHeaderText", "Profile", 18f, TextAlignmentOptions.MidlineLeft);
            RectTransform profileHeaderRect = (RectTransform)profileHeaderText.transform;
            profileHeaderRect.anchorMin = new Vector2(0f, 1f);
            profileHeaderRect.anchorMax = new Vector2(1f, 1f);
            profileHeaderRect.pivot = new Vector2(0.5f, 1f);
            profileHeaderRect.sizeDelta = new Vector2(0f, 30f);
            profileHeaderRect.anchoredPosition = Vector2.zero;

            Button logOffButton = CreateButton(contentAreaRect, "LogOffButton", "Log Off", out TextMeshProUGUI _);
            RectTransform logOffRect = (RectTransform)logOffButton.transform;
            logOffRect.anchorMin = new Vector2(0f, 1f);
            logOffRect.anchorMax = new Vector2(1f, 1f);
            logOffRect.pivot = new Vector2(0.5f, 1f);
            logOffRect.sizeDelta = new Vector2(0f, 50f);
            logOffRect.anchoredPosition = new Vector2(0f, -44f);

            TextMeshProUGUI languageHeaderText = CreateText(contentAreaRect, "LanguageHeaderText", "Language", 18f, TextAlignmentOptions.MidlineLeft);
            RectTransform languageHeaderRect = (RectTransform)languageHeaderText.transform;
            languageHeaderRect.anchorMin = new Vector2(0f, 1f);
            languageHeaderRect.anchorMax = new Vector2(1f, 1f);
            languageHeaderRect.pivot = new Vector2(0.5f, 1f);
            languageHeaderRect.sizeDelta = new Vector2(0f, 30f);
            languageHeaderRect.anchoredPosition = new Vector2(0f, -104f);

            (Button englishButton, GameObject englishHighlight) = BuildLanguageOptionRow(contentAreaRect, "English", -138f);
            (Button czechButton, GameObject czechHighlight) = BuildLanguageOptionRow(contentAreaRect, "Czech", -182f);
            (Button germanButton, GameObject germanHighlight) = BuildLanguageOptionRow(contentAreaRect, "German", -226f);
            (Button polishButton, GameObject polishHighlight) = BuildLanguageOptionRow(contentAreaRect, "Polish", -270f);

            UiLanguagePickerPanel languagePicker = windowObject.AddComponent<UiLanguagePickerPanel>();
            languagePicker.SyncProxy = syncProxy;
            languagePicker.NetworkClient = networkClient;
            languagePicker.EnglishButton = englishButton;
            languagePicker.CzechButton = czechButton;
            languagePicker.GermanButton = germanButton;
            languagePicker.PolishButton = polishButton;
            languagePicker.EnglishActiveHighlight = englishHighlight;
            languagePicker.CzechActiveHighlight = czechHighlight;
            languagePicker.GermanActiveHighlight = germanHighlight;
            languagePicker.PolishActiveHighlight = polishHighlight;

            TextMeshProUGUI autoEatHeaderText = CreateText(contentAreaRect, "AutoEatHeaderText", "Auto-Eat Threshold", 18f, TextAlignmentOptions.MidlineLeft);
            RectTransform autoEatHeaderRect = (RectTransform)autoEatHeaderText.transform;
            autoEatHeaderRect.anchorMin = new Vector2(0f, 1f);
            autoEatHeaderRect.anchorMax = new Vector2(1f, 1f);
            autoEatHeaderRect.pivot = new Vector2(0.5f, 1f);
            autoEatHeaderRect.sizeDelta = new Vector2(0f, 30f);
            autoEatHeaderRect.anchoredPosition = new Vector2(0f, -314f);

            TextMeshProUGUI autoEatThresholdText = CreateText(contentAreaRect, "AutoEatThresholdText", "Auto-Eat: 0%", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform autoEatThresholdRect = (RectTransform)autoEatThresholdText.transform;
            autoEatThresholdRect.anchorMin = new Vector2(0f, 1f);
            autoEatThresholdRect.anchorMax = new Vector2(0.6f, 1f);
            autoEatThresholdRect.pivot = new Vector2(0.5f, 1f);
            autoEatThresholdRect.sizeDelta = new Vector2(0f, 40f);
            autoEatThresholdRect.anchoredPosition = new Vector2(0f, -348f);

            Button autoEatDecreaseButton = CreateButton(contentAreaRect, "AutoEatDecreaseButton", "-10%", out TextMeshProUGUI _);
            RectTransform autoEatDecreaseRect = (RectTransform)autoEatDecreaseButton.transform;
            autoEatDecreaseRect.anchorMin = new Vector2(0.62f, 1f);
            autoEatDecreaseRect.anchorMax = new Vector2(0.8f, 1f);
            autoEatDecreaseRect.pivot = new Vector2(0.5f, 1f);
            autoEatDecreaseRect.sizeDelta = new Vector2(0f, 40f);
            autoEatDecreaseRect.anchoredPosition = new Vector2(0f, -348f);

            Button autoEatIncreaseButton = CreateButton(contentAreaRect, "AutoEatIncreaseButton", "+10%", out TextMeshProUGUI _);
            RectTransform autoEatIncreaseRect = (RectTransform)autoEatIncreaseButton.transform;
            autoEatIncreaseRect.anchorMin = new Vector2(0.82f, 1f);
            autoEatIncreaseRect.anchorMax = new Vector2(1f, 1f);
            autoEatIncreaseRect.pivot = new Vector2(0.5f, 1f);
            autoEatIncreaseRect.sizeDelta = new Vector2(0f, 40f);
            autoEatIncreaseRect.anchoredPosition = new Vector2(0f, -348f);

            UiAutoEatThresholdPanel autoEatPanel = windowObject.AddComponent<UiAutoEatThresholdPanel>();
            autoEatPanel.SyncProxy = syncProxy;
            autoEatPanel.NetworkClient = networkClient;
            autoEatPanel.ThresholdText = autoEatThresholdText;
            autoEatPanel.DecreaseButton = autoEatDecreaseButton;
            autoEatPanel.IncreaseButton = autoEatIncreaseButton;

            return (windowObject, logOffButton);
        }

        private static (Button button, GameObject highlight) BuildLanguageOptionRow(RectTransform contentAreaRect, string label, float anchoredY)
        {
            Button button = CreateButton(contentAreaRect, label + "LanguageButton", label, out TextMeshProUGUI _);
            RectTransform buttonRect = (RectTransform)button.transform;
            buttonRect.anchorMin = new Vector2(0f, 1f);
            buttonRect.anchorMax = new Vector2(1f, 1f);
            buttonRect.pivot = new Vector2(0.5f, 1f);
            buttonRect.sizeDelta = new Vector2(0f, 40f);
            buttonRect.anchoredPosition = new Vector2(0f, anchoredY);

            GameObject highlight = new GameObject(label + "ActiveHighlight", typeof(RectTransform), typeof(Image));
            highlight.transform.SetParent(button.transform, false);
            RectTransform highlightRect = (RectTransform)highlight.transform;
            highlightRect.anchorMin = Vector2.zero;
            highlightRect.anchorMax = new Vector2(0.05f, 1f);
            highlightRect.offsetMin = Vector2.zero;
            highlightRect.offsetMax = Vector2.zero;
            Image highlightImage = highlight.GetComponent<Image>();
            highlightImage.color = new Color(0.35f, 0.75f, 0.35f, 1f);
            highlight.SetActive(false);

            return (button, highlight);
        }

        // Modul: Play Mode audit fix. Banked Chrono Seconds (offline-time
        // catch-up currency, see UiChronoBankPanel's own comment) had every
        // sync field and both spend-command senders wired end to end with
        // no panel anywhere ever calling them - the game's own core idle
        // acceleration mechanic was completely unreachable. Same
        // BuildSimpleListWindowShell shell as Settings/Achievements.
        private static GameObject BuildChronoBankWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("ChronoBankWindow", canvasTransform, "Time Bank", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI bankedText = CreateText(contentAreaRect, "BankedSecondsText", "Banked: 0h 0m", 20f, TextAlignmentOptions.MidlineLeft);
            RectTransform bankedRect = (RectTransform)bankedText.transform;
            bankedRect.anchorMin = new Vector2(0f, 1f);
            bankedRect.anchorMax = new Vector2(1f, 1f);
            bankedRect.pivot = new Vector2(0.5f, 1f);
            bankedRect.sizeDelta = new Vector2(0f, 32f);
            bankedRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI statusText = CreateText(contentAreaRect, "StatusText", "Idle", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = new Vector2(0f, 26f);
            statusRect.anchoredPosition = new Vector2(0f, -36f);

            Button boost2xButton = CreateButton(contentAreaRect, "Boost2xButton", "Boost 2x", out TextMeshProUGUI _);
            RectTransform boost2xRect = (RectTransform)boost2xButton.transform;
            boost2xRect.anchorMin = new Vector2(0f, 1f);
            boost2xRect.anchorMax = new Vector2(1f, 1f);
            boost2xRect.pivot = new Vector2(0.5f, 1f);
            boost2xRect.sizeDelta = new Vector2(0f, 50f);
            boost2xRect.anchoredPosition = new Vector2(0f, -76f);

            Button boost4xButton = CreateButton(contentAreaRect, "Boost4xButton", "Boost 4x", out TextMeshProUGUI _);
            RectTransform boost4xRect = (RectTransform)boost4xButton.transform;
            boost4xRect.anchorMin = new Vector2(0f, 1f);
            boost4xRect.anchorMax = new Vector2(1f, 1f);
            boost4xRect.pivot = new Vector2(0.5f, 1f);
            boost4xRect.sizeDelta = new Vector2(0f, 50f);
            boost4xRect.anchoredPosition = new Vector2(0f, -134f);

            Button instantWarpButton = CreateButton(contentAreaRect, "InstantWarpButton", "Instant Warp (1 day)", out TextMeshProUGUI _);
            RectTransform instantWarpRect = (RectTransform)instantWarpButton.transform;
            instantWarpRect.anchorMin = new Vector2(0f, 1f);
            instantWarpRect.anchorMax = new Vector2(1f, 1f);
            instantWarpRect.pivot = new Vector2(0.5f, 1f);
            instantWarpRect.sizeDelta = new Vector2(0f, 50f);
            instantWarpRect.anchoredPosition = new Vector2(0f, -192f);

            UiChronoBankPanel panel = windowObject.AddComponent<UiChronoBankPanel>();
            panel.SyncProxy = syncProxy;
            panel.NetworkClient = networkClient;
            panel.BankedSecondsText = bankedText;
            panel.StatusText = statusText;
            panel.Boost2xButton = boost2xButton;
            panel.Boost4xButton = boost4xButton;
            panel.InstantWarpButton = instantWarpButton;

            return windowObject;
        }

        // Modul: Play Mode audit fix. Legacy Shop's 3 prestige perks - see
        // UiLegacyShopPanel's own header comment for why
        // LegacyStoreEngine.PurchaseLegacyUnlockAsync had a working sender
        // with no purchasable UI. Same BuildSimpleListWindowShell shell as
        // Settings/Achievements/Time Bank.
        private static GameObject BuildLegacyShopWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("LegacyShopWindow", canvasTransform, "Legacy Shop", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI shardBalanceText = CreateText(contentAreaRect, "ShardBalanceText", "Legacy Shards: 0", 20f, TextAlignmentOptions.MidlineLeft);
            RectTransform shardBalanceRect = (RectTransform)shardBalanceText.transform;
            shardBalanceRect.anchorMin = new Vector2(0f, 1f);
            shardBalanceRect.anchorMax = new Vector2(1f, 1f);
            shardBalanceRect.pivot = new Vector2(0.5f, 1f);
            shardBalanceRect.sizeDelta = new Vector2(0f, 32f);
            shardBalanceRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI xpRankText = CreateText(contentAreaRect, "XpMultiplierRankText", "Rank 0 (+0%)", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform xpRankRect = (RectTransform)xpRankText.transform;
            xpRankRect.anchorMin = new Vector2(0f, 1f);
            xpRankRect.anchorMax = new Vector2(1f, 1f);
            xpRankRect.pivot = new Vector2(0.5f, 1f);
            xpRankRect.sizeDelta = new Vector2(0f, 24f);
            xpRankRect.anchoredPosition = new Vector2(0f, -40f);

            Button purchaseXpButton = CreateButton(contentAreaRect, "PurchaseXpMultiplierButton", "Purchase XP Multiplier", out TextMeshProUGUI _);
            RectTransform purchaseXpRect = (RectTransform)purchaseXpButton.transform;
            purchaseXpRect.anchorMin = new Vector2(0f, 1f);
            purchaseXpRect.anchorMax = new Vector2(1f, 1f);
            purchaseXpRect.pivot = new Vector2(0.5f, 1f);
            purchaseXpRect.sizeDelta = new Vector2(0f, 44f);
            purchaseXpRect.anchoredPosition = new Vector2(0f, -68f);

            TextMeshProUGUI goldRankText = CreateText(contentAreaRect, "GoldDropRateRankText", "Rank 0 (+0%)", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform goldRankRect = (RectTransform)goldRankText.transform;
            goldRankRect.anchorMin = new Vector2(0f, 1f);
            goldRankRect.anchorMax = new Vector2(1f, 1f);
            goldRankRect.pivot = new Vector2(0.5f, 1f);
            goldRankRect.sizeDelta = new Vector2(0f, 24f);
            goldRankRect.anchoredPosition = new Vector2(0f, -122f);

            Button purchaseGoldButton = CreateButton(contentAreaRect, "PurchaseGoldDropRateButton", "Purchase Gold Drop Rate", out TextMeshProUGUI _);
            RectTransform purchaseGoldRect = (RectTransform)purchaseGoldButton.transform;
            purchaseGoldRect.anchorMin = new Vector2(0f, 1f);
            purchaseGoldRect.anchorMax = new Vector2(1f, 1f);
            purchaseGoldRect.pivot = new Vector2(0.5f, 1f);
            purchaseGoldRect.sizeDelta = new Vector2(0f, 44f);
            purchaseGoldRect.anchoredPosition = new Vector2(0f, -150f);

            TextMeshProUGUI combatSpeedRankText = CreateText(contentAreaRect, "CombatSpeedRankText", "Rank 0 (+0%)", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform combatSpeedRankRect = (RectTransform)combatSpeedRankText.transform;
            combatSpeedRankRect.anchorMin = new Vector2(0f, 1f);
            combatSpeedRankRect.anchorMax = new Vector2(1f, 1f);
            combatSpeedRankRect.pivot = new Vector2(0.5f, 1f);
            combatSpeedRankRect.sizeDelta = new Vector2(0f, 24f);
            combatSpeedRankRect.anchoredPosition = new Vector2(0f, -204f);

            Button purchaseCombatSpeedButton = CreateButton(contentAreaRect, "PurchaseCombatSpeedButton", "Purchase Combat Speed", out TextMeshProUGUI _);
            RectTransform purchaseCombatSpeedRect = (RectTransform)purchaseCombatSpeedButton.transform;
            purchaseCombatSpeedRect.anchorMin = new Vector2(0f, 1f);
            purchaseCombatSpeedRect.anchorMax = new Vector2(1f, 1f);
            purchaseCombatSpeedRect.pivot = new Vector2(0.5f, 1f);
            purchaseCombatSpeedRect.sizeDelta = new Vector2(0f, 44f);
            purchaseCombatSpeedRect.anchoredPosition = new Vector2(0f, -232f);

            TextMeshProUGUI citizenSlotsText = CreateText(contentAreaRect, "CitizenSlotsText", "Slots: 0/32", 16f, TextAlignmentOptions.MidlineLeft);
            RectTransform citizenSlotsRect = (RectTransform)citizenSlotsText.transform;
            citizenSlotsRect.anchorMin = new Vector2(0f, 1f);
            citizenSlotsRect.anchorMax = new Vector2(1f, 1f);
            citizenSlotsRect.pivot = new Vector2(0.5f, 1f);
            citizenSlotsRect.sizeDelta = new Vector2(0f, 24f);
            citizenSlotsRect.anchoredPosition = new Vector2(0f, -286f);

            Button purchaseCitizenSlotButton = CreateButton(contentAreaRect, "PurchaseCitizenSlotButton", "Unlock Citizen Slot", out TextMeshProUGUI _);
            RectTransform purchaseCitizenSlotRect = (RectTransform)purchaseCitizenSlotButton.transform;
            purchaseCitizenSlotRect.anchorMin = new Vector2(0f, 1f);
            purchaseCitizenSlotRect.anchorMax = new Vector2(1f, 1f);
            purchaseCitizenSlotRect.pivot = new Vector2(0.5f, 1f);
            purchaseCitizenSlotRect.sizeDelta = new Vector2(0f, 44f);
            purchaseCitizenSlotRect.anchoredPosition = new Vector2(0f, -314f);

            UiLegacyShopPanel panel = windowObject.AddComponent<UiLegacyShopPanel>();
            panel.SyncProxy = syncProxy;
            panel.NetworkClient = networkClient;
            panel.ShardBalanceText = shardBalanceText;
            panel.XpMultiplierRankText = xpRankText;
            panel.PurchaseXpMultiplierButton = purchaseXpButton;
            panel.GoldDropRateRankText = goldRankText;
            panel.PurchaseGoldDropRateButton = purchaseGoldButton;
            panel.CombatSpeedRankText = combatSpeedRankText;
            panel.PurchaseCombatSpeedButton = purchaseCombatSpeedButton;
            panel.CitizenSlotsText = citizenSlotsText;
            panel.PurchaseCitizenSlotButton = purchaseCitizenSlotButton;

            return windowObject;
        }

        // Modul: Play Mode audit fix. Mentorship contracts (real cross-
        // player XP-bonus relationships) - see UiMentorshipContractPanel's
        // own header comment for why EstablishMentorship/TerminateMentorship
        // had working senders with no caller anywhere. Player lookup reuses
        // the same username input + FriendsCache.RequestResolve pattern as
        // BuildFriendsWindow.
        private static GameObject BuildMentorshipContractWindow(Transform canvasTransform, VisualSyncProxy syncProxy, WebSocketClient networkClient)
        {
            GameObject windowObject = BuildSimpleListWindowShell("MentorshipContractWindow", canvasTransform, "Mentorship", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI statusText = CreateText(contentAreaRect, "StatusText", "No Mentor", 18f, TextAlignmentOptions.MidlineLeft);
            RectTransform statusRect = (RectTransform)statusText.transform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.sizeDelta = new Vector2(0f, 40f);
            statusRect.anchoredPosition = Vector2.zero;

            TMP_InputField mentorUsernameField = CreateInputField(contentAreaRect, "MentorUsernameField", "Mentor username");
            RectTransform mentorUsernameRect = (RectTransform)mentorUsernameField.transform;
            mentorUsernameRect.anchorMin = new Vector2(0f, 1f);
            mentorUsernameRect.anchorMax = new Vector2(1f, 1f);
            mentorUsernameRect.pivot = new Vector2(0.5f, 1f);
            mentorUsernameRect.sizeDelta = new Vector2(0f, 44f);
            mentorUsernameRect.anchoredPosition = new Vector2(0f, -52f);

            Button establishButton = CreateButton(contentAreaRect, "EstablishButton", "Establish Mentorship", out TextMeshProUGUI _);
            RectTransform establishRect = (RectTransform)establishButton.transform;
            establishRect.anchorMin = new Vector2(0f, 1f);
            establishRect.anchorMax = new Vector2(1f, 1f);
            establishRect.pivot = new Vector2(0.5f, 1f);
            establishRect.sizeDelta = new Vector2(0f, 44f);
            establishRect.anchoredPosition = new Vector2(0f, -104f);

            Button terminateButton = CreateButton(contentAreaRect, "TerminateButton", "End Mentorship", out TextMeshProUGUI _);
            RectTransform terminateRect = (RectTransform)terminateButton.transform;
            terminateRect.anchorMin = new Vector2(0f, 1f);
            terminateRect.anchorMax = new Vector2(1f, 1f);
            terminateRect.pivot = new Vector2(0.5f, 1f);
            terminateRect.sizeDelta = new Vector2(0f, 44f);
            terminateRect.anchoredPosition = new Vector2(0f, -156f);

            // Modul: Play Mode audit fix. Academy character-mentor-slot
            // assignment (AssignMentor/ExecuteAssignMentorAsync) - see
            // UiMentorshipContractPanel's own header comment. 5 slot
            // buttons match ValidateMentorshipAssignment's hard slotIndex
            // < 5 bound; only the first AcademyLevel of them are ever
            // interactable (gated live by RefreshDisplay).
            TextMeshProUGUI academyHeaderText = CreateText(contentAreaRect, "AcademyHeaderText", "Academy Assignment", 18f, TextAlignmentOptions.MidlineLeft);
            RectTransform academyHeaderRect = (RectTransform)academyHeaderText.transform;
            academyHeaderRect.anchorMin = new Vector2(0f, 1f);
            academyHeaderRect.anchorMax = new Vector2(1f, 1f);
            academyHeaderRect.pivot = new Vector2(0.5f, 1f);
            academyHeaderRect.sizeDelta = new Vector2(0f, 30f);
            academyHeaderRect.anchoredPosition = new Vector2(0f, -208f);

            GameObject slotRowObject = new GameObject("MentorSlotRow", typeof(RectTransform));
            slotRowObject.transform.SetParent(contentAreaRect, false);
            RectTransform slotRowRect = (RectTransform)slotRowObject.transform;
            slotRowRect.anchorMin = new Vector2(0f, 1f);
            slotRowRect.anchorMax = new Vector2(1f, 1f);
            slotRowRect.pivot = new Vector2(0.5f, 1f);
            slotRowRect.sizeDelta = new Vector2(0f, 40f);
            slotRowRect.anchoredPosition = new Vector2(0f, -242f);

            HorizontalLayoutGroup slotRowLayout = slotRowObject.AddComponent<HorizontalLayoutGroup>();
            slotRowLayout.spacing = 4f;
            slotRowLayout.childControlWidth = true;
            slotRowLayout.childForceExpandWidth = true;
            slotRowLayout.childControlHeight = true;
            slotRowLayout.childForceExpandHeight = true;

            const int mentorSlotCount = 5;
            Button[] slotButtons = new Button[mentorSlotCount];
            GameObject[] slotArmedIndicators = new GameObject[mentorSlotCount];
            for (int i = 0; i < mentorSlotCount; i++)
            {
                Button slotButton = CreateButton(slotRowRect, "MentorSlotButton_" + i, "Slot " + i, out TextMeshProUGUI _);
                slotButtons[i] = slotButton;

                GameObject armedIndicator = new GameObject("ArmedIndicator", typeof(RectTransform), typeof(Image));
                armedIndicator.transform.SetParent(slotButton.transform, false);
                RectTransform armedRect = (RectTransform)armedIndicator.transform;
                armedRect.anchorMin = Vector2.zero;
                armedRect.anchorMax = new Vector2(1f, 0.1f);
                armedRect.offsetMin = Vector2.zero;
                armedRect.offsetMax = Vector2.zero;
                armedIndicator.GetComponent<Image>().color = new Color(0.35f, 0.75f, 0.35f, 1f);
                armedIndicator.SetActive(false);
                slotArmedIndicators[i] = armedIndicator;
            }

            GameObject characterScrollAreaObject = new GameObject("CharacterScrollArea", typeof(RectTransform));
            characterScrollAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform characterScrollAreaRect = (RectTransform)characterScrollAreaObject.transform;
            characterScrollAreaRect.anchorMin = Vector2.zero;
            characterScrollAreaRect.anchorMax = Vector2.one;
            characterScrollAreaRect.offsetMin = Vector2.zero;
            characterScrollAreaRect.offsetMax = new Vector2(0f, -286f);

            (ScrollRect _, RectTransform characterContent) = ChatSceneBuilder.BuildScrollView(characterScrollAreaRect);

            GameObject characterRowPrefabAsset = BuildAndSaveBreedingRosterRowPrefab();

            UiMentorshipContractPanel panel = windowObject.AddComponent<UiMentorshipContractPanel>();
            panel.SyncProxy = syncProxy;
            panel.NetworkClient = networkClient;
            panel.StatusText = statusText;
            panel.MentorUsernameField = mentorUsernameField;
            panel.EstablishButton = establishButton;
            panel.TerminateButton = terminateButton;
            panel.SlotButtons = slotButtons;
            panel.SlotArmedIndicators = slotArmedIndicators;
            panel.CharacterRowContainer = characterContent;
            panel.CharacterRowPrefab = characterRowPrefabAsset.GetComponent<UiBreedingRosterRow>();

            return windowObject;
        }

        // ------------------------------------------------------------
        // Hamburger sliding menu - folds every screen not represented as
        // one of the 5 map zones. A full-height blocker behind the panel
        // both dims the rest of the screen and closes the menu on an
        // outside click.
        // ------------------------------------------------------------
        // ------------------------------------------------------------
        // Hamburger sliding menu.
        //
        // Modul: UI rework. This menu rendered as a completely empty dark
        // strip - the reported "there is nothing in the burger menu". The
        // cause was a layout mismatch, not missing entries: the buttons went
        // into ChatSceneBuilder.BuildScrollView's shared content object,
        // whose VerticalLayoutGroup is configured childControlHeight=false
        // (correct for the pooled prefab rows every other caller feeds it,
        // which carry their own baked sizeDelta). A GameObject created here
        // by CreateButton has a default RectTransform, i.e. height 0, and
        // with childControlHeight=false the LayoutElement.preferredHeight of
        // 50 set on each one was simply ignored - so all 17 entries stacked
        // up as zero-height slivers.
        //
        // Rebuilt with its own layout rather than the shared one, plus the
        // structure the menu actually needs to be usable at 17+ entries:
        // a titled header, section dividers, and the map destinations
        // themselves (previously reachable ONLY by returning to the map and
        // hunting for the right zone).
        // ------------------------------------------------------------
        private const float HamburgerPanelWidth = 400f;
        private const float HamburgerHeaderHeight = 56f;
        private const float HamburgerEntryHeight = 52f;
        private const float HamburgerSectionHeight = 30f;

        private static (GameObject blocker, UiHamburgerMenuPanel component, Dictionary<string, Button> menuButtons) BuildHamburgerPanel(
            Transform canvasTransform,
            (string header, string[] entries)[] sections)
        {
            GameObject blockerObject = new GameObject("HamburgerBlocker", typeof(RectTransform));
            blockerObject.transform.SetParent(canvasTransform, false);
            StretchFull((RectTransform)blockerObject.transform);
            Image blockerImage = blockerObject.AddComponent<Image>();
            blockerImage.color = new Color(0f, 0f, 0f, 0.6f);
            Button blockerButton = blockerObject.AddComponent<Button>();
            blockerButton.targetGraphic = blockerImage;

            GameObject panelObject = new GameObject("HamburgerPanel", typeof(RectTransform));
            panelObject.transform.SetParent(canvasTransform, false);
            RectTransform panelRect = (RectTransform)panelObject.transform;
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 0.5f);
            panelRect.sizeDelta = new Vector2(HamburgerPanelWidth, 0f);
            panelRect.anchoredPosition = Vector2.zero;
            panelObject.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.09f, 0.99f);

            // Header: title plus an explicit close button. The blocker
            // behind the panel already closes on click, but that is not
            // discoverable, and on a phone the panel covers the hamburger
            // toggle that opened it.
            GameObject headerObject = new GameObject("MenuHeader", typeof(RectTransform));
            headerObject.transform.SetParent(panelRect, false);
            RectTransform headerRect = (RectTransform)headerObject.transform;
            headerRect.anchorMin = new Vector2(0f, 1f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.pivot = new Vector2(0.5f, 1f);
            headerRect.sizeDelta = new Vector2(0f, HamburgerHeaderHeight);
            headerRect.anchoredPosition = Vector2.zero;
            headerObject.AddComponent<Image>().color = new Color(0.16f, 0.13f, 0.10f, 1f);

            TextMeshProUGUI menuTitle = CreateText(headerRect, "MenuTitle", "Menu", 22f, TextAlignmentOptions.MidlineLeft);
            RectTransform menuTitleRect = (RectTransform)menuTitle.transform;
            menuTitleRect.anchorMin = Vector2.zero;
            menuTitleRect.anchorMax = Vector2.one;
            menuTitleRect.offsetMin = new Vector2(18f, 0f);
            menuTitleRect.offsetMax = new Vector2(-60f, 0f);

            Button closeButton = CreateButton(headerRect, "CloseMenuButton", "X", out TextMeshProUGUI _);
            ((Image)closeButton.targetGraphic).color = new Color(0.35f, 0.18f, 0.18f, 1f);
            RectTransform closeRect = (RectTransform)closeButton.transform;
            closeRect.anchorMin = new Vector2(1f, 0.5f);
            closeRect.anchorMax = new Vector2(1f, 0.5f);
            closeRect.pivot = new Vector2(1f, 0.5f);
            closeRect.sizeDelta = new Vector2(44f, 40f);
            closeRect.anchoredPosition = new Vector2(-10f, 0f);

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(panelRect, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = new Vector2(0f, 0f);
            scrollAreaRect.offsetMax = new Vector2(0f, -HamburgerHeaderHeight);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(scrollAreaRect);
            StretchFull((RectTransform)content.parent.parent);

            // The fix for the empty menu: this menu builds its children live
            // rather than instantiating pre-sized prefab rows, so the layout
            // group has to be the thing that decides their height.
            VerticalLayoutGroup contentLayout = content.GetComponent<VerticalLayoutGroup>();
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.spacing = 4f;
            contentLayout.padding = new RectOffset(10, 10, 8, 24);

            Dictionary<string, Button> menuButtons = new Dictionary<string, Button>(32);

            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                (string header, string[] entries) = sections[sectionIndex];

                TextMeshProUGUI sectionLabel = CreateText(content, "Section_" + header, header.ToUpperInvariant(), 13f, TextAlignmentOptions.MidlineLeft);
                sectionLabel.color = new Color(0.85f, 0.72f, 0.45f, 1f);
                sectionLabel.characterSpacing = 6f;
                LayoutElement sectionLayout = sectionLabel.gameObject.AddComponent<LayoutElement>();
                sectionLayout.preferredHeight = HamburgerSectionHeight;
                sectionLayout.minHeight = HamburgerSectionHeight;

                for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
                {
                    string label = entries[entryIndex];

                    Button button = CreateButton(content, "MenuButton_" + label, label, out TextMeshProUGUI buttonLabel);
                    ((Image)button.targetGraphic).color = new Color(0.16f, 0.16f, 0.21f, 1f);
                    buttonLabel.alignment = TextAlignmentOptions.MidlineLeft;
                    buttonLabel.fontSize = 17f;
                    RectTransform buttonLabelRect = (RectTransform)buttonLabel.transform;
                    buttonLabelRect.offsetMin = new Vector2(16f, 0f);
                    buttonLabelRect.offsetMax = new Vector2(-10f, 0f);

                    LayoutElement buttonLayout = button.gameObject.AddComponent<LayoutElement>();
                    buttonLayout.preferredHeight = HamburgerEntryHeight;
                    buttonLayout.minHeight = HamburgerEntryHeight;

                    menuButtons[label] = button;
                }
            }

            UiHamburgerMenuPanel hamburgerComponent = panelObject.AddComponent<UiHamburgerMenuPanel>();
            hamburgerComponent.PanelRect = panelRect;
            hamburgerComponent.Blocker = blockerObject;
            hamburgerComponent.HiddenPositionX = -(HamburgerPanelWidth + 20f);
            hamburgerComponent.ShownPositionX = 0f;

            UnityEditor.Events.UnityEventTools.AddPersistentListener(blockerButton.onClick, hamburgerComponent.Close);
            UnityEditor.Events.UnityEventTools.AddPersistentListener(closeButton.onClick, hamburgerComponent.Close);

            return (blockerObject, hamburgerComponent, menuButtons);
        }

        // ------------------------------------------------------------
        // Persistent overlay bars - top-left Hamburger/Map buttons,
        // top-right real Gold/Gems currency, bottom Season Pass banner.
        // Stay visible across every screen (map hub, sub-panels, hamburger
        // windows alike) per the map-hub spec's UI persistence
        // requirement.
        // ------------------------------------------------------------
        private static (Image icon, TextMeshProUGUI text) CreateCurrencyRow(Transform parent, string placeholderText)
        {
            GameObject rowObject = new GameObject("CurrencyRow_" + placeholderText, typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);
            SetFixedLayoutHeight(rowObject, 22f);

            // Modul: UI rework. Same trap CreateStatRow already documents:
            // the parent VerticalLayoutGroup runs childControlHeight = false,
            // so preferredHeight above only feeds the group's spacing math
            // and never reaches the RectTransform. A GameObject created here
            // starts at height 0, so both currency rows collapsed to
            // nothing - the top-right panel rendered as an empty grey box
            // with the Gold and Gems labels spilling out of it at unrelated
            // screen positions. Set the real height to match.
            ((RectTransform)rowObject.transform).sizeDelta = new Vector2(0f, 22f);

            HorizontalLayoutGroup rowLayout = rowObject.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 4f;
            rowLayout.childControlWidth = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandHeight = true;

            GameObject iconObject = new GameObject("Icon", typeof(RectTransform));
            iconObject.transform.SetParent(rowObject.transform, false);
            Image icon = iconObject.AddComponent<Image>();
            icon.preserveAspect = true;
            icon.enabled = false;
            LayoutElement iconLayoutElement = iconObject.AddComponent<LayoutElement>();
            iconLayoutElement.preferredWidth = 20f;

            TextMeshProUGUI text = CreateText(rowObject.transform, "Stat_" + placeholderText, placeholderText, 16f, TextAlignmentOptions.MidlineLeft);

            return (icon, text);
        }

        private static (Button hamburgerToggleButton, Button homeButton, Button battlePassBannerButton) BuildPersistentBars(Transform canvasTransform, VisualSyncProxy syncProxy, AssetRegistry assetRegistry)
        {
            GameObject barRootObject = new GameObject("PersistentBars", typeof(RectTransform));
            barRootObject.transform.SetParent(canvasTransform, false);
            StretchFull((RectTransform)barRootObject.transform);

            GameObject topLeftRowObject = new GameObject("TopLeftControls", typeof(RectTransform));
            topLeftRowObject.transform.SetParent(barRootObject.transform, false);
            RectTransform topLeftRowRect = (RectTransform)topLeftRowObject.transform;
            topLeftRowRect.anchorMin = new Vector2(0f, 1f);
            topLeftRowRect.anchorMax = new Vector2(0f, 1f);
            topLeftRowRect.pivot = new Vector2(0f, 1f);
            topLeftRowRect.anchoredPosition = new Vector2(16f, -16f);
            topLeftRowRect.sizeDelta = new Vector2(220f, 46f);

            HorizontalLayoutGroup topLeftLayout = topLeftRowObject.AddComponent<HorizontalLayoutGroup>();
            topLeftLayout.spacing = 8f;
            topLeftLayout.childControlWidth = true;
            topLeftLayout.childForceExpandWidth = false;
            topLeftLayout.childControlHeight = true;
            topLeftLayout.childForceExpandHeight = true;

            Button hamburgerToggleButton = CreateButton(topLeftRowRect, "HamburgerToggleButton", "Menu", out TextMeshProUGUI _);
            LayoutElement hamburgerButtonLayout = hamburgerToggleButton.gameObject.AddComponent<LayoutElement>();
            hamburgerButtonLayout.preferredWidth = 100f;

            Button homeButton = CreateButton(topLeftRowRect, "HomeButton", "Map", out TextMeshProUGUI _);
            LayoutElement homeButtonLayout = homeButton.gameObject.AddComponent<LayoutElement>();
            homeButtonLayout.preferredWidth = 100f;

            GameObject currencyPanelObject = new GameObject("CurrencyDisplay", typeof(RectTransform));
            currencyPanelObject.transform.SetParent(barRootObject.transform, false);
            RectTransform currencyPanelRect = (RectTransform)currencyPanelObject.transform;
            currencyPanelRect.anchorMin = new Vector2(1f, 1f);
            currencyPanelRect.anchorMax = new Vector2(1f, 1f);
            currencyPanelRect.pivot = new Vector2(1f, 1f);
            currencyPanelRect.anchoredPosition = new Vector2(-16f, -120f);
            currencyPanelRect.sizeDelta = new Vector2(200f, 46f);
            currencyPanelObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);

            VerticalLayoutGroup currencyLayout = currencyPanelObject.AddComponent<VerticalLayoutGroup>();
            currencyLayout.padding = new RectOffset(10, 10, 4, 4);
            currencyLayout.childControlWidth = true;
            currencyLayout.childForceExpandWidth = true;
            currencyLayout.childControlHeight = false;
            currencyLayout.childForceExpandHeight = false;

            (Image goldIcon, TextMeshProUGUI goldText) = CreateCurrencyRow(currencyPanelObject.transform, "Gold: 0");
            (Image gemsIcon, TextMeshProUGUI gemsText) = CreateCurrencyRow(currencyPanelObject.transform, "Gems: 0");

            UiCurrencyDisplay currencyDisplay = currencyPanelObject.AddComponent<UiCurrencyDisplay>();
            currencyDisplay.SyncProxy = syncProxy;
            currencyDisplay.GoldText = goldText;
            currencyDisplay.GemsText = gemsText;
            currencyDisplay.GoldIcon = goldIcon;
            currencyDisplay.GemsIcon = gemsIcon;

            if (assetRegistry != null)
            {
                goldIcon.sprite = assetRegistry.GoldIcon;
                goldIcon.enabled = assetRegistry.GoldIcon != null;
                gemsIcon.sprite = assetRegistry.GemsIcon;
                gemsIcon.enabled = assetRegistry.GemsIcon != null;
            }

            Button battlePassBannerButton = CreateButton(barRootObject.transform, "BattlePassBanner", "Season Pass", out TextMeshProUGUI _);
            RectTransform bannerRect = (RectTransform)battlePassBannerButton.transform;
            bannerRect.anchorMin = new Vector2(0f, 0f);
            bannerRect.anchorMax = new Vector2(1f, 0f);
            bannerRect.pivot = new Vector2(0.5f, 0f);
            bannerRect.sizeDelta = new Vector2(0f, 54f);
            bannerRect.anchoredPosition = Vector2.zero;

            return (hamburgerToggleButton, homeButton, battlePassBannerButton);
        }

        // ------------------------------------------------------------
        // Chat panels.
        //
        // Modul: UI rework. Chat used to be exactly one window: a single
        // always-on bottom-left overlay parented straight to the Canvas as
        // its FIRST child, which meant every full-screen game window built
        // after it (all 20+ of them) drew right over the top of it - the
        // reported "chat overlaps things / chat is broken" symptom. It also
        // mixed all three server channels into one log, and, most simply,
        // its UiChatWindow.NetworkClient was never assigned by anything, so
        // it could neither send nor receive a single message.
        //
        // Now there are three separate, channel-filtered instances, each
        // parented INTO the screen that owns it so Unity's own sibling
        // ordering and the screen switcher handle visibility for free:
        //   - World chat   -> child of MainMapHub (the map/home screen)
        //   - Guild chat   -> a sub-tab of the Guild window
        //   - Private chat -> the right-hand half of the Friends window
        // ------------------------------------------------------------
        private const float ChatHeaderHeight = 30f;
        private const float ChatComposeHeight = 40f;
        private const float ChatSendButtonWidth = 78f;

        private static UiChatWindow BuildChatPanel(
            Transform parent,
            string panelName,
            string headerTitle,
            ChatChannelType channel,
            WebSocketClient networkClient,
            bool withMinimizeToggle)
        {
            GameObject panelObject = new GameObject(panelName, typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);
            RectTransform panelRect = (RectTransform)panelObject.transform;
            StretchFull(panelRect);

            panelObject.AddComponent<Image>().color = new Color(0.05f, 0.05f, 0.08f, 0.72f);

            UiChatWindow chatWindow = panelObject.AddComponent<UiChatWindow>();
            chatWindow.NetworkClient = networkClient;
            chatWindow.Channel = (byte)channel;
            chatWindow.RowPrefabAddressableKey = ChatSceneBuilder.ChatConstants.RowPrefabAddressableKey;

            // Header bar - title on the left, optional minimize toggle on
            // the right. Always present so no chat panel is ever an
            // unlabelled grey box.
            GameObject headerBarObject = new GameObject("HeaderBar", typeof(RectTransform));
            headerBarObject.transform.SetParent(panelRect, false);
            RectTransform headerBarRect = (RectTransform)headerBarObject.transform;
            headerBarRect.anchorMin = new Vector2(0f, 1f);
            headerBarRect.anchorMax = new Vector2(1f, 1f);
            headerBarRect.pivot = new Vector2(0.5f, 1f);
            headerBarRect.sizeDelta = new Vector2(0f, ChatHeaderHeight);
            headerBarRect.anchoredPosition = Vector2.zero;
            headerBarObject.AddComponent<Image>().color = new Color(0.14f, 0.13f, 0.18f, 0.95f);

            TextMeshProUGUI headerLabel = CreateText(headerBarRect, "HeaderLabel", headerTitle, 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform headerLabelRect = (RectTransform)headerLabel.transform;
            headerLabelRect.anchorMin = Vector2.zero;
            headerLabelRect.anchorMax = Vector2.one;
            headerLabelRect.offsetMin = new Vector2(10f, 0f);
            headerLabelRect.offsetMax = new Vector2(-40f, 0f);
            chatWindow.HeaderLabel = headerLabel;

            GameObject expandedContentObject = new GameObject("ExpandedContent", typeof(RectTransform));
            expandedContentObject.transform.SetParent(panelRect, false);
            RectTransform expandedContentRect = (RectTransform)expandedContentObject.transform;
            expandedContentRect.anchorMin = Vector2.zero;
            expandedContentRect.anchorMax = Vector2.one;
            expandedContentRect.offsetMin = new Vector2(6f, 6f);
            expandedContentRect.offsetMax = new Vector2(-6f, -(ChatHeaderHeight + 4f));

            // Message log. Fixed-pixel compose strip at the bottom rather
            // than the shared BuildScrollView's own 18% bottom anchor - a
            // percentage there gives a comically tall input box in the tall
            // Friends panel and an unusably short one in the map overlay.
            GameObject logAreaObject = new GameObject("LogArea", typeof(RectTransform));
            logAreaObject.transform.SetParent(expandedContentRect, false);
            RectTransform logAreaRect = (RectTransform)logAreaObject.transform;
            logAreaRect.anchorMin = Vector2.zero;
            logAreaRect.anchorMax = Vector2.one;
            logAreaRect.offsetMin = new Vector2(0f, ChatComposeHeight + 6f);
            logAreaRect.offsetMax = Vector2.zero;

            (ScrollRect scrollRect, RectTransform content) = ChatSceneBuilder.BuildScrollView(logAreaRect);
            StretchFull((RectTransform)scrollRect.transform);

            // UiChatWindow drives row positions and the content height
            // itself (fixed-slot virtualization) - a VerticalLayoutGroup /
            // ContentSizeFitter would fight it for control of both, so the
            // shared scroll view's layout components come straight back off.
            Object.DestroyImmediate(content.GetComponent<ContentSizeFitter>());
            Object.DestroyImmediate(content.GetComponent<VerticalLayoutGroup>());

            TextMeshProUGUI emptyStateText = CreateText(logAreaRect, "EmptyStateText", string.Empty, 13f, TextAlignmentOptions.Center);
            emptyStateText.color = new Color(1f, 1f, 1f, 0.45f);
            emptyStateText.fontStyle = FontStyles.Italic;
            StretchFull((RectTransform)emptyStateText.transform);
            chatWindow.EmptyStateText = emptyStateText;

            // Compose strip.
            TMP_InputField inputField = CreateInputField(expandedContentRect, "MessageInputField", "Type a message...");
            inputField.lineType = TMP_InputField.LineType.SingleLine;

            // CreateInputField paints a white box with black text, which is
            // right for a form field on a light panel but reads as a glaring
            // slab under a dark, semi-transparent chat log. Re-tinted to sit
            // in the panel rather than on top of it.
            if (inputField.targetGraphic is Image inputBackground)
            {
                inputBackground.color = new Color(0.13f, 0.13f, 0.18f, 1f);
            }
            if (inputField.textComponent != null)
            {
                inputField.textComponent.color = Color.white;
            }
            if (inputField.placeholder is TMP_Text placeholderLabel)
            {
                placeholderLabel.color = new Color(1f, 1f, 1f, 0.4f);
            }
            RectTransform inputRect = (RectTransform)inputField.transform;
            inputRect.anchorMin = new Vector2(0f, 0f);
            inputRect.anchorMax = new Vector2(1f, 0f);
            inputRect.pivot = new Vector2(0f, 0f);
            inputRect.sizeDelta = new Vector2(-(ChatSendButtonWidth + 6f), ChatComposeHeight);
            inputRect.anchoredPosition = Vector2.zero;

            Button sendButton = CreateButton(expandedContentRect, "SendButton", "Send", out TextMeshProUGUI _);
            RectTransform sendRect = (RectTransform)sendButton.transform;
            sendRect.anchorMin = new Vector2(1f, 0f);
            sendRect.anchorMax = new Vector2(1f, 0f);
            sendRect.pivot = new Vector2(1f, 0f);
            sendRect.sizeDelta = new Vector2(ChatSendButtonWidth, ChatComposeHeight);
            sendRect.anchoredPosition = Vector2.zero;

            chatWindow.ChatScrollRect = scrollRect;
            chatWindow.RowContainer = content;
            chatWindow.MessageInputField = inputField;
            chatWindow.SendButton = sendButton;

            if (withMinimizeToggle)
            {
                Button minimizeButton = CreateButton(headerBarRect, "MinimizeToggleButton", "-", out TextMeshProUGUI minimizeLabel);
                RectTransform minimizeRect = (RectTransform)minimizeButton.transform;
                minimizeRect.anchorMin = new Vector2(1f, 0f);
                minimizeRect.anchorMax = new Vector2(1f, 1f);
                minimizeRect.pivot = new Vector2(1f, 0.5f);
                minimizeRect.sizeDelta = new Vector2(34f, -6f);
                minimizeRect.anchoredPosition = new Vector2(-3f, 0f);

                UiChatMinimizePanel minimizePanel = panelObject.AddComponent<UiChatMinimizePanel>();
                minimizePanel.ExpandedContent = expandedContentObject;
                minimizePanel.MinimizeToggleButton = minimizeButton;
                minimizePanel.ToggleButtonLabel = minimizeLabel;
            }

            return chatWindow;
        }

        // World chat, docked into the bottom of the map hub itself.
        // Parenting it to the hub (rather than the Canvas) is what confines
        // it to the map screen per the "world chat belongs on the map, guild
        // and private chat live in their own screens" split, and removes the
        // whole class of overlap bugs that came from a Canvas-level overlay
        // competing for z-order with every full-screen window.
        private static UiChatWindow BuildWorldChatOverlay(Transform mapHubTransform, WebSocketClient networkClient)
        {
            GameObject dockObject = new GameObject("WorldChatDock", typeof(RectTransform));
            dockObject.transform.SetParent(mapHubTransform, false);
            RectTransform dockRect = (RectTransform)dockObject.transform;
            dockRect.anchorMin = new Vector2(0f, 0f);
            dockRect.anchorMax = new Vector2(1f, 0f);
            dockRect.pivot = new Vector2(0.5f, 0f);
            dockRect.sizeDelta = new Vector2(-24f, 300f);
            dockRect.anchoredPosition = new Vector2(0f, 64f);

            return BuildChatPanel(dockRect, "WorldChatPanel", "World Chat", ChatChannelType.Global, networkClient, withMinimizeToggle: true);
        }

        // ------------------------------------------------------------
        // Shared UI construction helpers
        // ------------------------------------------------------------
        private static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string objectName, string initialText, float fontSize, TextAlignmentOptions alignment)
        {
            GameObject textObject = new GameObject(objectName, typeof(RectTransform));
            textObject.transform.SetParent(parent, false);
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = initialText;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            return text;
        }

        private static Button CreateButton(Transform parent, string objectName, string label, out TextMeshProUGUI labelText)
        {
            GameObject buttonObject = new GameObject(objectName, typeof(RectTransform));
            buttonObject.transform.SetParent(parent, false);
            buttonObject.AddComponent<Image>().color = new Color(0.2f, 0.5f, 0.9f, 1f);
            Button button = buttonObject.AddComponent<Button>();

            if (!string.IsNullOrEmpty(label))
            {
                labelText = CreateText(buttonObject.transform, "Text", label, 16f, TextAlignmentOptions.Center);
                labelText.color = Color.white;
                StretchFull((RectTransform)labelText.transform);
            }
            else
            {
                labelText = null;
            }

            return button;
        }

        private static TMP_InputField CreateInputField(Transform parent, string objectName, string placeholder)
        {
            GameObject inputFieldObject = new GameObject(objectName, typeof(RectTransform));
            inputFieldObject.transform.SetParent(parent, false);
            inputFieldObject.AddComponent<Image>().color = Color.white;
            TMP_InputField inputField = inputFieldObject.AddComponent<TMP_InputField>();

            GameObject textAreaObject = new GameObject("Text Area", typeof(RectTransform));
            textAreaObject.transform.SetParent(inputFieldObject.transform, false);
            RectTransform textAreaRect = (RectTransform)textAreaObject.transform;
            textAreaRect.anchorMin = Vector2.zero;
            textAreaRect.anchorMax = Vector2.one;
            textAreaRect.offsetMin = new Vector2(8f, 4f);
            textAreaRect.offsetMax = new Vector2(-8f, -4f);
            textAreaObject.AddComponent<RectMask2D>();

            TextMeshProUGUI placeholderText = CreateText(textAreaRect, "Placeholder", placeholder, 16f, TextAlignmentOptions.MidlineLeft);
            placeholderText.fontStyle = FontStyles.Italic;
            placeholderText.color = new Color(0f, 0f, 0f, 0.5f);
            StretchFull((RectTransform)placeholderText.transform);

            TextMeshProUGUI inputText = CreateText(textAreaRect, "Text", string.Empty, 16f, TextAlignmentOptions.MidlineLeft);
            inputText.color = Color.black;
            StretchFull((RectTransform)inputText.transform);

            inputField.textViewport = textAreaRect;
            inputField.textComponent = inputText;
            inputField.placeholder = placeholderText;

            return inputField;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = System.IO.Path.GetDirectoryName(path)!.Replace('\\', '/');
            string folderName = System.IO.Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }
            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
