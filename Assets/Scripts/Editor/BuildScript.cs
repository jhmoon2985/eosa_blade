#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace EosaBlade.Editor
{
    /// <summary>
    /// Patches generated AndroidManifest.xml from userLandscape -> sensorLandscape so rotating the phone
    /// 180 degrees between Landscape Left and Reverse Landscape Right ALWAYS works even if system Auto-Rotate is off.
    /// </summary>
    public class SensorLandscapeManifestPostProcessor : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 100;

        public void OnPostGenerateGradleAndroidProject(string basePath)
        {
            string manifestPath = Path.Combine(basePath, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath)) return;
            string xml = File.ReadAllText(manifestPath);
            if (xml.Contains("android:screenOrientation=\"userLandscape\""))
            {
                xml = xml.Replace("android:screenOrientation=\"userLandscape\"", "android:screenOrientation=\"sensorLandscape\"");
                File.WriteAllText(manifestPath, xml);
                Debug.Log("[EosaBlade] Patched AndroidManifest.xml -> android:screenOrientation=\"sensorLandscape\"");
            }
        }
    }
    /// 경량 APK 빌드: Builds/eosa_blade_v<버전>_lite.apk, 이전 APK는 삭제.
    /// 배치 실행: Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod EosaBlade.Editor.BuildScript.AndroidLite
    public static class BuildScript
    {
        const string OutDir = "Builds";
        const string Prefix = "eosa_blade_v";

        [MenuItem("EosaBlade/Build Android Lite APK")]
        public static void AndroidLite()
        {
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory(OutDir);
            string path = $"{OutDir}/{Prefix}{PlayerSettings.bundleVersion}_lite.apk";

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = path,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });

            var s = report.summary;
            if (s.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[EosaBlade] 빌드 실패: {s.result}, 오류 {s.totalErrors}개");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            foreach (var old in Directory.GetFiles(OutDir, Prefix + "*_lite.apk"))
                if (Path.GetFullPath(old) != Path.GetFullPath(path)) File.Delete(old);

            Debug.Log($"[EosaBlade] 빌드 완료: {path} ({s.totalSize / 1024f / 1024f:F1}MB)");
        }
    }
}
#endif
