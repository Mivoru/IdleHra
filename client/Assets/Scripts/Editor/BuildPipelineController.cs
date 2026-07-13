using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace FolkIdle.Client.Editor
{
    public static class BuildPipelineController
    {
        public static void ExecuteAndroidReleaseBuild()
        {
            try
            {
                Console.WriteLine("[BuildPipeline] Starting Android Release Build...");

                // Parse command line arguments for output path
                string[] args = Environment.GetCommandLineArgs();
                string outputPath = "Builds/Android/FolkIdleRelease.apk"; // Default

                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-outputPath" && i + 1 < args.Length)
                    {
                        outputPath = args[i + 1];
                    }
                }

                Console.WriteLine($"[BuildPipeline] Output Path set to: {outputPath}");

                // Hard-locked constraints
                EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

                string[] scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();

                if (scenes.Length == 0)
                {
                    Console.WriteLine("[BuildPipeline] Error: No enabled scenes found in build settings.");
                    EditorApplication.Exit(1);
                    return;
                }

                BuildPlayerOptions buildPlayerOptions = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                };

                BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
                BuildSummary summary = report.summary;

                if (summary.result == BuildResult.Succeeded)
                {
                    Console.WriteLine($"[BuildPipeline] Build succeeded: {summary.totalSize} bytes.");
                    EditorApplication.Exit(0);
                }
                else if (summary.result == BuildResult.Failed)
                {
                    Console.WriteLine("[BuildPipeline] Build failed.");
                    EditorApplication.Exit(1);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BuildPipeline] Fatal Error: {ex}");
                EditorApplication.Exit(1);
            }
        }
    }
}
