using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using TideShot.Gameplay;
using Debug = UnityEngine.Debug;

namespace TideShot.Control
{
    /// <summary>
    /// 录屏。按 F9 开始 / 再按 F9 结束。
    ///
    /// 为什么不用 Unity Recorder：那需要装 package 且配置一堆参数；
    /// 这里只要「能回看并定位问题」，8fps 的帧序列完全够用，
    /// 而且帧序列比视频更好分析——可以直接挑某一帧细看。
    ///
    /// 输出：
    ///   Recordings/rec_YYYYMMDD_HHMMSS/frame_00001.jpg ...
    ///   Recordings/rec_YYYYMMDD_HHMMSS/meta.json     （成绩、时长、帧数）
    ///   Recordings/rec_YYYYMMDD_HHMMSS/playback.mp4  （若系统里有 ffmpeg 则自动合成）
    /// </summary>
    public sealed class ScreenRecorder : MonoBehaviour
    {
        /// <summary>抓帧频率。8fps 足以看出弹幕密度变化与操作是否合理，
        /// 又不至于让磁盘 IO 影响游戏本身。</summary>
        private const float CaptureFps = 8f;

        private const int JpegQuality = 78;

        public bool IsRecording { get; private set; }
        public string CurrentDirectory { get; private set; }
        public int FrameCount => _frames;
        public float RecordedSeconds => _recordedSeconds;

        private Camera _cam;
        private GameDirector _game;
        private Gameplay.BreakerDirector _breaker;
        private float _timer;
        private int _frames;
        private float _recordedSeconds;

        public void Initialize(Camera cam, GameDirector game)
        {
            _cam = cam;
            _game = game;
        }

        /// <summary>破潮模式重载：meta.json 写破潮字段（F9 录屏两模式通用）。</summary>
        public void Initialize(Camera cam, Gameplay.BreakerDirector breaker)
        {
            _cam = cam;
            _breaker = breaker;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (IsRecording)
                {
                    StopRecording();
                }
                else
                {
                    StartRecording();
                }
            }

            if (!IsRecording)
            {
                return;
            }

            _recordedSeconds += Time.unscaledDeltaTime;
            _timer += Time.unscaledDeltaTime;
            float interval = 1f / CaptureFps;
            if (_timer >= interval)
            {
                _timer -= interval;
                CaptureFrame();
            }
        }

        public void StartRecording()
        {
            if (IsRecording)
            {
                return;
            }
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string root = Path.Combine(Application.dataPath, "..", "Recordings");
                CurrentDirectory = Path.Combine(root, "rec_" + stamp);
                Directory.CreateDirectory(CurrentDirectory);

                _frames = 0;
                _recordedSeconds = 0f;
                _timer = 0f;
                IsRecording = true;

                Debug.Log("[Recorder] 开始录制 -> " + Path.GetFullPath(CurrentDirectory));
                FxLayer.Instance?.Shake(0.15f, 0.1f);
            }
            catch (Exception e)
            {
                Debug.LogError("[Recorder] 启动失败: " + e.Message);
                IsRecording = false;
            }
        }

        public void StopRecording()
        {
            if (!IsRecording)
            {
                return;
            }
            IsRecording = false;

            try
            {
                WriteMeta();
                string mp4 = TryEncodeWithFfmpeg();
                Debug.Log("[Recorder] 录制结束 帧数=" + _frames
                          + " 时长=" + _recordedSeconds.ToString("0.0") + "s"
                          + (string.IsNullOrEmpty(mp4) ? "（未合成视频，可手动用 ffmpeg 合成帧序列）"
                                                       : " 视频=" + mp4));
            }
            catch (Exception e)
            {
                Debug.LogError("[Recorder] 收尾失败: " + e.Message);
            }
        }

        private void CaptureFrame()
        {
            if (string.IsNullOrEmpty(CurrentDirectory))
            {
                return;
            }
            try
            {
                // 抓整屏而不是只抓相机：HUD（分数/生命/连击）必须一起录进来，
                // 否则回看时无法判断「当时是什么状态导致了这个操作」。
                Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null)
                {
                    return;
                }
                _frames++;
                byte[] jpg = ImageConversion.EncodeToJPG(shot, JpegQuality);
                UnityEngine.Object.Destroy(shot);
                if (jpg == null || jpg.Length == 0)
                {
                    return;
                }
                string file = Path.Combine(CurrentDirectory,
                    "frame_" + _frames.ToString("D5") + ".jpg");
                File.WriteAllBytes(file, jpg);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Recorder] 抓帧失败: " + e.Message);
            }
        }

        private void WriteMeta()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"recordedAt\": \"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\",\n");
            sb.Append("  \"frames\": ").Append(_frames).Append(",\n");
            sb.Append("  \"seconds\": ").Append(_recordedSeconds.ToString("0.00")).Append(",\n");
            sb.Append("  \"captureFps\": ").Append(CaptureFps.ToString("0")).Append(",\n");
            if (_breaker != null)
            {
                sb.Append("  \"finalTide\": ").Append(_breaker.Tide).Append(",\n");
                sb.Append("  \"finalWave\": ").Append(_breaker.Wave).Append(",\n");
                sb.Append("  \"fleet\": ").Append(_breaker.LivingFleet()).Append(",\n");
                sb.Append("  \"commander\": \"").Append(_breaker.CommanderName).Append("\",\n");
                sb.Append("  \"state\": \"").Append(_breaker.State).Append("\",\n");
            }
            else if (_game != null)
            {
                sb.Append("  \"finalScore\": ").Append(_game.Score).Append(",\n");
                sb.Append("  \"finalWave\": ").Append(_game.Wave).Append(",\n");
                sb.Append("  \"finalCombo\": ").Append(_game.Combo).Append(",\n");
                sb.Append("  \"totalKills\": ").Append(_game.TotalKills).Append(",\n");
                sb.Append("  \"pressure\": ").Append(_game.Difficulty.Pressure.ToString("0.00")).Append(",\n");
                sb.Append("  \"state\": \"").Append(_game.State).Append("\",\n");
            }
            sb.Append("  \"note\": \"帧序列按文件名顺序即时间顺序\"\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(CurrentDirectory, "meta.json"), sb.ToString());
        }

        /// <summary>尝试调用系统 ffmpeg 把帧序列合成 mp4。找不到就跳过，帧序列仍然可用。</summary>
        private string TryEncodeWithFfmpeg()
        {
            if (_frames < 2)
            {
                return null;
            }
            string output = Path.Combine(CurrentDirectory, "playback.mp4");
            string args = "-y -framerate " + CaptureFps.ToString("0")
                          + " -i \"" + Path.Combine(CurrentDirectory, "frame_%05d.jpg") + "\""
                          + " -c:v libx264 -pix_fmt yuv420p -crf 26 \"" + output + "\"";
            try
            {
                var psi = new ProcessStartInfo("ffmpeg", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (Process proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        return null;
                    }
                    proc.WaitForExit(30000);
                    if (proc.HasExited && proc.ExitCode == 0 && File.Exists(output))
                    {
                        return output;
                    }
                }
            }
            catch (Exception)
            {
                // ffmpeg 不在 PATH 里，属于正常情况：帧序列已落盘，可稍后合成
            }
            return null;
        }
    }
}
