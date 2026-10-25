using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 破潮 AI 指挥官接口 @trace DATA-001。
    /// Agnes 以「指挥官」身份真玩：只做调度决策（点节点/造舰/开波），
    /// 不做旗舰微操——这恰好验证设计支柱「调度 > 微操」。
    /// 旗舰默认护航模式（跟随舰队重心），玩家按键即接管。
    /// </summary>
    public class BreakerServer : MonoBehaviour
    {
        private BreakerDirector _d;
        private PlayerShip _player;
        private System.Net.Sockets.TcpListener _listener;
        private System.Net.Sockets.TcpClient _client;
        private readonly System.Text.StringBuilder _rx = new();
        public bool Enabled { get; private set; }

        public void Initialize(BreakerDirector d, PlayerShip player)
        {
            _d = d;
            _player = player;
            try
            {
                _listener = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Loopback, 7799);
                _listener.Start();
                _listener.BeginAcceptTcpClient(OnAccept, null);
                Enabled = true;
                Debug.Log("[BreakerServer] listening 7799");
            }
            catch (Exception ex)
            {
                Debug.Log("[BreakerServer] disabled: " + ex.Message);
            }
        }

        private void OnAccept(IAsyncResult ar)
        {
            try
            {
                _client = _listener.EndAcceptTcpClient(ar);
                _rx.Clear();
                _client.GetStream().BeginRead(_rxArr, 0, _rxArr.Length, OnRead, null);
                Debug.Log("[BreakerServer] client connected");
            }
            catch { }
        }

        private readonly byte[] _rxArr = new byte[8192];

        private void OnRead(IAsyncResult ar)
        {
            try
            {
                int n = _client.GetStream().EndRead(ar);
                if (n <= 0) { return; }
                _rx.Append(System.Text.Encoding.UTF8.GetString(_rxArr, 0, n));
                string buf = _rx.ToString();
                int idx;
                while ((idx = buf.IndexOf('\n')) >= 0)
                {
                    string line = buf.Substring(0, idx).Trim();
                    buf = buf.Substring(idx + 1);
                    if (line.Length > 0) { Handle(line); }
                }
                _rx.Clear();
                _rx.Append(buf);
                _client.GetStream().BeginRead(_rxArr, 0, _rxArr.Length, OnRead, null);
            }
            catch { }
        }

        private void Handle(string json)
        {
            try
            {
                var p = JsonUtility.FromJson<Cmd>(json);
                switch (p.cmd)
                {
                    case "state":
                        Send(BuildState());
                        break;
                    case "order":   // 设定舰队目的地
                        _d.TrySetDestination(p.node);
                        Send("{\"ok\":1}");
                        break;
                    case "build":   // 造舰 1炮/2登/3工
                        bool ok = _d.BuildFleet((Fleet.Kind)(p.kind - 1));
                        Send("{\"ok\":" + (ok ? 1 : 0) + ",\"reason\":\"" +
                            (_d.Building ? "queue busy" : (_d.Tide < 16 ? "no tide" : "limit")) + "\"}");
                        break;
                    case "refinery":
                        bool r = _d.BuildRefinery(p.node);
                        Send("{\"ok\":" + (r ? 1 : 0) + "}");
                        break;
                    case "wave":    // 提前开波
                        _d.ForceStartWave();
                        Send("{\"ok\":1}");
                        break;
                    default:
                        Send("{\"err\":1}");
                        break;
                }
            }
            catch (Exception ex) { Send("{\"err\":\"" + ex.Message + "\"}"); }
        }

        [Serializable]
        private class Cmd { public string cmd; public int node; public int kind; }

        private string BuildState()
        {
            var sb = new System.Text.StringBuilder();
            var m = _d.Map;
            sb.Append("{\"tide\":").Append(_d.Tide)
              .Append(",\"state\":\"").Append(_d.State).Append('"')
              .Append(",\"wave\":").Append(_d.Wave)
              .Append(",\"waveTimer\":").Append(_d.WaveTimer.ToString("F1"))
              .Append(",\"commander\":\"").Append(_d.CommanderName).Append('"')
              .Append(",\"sel\":").Append(_d.SelectedNode)
              .Append(",\"fleet\":").Append(_d.LivingFleet())
              .Append(",\"lastNet\":").Append(_d.LastIncome - _d.LastUpkeep)
              .Append(",\"building\":").Append(_d.BuildProgress.ToString("F2"))
              .Append(",\"nodes\":[");
            for (int i = 0; i < m.Nodes.Count; i++)
            {
                var n = m.Nodes[i];
                if (i > 0) { sb.Append(','); }
                sb.Append("{\"id\":").Append(i)
                  .Append(",\"t\":\"").Append(n.Type).Append('"')
                  .Append(",\"x\":").Append(n.Pos.x.ToString("F1"))
                  .Append(",\"y\":").Append(n.Pos.y.ToString("F1"))
                  .Append(",\"own\":").Append(n.Owned ? 1 : 0)
                  .Append(",\"gar\":").Append(n.Ambushed ? -1 : n.Garrison.Count)
                  .Append(",\"atk\":").Append(_d.CanAttack(n) ? 1 : 0)
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString() + "\n";
        }

        private void Send(string s)
        {
            try
            {
                // 统一补换行：缺它会让连续两次 ok 响应粘包（{"ok":1}{"ok":1}）
                var b = System.Text.Encoding.UTF8.GetBytes(s.EndsWith("\n") ? s : s + "\n");
                _client.GetStream().Write(b, 0, b.Length);
            }
            catch { }
        }

        private void OnDestroy()
        {
            try { _listener?.Stop(); } catch { }
        }
    }
}
