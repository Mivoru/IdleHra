using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;

namespace FolkIdle.Client.UI
{
    // Modul: UI audit follow-up. Compact 3D preview viewport for the
    // selected Forge recipe/equipment item - the structural gap
    // UiForgeCraftingPanel/UiEquipmentRerollPanel's own header comments
    // named ("there is no 3D preview viewport for Forge items yet"). Mirrors
    // UiCodex3DViewer's render-texture approach, scaled down (this sits in a
    // detail-panel corner, not a dedicated half-screen viewer) and not a
    // static singleton - Craft and Reroll each get their own independent
    // instance/layer/rig rather than sharing one, since both rigs live under
    // Managers (like CodexPreviewRig) and keep rendering even while their
    // own tab is inactive; a shared culling layer would let one leak into
    // the other's texture. AssetLifecycleCoordinator's
    // LoadMonsterPrefabAsync/ReleaseMonsterPrefab are reused as-is despite
    // the "Monster" name - the implementation is a generic Addressables
    // key->GameObject cache with no monster-specific behavior.
    public class UiForgeItemViewer : MonoBehaviour
    {
        private const int RenderTextureSize = 160;

        private static readonly Vector3 ModelLocalPosition = Vector3.zero;
        private static readonly Vector3 ModelLocalScale = Vector3.one;

        public AssetLifecycleCoordinator AssetCoordinator;
        public string PreviewLayerName = "UI_3D_Preview";

        [Header("Forge Item Viewer")]
        public Camera PreviewCamera;
        public RawImage PreviewImage;
        public Transform ModelAnchor;

        private RenderTexture _previewRenderTexture;
        private GameObject _activeInstance;
        private string _activeAssetKey;
        private int _previewLayer;

        private void Awake()
        {
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

        private void OnDestroy()
        {
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

        public void ShowItem(string assetKey)
        {
            if (string.IsNullOrEmpty(assetKey) || AssetCoordinator == null)
            {
                ClearActiveInstance();
                return;
            }

            if (assetKey == _activeAssetKey && _activeInstance != null)
            {
                return;
            }

            ClearActiveInstance();

            _activeAssetKey = assetKey;
            AssetCoordinator.LoadMonsterPrefabAsync(assetKey, prefab => OnItemPrefabLoaded(assetKey, prefab));
        }

        public void Clear()
        {
            ClearActiveInstance();
        }

        private void OnItemPrefabLoaded(string requestedAssetKey, GameObject prefab)
        {
            // The viewer may have moved on to a different (or no) item while this
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
