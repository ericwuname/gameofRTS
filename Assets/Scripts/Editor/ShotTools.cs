using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using TideShot.Boot;
using TideShot.Gameplay;

namespace TideShot.EditorTools
{
    /// <summary>
    /// 工程工具：建场景、打包、无人值守截图验证。
    /// 场景由代码生成（不手写 YAML），Battle 场景里只放一个 GameRoot + 主相机。
    /// </summary>
    public static class ShotTools
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string BuildDir = "Build/Windows";
        private const string ExeName = "TideShot.exe";

        // ---------------- 建场景 ----------------

        [MenuItem("TideShot/1. Build Scene")]
        public static void BuildScene()
        {
            if (!Directory.Exists("Assets/Scenes"))
            {
                Directory.CreateDirectory("Assets/Scenes");
                AssetDatabase.Refresh();
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("GameRoot");
            root.AddComponent<ShotBoot>();

            var camGo = new GameObject("MainCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 10f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color32(0x04, 0x07, 0x0D, 0xFF);
            camGo.transform.position = new Vector3(0f, 0f, -10f);
            camGo.AddComponent<AudioListener>();

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[ShotTools] scene saved=" + saved + " path=" + ScenePath);

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[ShotTools] scene added to build settings");
        }

        public static void BuildSceneAndExit()
        {
            try
            {
                BuildScene();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[ShotTools] scene build failed: " + e);
                EditorApplication.Exit(1);
            }
        }

        // ---------------- 打包 ----------------

        public static void BuildWindows64()
        {
            try
            {
                string[] scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled)
                    .Select(s => s.path)
                    .ToArray();
                if (scenes.Length == 0)
                {
                    Debug.LogError("[ShotTools] no scene in build settings");
                    EditorApplication.Exit(2);
                    return;
                }

                if (!Directory.Exists(BuildDir))
                {
                    Directory.CreateDirectory(BuildDir);
                }

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = Path.Combine(BuildDir, ExeName),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None
                };

                Debug.Log("[ShotTools] building to " + options.locationPathName);
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError("[ShotTools] build failed: " + report.summary.result);
                    EditorApplication.Exit(1);
                    return;
                }
                Debug.Log("[ShotTools] build ok size=" + (report.summary.totalSize / (1024 * 1024)) + "MB");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[ShotTools] build exception: " + e);
                EditorApplication.Exit(1);
            }
        }

        // ---------------- 无人值守截图 ----------------

        private const string KActive = "TideShot.Capture.Active";
        private const string KPhase = "TideShot.Capture.Phase";
        private const string KSince = "TideShot.Capture.Since";
        private const string KOutDir = "TideShot.Capture.OutDir";
        private static readonly float[] ShotTimes = { 4f, 10f, 16f, 24f };

        [InitializeOnLoadMethod]
        private static void ReattachAfterDomainReload()
        {
            if (!SessionState.GetBool(KActive, false))
            {
                return;
            }
            EditorApplication.update -= OnCaptureUpdate;
            EditorApplication.update += OnCaptureUpdate;
            Debug.Log("[ShotTools] capture reattached after domain reload");
        }

        public static void CaptureRunAndExit()
        {
            try
            {
                string outDir = Path.GetFullPath(Path.Combine(
                    Directory.GetCurrentDirectory(), "..", "docs", "shot-screenshots"));
                Directory.CreateDirectory(outDir);

                SessionState.SetString(KOutDir, outDir);
                SessionState.SetBool(KActive, true);
                SessionState.SetInt(KPhase, 0);
                SessionState.SetFloat(KSince, (float)EditorApplication.timeSinceStartup);

                EditorSceneManager.OpenScene(ScenePath);
                EditorApplication.update -= OnCaptureUpdate;
                EditorApplication.update += OnCaptureUpdate;
                EditorApplication.isPlaying = true;
                Debug.Log("[ShotTools] entering play mode for capture");
            }
            catch (Exception e)
            {
                Debug.LogError("[ShotTools] capture setup failed: " + e);
                FinishCapture(1);
            }
        }

        private static void OnCaptureUpdate()
        {
            if (!SessionState.GetBool(KActive, false))
            {
                EditorApplication.update -= OnCaptureUpdate;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            float phaseSince = SessionState.GetFloat(KSince, (float)now);
            float elapsed = (float)now - phaseSince;
            int phase = SessionState.GetInt(KPhase, 0);

            if (phase == 0)
            {
                if (elapsed > 40f)
                {
                    Debug.LogError("[ShotTools] timeout waiting for play mode");
                    FinishCapture(2);
                    return;
                }
                if (!EditorApplication.isPlaying)
                {
                    return;
                }
                Debug.Log("[ShotTools] play mode started");
                SessionState.SetInt(KPhase, 1);
                SessionState.SetFloat(KSince, (float)now);
                return;
            }

            if (phase == 1)
            {
                if (elapsed < 1.2f)
                {
                    return;
                }
                var boot = UnityEngine.Object.FindAnyObjectByType<ShotBoot>();
                if (boot == null || boot.Game == null)
                {
                    Debug.LogError("[ShotTools] ShotBoot not found");
                    FinishCapture(3);
                    return;
                }

                boot.Game.ManualDrive = true;
                boot.Game.Player.AutoPilot = true;
                boot.Game.StartRun();
                Debug.Log("[ShotTools] run started, state=" + boot.Game.State);

                FastForwardAndCapture(boot.Game);
                FinishCapture(0);
            }
        }

        private static void FastForwardAndCapture(GameDirector game)
        {
            const float step = 1f / 60f;
            float simTime = 0f;
            string outDir = SessionState.GetString(KOutDir, ".");

            for (int i = 0; i < ShotTimes.Length; i++)
            {
                int guard = 0;
                while (simTime < ShotTimes[i] && guard < 200000)
                {
                    game.TickFrame(step);
                    simTime += step;
                    guard++;
                }

                string name = "shot_" + i + "_t" + Mathf.RoundToInt(ShotTimes[i]) + "s.png";
                CaptureCamera(outDir, name);

                Debug.Log("[ShotTools] SUMMARY t=" + simTime.ToString("0.0")
                          + " state=" + game.State
                          + " score=" + game.Score
                          + " combo=" + game.Combo
                          + " wave=" + game.Wave
                          + " lives=" + (game.Player != null ? game.Player.Lives : -1)
                          + " enemies=" + (game.Enemies != null ? game.Enemies.ActiveCount : -1)
                          + " bullets=" + (game.Bullets != null ? game.Bullets.AliveCount : -1));
            }
        }

        private static void CaptureCamera(string outDir, string fileName)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                Debug.LogError("[ShotTools] no camera");
                return;
            }
            const int w = 720;
            const int h = 1280;

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture prevTarget = cam.targetTexture;

            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;

            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();

            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);

            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            if (png != null && png.Length > 0)
            {
                File.WriteAllBytes(Path.Combine(outDir, fileName), png);
                Debug.Log("[ShotTools] captured " + fileName + " (" + png.Length / 1024 + " KB)");
            }
        }

        private static void FinishCapture(int code)
        {
            Debug.Log("[ShotTools] capture finishing code=" + code);
            SessionState.SetBool(KActive, false);
            EditorApplication.update -= OnCaptureUpdate;
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
            }
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
