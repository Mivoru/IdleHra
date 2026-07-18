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
        private const string BankVaultRowPrefabPath = PrefabDirectory + "/UiBankVaultEntryRow.prefab";
        private const string BankDepositRowPrefabPath = PrefabDirectory + "/UiBankDepositCandidateRow.prefab";
        private const string AchievementRowPrefabPath = PrefabDirectory + "/UiAchievementRow.prefab";
        private const string LeaderboardRowPrefabPath = PrefabDirectory + "/UiLeaderboardEntryRow.prefab";
        private const string MailboxRowPrefabPath = PrefabDirectory + "/UiMailboxEntryRow.prefab";
        private const string StoreRowPrefabPath = PrefabDirectory + "/UiStoreEntryRow.prefab";
        private const string SeasonPassRowPrefabPath = PrefabDirectory + "/UiSeasonPassMilestoneRow.prefab";
        private const string ForgeRecipeRowPrefabPath = PrefabDirectory + "/UiForgeRecipeRow.prefab";
        private const string ForgeEquipmentRowPrefabPath = PrefabDirectory + "/UiForgeEquipmentRow.prefab";
        private const string AssetRegistryAssetPath = PrefabDirectory + "/AssetRegistry.asset";
        private const string CodexListRowPrefabPath = PrefabDirectory + "/UiCodexListRow.prefab";
        private const string CodexRegionRowPrefabPath = PrefabDirectory + "/UiCodexRegionRow.prefab";
        private const string BreedingRosterRowPrefabPath = PrefabDirectory + "/UiBreedingRosterRow.prefab";

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

            // Modul: Map Hub, Part 1. Chat is no longer one of the mutually
            // exclusive screens - it is now a persistent, semi-transparent,
            // minimizable bottom-left corner overlay, always active
            // regardless of which map-hub screen is showing.
            GameObject chatWindowObject = BuildChatOverlay(canvas.transform, rowPrefabAsset);

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
            GameObject forgeWindowObject = BuildForgeWindow(canvas.transform, inventoryCache, networkClient, syncProxy, assetRegistry);
            GameObject skillTreeWindowObject = BuildSkillTreeWindow(canvas.transform, networkClient, syncProxy);
            GameObject villageWindowObject = BuildVillageWindow(canvas.transform, syncProxy, networkClient);
            GameObject codexWindowObject = BuildCodexWindow(canvas.transform, assetRegistry, assetCoordinator, managers.transform);
            GameObject breedingLabWindowObject = BuildBreedingLabWindow(canvas.transform, networkClient);

            // Modul: Full-Game UI Architecture, Part 4. Simple list-style
            // screens - Achievements, Leaderboard, Mailbox, Store, Season
            // Pass. All real, network-wired scripts.
            GameObject achievementsWindowObject = BuildAchievementsWindow(canvas.transform);
            GameObject leaderboardWindowObject = BuildLeaderboardWindow(canvas.transform);
            GameObject mailboxWindowObject = BuildMailboxWindow(canvas.transform, syncProxy, networkClient);
            GameObject storeWindowObject = BuildStoreWindow(canvas.transform, syncProxy, networkClient);
            GameObject seasonPassWindowObject = BuildSeasonPassWindow(canvas.transform, syncProxy, networkClient);

            // Modul: Map Hub, Part 2. Honest static placeholders - Friends,
            // Statistics, and Login Bonus have no corresponding
            // engine/network code anywhere server-side (confirmed via
            // project-wide search), so unlike every other screen in this
            // file they are not wired to any real cache; they are plain
            // shells reachable from the hamburger menu, ready for real
            // content once that server-side support exists. Settings gets
            // a real (if minimal) Profile section - see BuildSettingsWindow
            // - since it hosts the one real, load-bearing action this pass
            // adds: Log Off.
            (GameObject settingsPanelObject, Button logOffButton) = BuildSettingsWindow(canvas.transform);
            GameObject friendsPanelObject = BuildPlaceholderWindow(canvas.transform, "FriendsPanel", "Friends", "Friends list is not implemented yet.");
            GameObject statisticsPanelObject = BuildPlaceholderWindow(canvas.transform, "StatisticsPanel", "Statistics", "Statistics are not implemented yet.");
            GameObject loginBonusPanelObject = BuildPlaceholderWindow(canvas.transform, "LoginBonusPanel", "Login Bonus", "Login bonus is not implemented yet.");

            // Modul: Map Hub, Part 3. Combat Selection (real region/
            // monster/character data, see UiCombatSelectionPanel) and Boss
            // World (real HP/attack plus the real global leaderboard, see
            // BuildBossWorldPanel) - the two new full-screen panels reached
            // from the map's Combat and Boss zones.
            (GameObject combatPanelObject, UiCombatSelectionPanel combatPanelComponent) = BuildCombatSelectionPanel(canvas.transform);
            GameObject bossWorldPanelObject = BuildBossWorldPanel(canvas.transform, syncProxy, sfxEngine, networkClient);

            // Modul: Map Hub, Part 4. The medieval map field itself - 5
            // clickable zone buttons (Combat, Village, Guild, Market,
            // Boss), now the default/home screen.
            (GameObject mainMapHubObject, Button combatZoneButton, Button villageZoneButton, Button guildZoneButton, Button marketZoneButton, Button bossZoneButton) = BuildMainMapHub(canvas.transform);

            // Modul: Map Hub, Part 5. Hamburger sliding menu - folds every
            // screen not represented as one of the 5 map zones (per user
            // direction: Bestiary reuses the existing Codex window rather
            // than duplicating it under a second name).
            (GameObject hamburgerBlocker, UiHamburgerMenuPanel hamburgerComponent, Button[] hamburgerMenuButtons) = BuildHamburgerPanel(canvas.transform, new[]
            {
                "Forge", "Skills", "Bestiary", "Breeding Lab", "Achievements", "Leaderboard",
                "Mailbox", "Store", "Season Pass", "Settings", "Friends", "Statistics", "Login Bonus"
            });

            // Modul: Map Hub, Part 6. Persistent top-left (hamburger toggle
            // + Home/Map button), top-right (real Gold/Gems currency), and
            // bottom (Season Pass banner) bars - stay visible across every
            // screen per the map-hub spec's UI persistence requirement.
            (Button hamburgerToggleButton, Button homeButton, Button battlePassBannerButton) = BuildPersistentBars(canvas.transform, syncProxy);

            // Modul: Map Hub, Part 7. One screen switcher for all 20
            // top-level screens - replaces the old flat scrollable nav-tab
            // strip. Index 0 (MainMapHub) has no dedicated Buttons[] entry
            // - it is the default/home screen, reached from every other
            // screen via homeButton, not a single "open map" button.
            GameObject screenManagerObject = new GameObject("ScreenManager", typeof(RectTransform));
            screenManagerObject.transform.SetParent(canvas.transform, false);

            GameObject[] screens =
            {
                mainMapHubObject, hudGroup, combatPanelObject, villageWindowObject, guildWindowObject,
                marketBankWindowObject, bossWorldPanelObject, forgeWindowObject, skillTreeWindowObject,
                codexWindowObject, breedingLabWindowObject, achievementsWindowObject, leaderboardWindowObject,
                mailboxWindowObject, storeWindowObject, seasonPassWindowObject, settingsPanelObject,
                friendsPanelObject, statisticsPanelObject, loginBonusPanelObject
            };

            Button[] screenButtons =
            {
                null, null, combatZoneButton, villageZoneButton, guildZoneButton,
                marketZoneButton, bossZoneButton, hamburgerMenuButtons[0], hamburgerMenuButtons[1],
                hamburgerMenuButtons[2], hamburgerMenuButtons[3], hamburgerMenuButtons[4], hamburgerMenuButtons[5],
                hamburgerMenuButtons[6], hamburgerMenuButtons[7], hamburgerMenuButtons[8], hamburgerMenuButtons[9],
                hamburgerMenuButtons[10], hamburgerMenuButtons[11], hamburgerMenuButtons[12]
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

            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(homeButton.onClick, screenTabGroup.ShowIndex, 0);
            UnityEditor.Events.UnityEventTools.AddIntPersistentListener(battlePassBannerButton.onClick, screenTabGroup.ShowIndex, SeasonPassScreenIndex);

            // Modul: plain field assignment, not a persistent listener -
            // UiHamburgerMenuPanel already self-wires ToggleButton.onClick
            // inside its own Awake() (established codebase convention),
            // it just couldn't be assigned inside BuildHamburgerPanel
            // itself since hamburgerToggleButton (built by
            // BuildPersistentBars) does not exist yet at that point.
            hamburgerComponent.ToggleButton = hamburgerToggleButton;

            // Modul: every hamburger menu button both switches screens (via
            // the index-aligned Buttons[] entry above, self-wired inside
            // UiTabGroup.Awake) and closes the sliding panel afterward -
            // two independent persistent listeners on the same onClick.
            for (int menuIndex = 0; menuIndex < hamburgerMenuButtons.Length; menuIndex++)
            {
                UnityEditor.Events.UnityEventTools.AddPersistentListener(hamburgerMenuButtons[menuIndex].onClick, hamburgerComponent.Close);
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
                forgeButton: hamburgerMenuButtons[0], marketButton: marketZoneButton, guildButton: guildZoneButton,
                skillTreeButton: hamburgerMenuButtons[1], chatButton: null);

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
            LayoutElement registerStep2EmailLabelLayout = registerStep2EmailLabel.gameObject.AddComponent<LayoutElement>();
            registerStep2EmailLabelLayout.preferredHeight = 26f;
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
            LayoutElement layoutElement = field.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 50f;
            if (isPassword)
            {
                field.contentType = TMP_InputField.ContentType.Password;
            }
            return field;
        }

        private static Button BuildAuthButton(Transform parent, string name, string label)
        {
            Button button = CreateButton(parent, name, label, out TextMeshProUGUI _);
            LayoutElement layoutElement = button.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 54f;
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
            LayoutElement layoutElement = text.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 22f;
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
            LayoutElement rowLayout = rowObject.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 44f;

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

            Button[] subTabButtons = BuildSubTabButtons(subTabHeaderRect, new[] { "Roster", "Logistics", "Raid", "War" });

            GameObject contentAreaObject = new GameObject("ContentArea", typeof(RectTransform));
            contentAreaObject.transform.SetParent(windowRect, false);
            RectTransform contentAreaRect = (RectTransform)contentAreaObject.transform;
            contentAreaRect.anchorMin = Vector2.zero;
            contentAreaRect.anchorMax = Vector2.one;
            contentAreaRect.offsetMin = new Vector2(20f, 20f);
            contentAreaRect.offsetMax = new Vector2(-20f, -64f);

            GameObject rosterGroup = BuildGuildRosterGroup(contentAreaRect, syncProxy, networkClient);
            GameObject logisticsGroup = BuildGuildLogisticsGroup(contentAreaRect, syncProxy, networkClient);
            GameObject raidGroup = BuildGuildRaidGroup(contentAreaRect, syncProxy, networkClient, sfxEngine);
            GameObject warGroup = BuildGuildWarGroup(contentAreaRect, syncProxy);

            logisticsGroup.SetActive(false);
            raidGroup.SetActive(false);
            warGroup.SetActive(false);

            UiTabGroup tabGroup = windowObject.AddComponent<UiTabGroup>();
            tabGroup.Groups = new[] { rosterGroup, logisticsGroup, raidGroup, warGroup };
            tabGroup.Buttons = subTabButtons;

            return windowObject;
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

            (TMP_InputField inviteInput, Button inviteButton) = BuildLabeledInputRow(actionsAreaObject.transform, "InvitePlayerRow", "Player Name", "Invite Player");
            createPanel.InvitePlayerInputField = inviteInput;
            createPanel.InvitePlayerButton = inviteButton;

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

            TextMeshProUGUI levelText = CreateStatRow(groupObject.transform, "Lv. 0");
            TextMeshProUGUI contributionText = CreateStatRow(groupObject.transform, "0 / 0");

            (GameObject barBackground, RectTransform barFill) = BuildAnchoredProgressBar(groupObject.transform, new Color(0.3f, 0.7f, 1f, 1f));
            LayoutElement barLayout = barBackground.AddComponent<LayoutElement>();
            barLayout.preferredHeight = 24f;

            Button donateButton = CreateButton(groupObject.transform, "DonateButton", "Donate", out TextMeshProUGUI _);
            LayoutElement donateLayout = donateButton.gameObject.AddComponent<LayoutElement>();
            donateLayout.preferredHeight = 44f;

            UiGuildLogisticsPanel panel = groupObject.AddComponent<UiGuildLogisticsPanel>();
            panel.SyncProxy = syncProxy;
            panel.NetworkClient = networkClient;
            panel.LogisticsLevelText = levelText;
            panel.ContributionText = contributionText;
            panel.ProgressBarFill = barFill;
            panel.DonateButton = donateButton;

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

            TextMeshProUGUI tierText = CreateStatRow(groupObject.transform, "Tier 0");
            TextMeshProUGUI hpText = CreateStatRow(groupObject.transform, "0 / 0");

            (GameObject barBackground, RectTransform barFill) = BuildAnchoredProgressBar(groupObject.transform, new Color(0.85f, 0.2f, 0.2f, 1f));
            LayoutElement barLayout = barBackground.AddComponent<LayoutElement>();
            barLayout.preferredHeight = 24f;

            Button launchButton = CreateButton(groupObject.transform, "LaunchRaidButton", "Launch Raid", out TextMeshProUGUI _);
            LayoutElement launchLayout = launchButton.gameObject.AddComponent<LayoutElement>();
            launchLayout.preferredHeight = 44f;

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

        // Guild War scoreboard - real, network-wired UiGuildWarPanel. No
        // buttons of its own (read-only status display), so nothing here
        // dispatches a command.
        private static GameObject BuildGuildWarGroup(Transform parent, VisualSyncProxy syncProxy)
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

            TextMeshProUGUI statusText = CreateStatRow(groupObject.transform, "War Status");

            GameObject noActiveWarRoot = new GameObject("NoActiveWarRoot", typeof(RectTransform));
            noActiveWarRoot.transform.SetParent(groupObject.transform, false);
            LayoutElement noActiveLayout = noActiveWarRoot.AddComponent<LayoutElement>();
            noActiveLayout.preferredHeight = 26f;
            TextMeshProUGUI countdownText = CreateText(noActiveWarRoot.transform, "MatchmakingCountdownText", string.Empty, 14f, TextAlignmentOptions.MidlineLeft);
            StretchFull((RectTransform)countdownText.transform);

            GameObject activeWarRoot = new GameObject("ActiveWarRoot", typeof(RectTransform));
            activeWarRoot.transform.SetParent(groupObject.transform, false);
            LayoutElement activeWarLayout = activeWarRoot.AddComponent<LayoutElement>();
            activeWarLayout.preferredHeight = 260f;

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

            UiGuildWarPanel panel = groupObject.AddComponent<UiGuildWarPanel>();
            panel.SyncProxy = syncProxy;
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

            return groupObject;
        }

        // A horizontal row of N equal-width tab buttons filling the given
        // RectTransform - shared by every UiTabGroup instance in this file
        // (Guild's four sub-tabs, Market & Bank's two).
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
            LayoutElement rowLayout = rowObject.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 44f;

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

            GameObject listingAreaObject = new GameObject("ListingArea", typeof(RectTransform));
            listingAreaObject.transform.SetParent(parent, false);
            RectTransform listingAreaRect = (RectTransform)listingAreaObject.transform;
            listingAreaRect.anchorMin = new Vector2(0f, 0.54f);
            listingAreaRect.anchorMax = new Vector2(1f, 1f);
            listingAreaRect.offsetMin = new Vector2(0f, 26f);
            listingAreaRect.offsetMax = new Vector2(0f, -122f);

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
            rect.sizeDelta = new Vector2(220f, 20f);
            // Modul: Map Hub. Below the shifted CharacterStatsPanel
            // (y -72 to -292) instead of the old -250, so the two never
            // overlap on the Character/Arena screen.
            rect.anchoredPosition = new Vector2(20f, -300f);

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

            TextMeshProUGUI humanText = CreateStatRow(panelObject.transform, "Human: +0%");
            TextMeshProUGUI vilaText = CreateStatRow(panelObject.transform, "Vila: +0%");
            TextMeshProUGUI draugrText = CreateStatRow(panelObject.transform, "Draugr: +0%");

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
            LayoutElement dismissLayout = dismissButton.gameObject.AddComponent<LayoutElement>();
            dismissLayout.preferredHeight = 48f;

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

        private static GameObject BuildForgeWindow(Transform canvasTransform, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient, VisualSyncProxy syncProxy, AssetRegistry assetRegistry)
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

            Button[] subTabButtons = BuildSubTabButtons(subTabHeaderRect, new[] { "Craft", "Reroll" });

            GameObject contentAreaObject = new GameObject("ContentArea", typeof(RectTransform));
            contentAreaObject.transform.SetParent(windowRect, false);
            RectTransform contentAreaRect = (RectTransform)contentAreaObject.transform;
            contentAreaRect.anchorMin = Vector2.zero;
            contentAreaRect.anchorMax = Vector2.one;
            contentAreaRect.offsetMin = new Vector2(20f, 20f);
            contentAreaRect.offsetMax = new Vector2(-20f, -64f);

            GameObject craftingGroup = BuildForgeCraftingGroup(contentAreaRect, inventoryCache, networkClient, assetRegistry);
            GameObject rerollGroup = BuildEquipmentRerollGroup(contentAreaRect, inventoryCache, networkClient, syncProxy, assetRegistry);

            rerollGroup.SetActive(false);

            UiTabGroup tabGroup = windowObject.AddComponent<UiTabGroup>();
            tabGroup.Groups = new[] { craftingGroup, rerollGroup };
            tabGroup.Buttons = subTabButtons;

            return windowObject;
        }

        // Top 58% recipe list (real UiForgeCraftingPanel), bottom 42%
        // detail panel with name/material text plus a Craft button.
        private static GameObject BuildForgeCraftingGroup(Transform parent, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient, AssetRegistry assetRegistry)
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

            VerticalLayoutGroup detailLayout = detailAreaObject.AddComponent<VerticalLayoutGroup>();
            detailLayout.spacing = 8f;
            detailLayout.childControlWidth = true;
            detailLayout.childForceExpandWidth = true;
            detailLayout.childControlHeight = false;
            detailLayout.childForceExpandHeight = false;

            TextMeshProUGUI selectedNameText = CreateStatRow(detailAreaObject.transform, "No Recipe Selected");
            TextMeshProUGUI requiredMaterialText = CreateStatRow(detailAreaObject.transform, "Materials: -");

            Button craftButton = CreateButton(detailAreaObject.transform, "CraftButton", "Craft", out TextMeshProUGUI _);
            LayoutElement craftButtonLayout = craftButton.gameObject.AddComponent<LayoutElement>();
            craftButtonLayout.preferredHeight = 44f;

            UiForgeCraftingPanel craftingPanel = groupObject.AddComponent<UiForgeCraftingPanel>();
            craftingPanel.InventoryCache = inventoryCache;
            craftingPanel.NetworkClient = networkClient;
            craftingPanel.RowContainer = recipeContent;
            craftingPanel.RowPrefab = recipeRowPrefabAsset.GetComponent<UiForgeRecipeRow>();
            craftingPanel.SelectedRecipeNameText = selectedNameText;
            craftingPanel.RequiredMaterialText = requiredMaterialText;
            craftingPanel.CraftButton = craftButton;
            craftingPanel.SufficientStockColor = Color.white;
            craftingPanel.InsufficientStockColor = new Color(1f, 0.35f, 0.35f, 1f);

            AssignAssetRegistry(craftingPanel, assetRegistry);

            return groupObject;
        }

        // Top 50% equipment list (real UiForgeEquipmentRow instances),
        // bottom 50% detail panel - selected item name, 4 fixed affix
        // slot rows (each: highlight bar + label + Select button), reroll
        // cost text, Reroll button. Real, network-wired UiEquipmentRerollPanel
        // (CommandType via SendRerollCommandZeroAlloc/SendEquipItemCommandZeroAlloc).
        private static GameObject BuildEquipmentRerollGroup(Transform parent, EquipmentInventoryCache inventoryCache, WebSocketClient networkClient, VisualSyncProxy syncProxy, AssetRegistry assetRegistry)
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

            VerticalLayoutGroup detailLayout = detailAreaObject.AddComponent<VerticalLayoutGroup>();
            detailLayout.spacing = 6f;
            detailLayout.childControlWidth = true;
            detailLayout.childForceExpandWidth = true;
            detailLayout.childControlHeight = false;
            detailLayout.childForceExpandHeight = false;

            TextMeshProUGUI selectedItemNameText = CreateStatRow(detailAreaObject.transform, "No Item Selected");

            TextMeshProUGUI[] affixTexts = new TextMeshProUGUI[4];
            Button[] affixButtons = new Button[4];
            GameObject[] affixHighlights = new GameObject[4];
            for (int i = 0; i < 4; i++)
            {
                GameObject affixRowObject = new GameObject("AffixSlotRow" + i, typeof(RectTransform));
                affixRowObject.transform.SetParent(detailAreaObject.transform, false);
                LayoutElement affixRowLayout = affixRowObject.AddComponent<LayoutElement>();
                affixRowLayout.preferredHeight = 30f;

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

            TextMeshProUGUI rerollCostText = CreateStatRow(detailAreaObject.transform, "Cost: -");

            Button rerollButton = CreateButton(detailAreaObject.transform, "RerollButton", "Reroll", out TextMeshProUGUI _);
            LayoutElement rerollButtonLayout = rerollButton.gameObject.AddComponent<LayoutElement>();
            rerollButtonLayout.preferredHeight = 44f;

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
            LayoutElement nodeLayout = nodeObject.AddComponent<LayoutElement>();
            nodeLayout.preferredHeight = 90f;
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

            UiVillageBuildingRow forgeRow = BuildVillageBuildingRow(content, "ForgeRow", 1, "Forge");
            UiVillageBuildingRow innRow = BuildVillageBuildingRow(content, "InnRow", 2, "Inn");
            UiVillageBuildingRow breedingRow = BuildVillageBuildingRow(content, "BreedingGroundsRow", 3, "Breeding Grounds");
            UiVillageBuildingRow academyRow = BuildVillageBuildingRow(content, "MentorshipAcademyRow", 4, "Mentorship Academy");
            UiVillageBuildingRow lumberjackRow = BuildVillageBuildingRow(content, "LumberjackRow", 5, "Lumberjack");
            UiVillageBuildingRow quarryRow = BuildVillageBuildingRow(content, "QuarryRow", 6, "Quarry");
            UiVillageBuildingRow mineRow = BuildVillageBuildingRow(content, "MineRow", 7, "Mine");
            UiVillageBuildingRow warehouseRow = BuildVillageBuildingRow(content, "WarehouseRow", 8, "Warehouse");

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
        private static UiVillageBuildingRow BuildVillageBuildingRow(Transform parent, string rowName, int buildingId, string displayName)
        {
            GameObject rowObject = new GameObject(rowName, typeof(RectTransform));
            rowObject.transform.SetParent(parent, false);
            LayoutElement rowLayout = rowObject.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 70f;
            rowObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);

            VerticalLayoutGroup rowLayoutGroup = rowObject.AddComponent<VerticalLayoutGroup>();
            rowLayoutGroup.padding = new RectOffset(8, 8, 6, 6);
            rowLayoutGroup.spacing = 4f;
            rowLayoutGroup.childControlWidth = true;
            rowLayoutGroup.childForceExpandWidth = true;
            rowLayoutGroup.childControlHeight = false;
            rowLayoutGroup.childForceExpandHeight = false;

            GameObject headerRowObject = new GameObject("HeaderRow", typeof(RectTransform));
            headerRowObject.transform.SetParent(rowObject.transform, false);
            LayoutElement headerRowLayout = headerRowObject.AddComponent<LayoutElement>();
            headerRowLayout.preferredHeight = 26f;

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
            LayoutElement progressBarLayout = progressBarRoot.AddComponent<LayoutElement>();
            progressBarLayout.preferredHeight = 20f;

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

            UiVillageBuildingRow rowComponent = rowObject.AddComponent<UiVillageBuildingRow>();
            rowComponent.BuildingId = buildingId;
            rowComponent.BuildingNameText = nameText;
            rowComponent.LevelText = levelText;
            rowComponent.UpgradeButton = upgradeButton;
            rowComponent.ProgressBarRoot = progressBarRoot;
            rowComponent.ProgressBarFill = fillImage;
            rowComponent.ProgressRemainingText = remainingText;

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

            TextMeshProUGUI rowText = CreateText(root.transform, "RowLabelText", "Monster", 15f, TextAlignmentOptions.MidlineLeft);
            RectTransform rowTextRect = (RectTransform)rowText.transform;
            rowTextRect.anchorMin = Vector2.zero;
            rowTextRect.anchorMax = Vector2.one;
            rowTextRect.offsetMin = new Vector2(8f, 0f);
            rowTextRect.offsetMax = new Vector2(-8f, 0f);

            UiCodexListRow rowComponent = root.AddComponent<UiCodexListRow>();
            rowComponent.RowLabelText = rowText;
            rowComponent.RowButton = rowButton;

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
            LayoutElement headerRowLayout = headerRow.AddComponent<LayoutElement>();
            headerRowLayout.preferredHeight = 18f;
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
            LayoutElement progressRowLayout = progressRow.AddComponent<LayoutElement>();
            progressRowLayout.preferredHeight = 16f;
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
            LayoutElement bonusTextLayout = bonusText.gameObject.AddComponent<LayoutElement>();
            bonusTextLayout.preferredHeight = 16f;
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
            LayoutElement slotRowLayout = slotRowObject.AddComponent<LayoutElement>();
            slotRowLayout.preferredHeight = 34f;
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
            LayoutElement fuseButtonLayout = fuseButton.gameObject.AddComponent<LayoutElement>();
            fuseButtonLayout.preferredHeight = 44f;

            GameObject hatchingRoot = new GameObject("HatchingAnimationRoot", typeof(RectTransform));
            hatchingRoot.transform.SetParent(detailAreaObject.transform, false);
            LayoutElement hatchingLayout = hatchingRoot.AddComponent<LayoutElement>();
            hatchingLayout.preferredHeight = 20f;
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
            LayoutElement rowLayout = rowObject.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = 26f;

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

        private static GameObject BuildAchievementsWindow(Transform canvasTransform)
        {
            GameObject windowObject = BuildSimpleListWindowShell("AchievementsWindow", canvasTransform, "Achievements", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(contentAreaRect);

            GameObject rowPrefabAsset = BuildAndSaveAchievementRowPrefab();

            UiAchievementsPanel panel = windowObject.AddComponent<UiAchievementsPanel>();
            panel.RowContainer = content;
            panel.RowPrefab = rowPrefabAsset.GetComponent<UiAchievementRow>();

            return windowObject;
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

            GameObject scrollAreaObject = new GameObject("ScrollArea", typeof(RectTransform));
            scrollAreaObject.transform.SetParent(contentAreaRect, false);
            RectTransform scrollAreaRect = (RectTransform)scrollAreaObject.transform;
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = Vector2.zero;
            scrollAreaRect.offsetMax = new Vector2(0f, -36f);

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

            return windowObject;
        }

        private static GameObject BuildAndSaveAchievementRowPrefab()
        {
            EnsureFolder(PrefabDirectory);

            GameObject root = new GameObject("UiAchievementRow", typeof(RectTransform));
            ((RectTransform)root.transform).sizeDelta = new Vector2(0f, 54f);
            root.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);

            VerticalLayoutGroup layout = root.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 4, 4);
            layout.spacing = 2f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            TextMeshProUGUI idText = CreateText(root.transform, "AchievementIdText", "Achievement 0", 14f, TextAlignmentOptions.MidlineLeft);
            LayoutElement idLayout = idText.gameObject.AddComponent<LayoutElement>();
            idLayout.preferredHeight = 18f;

            TextMeshProUGUI tierText = CreateText(root.transform, "TierText", "Tier None", 12f, TextAlignmentOptions.MidlineLeft);
            LayoutElement tierLayout = tierText.gameObject.AddComponent<LayoutElement>();
            tierLayout.preferredHeight = 14f;

            GameObject progressRow = new GameObject("ProgressRow", typeof(RectTransform));
            progressRow.transform.SetParent(root.transform, false);
            LayoutElement progressRowLayout = progressRow.AddComponent<LayoutElement>();
            progressRowLayout.preferredHeight = 16f;

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

            UiAchievementRow rowComponent = root.AddComponent<UiAchievementRow>();
            rowComponent.AchievementIdText = idText;
            rowComponent.TierText = tierText;
            rowComponent.ProgressText = progressText;
            rowComponent.ProgressBarFill = barFill;

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

            Button villageZone = BuildMapZone(mapFieldRect, "VillageZone", "Village", new Vector2(0.06f, 0.62f), new Vector2(0.48f, 0.80f), new Color(0.42f, 0.32f, 0.16f, 1f));
            Button guildZone = BuildMapZone(mapFieldRect, "GuildZone", "Guild Hall", new Vector2(0.52f, 0.62f), new Vector2(0.94f, 0.80f), new Color(0.30f, 0.24f, 0.42f, 1f));
            Button marketZone = BuildMapZone(mapFieldRect, "MarketZone", "Market", new Vector2(0.06f, 0.40f), new Vector2(0.48f, 0.58f), new Color(0.44f, 0.36f, 0.10f, 1f));
            Button bossZone = BuildMapZone(mapFieldRect, "BossZone", "World Boss", new Vector2(0.52f, 0.40f), new Vector2(0.94f, 0.58f), new Color(0.46f, 0.12f, 0.12f, 1f));
            Button combatZone = BuildMapZone(mapFieldRect, "CombatZone", "Combat", new Vector2(0.06f, 0.06f), new Vector2(0.94f, 0.36f), new Color(0.18f, 0.34f, 0.18f, 1f));

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
        // Combat Selection panel (map hub "Combat" zone) - 5 real region
        // rows (CodexRegionsCache) each with a TMP_Dropdown of the
        // player's real Codex monster inventory and a Deploy button, plus
        // 4 real character assignment slots (BreedingRosterCache). See
        // UiCombatSelectionPanel's header comment for the full data-source
        // rationale.
        // ------------------------------------------------------------
        private static (GameObject panel, UiCombatSelectionPanel component) BuildCombatSelectionPanel(Transform canvasTransform)
        {
            GameObject windowObject = BuildSimpleListWindowShell("CombatSelectionPanel", canvasTransform, "Combat", out RectTransform contentAreaRect, out TextMeshProUGUI _);

            (ScrollRect _, RectTransform scrollContent) = ChatSceneBuilder.BuildScrollView(contentAreaRect);

            TMP_Text[] regionLabels = new TMP_Text[5];
            TMP_Dropdown[] monsterDropdowns = new TMP_Dropdown[5];
            Button[] deployButtons = new Button[5];

            for (int i = 0; i < 5; i++)
            {
                GameObject rowObject = new GameObject("RegionRow" + i, typeof(RectTransform));
                rowObject.transform.SetParent(scrollContent, false);
                LayoutElement rowLayout = rowObject.AddComponent<LayoutElement>();
                rowLayout.preferredHeight = 96f;
                rowObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);

                VerticalLayoutGroup rowLayoutGroup = rowObject.AddComponent<VerticalLayoutGroup>();
                rowLayoutGroup.padding = new RectOffset(10, 10, 8, 8);
                rowLayoutGroup.spacing = 6f;
                rowLayoutGroup.childControlWidth = true;
                rowLayoutGroup.childForceExpandWidth = true;
                rowLayoutGroup.childControlHeight = false;
                rowLayoutGroup.childForceExpandHeight = false;

                TextMeshProUGUI regionLabel = CreateText(rowObject.transform, "RegionLabelText", "Region " + (i + 1), 15f, TextAlignmentOptions.MidlineLeft);
                LayoutElement regionLabelLayout = regionLabel.gameObject.AddComponent<LayoutElement>();
                regionLabelLayout.preferredHeight = 22f;

                GameObject subRowObject = new GameObject("DropdownRow", typeof(RectTransform));
                subRowObject.transform.SetParent(rowObject.transform, false);
                LayoutElement subRowLayout = subRowObject.AddComponent<LayoutElement>();
                subRowLayout.preferredHeight = 40f;

                HorizontalLayoutGroup subRowLayoutGroup = subRowObject.AddComponent<HorizontalLayoutGroup>();
                subRowLayoutGroup.spacing = 8f;
                subRowLayoutGroup.childControlWidth = true;
                subRowLayoutGroup.childForceExpandWidth = false;
                subRowLayoutGroup.childControlHeight = true;
                subRowLayoutGroup.childForceExpandHeight = true;

                TMP_Dropdown dropdown = CreateTmpDropdown(subRowObject.transform, "MonsterDropdown");
                LayoutElement dropdownLayout = dropdown.gameObject.AddComponent<LayoutElement>();
                dropdownLayout.flexibleWidth = 1f;

                Button deployButton = CreateButton(subRowObject.transform, "DeployButton", "Deploy", out TextMeshProUGUI _);
                LayoutElement deployLayout = deployButton.gameObject.AddComponent<LayoutElement>();
                deployLayout.preferredWidth = 100f;

                regionLabels[i] = regionLabel;
                monsterDropdowns[i] = dropdown;
                deployButtons[i] = deployButton;
            }

            TextMeshProUGUI slotsHeaderText = CreateText(scrollContent, "CharacterSlotsHeaderText", "Character Slots", 15f, TextAlignmentOptions.MidlineLeft);
            LayoutElement slotsHeaderLayout = slotsHeaderText.gameObject.AddComponent<LayoutElement>();
            slotsHeaderLayout.preferredHeight = 26f;

            GameObject slotsRowObject = new GameObject("CharacterSlotsRow", typeof(RectTransform));
            slotsRowObject.transform.SetParent(scrollContent, false);
            LayoutElement slotsRowLayout = slotsRowObject.AddComponent<LayoutElement>();
            slotsRowLayout.preferredHeight = 70f;

            HorizontalLayoutGroup slotsLayoutGroup = slotsRowObject.AddComponent<HorizontalLayoutGroup>();
            slotsLayoutGroup.spacing = 8f;
            slotsLayoutGroup.childControlWidth = true;
            slotsLayoutGroup.childForceExpandWidth = true;
            slotsLayoutGroup.childControlHeight = true;
            slotsLayoutGroup.childForceExpandHeight = true;

            TMP_Text[] slotTexts = new TMP_Text[4];
            Button[] slotButtons = new Button[4];
            GameObject[] slotHighlights = new GameObject[4];

            for (int i = 0; i < 4; i++)
            {
                GameObject slotObject = new GameObject("CharacterSlot" + i, typeof(RectTransform));
                slotObject.transform.SetParent(slotsRowObject.transform, false);
                Image slotImage = slotObject.AddComponent<Image>();
                slotImage.color = new Color(1f, 1f, 1f, 0.08f);
                Button slotButton = slotObject.AddComponent<Button>();
                slotButton.targetGraphic = slotImage;

                GameObject highlightObject = new GameObject("SelectedHighlight", typeof(RectTransform));
                highlightObject.transform.SetParent(slotObject.transform, false);
                StretchFull((RectTransform)highlightObject.transform);
                Image highlightImage = highlightObject.AddComponent<Image>();
                highlightImage.color = new Color(0.3f, 0.7f, 1f, 0.35f);
                highlightImage.raycastTarget = false;
                highlightObject.SetActive(false);

                TextMeshProUGUI slotText = CreateText(slotObject.transform, "SlotText", "(empty)", 12f, TextAlignmentOptions.Center);
                StretchFull((RectTransform)slotText.transform);

                slotTexts[i] = slotText;
                slotButtons[i] = slotButton;
                slotHighlights[i] = highlightObject;
            }

            UiCombatSelectionPanel panelComponent = windowObject.AddComponent<UiCombatSelectionPanel>();
            panelComponent.RegionLabelTexts = regionLabels;
            panelComponent.MonsterDropdowns = monsterDropdowns;
            panelComponent.DeployButtons = deployButtons;
            panelComponent.CharacterSlotTexts = slotTexts;
            panelComponent.CharacterSlotButtons = slotButtons;
            panelComponent.CharacterSlotSelectedHighlights = slotHighlights;

            return (windowObject, panelComponent);
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
            viewportObject.AddComponent<Image>().color = Color.clear;
            viewportObject.AddComponent<Mask>().showMaskGraphic = false;

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

            GameObject hpSectionObject = new GameObject("BossHpSection", typeof(RectTransform));
            hpSectionObject.transform.SetParent(contentAreaRect, false);
            RectTransform hpSectionRect = (RectTransform)hpSectionObject.transform;
            hpSectionRect.anchorMin = new Vector2(0f, 0.62f);
            hpSectionRect.anchorMax = new Vector2(1f, 1f);
            hpSectionRect.offsetMin = Vector2.zero;
            hpSectionRect.offsetMax = Vector2.zero;
            hpSectionObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);

            (GameObject hpBackground, RectTransform hpFill) = BuildAnchoredProgressBar(hpSectionRect, new Color(0.8f, 0.1f, 0.1f, 1f));
            RectTransform hpBgRect = (RectTransform)hpBackground.transform;
            hpBgRect.anchorMin = new Vector2(0f, 1f);
            hpBgRect.anchorMax = new Vector2(1f, 1f);
            hpBgRect.pivot = new Vector2(0.5f, 1f);
            hpBgRect.sizeDelta = new Vector2(-20f, 28f);
            hpBgRect.anchoredPosition = new Vector2(0f, -16f);

            TextMeshProUGUI hpText = CreateText(hpSectionRect, "BossHpText", "0 / 0", 16f, TextAlignmentOptions.Center);
            RectTransform hpTextRect = (RectTransform)hpText.transform;
            hpTextRect.anchorMin = new Vector2(0f, 1f);
            hpTextRect.anchorMax = new Vector2(1f, 1f);
            hpTextRect.pivot = new Vector2(0.5f, 1f);
            hpTextRect.sizeDelta = new Vector2(0f, 24f);
            hpTextRect.anchoredPosition = new Vector2(0f, -50f);

            TextMeshProUGUI runsText = CreateText(hpSectionRect, "WorldBossRunsText", "Runs: 0", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform runsTextRect = (RectTransform)runsText.transform;
            runsTextRect.anchorMin = new Vector2(0f, 0f);
            runsTextRect.anchorMax = new Vector2(0.5f, 0f);
            runsTextRect.pivot = new Vector2(0f, 0f);
            runsTextRect.sizeDelta = new Vector2(0f, 30f);
            runsTextRect.anchoredPosition = new Vector2(14f, 10f);

            Button attackButton = CreateButton(hpSectionRect, "WorldBossAttackButton", "Attack", out TextMeshProUGUI _);
            RectTransform attackRect = (RectTransform)attackButton.transform;
            attackRect.anchorMin = new Vector2(0.5f, 0f);
            attackRect.anchorMax = new Vector2(1f, 0f);
            attackRect.pivot = new Vector2(1f, 0f);
            attackRect.sizeDelta = new Vector2(-14f, 40f);
            attackRect.anchoredPosition = new Vector2(-14f, 10f);

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
            binder.SoundEngine = sfxEngine;

            GameObject leaderboardSectionObject = new GameObject("TopPlayersSection", typeof(RectTransform));
            leaderboardSectionObject.transform.SetParent(contentAreaRect, false);
            RectTransform leaderboardSectionRect = (RectTransform)leaderboardSectionObject.transform;
            leaderboardSectionRect.anchorMin = Vector2.zero;
            leaderboardSectionRect.anchorMax = new Vector2(1f, 0.6f);
            leaderboardSectionRect.offsetMin = Vector2.zero;
            leaderboardSectionRect.offsetMax = Vector2.zero;

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
        private static (GameObject panel, Button logOffButton) BuildSettingsWindow(Transform canvasTransform)
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

            return (windowObject, logOffButton);
        }

        // ------------------------------------------------------------
        // Honest static placeholders - Friends, Statistics, and Login Bonus
        // have no corresponding engine/network code anywhere server-side
        // (confirmed via project-wide search), so unlike every other
        // screen in this file they are not wired to any real cache; they
        // are plain shells reachable from the hamburger menu, ready for
        // real content once that server-side support exists.
        // ------------------------------------------------------------
        private static GameObject BuildPlaceholderWindow(Transform canvasTransform, string windowName, string title, string message)
        {
            GameObject windowObject = BuildSimpleListWindowShell(windowName, canvasTransform, title, out RectTransform contentAreaRect, out TextMeshProUGUI _);

            TextMeshProUGUI messageText = CreateText(contentAreaRect, "PlaceholderMessageText", message, 16f, TextAlignmentOptions.Center);
            StretchFull((RectTransform)messageText.transform);

            return windowObject;
        }

        // ------------------------------------------------------------
        // Hamburger sliding menu - folds every screen not represented as
        // one of the 5 map zones. A full-height blocker behind the panel
        // both dims the rest of the screen and closes the menu on an
        // outside click.
        // ------------------------------------------------------------
        private static (GameObject blocker, UiHamburgerMenuPanel component, Button[] menuButtons) BuildHamburgerPanel(Transform canvasTransform, string[] labels)
        {
            GameObject blockerObject = new GameObject("HamburgerBlocker", typeof(RectTransform));
            blockerObject.transform.SetParent(canvasTransform, false);
            StretchFull((RectTransform)blockerObject.transform);
            Image blockerImage = blockerObject.AddComponent<Image>();
            blockerImage.color = new Color(0f, 0f, 0f, 0.5f);
            Button blockerButton = blockerObject.AddComponent<Button>();
            blockerButton.targetGraphic = blockerImage;

            GameObject panelObject = new GameObject("HamburgerPanel", typeof(RectTransform));
            panelObject.transform.SetParent(canvasTransform, false);
            RectTransform panelRect = (RectTransform)panelObject.transform;
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 0.5f);
            panelRect.sizeDelta = new Vector2(360f, 0f);
            panelRect.anchoredPosition = Vector2.zero;
            panelObject.AddComponent<Image>().color = new Color(0.08f, 0.08f, 0.1f, 0.98f);

            (ScrollRect _, RectTransform content) = ChatSceneBuilder.BuildScrollView(panelRect);

            Button[] menuButtons = new Button[labels.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                Button button = CreateButton(content, "MenuButton_" + labels[i], labels[i], out TextMeshProUGUI _);
                LayoutElement buttonLayout = button.gameObject.AddComponent<LayoutElement>();
                buttonLayout.preferredHeight = 50f;
                menuButtons[i] = button;
            }

            UiHamburgerMenuPanel hamburgerComponent = panelObject.AddComponent<UiHamburgerMenuPanel>();
            hamburgerComponent.PanelRect = panelRect;
            hamburgerComponent.Blocker = blockerObject;
            hamburgerComponent.HiddenPositionX = -380f;
            hamburgerComponent.ShownPositionX = 0f;

            UnityEditor.Events.UnityEventTools.AddPersistentListener(blockerButton.onClick, hamburgerComponent.Close);

            return (blockerObject, hamburgerComponent, menuButtons);
        }

        // ------------------------------------------------------------
        // Persistent overlay bars - top-left Hamburger/Map buttons,
        // top-right real Gold/Gems currency, bottom Season Pass banner.
        // Stay visible across every screen (map hub, sub-panels, hamburger
        // windows alike) per the map-hub spec's UI persistence
        // requirement.
        // ------------------------------------------------------------
        private static (Button hamburgerToggleButton, Button homeButton, Button battlePassBannerButton) BuildPersistentBars(Transform canvasTransform, VisualSyncProxy syncProxy)
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

            TextMeshProUGUI goldText = CreateStatRow(currencyPanelObject.transform, "Gold: 0");
            TextMeshProUGUI gemsText = CreateStatRow(currencyPanelObject.transform, "Gems: 0");

            UiCurrencyDisplay currencyDisplay = currencyPanelObject.AddComponent<UiCurrencyDisplay>();
            currencyDisplay.SyncProxy = syncProxy;
            currencyDisplay.GoldText = goldText;
            currencyDisplay.GemsText = gemsText;

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
        // Chat overlay - persistent, semi-transparent, minimizable
        // bottom-left corner window. Reuses ChatSceneBuilder.BuildChatWindow
        // unmodified, then adds a header bar (title + minimize toggle) and
        // reparents the 3 existing children (Scroll View, MessageInputField,
        // SendButton - in that exact creation order) under a new
        // ExpandedContent container the toggle can hide as one unit.
        // ------------------------------------------------------------
        private static GameObject BuildChatOverlay(Transform canvasTransform, GameObject chatRowPrefabAsset)
        {
            GameObject chatWindowObject = ChatSceneBuilder.BuildChatWindow(canvasTransform, chatRowPrefabAsset);

            RectTransform windowRect = (RectTransform)chatWindowObject.transform;
            windowRect.anchorMin = new Vector2(0f, 0f);
            windowRect.anchorMax = new Vector2(0.34f, 0.38f);
            windowRect.offsetMin = new Vector2(12f, 78f);
            windowRect.offsetMax = new Vector2(-12f, 0f);

            Image background = chatWindowObject.AddComponent<Image>();
            background.color = new Color(0.05f, 0.05f, 0.08f, 0.55f);

            Transform scrollViewTransform = chatWindowObject.transform.GetChild(0);
            Transform inputFieldTransform = chatWindowObject.transform.GetChild(1);
            Transform sendButtonTransform = chatWindowObject.transform.GetChild(2);

            GameObject expandedContentObject = new GameObject("ExpandedContent", typeof(RectTransform));
            expandedContentObject.transform.SetParent(chatWindowObject.transform, false);
            RectTransform expandedContentRect = (RectTransform)expandedContentObject.transform;
            expandedContentRect.anchorMin = Vector2.zero;
            expandedContentRect.anchorMax = Vector2.one;
            expandedContentRect.offsetMin = Vector2.zero;
            expandedContentRect.offsetMax = new Vector2(0f, -34f);

            scrollViewTransform.SetParent(expandedContentRect, false);
            inputFieldTransform.SetParent(expandedContentRect, false);
            sendButtonTransform.SetParent(expandedContentRect, false);

            GameObject headerBarObject = new GameObject("HeaderBar", typeof(RectTransform));
            headerBarObject.transform.SetParent(chatWindowObject.transform, false);
            RectTransform headerBarRect = (RectTransform)headerBarObject.transform;
            headerBarRect.anchorMin = new Vector2(0f, 1f);
            headerBarRect.anchorMax = new Vector2(1f, 1f);
            headerBarRect.pivot = new Vector2(0.5f, 1f);
            headerBarRect.sizeDelta = new Vector2(0f, 30f);
            headerBarRect.anchoredPosition = Vector2.zero;
            headerBarObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

            TextMeshProUGUI headerLabel = CreateText(headerBarRect, "ChatHeaderLabel", "Chat", 14f, TextAlignmentOptions.MidlineLeft);
            RectTransform headerLabelRect = (RectTransform)headerLabel.transform;
            headerLabelRect.anchorMin = Vector2.zero;
            headerLabelRect.anchorMax = Vector2.one;
            headerLabelRect.offsetMin = new Vector2(10f, 0f);
            headerLabelRect.offsetMax = new Vector2(-40f, 0f);

            Button minimizeButton = CreateButton(headerBarRect, "MinimizeToggleButton", "-", out TextMeshProUGUI minimizeLabel);
            RectTransform minimizeRect = (RectTransform)minimizeButton.transform;
            minimizeRect.anchorMin = new Vector2(1f, 0f);
            minimizeRect.anchorMax = new Vector2(1f, 1f);
            minimizeRect.pivot = new Vector2(1f, 0.5f);
            minimizeRect.sizeDelta = new Vector2(32f, 0f);
            minimizeRect.anchoredPosition = Vector2.zero;

            UiChatMinimizePanel minimizePanel = chatWindowObject.AddComponent<UiChatMinimizePanel>();
            minimizePanel.ExpandedContent = expandedContentObject;
            minimizePanel.MinimizeToggleButton = minimizeButton;
            minimizePanel.ToggleButtonLabel = minimizeLabel;

            return chatWindowObject;
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
