using UnityEngine;
using TideShot.Core;
using TideShot.Gameplay;

namespace TideShot.UI
{
    /// <summary>
    /// HUD 与菜单。用 IMGUI 是为了零资产依赖——
    /// 这类游戏的信息层很简单，不值得为它引入 Canvas 和字体资源。
    /// </summary>
    public sealed class ShotHud : MonoBehaviour
    {
        private GameDirector _game;
        private Camera _cam;
        private TideShot.Control.ScreenRecorder _recorder;
        private Font _font;
        private bool _fontReady;

        private static readonly Color Cyan = new Color32(0x7F, 0xF0, 0xFF, 0xFF);
        private static readonly Color Gold = new Color32(0xFF, 0xE9, 0xA8, 0xFF);
        private static readonly Color Red = new Color32(0xFF, 0x4D, 0x5E, 0xFF);
        private static readonly Color Dim = new Color32(0x8F, 0xA3, 0xB3, 0xFF);
        private static readonly Color Main = new Color32(0xE8, 0xF1, 0xF5, 0xFF);
        private static readonly Color Purple = new Color32(0xC4, 0x9B, 0xFF, 0xFF);

        public void Initialize(GameDirector game, Camera cam)
        {
            _game = game;
            _cam = cam;
        }

        private void EnsureFont()
        {
            if (_fontReady)
            {
                return;
            }
            _fontReady = true;
            string[] candidates = { "Microsoft YaHei", "SimHei", "SimSun", "Arial Unicode MS" };
            _font = Font.CreateDynamicFontFromOSFont(candidates, 16);
        }

