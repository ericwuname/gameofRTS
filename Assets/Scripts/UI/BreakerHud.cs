using UnityEngine;
using TideShot.Gameplay;

namespace TideShot.UI
{
    /// <summary>
    /// 破潮 HUD。@trace UI-001 收支表常驻 / 进度永不隐藏；
    /// 屏幕流见 docs/design/06。STG 三铁律全部落在此档。
    /// </summary>
    public sealed class BreakerHud : MonoBehaviour
    {
        private BreakerDirector _d;
        private Camera _cam;
        private Texture2D _white;
        private GUIStyle _big;
        private bool _inited;

        private static readonly Color Main = new Color32(0xE8, 0xF1, 0xF5, 0xFF);
        private static readonly Color Dim = new Color32(0x8A, 0x9B, 0xA8, 0xFF);
        private static readonly Color Cyan = new Color32(0x7F, 0xF0, 0xFF, 0xFF);
        private static readonly Color Gold = new Color32(0xFF, 0xD2, 0x4D, 0xFF);
        private static readonly Color Red = new Color32(0xFF, 0x6B, 0x4D, 0xFF);

        public void Initialize(BreakerDirector d, Camera cam)
        {
            _d = d;
            _cam = cam;
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _inited = true;
        }

        private void OnGUI()
        {
            if (!_inited || _d == null) { return; }
            float k = Screen.height / 720f;
            DrawTopBar(k);

            if (_d.State == BreakerState.Layout) { DrawLayout(k); }
            else if (_d.State == BreakerState.Wave) { DrawWave(k); }
            else { DrawEnd(k); }

            DrawNodeLabels(k);
        }

