using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace FolkIdle.Client.Engine
{
    public class AssetLifecycleCoordinator : MonoBehaviour
    {
        private NativeHashMap<int, int> _assetReferenceCounts;
        private Queue<int> _evacuationLog;
        private float _lastUnloadTime;
        private bool _unloadPending;
        private Dictionary<int, string> _hashToKeyMap;

        // Modul 15/19: tracks in-flight/completed Addressables handles for the 3D
        // codex model viewer, keyed by asset key, so every load has an exact matching
        // release and no handle is ever dropped.
        private readonly Dictionary<string, AsyncOperationHandle<GameObject>> _activeHandles =
            new Dictionary<string, AsyncOperationHandle<GameObject>>();

        private void Awake()
        {
            _assetReferenceCounts = new NativeHashMap<int, int>(1000, Allocator.Persistent);
            _evacuationLog = new Queue<int>();
            _hashToKeyMap = new Dictionary<int, string>();
            _lastUnloadTime = Time.realtimeSinceStartup;
        }

        private void OnDestroy()
        {
            if (_assetReferenceCounts.IsCreated)
            {
                _assetReferenceCounts.Dispose();
            }

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

        private int GetDeterministicHash(string key)
        {
            // FNV-1a 32-bit hash
            uint hash = 2166136261;
            foreach (char c in key)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }

        public void Acquire(string addressableKey)
        {
            int hash = GetDeterministicHash(addressableKey);

            if (!_hashToKeyMap.ContainsKey(hash))
            {
                _hashToKeyMap[hash] = addressableKey;
            }

            if (_assetReferenceCounts.TryGetValue(hash, out int count))
            {
                _assetReferenceCounts[hash] = count + 1;
            }
            else
            {
                _assetReferenceCounts.Add(hash, 1);
            }
        }

        public void Release(string addressableKey)
        {
            int hash = GetDeterministicHash(addressableKey);

            if (_assetReferenceCounts.TryGetValue(hash, out int count))
            {
                count--;
                if (count <= 0)
                {
                    _assetReferenceCounts.Remove(hash);
                    Addressables.Release(addressableKey);
                    _evacuationLog.Enqueue(hash);
                    _unloadPending = true;
                }
                else
                {
                    _assetReferenceCounts[hash] = count;
                }
            }
        }

        private void Update()
        {
            if (_unloadPending && (Time.realtimeSinceStartup - _lastUnloadTime) >= 5.0f)
            {
                // Clear the evacuation log
                while (_evacuationLog.Count > 0)
                {
                    int hash = _evacuationLog.Dequeue();
                    _hashToKeyMap.Remove(hash);
                }

                Resources.UnloadUnusedAssets();
                
                _lastUnloadTime = Time.realtimeSinceStartup;
                _unloadPending = false;
            }
        }
    }
}
