using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ThreeUnity.Bridge.Editor
{
    public static class ThreeUnityBatchBuilder
    {
        public static void BuildFromCommandLine()
        {
            var assetPath = GetArgument("-threeUnityAsset");
            var outputPath = GetArgument("-threeUnityOutput");
            var scenePath = GetOptionalArgument("-threeUnityScene") ?? "Assets/ThreeUnityBridge/GeneratedPlayable.unity";
            if (string.IsNullOrEmpty(assetPath)) throw new ArgumentException("-threeUnityAsset is required.");
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("-threeUnityOutput is required.");

            var sceneDirectory = Path.GetDirectoryName(scenePath);
            if (!string.IsNullOrEmpty(sceneDirectory)) Directory.CreateDirectory(sceneDirectory);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            ThreeUnityPlayableSceneBuilder.CreateScene(assetPath, scenePath);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Three Unity build failed: {report.summary.result} ({report.summary.totalErrors} errors).");
            Debug.Log($"THREE_UNITY_BATCH_BUILD_PASS asset={assetPath} output={outputPath} bytes={report.summary.totalSize}");
        }

        private static string GetArgument(string name)
        {
            var value = GetOptionalArgument(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string GetOptionalArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase)) return arguments[index + 1];
            return null;
        }
    }
}