        private GUIStyle Style(int size, Color c, TextAnchor anchor = TextAnchor.UpperLeft, bool bold = false)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                alignment = anchor,
                normal = { textColor = c },
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
            };
            return s;
        }

        private void Bar(Rect r, float frac, Color c)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(r, _white);
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), _white);
            GUI.color = Color.white;
        }

        /// <summary>@trace UI-001 收支表常驻左上。</summary>
        private void DrawTopBar(float k)
        {
            GUI.Label(new Rect(20f * k, 12f * k, 200f * k, 34f * k),
                "潮力 " + _d.Tide, Style(Mathf.RoundToInt(24 * k), Gold, TextAnchor.UpperLeft, true));

            // 收支表：上一波实绩 + 当前预估 @trace ECON-001
            Color netCol = _d.LastIncome - _d.LastUpkeep >= 0 ? Cyan : Red;
            GUI.Label(new Rect(20f * k, 44f * k, 300f * k, 20f * k),
                $"上波  收入 {_d.LastIncome}  −  维护 {_d.LastUpkeep}  =  {_d.LastIncome - _d.LastUpkeep:+#;-#;0}",
                Style(Mathf.RoundToInt(13 * k), netCol));
            string streak = _d.NegativeStreak > 0
                ? $"  ⚠ 连续赤字 {_d.NegativeStreak}/2"
                : "";
            GUI.Label(new Rect(20f * k, 62f * k, 320f * k, 20f * k),
                $"舰队 {_d.LivingFleet()}/{BreakerDirector.MaxFleet}{streak}",
                Style(Mathf.RoundToInt(13 * k), _d.NegativeStreak > 0 ? Red : Dim));

            GUI.Label(new Rect(20f * k, 84f * k, 400f * k, 20f * k),
                "指挥官 " + _d.CommanderName, Style(Mathf.RoundToInt(13 * k), Dim));
            GUI.Label(new Rect(20f * k, 100f * k, 460f * k, 20f * k),
                _d.CommanderRule, Style(Mathf.RoundToInt(12 * k), new Color(1f, 0.85f, 0.45f, 0.8f)));
        }

        private void DrawLayout(float k)
        {
            float w = Screen.width;
            var c = GUI.skin.button;
            c.fontSize = Mathf.RoundToInt(15 * k);

            // 造舰菜单（波间）@trace UNIT-001
            GUI.Label(new Rect(w * 0.5f - 150f * k, Screen.height - 96f * k, 300f * k, 20f * k),
                "—— 布局阶段（按 空格 开波）——",
                Style(Mathf.RoundToInt(13 * k), Dim, TextAnchor.UpperLeft));
            if (GUI.Button(new Rect(w * 0.5f - 240f * k, Screen.height - 70f * k, 150f * k, 40f * k),
                    "1 炮舰 22")) { _d.BuildFleet(Fleet.Kind.Gunner); }
            if (GUI.Button(new Rect(w * 0.5f - 75f * k, Screen.height - 70f * k, 150f * k, 40f * k),
                    "2 登舰 16")) { _d.BuildFleet(Fleet.Kind.Boarder); }
            if (GUI.Button(new Rect(w * 0.5f + 90f * k, Screen.height - 70f * k, 150f * k, 40f * k),
                    "3 工程 20")) { _d.BuildFleet(Fleet.Kind.Engineer); }
            if (GUI.Button(new Rect(w * 0.5f + 255f * k, Screen.height - 70f * k, 170f * k, 40f * k),
                    "4 精炼所 30")) { _d.BuildRefinery(_d.SelectedNode); }

            GUI.Label(new Rect(w * 0.5f - 240f * k, Screen.height - 20f * k, 500f * k, 20f * k),
                "点击节点 = 设定舰队目的地（须与我方控制区相邻）",
                Style(Mathf.RoundToInt(12 * k), Dim));
        }

        private void DrawWave(float k)
        {
            // 波次倒计时
            GUI.Label(new Rect(Screen.width * 0.5f - 80f * k, 12f * k, 160f * k, 30f * k),
                $"第 {_d.Wave} 波   {_d.WaveTimer:F0}s",
                Style(Mathf.RoundToInt(20 * k), Main, TextAnchor.UpperCenter, true));
        }

        private void DrawEnd(float k)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _white);
            GUI.color = Color.white;

            Color c = _d.Victory ? Gold : Red;
            GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 70f * k),
                _d.Victory ? "潮 汐 平 息" : "远 征 失 败",
                Style(Mathf.RoundToInt(44 * k), c, TextAnchor.MiddleCenter, true));
            GUI.Label(new Rect(0, Screen.height * 0.3f + 70f * k, Screen.width, 60f * k),
                _d.EndReason, Style(Mathf.RoundToInt(17 * k), Main, TextAnchor.MiddleCenter));

            GUI.Label(new Rect(0, Screen.height * 0.3f + 140f * k, Screen.width, 30f * k),
                "波次 " + _d.Wave + "    潮力 " + _d.Tide,
                Style(Mathf.RoundToInt(15 * k), Dim, TextAnchor.MiddleCenter));
            GUI.Label(new Rect(0, Screen.height * 0.3f + 180f * k, Screen.width, 30f * k),
                "按 R 重新远征（seed 会重新抽取）",
                Style(Mathf.RoundToInt(14 * k), Cyan, TextAnchor.MiddleCenter));

            if (Input.GetKeyDown(KeyCode.R))
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            }
        }

        /// <summary>节点标签与点击（屏幕世界换算）。</summary>
        private void DrawNodeLabels(float k)
        {
            var map = _d.Map;
            if (map == null) { return; }

            // 点击选节点
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                _d.State == BreakerState.Layout)
            {
                Vector3 wp = _cam.ScreenToWorldPoint(new Vector3(Event.current.mousePosition.x,
                    Screen.height - Event.current.mousePosition.y, 10f));
                for (int i = 0; i < map.Nodes.Count; i++)
                {
                    if ((map.Nodes[i].Pos - new Vector2(wp.x, wp.y)).sqrMagnitude < 2.2f)
                    {
                        _d.TrySetDestination(i);
                        Event.current.Use();
                        break;
                    }
                }
            }

            // 标签（伏击节点不显示真实驻军数 @trace DEF-001）
            foreach (var n in map.Nodes)
            {
                Vector3 sp = _cam.WorldToScreenPoint(new Vector3(n.Pos.x, n.Pos.y, 0f));
                float x = sp.x - 40f * k;
                float y = Screen.height - sp.y + 14f * k;
                // QA 条件 1：可达目的地高亮"可攻"标记，缓解点选式路线的操作焦虑
                bool attackable = _d.State == BreakerState.Layout && _d.CanAttack(n);
                Color c = n.Owned ? Cyan
                    : attackable ? Gold
                    : (n.Type == NodeMap.NodeType.Bastion ? Red : Dim);
                string garrisonTxt = n.Owned || n.GarrisonQuota == 0
                    ? ""
                    : (n.Ambushed ? "守军 ?" : "守军 " + n.GarrisonQuota);
                string tag = attackable ? "\n▸ 可攻" : "";
                GUI.Label(new Rect(x, y, 80f * k, 40f * k),
                    n.Name + "\n" + garrisonTxt + tag,
                    Style(Mathf.RoundToInt(11 * k), c, TextAnchor.UpperCenter));
            }

            // QA 条件 3：首次赤字的容错提示（经济循环教学）
            if (_d.NegativeStreak == 1)
            {
                GUI.Label(new Rect(Screen.width * 0.5f - 260f * k, Screen.height * 0.72f, 520f * k, 40f * k),
                    "⚠ 收支转负！再赤字一波将破产——裁军（沉掉高价舰）或先占矿脉补收入",
                    Style(Mathf.RoundToInt(15 * k), Red, TextAnchor.UpperCenter, true));
            }

            // 要塞血条 @trace UNIT-002
            var bast = map.Get(map.BastionId);
            if (bast != null && bast.Garrison.Count > 0)
            {
                float frac = Mathf.Clamp01(bast.Garrison[0].Hp / 40f);
                Bar(new Rect(Screen.width * 0.5f - 90f * k, 44f * k, 180f * k, 6f * k), frac, Red);
                GUI.Label(new Rect(Screen.width * 0.5f - 90f * k, 50f * k, 180f * k, 18f * k),
                    "要 塞", Style(Mathf.RoundToInt(11 * k), Red, TextAnchor.UpperCenter));
            }
        }
    }
}
