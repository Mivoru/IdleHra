using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

namespace FolkIdle.Client.Engine
{
    public class ThermalOptimizationBroker : MonoBehaviour
    {
        public Canvas StandardGeometryCanvas;
        public Image BlackAlphaOverlay;

        private float _timeSinceLastPoll = 0f;
        private int _currentThermalStatus = 0;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int GetProcessThermalState();
#endif

        private void Start()
        {
            if (BlackAlphaOverlay != null)
            {
                BlackAlphaOverlay.color = new Color(0, 0, 0, 0);
                BlackAlphaOverlay.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            _timeSinceLastPoll += Time.deltaTime;
            if (_timeSinceLastPoll >= 2.0f)
            {
                _timeSinceLastPoll = 0f;
                PollThermalStatus();
                ApplyThrottling();
            }
        }

        private void PollThermalStatus()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (AndroidJavaObject powerManager = currentActivity.Call<AndroidJavaObject>("getSystemService", "power"))
                {
                    if (powerManager != null)
                    {
                        _currentThermalStatus = powerManager.Call<int>("getCurrentThermalStatus");
                    }
                }
            }
            catch (Exception)
            {
                _currentThermalStatus = 0;
            }
#elif UNITY_IOS && !UNITY_EDITOR
            try
            {
                int state = GetProcessThermalState();
                // Map NSProcessInfoThermalState to similar 0-5 scale
                // 0: Nominal, 1: Fair, 2: Serious, 3: Critical
                if (state == 0) _currentThermalStatus = 0;
                else if (state == 1) _currentThermalStatus = 2;
                else if (state == 2) _currentThermalStatus = 3;
                else if (state == 3) _currentThermalStatus = 5;
            }
            catch (Exception)
            {
                _currentThermalStatus = 0;
            }
#else
            _currentThermalStatus = 0;
#endif
        }

        private void ApplyThrottling()
        {
            if (_currentThermalStatus <= 1)
            {
                // Optimal/Light
                Application.targetFrameRate = 60;
                RestoreVolatiles();
                RestoreGeometry();
            }
            else if (_currentThermalStatus == 2)
            {
                // Moderate
                Application.targetFrameRate = 30;
                RestoreVolatiles();
                RestoreGeometry();
            }
            else if (_currentThermalStatus == 3 || _currentThermalStatus == 4)
            {
                // Severe/Critical
                Application.targetFrameRate = 15;
                DisableVolatiles();
                RestoreGeometry();
            }
            else if (_currentThermalStatus >= 5)
            {
                // Emergency
                Application.targetFrameRate = 5;
                DisableVolatiles();
                DisableGeometry();
            }
        }

        private void DisableVolatiles()
        {
            var emitters = FindObjectsByType<ParticleSystem>(FindObjectsInactive.Exclude);
            foreach (var emitter in emitters)
            {
                if (emitter.isPlaying) emitter.Stop();
            }
            
            // Assuming glow shaders are disabled via global shader property or material switch
            Shader.SetGlobalFloat("_DisableGlow", 1f);
        }

        private void RestoreVolatiles()
        {
            // We do not auto-restart particles as they are state-dependent, but we allow them to play again
            Shader.SetGlobalFloat("_DisableGlow", 0f);
        }

        private void DisableGeometry()
        {
            if (StandardGeometryCanvas != null)
            {
                StandardGeometryCanvas.enabled = false;
            }

            if (BlackAlphaOverlay != null)
            {
                BlackAlphaOverlay.gameObject.SetActive(true);
                BlackAlphaOverlay.color = new Color(0, 0, 0, 0.8f);
            }
        }

        private void RestoreGeometry()
        {
            if (StandardGeometryCanvas != null)
            {
                StandardGeometryCanvas.enabled = true;
            }

            if (BlackAlphaOverlay != null)
            {
                BlackAlphaOverlay.gameObject.SetActive(false);
            }
        }
    }
}