        private GUIStyle Style(int size, Color color, TextAnchor anchor = TextAnchor.UpperLeft, bool bold = false)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                alignment = anchor,
                richText = true
            };
            if (_font != null)
            {
                s.font = _font;
            }
            s.normal.textColor = color;
            s.hover.textColor = color;
            s.active.textColor = color;
            s.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            return s;
        }

        private GUIStyle ButtonStyle(int size, Color textColor, Color bg)
        {
            var s = new GUIStyle(GUI.skin.button)
            {
                fontSize = size,
                alignment = TextAnchor.MiddleCenter
            };
            if (_font != null)
            {
                s.font = _font;
            }
            s.normal.textColor = textColor;
            s.hover.textColor = Gold;
            s.active.textColor = Gold;
            s.normal.background = MakeTex(bg);
            s.hover.background = MakeTex(new Color(bg.r + 0.1f, bg.g + 0.1f, bg.b + 0.12f, 1f));
            s.active.background = MakeTex(new Color(bg.r + 0.18f, bg.g + 0.18f, bg.b + 0.2f, 1f));
            return s;
        }

        private static Texture2D _white;
        private static Texture2D MakeTex(Color c)
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void OnGUI()
        {
            if (_game == null)
            {
                return;
            }
            EnsureFont();

            float k = Screen.height / 720f;

            if (_game.State == ShotState.Playing)
            {
                // 选择界面优先于 HUD：弹出选择时，那是玩家当前唯一该做的事。
                // HUD 继续画在底下会被遮罩盖住，但浮动文字要留——
                // 否则「选择强化」那个提示玩家根本看不到。
                if (_game.Player.AnyChoicePending)
                {
                    if (_game.Player.UpgradePending)
                    {
                        DrawUpgradeChoice(k);
                    }
                    else
                    {
                        DrawEvolution(k);
                    }
                    DrawFloatingText(k);
                    return;
                }
                DrawHud(k);
                DrawModifier(k);
                DrawShrink(k);
                DrawBuild(k);
                DrawGamble(k);
                DrawFloatingText(k);
                DrawRecorderIndicator(k);
            }
            else if (_game.State == ShotState.Menu)
            {
                DrawMenu(k);
            }
            else
            {
                DrawResult(k);
            }
        }

        private void DrawHud(float k)
        {
            // 分数：左上，大号，跳得越厉害越爽
            GUI.Label(new Rect(24f * k, 18f * k, 400f * k, 46f * k),
                _game.Score.ToString("N0"), Style(Mathf.RoundToInt(34 * k), Main, TextAnchor.UpperLeft, true));

            GUI.Label(new Rect(24f * k, 58f * k, 400f * k, 24f * k),
                "WAVE " + _game.Wave, Style(Mathf.RoundToInt(15 * k), Dim));

            DrawEnergyProgress(k);

            // 连击：中上，倍率越高越亮越大
            if (_game.Combo > 1)
            {
                float scale = Mathf.Clamp(_game.ComboTimer01, 0f, 1f);
                int size = Mathf.RoundToInt((26 + Mathf.Min(_game.Combo, 20)) * k);
                Color c = _game.Combo >= 10 ? Gold : (_game.Combo >= 5 ? Cyan : Purple);
                c.a = 0.5f + 0.5f * scale;

                GUI.Label(new Rect(0, 110f * k, Screen.width, 50f * k),
                    _game.Combo + " COMBO", Style(size, c, TextAnchor.MiddleCenter, true));

                GUI.Label(new Rect(0, 154f * k, Screen.width, 24f * k),
                    "x" + _game.ComboMultiplier.ToString("0.00"),
                    Style(Mathf.RoundToInt(15 * k), new Color(c.r, c.g, c.b, c.a * 0.85f), TextAnchor.MiddleCenter));

                // 连击计时条
                float barW = 160f * k;
                float barX = (Screen.width - barW) * 0.5f;
                Rect back = new Rect(barX, 182f * k, barW, 4f * k);
                GUI.color = new Color(0f, 0f, 0f, 0.5f);
                GUI.DrawTexture(back, _white);
                GUI.color = new Color(c.r, c.g, c.b, c.a);
                GUI.DrawTexture(new Rect(back.x, back.y, barW * scale, back.height), _white);
                GUI.color = Color.white;
            }

            // 张力条：擦弹进度，满 50 触发一次潮涌（清弹）。
            // 擦弹是「贴弹飞」的奖励，但奖励如果不**可见**，
            // 玩家根本不知道自己的极限操作被系统记住了——
            // 看不见的奖励等于没有奖励，这是进度显示的第三次血泪课。
            int tension = _game.Tension;
            if (tension > 0)
            {
                const int Full = 50;
                float tFrac = Mathf.Clamp01(tension / (float)Full);
                float tW = 220f * k;
                float tX = (Screen.width - tW) * 0.5f;
                float tY = 200f * k;
                Rect tBack = new Rect(tX, tY, tW, 5f * k);
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.DrawTexture(tBack, _white);
                Color gc = new Color32(0xB4, 0xFF, 0xE8, 0xFF);
                GUI.color = gc;
                GUI.DrawTexture(new Rect(tBack.x, tBack.y, tW * tFrac, tBack.height), _white);
                GUI.color = Color.white;
                GUI.Label(new Rect(tX, tY + 7f * k, tW, 20f * k),
                    "张力 " + tension + " / " + Full + "    擦弹 " + _game.GrazeCount,
                    Style(Mathf.RoundToInt(11 * k),
                        new Color(gc.r, gc.g, gc.b, 0.9f), TextAnchor.UpperCenter));
            }

            // 命：底部
            float lifeX = 24f * k;
            float lifeY = Screen.height - 46f * k;

            // 过载指示：受伤后射速减半。必须可见——
            // 玩家需要知道「现在打不动不是我的错觉」，否则会把挫败感
            // 归因到自己操作上，进而更不敢往弹幕里压，那就本末倒置了。
            if (_game.Player.OverloadRemaining > 0f)
            {
                float ovW = 150f * k;
                float ovH = 5f * k;
                Rect ovBack = new Rect(lifeX, lifeY - 28f * k, ovW, ovH);
                GUI.color = new Color(0f, 0f, 0f, 0.5f);
                GUI.DrawTexture(ovBack, _white);
                float frac = Mathf.Clamp01(_game.Player.OverloadRemaining / PlayerShip.OverloadDuration);
                GUI.color = new Color(1f, 0.45f, 0.15f, 0.95f);
                GUI.DrawTexture(new Rect(ovBack.x, ovBack.y, ovW * frac, ovH), _white);
                GUI.color = Color.white;
                GUI.Label(new Rect(lifeX, lifeY - 54f * k, ovW, 22f * k),
                    "过 载  射速减半 " + _game.Player.OverloadRemaining.ToString("0.0") + "s",
                    Style(Mathf.RoundToInt(13 * k), new Color(1f, 0.52f, 0.22f, 1f),
                        TextAnchor.UpperLeft, true));
            }

            for (int i = 0; i < _game.Player.Lives; i++)
            {
                GUI.Label(new Rect(lifeX + i * 26f * k, lifeY, 24f * k, 26f * k), "▲",
                    Style(Mathf.RoundToInt(20 * k), Cyan, TextAnchor.UpperLeft, true));
            }
            if (_game.Player.Lives <= 0)
            {
                GUI.Label(new Rect(lifeX, lifeY, 200f * k, 26f * k), "无余命",
                    Style(Mathf.RoundToInt(14 * k), Red));
            }

            GUI.Label(new Rect(Screen.width - 180f * k, Screen.height - 44f * k, 156f * k, 24f * k),
                "最高 " + _game.BestScore.ToString("N0"),
                Style(Mathf.RoundToInt(14 * k), Dim, TextAnchor.UpperRight));
        }

        /// <summary>
        /// 能量球进度条。**进度的可见性是「成长感」的一半。**
        ///
        /// 这里栽过两次，都是同一个错误模式——用状态条件把进度藏起来：
        /// 第一次是「未满级就隐藏」，第二次是「已进化就隐藏」
        /// （`if (Form != Standard) return`）。两次的后果都一样：
        /// 玩家看不见进度 → 不知道要捡球 → 不去捡 → 整条成长线对他不存在。
        /// 帧序列里看到 61 秒、107 连击的一局，BUILD 完全是空的，
        /// 就是因为进化后进度条消失，他再没捡过一颗球。
        ///
        /// 现在改成**任何状态都显示**，只用文字区分当前目标：
        /// 未进化 → 「→ 武器进化」，已进化 → 「→ 随机强化」。
        /// 压暗可以，隐藏不行。
        /// </summary>
        private void DrawEnergyProgress(float k)
        {
            var p = _game.Player;
            int have = p.EnergyOrbs;
            int need = PlayerShip.EvolutionCost;
            float frac = Mathf.Clamp01(have / (float)need);
            bool canEvolve = p.Form == WeaponForm.Standard && p.PowerLevel >= 3;
            bool full = p.Form != WeaponForm.Standard;

            float barW = 160f * k;
            float barH = 7f * k;
            float bx = 24f * k;
            float by = 88f * k;

            // 还没到能进化时整条压暗：进度是真的在涨，目标还没解锁。
            Color barColor = (canEvolve || full) ? Gold : new Color(Gold.r, Gold.g, Gold.b, 0.45f);
            Rect back = new Rect(bx, by, barW, barH);
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(back, _white);
            GUI.color = barColor;
            GUI.DrawTexture(new Rect(bx, by, barW * frac, barH), _white);
            GUI.color = Color.white;

            string label = full
                ? "能量球 " + have + " / " + need + "   →   随机强化"
                : (canEvolve
                    ? "能量球 " + have + " / " + need + "   →   武器进化"
                    : "能量球 " + have + " / " + need + "   （火力满级后可进化）");
            GUI.Label(new Rect(bx, by + barH + 4f * k, 360f * k, 24f * k),
                label, Style(Mathf.RoundToInt(13 * k), barColor, TextAnchor.UpperLeft));
        }

        /// <summary>
        /// 收缩边界的四边灼烧带 + 倒计时。
        /// 预警期闪烁但不真的收，让玩家有时间挪窝；
        /// 进行中常亮并显示剩余秒数——玩家需要知道还要熬多久。
        /// </summary>
        /// <summary>
        /// 本局规则显示：开局 4.5 秒大字公告（名称+描述），之后右上角常驻小标。
        /// 常驻小标是必须的——「孤注」这种规则影响整局的决策，
        /// 只在开局念一遍，玩家三分钟后就忘了自己在打什么规则。
        /// </summary>
        private void DrawModifier(float k)
        {
            var m = _game.Modifier;
            if (m == RunModifier.None)
            {
                return;
            }
            float announce = _game.ModifierAnnounce;
            if (announce > 0f)
            {
                float a = Mathf.Clamp01(announce / 1.0f);   // 最后 1 秒淡出
                Color c = new Color(1f, 0.85f, 0.45f, a);
                GUI.Label(new Rect(0, Screen.height * 0.20f, Screen.width, 76f * k),
                    RunModifierInfo.Name(m),
                    Style(Mathf.RoundToInt(52 * k), c, TextAnchor.MiddleCenter, true));
                GUI.Label(new Rect(0, Screen.height * 0.20f + 70f * k, Screen.width, 30f * k),
                    RunModifierInfo.Desc(m),
                    Style(Mathf.RoundToInt(16 * k), new Color(c.r, c.g, c.b, a * 0.9f),
                        TextAnchor.MiddleCenter));
            }
            else
            {
                GUI.Label(new Rect(Screen.width - 270f * k, 44f * k, 246f * k, 24f * k),
                    "规则 " + RunModifierInfo.Name(m),
                    Style(Mathf.RoundToInt(12 * k),
                        new Color(1f, 0.85f, 0.45f, 0.65f), TextAnchor.UpperRight));
            }
        }

        private void DrawShrink(float k)
        {
            bool active = _game.ShrinkActive;
            bool warning = _game.ShrinkWarning;

            if (!active && !warning)
            {
                // 平静期：右上角一个不起眼的小倒计时，让节奏可预期
                GUI.Label(new Rect(Screen.width - 200f * k, 18f * k, 176f * k, 24f * k),
                    "边界收缩  " + Mathf.CeilToInt(Mathf.Max(0f, _game.ShrinkCountdown)) + "s",
                    Style(Mathf.RoundToInt(12 * k),
                        new Color(Dim.r, Dim.g, Dim.b, 0.45f), TextAnchor.UpperRight));
                return;
            }

            float blink = active
                ? 1f
                : (Mathf.Repeat(Time.unscaledTime, 0.4f) < 0.25f ? 1f : 0.12f);
            float alpha = (active ? 0.34f : 0.16f) * blink;

            // 世界单位 → 像素：屏幕高对应 24 个世界单位（orthographicSize = 12）
            float pxPerUnit = Screen.height / 24f;
            float band = 1.2f * pxPerUnit * (active ? 1f : 0.4f);
            Color burn = new Color(1f, 0.32f, 0.22f, alpha);

            Fill(new Rect(0, 0, Screen.width, band), burn);
            Fill(new Rect(0, Screen.height - band, Screen.width, band), burn);
            Fill(new Rect(0, 0, band, Screen.height), burn);
            Fill(new Rect(Screen.width - band, 0, band, Screen.height), burn);

            if (active)
            {
                GUI.Label(new Rect(0, Screen.height * 0.5f - 46f * k, Screen.width, 44f * k),
                    "边 界 收 缩   " + _game.ShrinkRemaining.ToString("0.0") + "s",
                    Style(Mathf.RoundToInt(22 * k), new Color(1f, 0.55f, 0.35f, 1f),
                        TextAnchor.MiddleCenter, true));
            }
        }

        /// <summary>
        /// 进化三选一。整卡可点，也支持 1/2/3 键盘选择——
        /// 弹幕游戏玩家的手基本长在键盘上，逼他们移去摸鼠标是打断心流的。
        /// </summary>
        private void DrawEvolution(float k)
        {
            Fill(new Rect(0, 0, Screen.width, Screen.height),
                new Color(0.02f, 0.04f, 0.07f, 0.9f));

            float cy = Screen.height * 0.13f;
            GUI.Label(new Rect(0, cy, Screen.width, 90f * k), "武 器 进 化",
                Style(Mathf.RoundToInt(54 * k), Gold, TextAnchor.MiddleCenter, true));
            GUI.Label(new Rect(0, cy + 84f * k, Screen.width, 30f * k),
                "选择一个分支 · 选定后不可更改",
                Style(Mathf.RoundToInt(15 * k), Dim, TextAnchor.MiddleCenter));

            float cardW = 290f * k;
            float cardH = 300f * k;
            float gap = 22f * k;
            float totalW = cardW * 3f + gap * 2f;
            float startX = (Screen.width - totalW) * 0.5f;
            float cardY = Screen.height * 0.30f;

            DrawEvolutionCard(k, startX, cardY, cardW, cardH, "潮  锋", "1",
                "单发粗弹，贯穿一整列敌人", "慢 · 贵 · 准", WeaponForm.TidePierce);
            DrawEvolutionCard(k, startX + cardW + gap, cardY, cardW, cardH, "扇  幕", "2",
                "五发大扇形，正面压制", "快 · 散 · 猛", WeaponForm.FanBarrage);
            DrawEvolutionCard(k, startX + (cardW + gap) * 2f, cardY, cardW, cardH, "回  澜", "3",
                "前后双向开火，没有盲区", "稳 · 全向", WeaponForm.EchoWave);

            GUI.Label(new Rect(0, cardY + cardH + 26f * k, Screen.width, 28f * k),
                "按 1 / 2 / 3 或点击卡片选择",
                Style(Mathf.RoundToInt(14 * k), new Color(Dim.r, Dim.g, Dim.b, 0.8f),
                    TextAnchor.MiddleCenter));

            if (Event.current.isKey && Event.current.type == EventType.KeyDown)
            {
                KeyCode key = Event.current.keyCode;
                if (key == KeyCode.Alpha1 || key == KeyCode.Keypad1)
                {
                    _game.Player.ChooseEvolution(WeaponForm.TidePierce);
                }
                else if (key == KeyCode.Alpha2 || key == KeyCode.Keypad2)
                {
                    _game.Player.ChooseEvolution(WeaponForm.FanBarrage);
                }
                else if (key == KeyCode.Alpha3 || key == KeyCode.Keypad3)
                {
                    _game.Player.ChooseEvolution(WeaponForm.EchoWave);
                }
            }
        }

        private void DrawEvolutionCard(float k, float x, float y, float w, float h,
            string name, string hotkey, string desc, string tag, WeaponForm form)
        {
            if (GUI.Button(new Rect(x, y, w, h), "",
                    ButtonStyle(Mathf.RoundToInt(24 * k), Main, new Color(0.05f, 0.15f, 0.21f, 1f))))
            {
                _game.Player.ChooseEvolution(form);
            }

            // 热键角标：让键盘玩家一眼看到对应关系
            GUI.Label(new Rect(x + 14f * k, y + 12f * k, 40f * k, 34f * k), hotkey,
                Style(Mathf.RoundToInt(20 * k), Gold, TextAnchor.UpperLeft, true));

            GUI.Label(new Rect(x, y + 44f * k, w, 50f * k), name,
                Style(Mathf.RoundToInt(34 * k), Cyan, TextAnchor.MiddleCenter, true));
            GUI.Label(new Rect(x + 22f * k, y + 116f * k, w - 44f * k, 110f * k), desc,
                Style(Mathf.RoundToInt(16 * k), Main, TextAnchor.MiddleCenter));
            GUI.Label(new Rect(x, y + h - 62f * k, w, 30f * k), tag,
                Style(Mathf.RoundToInt(14 * k), Gold, TextAnchor.MiddleCenter));
        }

        /// <summary>
        /// Build 展示：把已获得的升级列在屏幕左下。
        /// 这是「我在变强」的唯一持续证据——没有它，玩家只感觉到伤害数字变了，
        /// 但说不清自己这局走的是什么路线，重玩时也不会想换个 Build 试试。
        /// 而「换个 Build 再玩一局」正是重玩动力本身。
        /// </summary>
        private void DrawBuild(float k)
        {
            var p = _game.Player;
            var sb = new System.Text.StringBuilder();
            int count = 0;
            for (int i = 0; i < UpgradePool.Count; i++)
            {
                int s = p.UpgradeStacks(i);
                if (s <= 0)
                {
                    continue;
                }
                if (count > 0)
                {
                    sb.Append("   ");
                }
                sb.Append(UpgradePool.Get(i).Name);
                if (s > 1)
                {
                    sb.Append("×").Append(s);
                }
                count++;
                if (count >= 6)
                {
                    sb.Append("  …");
                    break;
                }
            }
            if (count == 0)
            {
                return;
            }
            if (p.ShieldCount > 0)
            {
                sb.Append("   ⛨×").Append(p.ShieldCount);
            }

            GUI.Label(new Rect(24f * k, Screen.height - 78f * k, 520f * k, 26f * k),
                "BUILD   " + sb.ToString(),
                Style(Mathf.RoundToInt(13 * k),
                    new Color(0.78f, 0.88f, 0.92f, 0.92f), TextAnchor.UpperLeft));
        }

        /// <summary>
        /// 随机三选一。整卡可点，也支持 1/2/3 键盘——
        /// 弹幕游戏玩家的手基本长在键盘上，逼他们移去摸鼠标是打断心流的。
        /// </summary>
        private void DrawUpgradeChoice(float k)
        {
            Fill(new Rect(0, 0, Screen.width, Screen.height),
                new Color(0.02f, 0.04f, 0.07f, 0.9f));

            float cy = Screen.height * 0.13f;
            GUI.Label(new Rect(0, cy, Screen.width, 90f * k), "选 择 强 化",
                Style(Mathf.RoundToInt(54 * k), Gold, TextAnchor.MiddleCenter, true));
            GUI.Label(new Rect(0, cy + 84f * k, Screen.width, 30f * k),
                "每次随机三个 · Build 由你自己拼出来",
                Style(Mathf.RoundToInt(15 * k), Dim, TextAnchor.MiddleCenter));

            float cardW = 290f * k;
            float cardH = 250f * k;
            float gap = 22f * k;
            float totalW = cardW * 3f + gap * 2f;
            float startX = (Screen.width - totalW) * 0.5f;
            float cardY = Screen.height * 0.30f;

            var choices = _game.Player.PendingChoices;
            for (int i = 0; i < choices.Length; i++)
            {
                DrawUpgradeCard(k, startX + (cardW + gap) * i, cardY, cardW, cardH,
                    choices[i], i + 1);
            }

            if (Event.current.isKey && Event.current.type == EventType.KeyDown)
            {
                KeyCode key = Event.current.keyCode;
                int idx = -1;
                if (key == KeyCode.Alpha1 || key == KeyCode.Keypad1)
                {
                    idx = 0;
                }
                else if (key == KeyCode.Alpha2 || key == KeyCode.Keypad2)
                {
                    idx = 1;
                }
                else if (key == KeyCode.Alpha3 || key == KeyCode.Keypad3)
                {
                    idx = 2;
                }
                if (idx >= 0)
                {
                    _game.Player.ChooseUpgrade(idx);
                }
            }
        }

        private void DrawUpgradeCard(float k, float x, float y, float w, float h,
            int id, int hotkey)
        {
            UpgradeDef def = UpgradePool.Get(id);
            bool valid = def.Id >= 0;
            int stacks = valid ? _game.Player.UpgradeStacks(id) : 0;

            if (GUI.Button(new Rect(x, y, w, h), "",
                    ButtonStyle(Mathf.RoundToInt(24 * k), Main,
                        valid ? new Color(0.05f, 0.15f, 0.21f, 1f)
                              : new Color(0.12f, 0.12f, 0.14f, 1f))))
            {
                if (valid)
                {
                    _game.Player.ChooseUpgrade(hotkey - 1);
                }
            }

            GUI.Label(new Rect(x + 14f * k, y + 12f * k, 40f * k, 34f * k), hotkey.ToString(),
                Style(Mathf.RoundToInt(20 * k), valid ? Gold : Dim, TextAnchor.UpperLeft, true));

            GUI.Label(new Rect(x, y + 46f * k, w, 50f * k),
                valid ? def.Name : "已满层",
                Style(Mathf.RoundToInt(32 * k), valid ? Cyan : Dim,
                    TextAnchor.MiddleCenter, true));

            GUI.Label(new Rect(x + 20f * k, y + 110f * k, w - 40f * k, 70f * k),
                valid ? def.Desc : "这个方向已经走到头了",
                Style(Mathf.RoundToInt(15 * k), valid ? Main : Dim, TextAnchor.MiddleCenter));

            if (valid)
            {
                // 层数显示：让玩家知道这是第几层、满层还有多远。
                // 没有层数显示，「射速+12%」叠到第 5 层时玩家是感觉不到的。
                GUI.Label(new Rect(x, y + h - 54f * k, w, 28f * k),
                    "已叠 " + stacks + " / " + def.MaxStacks + " 层",
                    Style(Mathf.RoundToInt(13 * k),
                        stacks >= def.MaxStacks ? Gold : Dim, TextAnchor.MiddleCenter));
            }
        }

        /// <summary>
        /// 赌徒模式 HUD。三种状态：开盘中（金色淡罩 + 倒计时 + 倍率）、
        /// 冷却中（小字）、可开盘（提示按键）。
        ///
        /// 只在连击 ≥10 时显示「可开盘」提示：低于这个数押上去没意义，
        /// 一直显示只会变成屏幕噪音，让玩家学会忽略 HUD。
        /// </summary>
        private void DrawGamble(float k)
        {
            if (_game.GambleActive)
            {
                Fill(new Rect(0, 0, Screen.width, Screen.height),
                    new Color(1f, 0.85f, 0.4f, 0.09f));

                float cy = Screen.height * 0.5f;
                GUI.Label(new Rect(0, cy - 86f * k, Screen.width, 46f * k),
                    "赌 局   " + _game.GambleRemaining.ToString("0.0") + "s",
                    Style(Mathf.RoundToInt(24 * k), Gold, TextAnchor.MiddleCenter, true));

                GUI.Label(new Rect(0, cy - 20f * k, Screen.width, 42f * k),
                    "x" + _game.ComboMultiplier.ToString("0.00"),
                    Style(Mathf.RoundToInt(32 * k), Gold, TextAnchor.MiddleCenter, true));

                GUI.Label(new Rect(0, cy + 22f * k, Screen.width, 26f * k),
                    "连击不断 → 倍率 ×2（封顶 16）    断了 → 归零",
                    Style(Mathf.RoundToInt(13 * k), new Color(1f, 0.9f, 0.62f, 0.92f),
                        TextAnchor.MiddleCenter));
                return;
            }

            float cd = _game.GambleCooldownRemaining;
            if (cd > 0f)
            {
                GUI.Label(new Rect(Screen.width - 270f * k, Screen.height - 104f * k, 246f * k, 24f * k),
                    "赌局冷却  " + cd.ToString("0.0") + "s",
                    Style(Mathf.RoundToInt(12 * k),
                        new Color(Dim.r, Dim.g, Dim.b, 0.5f), TextAnchor.UpperRight));
            }
            else if (_game.Combo >= 10)
            {
                GUI.Label(new Rect(Screen.width - 270f * k, Screen.height - 104f * k, 246f * k, 24f * k),
                    "Shift / J  开赌（倍率 ×2）",
                    Style(Mathf.RoundToInt(12 * k),
                        new Color(Gold.r, Gold.g, Gold.b, 0.75f), TextAnchor.UpperRight));
            }
        }

        private void DrawRecorderIndicator(float k)
        {
            if (_recorder == null)
            {
                _recorder = UnityEngine.Object.FindAnyObjectByType<TideShot.Control.ScreenRecorder>();
            }

            if (_recorder != null && _recorder.IsRecording)
            {
                // 录制中：右上角红点闪烁 + 已录时长
                float blink = Mathf.Repeat(Time.unscaledTime, 1f) < 0.6f ? 1f : 0.25f;
                float r = 8f * k;
                Rect dot = new Rect(Screen.width - 122f * k, 22f * k, r * 2f, r * 2f);
                GUI.color = new Color(1f, 0.2f, 0.25f, blink);
                GUI.DrawTexture(dot, _white);
                GUI.color = Color.white;

                int sec = Mathf.FloorToInt(_recorder.RecordedSeconds);
                GUI.Label(new Rect(Screen.width - 104f * k, 20f * k, 110f * k, 24f * k),
                    "REC " + (sec / 60).ToString("00") + ":" + (sec % 60).ToString("00"),
                    Style(Mathf.RoundToInt(14 * k), Red, TextAnchor.UpperLeft, true));
            }
            else
            {
                GUI.Label(new Rect(Screen.width - 150f * k, Screen.height - 70f * k, 140f * k, 22f * k),
                    "F9 录屏", Style(Mathf.RoundToInt(12 * k), new Color(Dim.r, Dim.g, Dim.b, 0.6f),
                        TextAnchor.UpperRight));
            }
        }

        private void DrawFloatingText(float k)
        {
            if (_cam == null)
            {
                return;
            }
            var list = FloatText.Snapshot();
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                Vector3 sp = _cam.WorldToScreenPoint(e.pos);
                if (sp.z < 0f)
                {
                    continue;
                }
                float y = Screen.height - sp.y;
                int size = Mathf.RoundToInt((e.big ? 24 : 17) * k);
                Color c = e.color;
                c.a = e.alpha;
                GUI.Label(new Rect(sp.x - 60f * k, y - 12f * k, 120f * k, 26f * k), e.text,
                    Style(size, c, TextAnchor.MiddleCenter, e.big));
            }
        }

        private void DrawCraftCard(float k, float x, float y, float w, float h,
            PlayerCraft craft, string name, string desc, string tag, string hotkey)
        {
            bool selected = _game.Player.Craft == craft;
            Color bg = selected ? new Color(0.05f, 0.24f, 0.30f, 1f)
                                : new Color(0.05f, 0.15f, 0.21f, 1f);
            if (GUI.Button(new Rect(x, y, w, h), "",
                    ButtonStyle(Mathf.RoundToInt(24 * k), Main, bg)))
            {
                _game.Player.Craft = craft;
            }

            Color nameColor = selected ? Cyan : new Color(Cyan.r, Cyan.g, Cyan.b, 0.55f);
            GUI.Label(new Rect(x, y + 16f * k, w, 44f * k), name,
                Style(Mathf.RoundToInt(28 * k), nameColor, TextAnchor.MiddleCenter, true));
            GUI.Label(new Rect(x + 14f * k, y + 74f * k, w - 28f * k, 56f * k), desc,
                Style(Mathf.RoundToInt(15 * k), Main, TextAnchor.MiddleCenter));
            GUI.Label(new Rect(x, y + h - 52f * k, w, 26f * k), tag,
                Style(Mathf.RoundToInt(13 * k), Gold, TextAnchor.MiddleCenter));
            GUI.Label(new Rect(x + 12f * k, y + 8f * k, 40f * k, 30f * k), hotkey,
                Style(Mathf.RoundToInt(17 * k), selected ? Gold : Dim, TextAnchor.UpperLeft, true));
        }

        private void DrawMenu(float k)
        {
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.02f, 0.04f, 0.07f, 0.82f));

            float cy = Screen.height * 0.3f;
            GUI.Label(new Rect(0, cy, Screen.width, 90f * k), "汐 弹",
                Style(Mathf.RoundToInt(72 * k), Cyan, TextAnchor.MiddleCenter, true));
            GUI.Label(new Rect(0, cy + 82f * k, Screen.width, 28f * k), "T I D E S H O T",
                Style(Mathf.RoundToInt(16 * k), Dim, TextAnchor.MiddleCenter));
            GUI.Label(new Rect(0, cy + 118f * k, Screen.width, 28f * k),
                "满屏弹幕 · 连锁爆炸 · 活多久算多久",
                Style(Mathf.RoundToInt(15 * k), Dim, TextAnchor.MiddleCenter));

            // 机体三选一：主菜单最大的变化。选择发生在开局前，
            // 「这局我要用什么打法」从第一秒就成立——Path Variety 从这里开始。
            float cw = 250f * k;
            float ch = 190f * k;
            float gap = 18f * k;
            float totalW = cw * 3f + gap * 2f;
            float startX = (Screen.width - totalW) * 0.5f;
            float craftY = Screen.height * 0.46f;

            DrawCraftCard(k, startX, craftY, cw, ch, PlayerCraft.Lone, "孤 潮",
                "均衡双发直线弹", "稳妥 · 全能", "1");
            DrawCraftCard(k, startX + cw + gap, craftY, cw, ch, PlayerCraft.Star, "碎 星",
                "单发重炮，击杀召碎片", "爆发 · 召唤", "2");
            DrawCraftCard(k, startX + (cw + gap) * 2f, craftY, cw, ch, PlayerCraft.Wall, "回 壁",
                "子弹撞墙反弹两次", "弹幕 · 封路", "3");

            if (Event.current.isKey && Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Alpha1) { _game.Player.Craft = PlayerCraft.Lone; }
                else if (Event.current.keyCode == KeyCode.Alpha2) { _game.Player.Craft = PlayerCraft.Star; }
                else if (Event.current.keyCode == KeyCode.Alpha3) { _game.Player.Craft = PlayerCraft.Wall; }
                else if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                {
                    _game.StartRun();
                }
            }

            float bw = 300f * k;
            float bh = 66f * k;
            if (GUI.Button(new Rect((Screen.width - bw) * 0.5f, Screen.height * 0.80f, bw, bh), "开 始",
                    ButtonStyle(Mathf.RoundToInt(24 * k), Main, new Color(0.06f, 0.22f, 0.28f, 1f))))
            {
                _game.StartRun();
            }

            string help =
                "WASD / 方向键  移动（自动开火）\n" +
                "ESC  暂停\n\n" +
                "击杀敌人会连锁引爆附近的敌人\n" +
                "连续击杀提升分数倍率，2.6 秒不断则重置";
            GUI.Label(new Rect(0, Screen.height * 0.72f, Screen.width, 160f * k), help,
                Style(Mathf.RoundToInt(15 * k), Dim, TextAnchor.UpperCenter));

            GUI.Label(new Rect(0, Screen.height - 44f * k, Screen.width, 24f * k),
                "最高分  " + _game.BestScore.ToString("N0"),
                Style(Mathf.RoundToInt(16 * k), Gold, TextAnchor.MiddleCenter));
        }

        private void DrawResult(float k)
        {
            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.04f, 0.02f, 0.03f, 0.86f));

            float cy = Screen.height * 0.28f;
            GUI.Label(new Rect(0, cy, Screen.width, 80f * k), "击 坠",
                Style(Mathf.RoundToInt(60 * k), Red, TextAnchor.MiddleCenter, true));

            GUI.Label(new Rect(0, cy + 96f * k, Screen.width, 50f * k),
                _game.Score.ToString("N0"),
                Style(Mathf.RoundToInt(46 * k), Main, TextAnchor.MiddleCenter, true));
            GUI.Label(new Rect(0, cy + 148f * k, Screen.width, 26f * k),
                "最高 " + _game.BestScore.ToString("N0") + "   ·   坚持 " + _game.RunTime.ToString("0.0") + " 秒   ·   WAVE " + _game.Wave,
                Style(Mathf.RoundToInt(15 * k), Dim, TextAnchor.MiddleCenter));

            float bw = 300f * k;
            float bh = 62f * k;
            if (GUI.Button(new Rect((Screen.width - bw) * 0.5f, Screen.height * 0.62f, bw, bh), "再 来 一 局",
                    ButtonStyle(Mathf.RoundToInt(22 * k), Main, new Color(0.06f, 0.22f, 0.28f, 1f))))
            {
                _game.StartRun();
            }
            if (GUI.Button(new Rect((Screen.width - bw) * 0.5f, Screen.height * 0.62f + bh + 16f * k, bw, bh), "返回标题",
                    ButtonStyle(Mathf.RoundToInt(20 * k), Dim, new Color(0.1f, 0.1f, 0.12f, 1f))))
            {
                _game.ReturnToMenu();
            }
        }

        private void Fill(Rect r, Color c)
        {
            if (_white == null)
            {
                MakeTex(Color.white);
            }
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = Color.white;
        }
    }
}
