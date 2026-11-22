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

            // Lock orientation to Landscape Left / Right only
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.orientation = ScreenOrientation.AutoRotation;

            // Avoid redundant transform-physics syncs on mobile CPU
            Physics.autoSyncTransforms = false;
        }
    }
}
