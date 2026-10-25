using UnityEngine;
using TideShot.Core;
using TideShot.Gameplay;
using TideShot.UI;

namespace TideShot.Boot
{
    /// <summary>
    /// 唯一入口。所有对象与材质都在这里程序化创建，工程不依赖任何预制体或图片资源。
    /// </summary>
    public sealed class ShotBoot : MonoBehaviour
    {
        public GameDirector Game { get; private set; }
        public TideShot.Control.ScreenRecorder Recorder { get; private set; }

        private void Awake()
        {
            Debug.Log("[ShotBoot] boot start");

            Camera cam = SetupCamera();
            Material mat = CreateMaterial();

            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Application.runInBackground = true;

#if !UNITY_EDITOR
            // 1280×720 横屏（16:9）。
            // 原来是 640×1138 竖屏——那是个手机比例的小窗，横向只有 ±6.5 单位，
            // 同屏能容纳的敌机和弹幕极其有限，「内容丰富」根本无从谈起。
            // 改横屏后横向视野扩到 ±11.4 单位，接近翻倍，
            // 才装得下多波次、多敌种、Boss 战和更多视觉层。
            Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
#endif

            var starGo = new GameObject("Starfield");
            starGo.transform.SetParent(transform, false);
            var starfield = starGo.AddComponent<Starfield>();
            starfield.Initialize(mat);

            var playerGo = new GameObject("Player");
            playerGo.transform.SetParent(transform, false);
            var player = playerGo.AddComponent<PlayerShip>();

            var enemyGo = new GameObject("Enemies");
            enemyGo.transform.SetParent(transform, false);
            var enemies = enemyGo.AddComponent<EnemyManager>();

            var bulletGo = new GameObject("Bullets");
            bulletGo.transform.SetParent(transform, false);
            var bullets = bulletGo.AddComponent<BulletField>();

            var fxGo = new GameObject("FxLayer");
            fxGo.transform.SetParent(transform, false);
            var fx = fxGo.AddComponent<FxLayer>();

            var orbGo = new GameObject("EnergyOrbs");
            orbGo.transform.SetParent(transform, false);
            var orbs = orbGo.AddComponent<EnergyOrbField>();

            var rewardGo = new GameObject("Rewards");
            rewardGo.transform.SetParent(transform, false);
            var rewards = rewardGo.AddComponent<RewardField>();

            var gameGo = new GameObject("GameDirector");
            gameGo.transform.SetParent(transform, false);
            Game = gameGo.AddComponent<GameDirector>();

            var cfg = new PlayerStatsConfig();
            player.Initialize(cfg);
            fx.Initialize(mat);
            orbs.Initialize(mat);
            rewards.Initialize(mat);
            enemies.Initialize(bullets, player, mat);
            bullets.Initialize(mat, enemies, player);

            Game.Setup(cam, player, bullets, enemies, fx, starfield, orbs, rewards);

            Sfx.Initialize(gameObject);

            // 外部控制接口（MCP / AI 玩家）。只在本地回环监听，不对外暴露。
            var controlGo = new GameObject("ControlServer");
            controlGo.transform.SetParent(transform, false);
            var control = controlGo.AddComponent<TideShot.Control.ControlServer>();
            control.Initialize(Game, player);

            // 录屏：按 F9 开始/结束，产出帧序列（+ 有 ffmpeg 时自动合成 mp4）
            var recGo = new GameObject("Recorder");
            recGo.transform.SetParent(transform, false);
            var recorder = recGo.AddComponent<TideShot.Control.ScreenRecorder>();
            recorder.Initialize(cam, Game);
            Recorder = recorder;

            var hud = gameObject.AddComponent<ShotHud>();
            hud.Initialize(Game, cam);

            Debug.Log("[ShotBoot] boot done, state=" + Game.State);
        }

        private Camera SetupCamera()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("MainCamera");
                camGo.tag = "MainCamera";
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }
            cam.orthographic = true;
            // 竖版：可见高度 24 世界单位，宽度由窗口比例决定（9:16 时约 13.5）
            cam.orthographicSize = 10f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color32(0x04, 0x07, 0x0D, 0xFF);
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.transform.rotation = Quaternion.identity;
            return cam;
        }

        private static Material CreateMaterial()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Transparent");
            }
            if (shader == null)
            {
                Debug.LogError("[ShotBoot] no usable shader found");
                return null;
            }
            var mat = new Material(shader) { name = "GlowMat" };
            mat.mainTexture = Texture2D.whiteTexture;
            return mat;
        }
    }
}
