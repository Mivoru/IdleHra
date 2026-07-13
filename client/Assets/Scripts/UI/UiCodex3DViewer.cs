using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul 15/19: asynchronous 3D preview viewport for the Monster Codex. Renders a
    // single loaded monster prefab into an isolated Render Texture on its own
    // Sub-Canvas (Codex3DSubCanvas) so the preview never shares a mesh rebuild with
    // any 2D HUD canvas. Every load requested through AssetLifecycleCoordinator is
    // matched by exactly one release, either when switching monsters or on close.
    public class UiCodex3DViewer : MonoBehaviour
    {
        private const string PreviewLayerName = "UI_3D_Preview";
        private const int RenderTextureSize = 512;

        private static readonly Vector3 ModelLocalPosition = Vector3.zero;
        private static readonly Vector3 ModelLocalScale = Vector3.one;

        public static UiCodex3DViewer Instance { get; private set; }

        public AssetLifecycleCoordinator AssetCoordinator;

        [Header("Codex 3D Viewer - Canvas Isolation")]
        public Canvas Codex3DSubCanvas;
        public RectTransform ViewerPanelRect;

        [Header("Codex 3D Viewer")]
        public Camera PreviewCamera;
        public RawImage PreviewImage;
        public Transform ModelAnchor;

        private RenderTexture _previewRenderTexture;
        private GameObject _activeInstance;
        private string _activeAssetKey;
        private int _previewLayer;

        private void Awake()
        {
            Instance = this;

            if (ViewerPanelRect != null)
            {
                LayoutGroup layoutGroup = ViewerPanelRect.GetComponent<LayoutGroup>();
                if (layoutGroup != null)
                {
                    Destroy(layoutGroup);
                }

                ViewerPanelRect.anchorMin = new Vector2(0.5f, 0.5f);
                ViewerPanelRect.anchorMax = new Vector2(0.5f, 0.5f);
                ViewerPanelRect.pivot = new Vector2(0.5f, 0.5f);
                ViewerPanelRect.anchoredPosition = Vector2.zero;
            }

            _previewLayer = LayerMask.NameToLayer(PreviewLayerName);

            _previewRenderTexture = new RenderTexture(RenderTextureSize, RenderTextureSize, 16, RenderTextureFormat.ARGB32);
            _previewRenderTexture.Create();

            if (PreviewCamera != null)
            {
                PreviewCamera.orthographic = true;
                PreviewCamera.clearFlags = CameraClearFlags.SolidColor;
                PreviewCamera.backgroundColor = Color.black;
                PreviewCamera.cullingMask = _previewLayer >= 0 ? (1 << _previewLayer) : 0;
                PreviewCamera.targetTexture = _previewRenderTexture;
            }

            if (PreviewImage != null)
            {
                PreviewImage.texture = _previewRenderTexture;
            }

            ClearViewportToBlack();
        }

        private void Update()
        {
            if (_activeAssetKey != null && Input.GetKeyDown(KeyCode.Escape))
            {
                CloseViewer();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            ClearActiveInstance();

            if (_previewRenderTexture != null)
            {
                if (PreviewCamera != null)
                {
                    PreviewCamera.targetTexture = null;
                }

                _previewRenderTexture.Release();
                _previewRenderTexture = null;
            }
        }

        public void ShowMonster(string assetKey)
        {
            if (string.IsNullOrEmpty(assetKey) || AssetCoordinator == null)
            {
                return;
            }

            ClearActiveInstance();

            _activeAssetKey = assetKey;
            AssetCoordinator.LoadMonsterPrefabAsync(assetKey, prefab => OnMonsterPrefabLoaded(assetKey, prefab));
        }

        public void CloseViewer()
        {
            ClearActiveInstance();
        }

        private void OnMonsterPrefabLoaded(string requestedAssetKey, GameObject prefab)
        {
            // The viewer may have moved on to a different (or no) monster while this
            // load was in flight; stale results are dropped instead of instantiated.
            if (prefab == null || requestedAssetKey != _activeAssetKey)
            {
                return;
            }

            _activeInstance = Instantiate(prefab, ModelAnchor);
            _activeInstance.transform.localPosition = ModelLocalPosition;
            _activeInstance.transform.localScale = ModelLocalScale;

            if (_previewLayer >= 0)
            {
                SetLayerRecursively(_activeInstance.transform, _previewLayer);
            }
        }

        private void ClearActiveInstance()
        {
            if (_activeInstance != null)
            {
                Destroy(_activeInstance);
                _activeInstance = null;
            }

            if (_activeAssetKey != null && AssetCoordinator != null)
            {
                AssetCoordinator.ReleaseMonsterPrefab(_activeAssetKey);
            }
            _activeAssetKey = null;

            ClearViewportToBlack();
        }

        private void ClearViewportToBlack()
        {
            if (_previewRenderTexture == null)
            {
                return;
            }

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = _previewRenderTexture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = previousActive;
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            int childCount = root.childCount;
            for (int i = 0; i < childCount; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }
    }
}
