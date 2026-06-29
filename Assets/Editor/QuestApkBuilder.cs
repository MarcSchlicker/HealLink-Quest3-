#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

public static class QuestApkBuilder
{
    private const string DefaultOutputPath = "Builds/Quest3/MedXRQuest3.apk";

    [MenuItem("Build/Build Quest 3 APK")]
    public static void BuildQuest3ApkFromMenu()
    {
        BuildQuest3Apk();
    }

    public static void BuildQuest3Apk()
    {
        string outputPath = Path.GetFullPath(GetCommandLineArgument("-apkPath", DefaultOutputPath));
        string outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        EditorUserBuildSettings.buildAppBundle = false;
        PlayerSettings.Android.useCustomKeystore = false;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" };
        }

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException("Quest APK build failed with result " + summary.result + ". See the Unity Editor log for details.");
        }

        UnityEngine.Debug.Log("Quest APK build succeeded: " + outputPath);
    }

    private static string GetCommandLineArgument(string name, string fallback)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return fallback;
    }
}
#endif
