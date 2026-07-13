using System.Collections.Concurrent;
using UnityEngine;

namespace FolkIdle.Client.UI
{
    public class UIComponentPool<T> where T : Component
    {
        private readonly ConcurrentQueue<T> _pool = new ConcurrentQueue<T>();
        private readonly T _prefab;
        private readonly Transform _defaultParent;

        public UIComponentPool(T prefab, Transform defaultParent, int initialCapacity = 0)
        {
            _prefab = prefab;
            _defaultParent = defaultParent;

            for (int i = 0; i < initialCapacity; i++)
            {
                var instance = CreateInstance();
                instance.gameObject.SetActive(false);
                _pool.Enqueue(instance);
            }
        }

        public T Spawn()
        {
            if (_pool.TryDequeue(out T instance))
            {
                instance.gameObject.SetActive(true);
                return instance;
            }

            var newInstance = CreateInstance();
            newInstance.gameObject.SetActive(true);
            return newInstance;
        }

        public void Despawn(T instance)
        {
            if (instance != null)
            {
                // Toggle visibility strictly via gameObject.SetActive(false) 
                // without invoking parent reassignment to block layout reflow fragmentation
                instance.gameObject.SetActive(false);
                _pool.Enqueue(instance);
            }
        }

        private T CreateInstance()
        {
            return Object.Instantiate(_prefab, _defaultParent);
        }
    }
}
