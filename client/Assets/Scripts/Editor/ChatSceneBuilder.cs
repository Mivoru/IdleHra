using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using TMPro;
using FolkIdle.Client.UI;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.Editor
{
    // Modul: Unity UI & Network Automation, Part 1/2. Programmatic
    // hierarchy builder for the chat UI - the project has an authoritative
    // zero-allocation UiChatWindow/UiChatMessageRow/WebSocketClient
    // implementation but no physical GameObjects or prefabs anywhere to
    // host them. Editor-only by construction (this whole file lives under
    // Assets/Scripts/Editor/, excluded from every production build target
    // by Unity's own folder convention), so it never ships and never
    // contributes to runtime allocation budgets.
    public static class ChatSceneBuilder
    {
        private const string PrefabDirectory = "Assets/Prefabs/UI";
        private const string RowPrefabPath = PrefabDirectory + "/ChatMessageRow.prefab";

        [MenuItem("FolkIdle/Build Chat Scene and Prefabs")]
        public static void BuildChatSceneAndPrefabs()
        {
            EnsureEventSystem();
            Canvas canvas = BuildCanvas();

            GameObject rowPrefabAsset = BuildAndSaveRowPrefab();
            RegisterRowPrefabAsAddressable(rowPrefabAsset);

            BuildChatWindow(canvas.transform, rowPrefabAsset);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();

            Debug.Log("ChatSceneBuilder: chat scene hierarchy and ChatMessageRow prefab built successfully.");
        }

        // Modul: a scene can only ever have one active EventSystem - reuses
        // an existing one rather than creating a duplicate if this menu
        // item is run more than once against the same scene.
        internal static void EnsureEventSystem()
        {
            // Modul: FindAnyObjectByType, not FindFirstObjectByType - the
            // latter is deprecated in this Unity version (relies on
            // instance ID ordering); a scene has at most one EventSystem,
            // so which one is returned in a multi-match scenario is moot
            // here.
            if (Object.FindAnyObjectByType<EventSystem>() != null)
            {
                return;
            }

            GameObject eventSystemObject = new GameObject("EventSystem");

            // Modul: ProjectSettings has activeInputHandler set to the new
            // Input System exclusively (legacy Input Manager disabled), so
            // the UI event routing module must be InputSystemUIInputModule,
            // not the legacy StandaloneInputModule - the latter silently
            // fails to route any UI events at all under this project's
            // input configuration.
            eventSystemObject.AddComponent<EventSystem>();
            eventSystemObject.AddComponent<InputSystemUIInputModule>();
        }

        internal static Canvas BuildCanvas()
        {
            GameObject canvasObject = new GameObject("Canvas", typeof(RectTransform));
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Modul: Full-Game UI Architecture, Part 4 - portrait tuning.
            // ProjectSettings has AndroidMinSdkVersion configured and
            // autorotate enabled for all four orientations, but this is a
            // portrait-first mobile idle game in practice - reference
            // resolution switched from a 1920x1080 landscape assumption to
            // a 1080x1920 portrait one, and match mode from width/height
            // blend to width-locked (0f). Matching width keeps every
            // panel's horizontal layout (row widths, button widths,
            // side-by-side splits) identical across phones with the same
            // width but different height/notch cutouts - the actual
            // variance between real Android devices is almost entirely in
            // height, not width.
            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0f;

            canvasObject.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        // Modul: Part 1.2/1.3. The main chat panel, its Scroll View
        // (Viewport/Content with VerticalLayoutGroup + ContentSizeFitter
        // for dynamic row listing), and the compose row (TMP_InputField +
        // Send button) at the bottom - every UiChatWindow field with a
        // corresponding physical GameObject in this hierarchy is bound via
        // SerializedObject/FindProperty rather than left for manual
        // Inspector dragging.
        internal static GameObject BuildChatWindow(Transform canvasTransform, GameObject rowPrefabAsset)
        {
            GameObject windowObject = new GameObject("ChatWindow", typeof(RectTransform));
            windowObject.transform.SetParent(canvasTransform, false);
            RectTransform windowRect = (RectTransform)windowObject.transform;
            windowRect.anchorMin = new Vector2(0f, 0f);
            windowRect.anchorMax = new Vector2(0.4f, 0.55f);
            windowRect.offsetMin = new Vector2(20f, 20f);
            windowRect.offsetMax = new Vector2(-20f, -20f);

            UiChatWindow chatWindow = windowObject.AddComponent<UiChatWindow>();

            (ScrollRect scrollRect, RectTransform content) = BuildScrollView(windowRect);
            TMP_InputField inputField = BuildInputField(windowRect);
            Button sendButton = BuildSendButton(windowRect);

            chatWindow.RowPrefabAddressableKey = ChatConstants.RowPrefabAddressableKey;

            SerializedObject chatWindowSerialized = new SerializedObject(chatWindow);
            chatWindowSerialized.FindProperty(nameof(UiChatWindow.ChatScrollRect)).objectReferenceValue = scrollRect;
            chatWindowSerialized.FindProperty(nameof(UiChatWindow.RowContainer)).objectReferenceValue = content;
            chatWindowSerialized.FindProperty(nameof(UiChatWindow.MessageInputField)).objectReferenceValue = inputField;
            chatWindowSerialized.FindProperty(nameof(UiChatWindow.SendButton)).objectReferenceValue = sendButton;
            chatWindowSerialized.ApplyModifiedProperties();

            return windowObject;
        }

        // Modul: Full-Game UI Architecture, Part 1/2. Promoted from
        // private to internal so MainSceneBuilder can reuse the exact same
        // pooled-list scroll view shape (Viewport/Content with
        // VerticalLayoutGroup + ContentSizeFitter) for the Guild roster,
        // Market listings, Market sell candidates, and Bank vault/backpack
        // lists instead of re-implementing this ~50-line hierarchy five
        // more times.
        internal static (ScrollRect scrollRect, RectTransform content) BuildScrollView(RectTransform parent)
        {
            GameObject scrollViewObject = new GameObject("Scroll View", typeof(RectTransform));
            scrollViewObject.transform.SetParent(parent, false);
            RectTransform scrollViewRect = (RectTransform)scrollViewObject.transform;
            scrollViewRect.anchorMin = new Vector2(0f, 0.18f);
            scrollViewRect.anchorMax = new Vector2(1f, 1f);
            scrollViewRect.offsetMin = Vector2.zero;
            scrollViewRect.offsetMax = Vector2.zero;

            scrollViewObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);
            ScrollRect scrollRect = scrollViewObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            GameObject viewportObject = new GameObject("Viewport", typeof(RectTransform));
            viewportObject.transform.SetParent(scrollViewRect, false);
            RectTransform viewportRect = (RectTransform)viewportObject.transform;
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            viewportRect.pivot = new Vector2(0f, 1f);

            viewportObject.AddComponent<Image>().color = Color.clear;
            viewportObject.AddComponent<Mask>().showMaskGraphic = false;

            GameObject contentObject = new GameObject("Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewportRect, false);
            RectTransform contentRect = (RectTransform)contentObject.transform;
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0f, 1f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;

            VerticalLayoutGroup layoutGroup = contentObject.AddComponent<VerticalLayoutGroup>();
            layoutGroup.childControlWidth = true;
            layoutGroup.childControlHeight = false;
            layoutGroup.childForceExpandWidth = true;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.spacing = 2f;

            ContentSizeFitter sizeFitter = contentObject.AddComponent<ContentSizeFitter>();
            sizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;

            return (scrollRect, contentRect);
        }

        private static TMP_InputField BuildInputField(RectTransform parent)
        {
            GameObject inputFieldObject = new GameObject("MessageInputField", typeof(RectTransform));
            inputFieldObject.transform.SetParent(parent, false);
            RectTransform inputFieldRect = (RectTransform)inputFieldObject.transform;
            inputFieldRect.anchorMin = new Vector2(0f, 0f);
            inputFieldRect.anchorMax = new Vector2(0.8f, 0.12f);
            inputFieldRect.offsetMin = Vector2.zero;
            inputFieldRect.offsetMax = Vector2.zero;

            inputFieldObject.AddComponent<Image>().color = Color.white;
            TMP_InputField inputField = inputFieldObject.AddComponent<TMP_InputField>();
            inputField.lineType = TMP_InputField.LineType.SingleLine;

            GameObject textAreaObject = new GameObject("Text Area", typeof(RectTransform));
            textAreaObject.transform.SetParent(inputFieldRect, false);
            RectTransform textAreaRect = (RectTransform)textAreaObject.transform;
            textAreaRect.anchorMin = Vector2.zero;
            textAreaRect.anchorMax = Vector2.one;
            textAreaRect.offsetMin = new Vector2(8f, 4f);
            textAreaRect.offsetMax = new Vector2(-8f, -4f);
            textAreaObject.AddComponent<RectMask2D>();

            GameObject placeholderObject = new GameObject("Placeholder", typeof(RectTransform));
            placeholderObject.transform.SetParent(textAreaRect, false);
            RectTransform placeholderRect = (RectTransform)placeholderObject.transform;
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = Vector2.zero;
            placeholderRect.offsetMax = Vector2.zero;
            TextMeshProUGUI placeholderText = placeholderObject.AddComponent<TextMeshProUGUI>();
            placeholderText.text = "Send a message...";
            placeholderText.fontStyle = FontStyles.Italic;
            placeholderText.color = new Color(0f, 0f, 0f, 0.5f);

            GameObject textObject = new GameObject("Text", typeof(RectTransform));
            textObject.transform.SetParent(textAreaRect, false);
            RectTransform textRect = (RectTransform)textObject.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            TextMeshProUGUI inputText = textObject.AddComponent<TextMeshProUGUI>();
            inputText.color = Color.black;

            inputField.textViewport = textAreaRect;
            inputField.textComponent = inputText;
            inputField.placeholder = placeholderText;

            return inputField;
        }

        private static Button BuildSendButton(RectTransform parent)
        {
            GameObject buttonObject = new GameObject("SendButton", typeof(RectTransform));
            buttonObject.transform.SetParent(parent, false);
            RectTransform buttonRect = (RectTransform)buttonObject.transform;
            buttonRect.anchorMin = new Vector2(0.82f, 0f);
            buttonRect.anchorMax = new Vector2(1f, 0.12f);
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            buttonObject.AddComponent<Image>().color = new Color(0.2f, 0.5f, 0.9f, 1f);
            Button button = buttonObject.AddComponent<Button>();

            GameObject labelObject = new GameObject("Text", typeof(RectTransform));
            labelObject.transform.SetParent(buttonRect, false);
            RectTransform labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            TextMeshProUGUI label = labelObject.AddComponent<TextMeshProUGUI>();
            label.text = "Send";
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;

            return button;
        }

        // Modul: Part 2. Builds a single staging "ChatMessageRow" instance
        // in the currently active scene purely as scaffolding to bind and
        // save - PrefabUtility.SaveAsPrefabAsset needs a real scene
        // instance as its source, and this exact instance is torn back
        // down at the end so the scene is left containing only the
        // permanent ChatWindow hierarchy, never a leftover template.
        internal static GameObject BuildAndSaveRowPrefab()
        {
            if (!AssetDatabase.IsValidFolder(PrefabDirectory))
            {
                EnsureFolder("Assets/Prefabs");
                EnsureFolder(PrefabDirectory);
            }

            GameObject rowObject = new GameObject("ChatMessageRow", typeof(RectTransform));
            RectTransform rowRect = (RectTransform)rowObject.transform;
            rowRect.sizeDelta = new Vector2(0f, 30f);

            TextMeshProUGUI rowText = rowObject.AddComponent<TextMeshProUGUI>();
            rowText.fontSize = 18f;
            rowText.color = Color.white;
            rowText.alignment = TextAlignmentOptions.MidlineLeft;

            UiChatMessageRow rowComponent = rowObject.AddComponent<UiChatMessageRow>();

            // Modul: click-to-action target - HandleNameClicked exists on
            // UiChatMessageRow precisely for this, but nothing in the
            // codebase previously had a physical Button + OnClick wired to
            // reach it. Without this the entire click-to-action chain
            // built in the social layer task is unreachable dead code at
            // runtime.
            Button rowButton = rowObject.AddComponent<Button>();
            rowButton.targetGraphic = rowText;
            UnityEditor.Events.UnityEventTools.AddPersistentListener(rowButton.onClick, rowComponent.HandleNameClicked);

            SerializedObject rowSerialized = new SerializedObject(rowComponent);
            rowSerialized.FindProperty(nameof(UiChatMessageRow.RowText)).objectReferenceValue = rowText;
            rowSerialized.ApplyModifiedProperties();

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAsset(rowObject, RowPrefabPath, out bool saveSuccess);
            if (!saveSuccess)
            {
                Debug.LogError("ChatSceneBuilder: failed to save ChatMessageRow prefab asset.");
            }

            // Modul: destroys the temporary staging instance, not the
            // returned prefab asset - SaveAsPrefabAsset persists an
            // independent copy to disk, so this scene object's job is done
            // the moment that copy exists.
            Object.DestroyImmediate(rowObject);

            return prefabAsset;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = System.IO.Path.GetDirectoryName(path)!.Replace('\\', '/');
            string folderName = System.IO.Path.GetFileName(path);
            AssetDatabase.CreateFolder(parent, folderName);
        }

        // Modul: Part 2.3. UiChatWindow.RowPrefabAddressableKey is a
        // string key, not a direct UnityEngine.Object reference field -
        // the row template is resolved at runtime through
        // AssetManager.LoadAsync<GameObject>(RowPrefabAddressableKey, ...)
        // (see UiChatWindow.Awake), which is Addressables-backed by
        // design so the row template can ship and update over-the-air
        // independently of the client build. Injecting "the newly
        // generated prefab asset into the serialized reference slot" for
        // this field therefore means registering the prefab as an
        // Addressable asset under that exact key, not assigning an
        // object reference - there is no such field to assign.
        internal static void RegisterRowPrefabAsAddressable(GameObject rowPrefabAsset)
        {
            if (rowPrefabAsset == null)
            {
                return;
            }

            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            }

            if (settings == null)
            {
                Debug.LogError("ChatSceneBuilder: could not create or find AddressableAssetSettings - ChatMessageRow prefab was saved but not registered as Addressable.");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(rowPrefabAsset);
            string guid = AssetDatabase.AssetPathToGUID(assetPath);

            AddressableAssetGroup group = settings.DefaultGroup;
            AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group);
            entry.SetAddress(ChatConstants.RowPrefabAddressableKey);

            EditorUtility.SetDirty(settings);
        }

        // Modul: single source of truth for the address string shared
        // between BuildChatWindow (assigns UiChatWindow.RowPrefabAddressableKey)
        // and RegisterRowPrefabAsAddressable (assigns the Addressables
        // entry's address) - the two must always match exactly or
        // AssetManager.LoadAsync resolves nothing at runtime.
        internal static class ChatConstants
        {
            public const string RowPrefabAddressableKey = "UiChatMessageRow";
        }
    }
}
