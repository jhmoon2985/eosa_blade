using UnityEngine;

namespace EosaBlade
{
    /// <summary>
    /// Enforces 60fps target, mobile thermal-friendly settings, and Landscape orientation lock on startup.
    /// </summary>
    public static class PerformanceBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyMobilePerformanceDefaults()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

#if UNITY_ANDROID && !UNITY_EDITOR
            // CRITICAL: Do NOT set Screen.orientation = ScreenOrientation.AutoRotation on Android!
            // When Android's system Auto-Rotate is OFF (USER_ROTATION_LOCKED), Unity's Screen.orientation setter
            // overrides AndroidManifest's sensorLandscape (6) with SCREEN_ORIENTATION_LANDSCAPE (0 = one-side lock!).
            ForceAndroidSensorLandscape();
#else
            // Lock orientation to Landscape Left / Right only
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.orientation = ScreenOrientation.AutoRotation;
#endif

            // Avoid redundant transform-physics syncs on mobile CPU
            Physics.autoSyncTransforms = false;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject _cachedActivity;
        private static AndroidJavaRunnable _sensorLandscapeRunnable;
#endif

        public static void ForceAndroidSensorLandscape()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (_cachedActivity == null)
                {
                    using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    {
                        _cachedActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    }
                }
                if (_cachedActivity != null)
                {
                    if (_sensorLandscapeRunnable == null)
                    {
                        // 6 == ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE (flips 180° via sensor even when system Auto-Rotate is OFF!)
                        _sensorLandscapeRunnable = new AndroidJavaRunnable(() =>
                        {
                            try { _cachedActivity.Call("setRequestedOrientation", 6); } catch { }
                        });
                    }
                    _cachedActivity.Call("runOnUiThread", _sensorLandscapeRunnable);
                }
            }
            catch
            {
                // Ignore if called outside Android activity context
            }
#endif
        }
    }
}
