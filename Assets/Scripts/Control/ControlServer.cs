using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using TideShot.Gameplay;

namespace TideShot.Control
{
    /// <summary>
    /// 外部控制接口：TCP + 单行 JSON 协议。
    /// 用途：让 MCP server（进而让 LLM）能够「观察状态 → 决定输入 → 推进时间」，
    /// 即真正地玩这局游戏，而不是看录像。
    ///
    /// 协议（每行一个 JSON）：
    ///   请求 {"cmd":"hello"}                    → {"ok":true,"game":"TideShot"}
    ///   请求 {"cmd":"start"}                    → {"ok":true}
    ///   请求 {"cmd":"state"}                    → 完整状态
    ///   请求 {"cmd":"input","x":0.5,"y":0}      → 设置持续移动方向
    ///   请求 {"cmd":"step","frames":30}         → 推进 N 帧后返回状态
    ///   请求 {"cmd":"quit"}                     → 关闭
    ///
    /// 线程模型：网络线程只做收发，所有 Unity API 调用都在主线程 Update 里完成。
    /// </summary>
    public sealed class ControlServer : MonoBehaviour
    {
        private const int Port = 7799;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly object _streamLock = new object();

        private readonly ConcurrentQueue<string> _requests = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _responses = new ConcurrentQueue<string>();

        private GameDirector _game;
        private PlayerShip _player;
        private ScreenRecorder _recorder;
        private bool _stepping;

        public static bool Enabled { get; private set; }

        public void Initialize(GameDirector game, PlayerShip player, int port = Port)
        {
            _game = game;
            _player = player;
            StartServer(port);
        }

        private void StartServer(int port)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                Enabled = true;

                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TideShotControl" };
                _acceptThread.Start();

