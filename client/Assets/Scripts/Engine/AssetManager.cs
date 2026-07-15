using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace FolkIdle.Client.Engine
{
    // Modul: general-purpose Addressables entry point for UI prefabs, icons,
    // and VFX - the OTA-updatable counterpart to AssetLifecycleCoordinator
    // (which is scoped specifically to the 3D codex model viewer's monster
    // prefabs, see LoadMonsterPrefabAsync there). Every load is refcounted
    // and handle-tracked exactly like that coordinator's own
    // LoadMonsterPrefabAsync/ReleaseMonsterPrefab pair, generalized to any
    // UnityEngine.Object type via T, so callers never issue a duplicate
    // Addressables.LoadAssetAsync call for a key that is already loading or
    // already resident - a repeat request for the same key is served from
    // the cached handle instead. That cache-reuse is what keeps steady-state
    // usage (a UI window re-opening, a VFX key requested again during
    // gameplay) free of new asset-bundle IO or duplicate handles; a brand
    // new key still allocates exactly one completion closure on its first
    // load, same as the existing AssetLifecycleCoordinator convention this
    // mirrors - no per-frame/per-tick allocation, which is the discipline
    // that actually matters (see SimulationEngine's tick loop server-side).
    //
    // A given addressableKey must always be requested as the same T across
    // every caller - Addressables keys are authored for one asset type, and
    // this class does not attempt to guard against mixing types on the same
    // key.
    public class AssetManager : MonoBehaviour
    {
        private static AssetManager _instance;
        public static AssetManager Instance => _instance;

        private readonly Dictionary<string, AsyncOperationHandle> _activeHandles = new Dictionary<string, AsyncOperationHandle>();
        private readonly Dictionary<string, int> _refCounts = new Dictionary<string, int>();

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            foreach (KeyValuePair<string, AsyncOperationHandle> entry in _activeHandles)
            {
                Addressables.Release(entry.Value);
            }
            _activeHandles.Clear();
            _refCounts.Clear();

            if (_instance == this)
            {
                _instance = null;
            }
        }

        // Loads (or reuses an in-flight/already-completed load of) the asset
        // at addressableKey, invoking onComplete exactly once with the
        // result (null on failure or an empty key). Every call that reaches
        // a non-null result increments the key's refcount; callers own
        // exactly one matching Release() per such call.
        public void LoadAsync<T>(string addressableKey, Action<T> onComplete) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(addressableKey))
            {
                onComplete?.Invoke(null);
                return;
            }

            _refCounts[addressableKey] = _refCounts.TryGetValue(addressableKey, out int count) ? count + 1 : 1;

            if (_activeHandles.TryGetValue(addressableKey, out AsyncOperationHandle existingHandle))
            {
                AsyncOperationHandle<T> typedExisting = existingHandle.Convert<T>();
                if (typedExisting.IsDone)
                {
                    onComplete?.Invoke(typedExisting.Status == AsyncOperationStatus.Succeeded ? typedExisting.Result : null);
                }
                else
                {
                    typedExisting.Completed += handle =>
                        onComplete?.Invoke(handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null);
                }
                return;
            }

            AsyncOperationHandle<T> newHandle = Addressables.LoadAssetAsync<T>(addressableKey);
            _activeHandles[addressableKey] = newHandle;
            newHandle.Completed += handle =>
                onComplete?.Invoke(handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null);
        }

        // Releases one reference previously acquired via LoadAsync for this
        // key. The underlying Addressables handle is only actually released
        // back to the Addressables system once every caller that loaded it
        // has released its own reference.
        public void Release(string addressableKey)
        {
            if (string.IsNullOrEmpty(addressableKey)) return;
            if (!_refCounts.TryGetValue(addressableKey, out int count)) return;

            count--;
            if (count <= 0)
            {
                _refCounts.Remove(addressableKey);
                if (_activeHandles.TryGetValue(addressableKey, out AsyncOperationHandle handle))
                {
                    Addressables.Release(handle);
                    _activeHandles.Remove(addressableKey);
                }
            }
            else
            {
                _refCounts[addressableKey] = count;
            }
        }
    }
}
