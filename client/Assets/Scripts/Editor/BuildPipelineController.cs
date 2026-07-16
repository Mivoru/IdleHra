using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace FolkIdle.Client.Editor
{
    // Modul: CI entry point (invoked via Unity's own
    // -batchmode -executeMethod FolkIdle.Client.Editor.BuildPipelineController.ExecuteAndroidReleaseBuild)
    // for a full release build: Addressables remote content first, then the
    // player binary that ships with the resulting local catalog embedded.
    // Content must build before the player - the player build bakes in
    // whatever catalog/local-bundle state exists at the moment it runs, so
    // building it first would ship a stale or missing catalog.
    public static class BuildPipelineController
    {
        private const string ProductionProfileName = "Production";
        private const string DefaultApkOutputPath = "build_output/client.apk";
        private const string CdnStagingDirectory = "build_output/cdn";

        public static void ExecuteAndroidReleaseBuild()
        {
            try
            {
                Console.WriteLine("[BuildPipeline] Starting Android Release Build...");

                if (!ExecuteAddressablesContentBuild())
                {
                    EditorApplication.Exit(1);
                    return;
                }

                if (!ExecutePlayerBuild())
                {
                    EditorApplication.Exit(1);
                    return;
                }

                Console.WriteLine("[BuildPipeline] Android Release Build completed successfully.");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BuildPipeline] Fatal Error: {ex}");
                EditorApplication.Exit(1);
            }
        }

        // Modul: builds Addressables remote content against the
        // "Production" profile (the CDN host/version metadata
        // AssetManager's CheckForCatalogUpdates/UpdateCatalogs resolve
        // against at runtime - see AssetManager.cs) and stages every bundle
        // belonging to a remote-classified group into build_output/cdn/ for
        // the CI pipeline's separate upload-to-CDN step. Fails loudly
        // (returns false, never throws past this point) rather than
        // falling back to some other profile - building "Production"
        // content against the wrong profile's CDN URLs would silently ship
        // a broken OTA update, which is worse than failing the build.
        private static bool ExecuteAddressablesContentBuild()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Console.WriteLine("[BuildPipeline] Error: no AddressableAssetSettings found in this project - Addressables has not been configured.");
                return false;
            }

            string productionProfileId = settings.profileSettings.GetProfileId(ProductionProfileName);
            if (string.IsNullOrEmpty(productionProfileId))
            {
                Console.WriteLine($"[BuildPipeline] Error: no Addressables profile named '{ProductionProfileName}' exists - create it in the Addressables Profiles window before running a release build.");
                return false;
            }

            settings.activeProfileId = productionProfileId;
            Console.WriteLine($"[BuildPipeline] Active Addressables profile switched to '{ProductionProfileName}'.");

            try
            {
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

                if (!string.IsNullOrEmpty(result.Error))
                {
                    Console.WriteLine($"[BuildPipeline] Addressables content build failed: {result.Error}");
                    return false;
                }

                StageRemoteBundles(settings, result);
                Console.WriteLine("[BuildPipeline] Addressables content build succeeded.");
                return true;
            }
            catch (Exception ex)
            {
                // BuildPlayerContent can itself throw on a genuine
                // compilation/pipeline failure (as distinct from a
                // reported-but-non-throwing result.Error) - both paths are
                // treated as a failed content build.
                Console.WriteLine($"[BuildPipeline] Addressables content build threw an exception: {ex}");
                return false;
            }
        }

        private static void StageRemoteBundles(AddressableAssetSettings settings, AddressablesPlayerBuildResult result)
        {
            Directory.CreateDirectory(CdnStagingDirectory);

            int copiedCount = 0;
            foreach (AddressablesPlayerBuildResult.BundleBuildResult bundleResult in result.AssetBundleBuildResults)
            {
                if (bundleResult.SourceAssetGroup == null || string.IsNullOrEmpty(bundleResult.FilePath))
                {
                    continue;
                }

                if (!IsRemoteGroup(settings, bundleResult.SourceAssetGroup))
                {
                    continue;
                }

                if (!File.Exists(bundleResult.FilePath))
                {
                    Console.WriteLine($"[BuildPipeline] Warning: expected bundle file not found at '{bundleResult.FilePath}' - skipping.");
                    continue;
                }

                string destinationPath = Path.Combine(CdnStagingDirectory, Path.GetFileName(bundleResult.FilePath));
                File.Copy(bundleResult.FilePath, destinationPath, overwrite: true);
                copiedCount++;
            }

            Console.WriteLine($"[BuildPipeline] Staged {copiedCount} remote bundle(s) into '{CdnStagingDirectory}'.");
        }

        // Modul: classifies a group as remote by checking whether its
        // BuildPath profile variable resolves to a profile variable NAME
        // containing "Remote" - the standard Addressables convention
        // (Local.BuildPath vs Remote.BuildPath) every profile authored
        // through the Addressables Profiles window follows. A group with no
        // BundledAssetGroupSchema (not a bundled-content group at all) is
        // never remote.
        private static bool IsRemoteGroup(AddressableAssetSettings settings, AddressableAssetGroup group)
        {
            BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema == null)
            {
                return false;
            }

            string buildPathVariableName = schema.BuildPath.GetName(settings);
            return !string.IsNullOrEmpty(buildPathVariableName) && buildPathVariableName.IndexOf("Remote", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ExecutePlayerBuild()
        {
            string[] args = Environment.GetCommandLineArgs();
            string outputPath = DefaultApkOutputPath;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-outputPath" && i + 1 < args.Length)
                {
                    outputPath = args[i + 1];
                }
            }

            Console.WriteLine($"[BuildPipeline] Output Path set to: {outputPath}");

            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

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
                return false;
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
                Console.WriteLine($"[BuildPipeline] Player build succeeded: {summary.totalSize} bytes.");
                return true;
            }

            Console.WriteLine("[BuildPipeline] Player build failed.");
            return false;
        }
    }
}