                Debug.Log("[ControlServer] listening on 127.0.0.1:" + port);
            }
            catch (Exception e)
            {
                Debug.LogError("[ControlServer] failed to start: " + e.Message);
                Enabled = false;
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    lock (_streamLock)
                    {
                        _client?.Close();
                        _client = client;
                        _stream = client.GetStream();
                    }
                    Debug.Log("[ControlServer] client connected");
                    ClientLoop(client);
                }
                catch (SocketException)
                {
                    if (!_running)
                    {
                        return;
                    }
                }
                catch (Exception e)
                {
                    if (_running)
                    {
                        Debug.LogWarning("[ControlServer] accept error: " + e.Message);
                    }
                    Thread.Sleep(120);
                }
            }
        }

        private void ClientLoop(TcpClient client)
        {
            var buffer = new byte[8192];
            var pending = new StringBuilder();

            while (_running && client.Connected)
            {
                try
                {
                    int read = client.GetStream().Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    pending.Append(Encoding.UTF8.GetString(buffer, 0, read));

                    string all = pending.ToString();
                    int newline;
                    while ((newline = all.IndexOf('\n')) >= 0)
                    {
                        string line = all.Substring(0, newline).Trim();
                        all = all.Substring(newline + 1);
                        if (line.Length > 0)
                        {
                            _requests.Enqueue(line);
                        }
                    }
                    pending.Clear();
                    pending.Append(all);
                }
                catch (Exception)
                {
                    break;
                }
            }

            lock (_streamLock)
            {
                _stream = null;
                _client = null;
            }
            Debug.Log("[ControlServer] client disconnected");
        }

        private void Update()
        {
            // 回应答
            while (_responses.TryDequeue(out string reply))
            {
                try
                {
                    lock (_streamLock)
                    {
                        if (_stream == null)
                        {
                            continue;
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(reply + "\n");
                        _stream.Write(bytes, 0, bytes.Length);
                        _stream.Flush();
                    }
                }
                catch (Exception)
                {
                    lock (_streamLock)
                    {
                        _stream = null;
                    }
                }
            }

            if (_stepping)
            {
                return;
            }

            // 处理请求（主线程，可安全访问 Unity 对象）
            while (_requests.TryDequeue(out string json))
            {
                string response;
                try
                {
                    response = Handle(json);
                }
                catch (Exception e)
                {
                    response = "{\"ok\":false,\"error\":\"" + Escape(e.Message) + "\"}";
                }
                if (!string.IsNullOrEmpty(response))
                {
                    _responses.Enqueue(response);
                }
            }
        }

        private string Handle(string json)
        {
            string cmd = ExtractString(json, "cmd");
            if (string.IsNullOrEmpty(cmd))
            {
                return "{\"ok\":false,\"error\":\"missing cmd\"}";
            }

            switch (cmd)
            {
                case "hello":
                    return "{\"ok\":true,\"game\":\"TideShot\",\"version\":1}";

                case "start":
                    _game.ManualDrive = true;
                    _player.ExternalInput = Vector2.zero;
                    _game.StartRun();
                    return StateJson();

                case "state":
                    return StateJson();

                case "input":
                    float x = (float)ExtractNumber(json, "x");
                    float y = (float)ExtractNumber(json, "y");
                    _player.ExternalInput = new Vector2(Mathf.Clamp(x, -1f, 1f), Mathf.Clamp(y, -1f, 1f));
                    return "{\"ok\":true}";

                case "step":
                    int frames = Mathf.Clamp((int)ExtractNumber(json, "frames", 1), 1, 600);
                    return Step(frames);

                case "record":
                    // 让外部（脚本/AI）也能控制录屏，便于自动化回归与回放分析
                    var rec = _recorder != null
                        ? _recorder
                        : UnityEngine.Object.FindAnyObjectByType<ScreenRecorder>();
                    if (rec == null)
                    {
                        return "{\"ok\":false,\"error\":\"recorder not found\"}";
                    }
                    _recorder = rec;
                    int on = (int)ExtractNumber(json, "on", 0);
                    if (on == 1)
                    {
                        rec.StartRecording();
                    }
                    else
                    {
                        rec.StopRecording();
                    }
                    return "{\"ok\":true,\"recording\":" + (rec.IsRecording ? 1 : 0)
                           + ",\"frames\":" + rec.FrameCount
                           + ",\"dir\":\"" + Escape(rec.CurrentDirectory ?? "") + "\"}";

                case "quit":
                    _running = false;
                    return "{\"ok\":true,\"bye\":true}";
            }
            return "{\"ok\":false,\"error\":\"unknown cmd " + Escape(cmd) + "\"}";
        }

        private string Step(int frames)
        {
            const float dt = 1f / 60f;
            int killsBefore = _game.TotalKills;
            for (int i = 0; i < frames; i++)
            {
                _game.TickFrame(dt);
            }
            // 返回「这一批推进里杀了几个」：AI 需要这个才知道自己刚才的走位有没有效果，
            // 否则它只能看到分数涨了却不知道为什么，会把正常击杀当成异常。
            return StateJson(_game.TotalKills - killsBefore);
        }

        private string StateJson(int killedThisStep = 0)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"ok\":true");
            sb.Append(",\"state\":\"").Append(_game.State).Append("\"");
            sb.Append(",\"score\":").Append(_game.Score);
            sb.Append(",\"combo\":").Append(_game.Combo);
            sb.Append(",\"comboTimer\":").Append(_game.ComboRemaining.ToString("0.00"));
            sb.Append(",\"comboWindow\":").Append(_game.ComboWindowDuration.ToString("0.00"));
            sb.Append(",\"wave\":").Append(_game.Wave);
            sb.Append(",\"pressure\":").Append(_game.Difficulty.Pressure.ToString("0.00"));
            sb.Append(",\"difficultyTarget\":").Append(_game.Difficulty.TargetPressure.ToString("0.00"));
            sb.Append(",\"grace\":").Append(_game.Difficulty.InGrace ? 1 : 0);
            sb.Append(",\"killed\":").Append(killedThisStep);
            sb.Append(",\"totalKills\":").Append(_game.TotalKills);
            sb.Append(",\"time\":").Append(_game.RunTime.ToString("0.00"));
            sb.Append(",\"lives\":").Append(_player.Lives);
            sb.Append(",\"power\":").Append(_player.PowerLevel);
            sb.Append(",\"px\":").Append(_player.Position.x.ToString("0.00"));
            sb.Append(",\"py\":").Append(_player.Position.y.ToString("0.00"));
            sb.Append(",\"invuln\":").Append(_player.InvulnRemaining.ToString("0.00"));
            sb.Append(",\"lastStand\":").Append(_player.LastStand ? 1 : 0);
            sb.Append(",\"nextHeal\":").Append(_game.NextHealCombo);
            sb.Append(",\"form\":").Append((int)_player.Form);
            sb.Append(",\"orbs\":").Append(_player.EnergyOrbs);
            sb.Append(",\"orbCost\":").Append(PlayerShip.EvolutionCost);
            sb.Append(",\"evolving\":").Append(_player.EvolutionPending ? 1 : 0);
            sb.Append(",\"shrinkIn\":").Append(_game.ShrinkCountdown.ToString("0.0"));
            sb.Append(",\"shrinkActive\":").Append(_game.ShrinkActive ? 1 : 0);
            sb.Append(",\"nextSurge\":").Append(_game.NextSurgeCombo);

            // 敌机
            sb.Append(",\"enemies\":[");
            var enemies = _game.Enemies.Active;
            bool first = true;
            for (int i = 0; i < enemies.Count; i++)
            {
                var e = enemies[i];
                if (e == null || !e.IsAlive)
                {
                    continue;
                }
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;
                sb.Append("{\"x\":").Append(e.Position.x.ToString("0.00"));
                sb.Append(",\"y\":").Append(e.Position.y.ToString("0.00"));
                sb.Append(",\"k\":").Append((int)e.Kind);
                sb.Append(",\"r\":").Append(e.Radius.ToString("0.00"));
                sb.Append('}');
            }
            sb.Append(']');

            // 敌弹：只给离玩家最近的 14 颗，避免状态包太大
            sb.Append(",\"threats\":[");
            Vector2 p = _player.Position;
            AppendNearestBullets(sb, p, 14);
            sb.Append(']');

            // 能量球：最近的 10 个。AI 玩家需要「机会」而不只是「危险」——
            // 只给威胁的话，任何理性策略都会退化成躲到底，而那正是人类玩家的病。
            sb.Append(",\"orbPos\":[");
            var orbs = _game.Orbs != null ? _game.Orbs.SampleNearest(p, 10) : null;
            if (orbs != null)
            {
                for (int i = 0; i < orbs.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append('[').Append(orbs[i].x.ToString("0.00"))
                      .Append(',').Append(orbs[i].y.ToString("0.00")).Append(']');
                }
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        private void AppendNearestBullets(StringBuilder sb, Vector2 origin, int max)
        {
            // BulletField 内部是私有数组，这里通过公开的只读快照接口拿不到，
            // 改为让 BulletField 暴露一个采样方法（见 BulletField.SampleThreats）
            var list = _game.Bullets.SampleThreats(origin, max);
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append('[').Append(list[i].x.ToString("0.00"))
                  .Append(',').Append(list[i].y.ToString("0.00")).Append(']');
            }
        }

        private void OnDestroy()
        {
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // 忽略：关闭时的异常没有处置价值
            }
            lock (_streamLock)
            {
                _stream?.Close();
                _client?.Close();
            }
        }

        // ---------------- 极简 JSON 读取 ----------------

        private static string ExtractString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0)
            {
                return null;
            }
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0)
            {
                return null;
            }
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0)
            {
                return null;
            }
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static double ExtractNumber(string json, string key, double fallback = 0.0)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
            {
                return fallback;
            }
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0)
            {
                return fallback;
            }
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t'))
            {
                start++;
            }
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.'))
            {
                end++;
            }
            if (end <= start)
            {
                return fallback;
            }
            return double.TryParse(json.Substring(start, end - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value
                : fallback;
        }

        private static string Escape(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "/").Replace("\"", "'");
        }
    }
}
