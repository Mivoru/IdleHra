using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace FolkIdle.Client.Engine
{
    public class AssetLifecycleCoordinator : MonoBehaviour
    {
        private NativeHashMap<int, int> _assetReferenceCounts;
        private Queue<int> _evacuationLog;
        private float _lastUnloadTime;
        private bool _unloadPending;
        private Dictionary<int, string> _hashToKeyMap;

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
