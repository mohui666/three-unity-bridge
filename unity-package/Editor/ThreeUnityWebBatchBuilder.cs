using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using ThreeUnity.Bridge.Logic;

namespace ThreeUnity.Bridge.Editor
{
    public static class ThreeUnityWebBatchBuilder
    {
        public static void BuildFromCommandLine()
        {
            var webRoot = GetArgument("-threeUnityWebRoot") ?? "ThreeUnityWeb";
            var entry = GetArgument("-threeUnityWebEntry") ?? "index.html";
            var productName = GetArgument("-threeUnityProductName") ?? "Three Unity Web Bridge";
            var logicProfile = GetArgument("-threeUnityLogicProfile") ?? string.Empty;
            var output = GetArgument("-threeUnityOutput");
            if (string.IsNullOrEmpty(output)) throw new ArgumentException("-threeUnityOutput is required.");
            if (!string.IsNullOrEmpty(logicProfile))
                ThreeUnityLogicModuleRegistry.Create(logicProfile);

            const string scenePath = "Assets/ThreeUnityBridge/WebBridge.unity";
            Directory.CreateDirectory(Path.GetDirectoryName(scenePath));
            var outputDirectory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var bridgeObject = new GameObject("Three Unity Web Bridge");
            var launcher = bridgeObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            launcher.Configure(webRoot, entry);
            if (!string.IsNullOrEmpty(logicProfile))
                bridgeObject.AddComponent<ThreeUnityLogicBridge>().Configure(launcher, logicProfile);

            var cameraObject = new GameObject("Bridge Background Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            cameraObject.AddComponent<AudioListener>();

            if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new InvalidOperationException("Could not save Web Bridge scene.");
            PlayerSettings.productName = productName;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException($"Web Bridge build failed: {report.summary.result} ({report.summary.totalErrors} errors).");
            Debug.Log($"THREE_UNITY_WEB_BUILD_PASS output={output} bytes={report.summary.totalSize} entry={entry} logicProfile={logicProfile}");
        }

        private static string GetArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase)) return arguments[index + 1];
            return null;
        }
    }
}
