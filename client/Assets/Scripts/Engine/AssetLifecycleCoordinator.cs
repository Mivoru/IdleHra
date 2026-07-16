using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace FolkIdle.Client.Engine
{
    // Modul: Full-Stack Production Hardening Phase 3, Part 6. This class
    // previously also carried an Acquire(string)/Release(string) refcounting
    // API (NativeHashMap<int,int> _assetReferenceCounts, a Queue<int>
    // _evacuationLog, a Dictionary<int,string> _hashToKeyMap, and an
    // Update() polling block that flushed them on a 5-second timer) that
    // was never called from anywhere in the codebase - only
    // LoadMonsterPrefabAsync/ReleaseMonsterPrefab below (the 3D codex
    // model viewer's real, correctly load/release-matched lifecycle) were
    // ever used. That dead path was also structurally broken if it had
    // ever been exercised: Acquire() never actually called
    // Addressables.LoadAssetAsync, yet Release() called
    // Addressables.Release(addressableKey) on a bare string that was never
    // loaded through this path - not a live leak (unreachable), but dead
    // weight left in place risked a future caller mistaking it for a
    // working alternate API. Removed outright rather than fixed, since
    // nothing depends on it.
    public class AssetLifecycleCoordinator : MonoBehaviour
    {
        // Modul 15/19: tracks in-flight/completed Addressables handles for the 3D
        // codex model viewer, keyed by asset key, so every load has an exact matching
        // release and no handle is ever dropped.
        private readonly Dictionary<string, AsyncOperationHandle<GameObject>> _activeHandles =
            new Dictionary<string, AsyncOperationHandle<GameObject>>();

        private void OnDestroy()
        {
            foreach (KeyValuePair<string, AsyncOperationHandle<GameObject>> entry in _activeHandles)
            {
                Addressables.Release(entry.Value);
            }
            _activeHandles.Clear();
        }

        public void LoadMonsterPrefabAsync(string assetKey, Action<GameObject> onComplete)
        {
            if (_activeHandles.TryGetValue(assetKey, out AsyncOperationHandle<GameObject> existingHandle))
            {
                if (existingHandle.IsDone)
                {
                    onComplete?.Invoke(existingHandle.Status == AsyncOperationStatus.Succeeded ? existingHandle.Result : null);
                }
                else
                {
                    existingHandle.Completed += handle =>
                        onComplete?.Invoke(handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null);
                }
                return;
            }

            AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(assetKey);
            _activeHandles[assetKey] = handle;
            handle.Completed += completedHandle =>
                onComplete?.Invoke(completedHandle.Status == AsyncOperationStatus.Succeeded ? completedHandle.Result : null);
        }

        public void ReleaseMonsterPrefab(string assetKey)
        {
            if (_activeHandles.TryGetValue(assetKey, out AsyncOperationHandle<GameObject> handle))
            {
                Addressables.Release(handle);
                _activeHandles.Remove(assetKey);
            }
        }
    }
}
