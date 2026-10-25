using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 矿车：红警式经济节拍器 @trace ECON-001。
    /// 红警为什么好玩（2026-10 调研）：矿车自动往返、每趟倒矿——
    /// 经济是「看得见的钱在流动」，而不是结算屏上的数字。本类复刻该体验：
    /// 占领矿脉即自动出车，往返矿点↔母港，每趟 +3 潮力。
    /// </summary>
    public class Harvester
    {
        private static readonly Color OreColor = new Color32(0xFF, 0xD2, 0x4D, 0xFF);
        private const float LegTime = 1.4f;   // 单程秒数（往返 2.8s 一趟）
        public const int Haul = 3;            // 每趟运回

        public bool Loaded;                   // true=满载返港, false=空载去矿
        public float LegTimer;
        public Vector2 Pos;
        public GameObject Go;
        public bool Alive = true;
        public int SourceId;                  // 所属矿/精炼节点

        private readonly Vector2 _minePos;
        private readonly Vector2 _homePos;
        private readonly SpriteRenderer _sr;

        public Harvester(Vector2 minePos, Vector2 homePos, Transform parent,
            Sprite hull, Sprite glow)
        {
            _minePos = minePos;
            _homePos = homePos;
            Pos = minePos;
            Loaded = false;
            LegTimer = LegTime;

            Go = new GameObject("Harvester");
            Go.transform.SetParent(parent, false);
            Go.transform.position = new Vector3(Pos.x, Pos.y, 0f);
            _sr = Go.AddComponent<SpriteRenderer>();
            _sr.sprite = hull;
            _sr.sortingOrder = 6;
            _sr.color = OreColor;
            if (glow != null)
            {
                var gGo = new GameObject("glow");
                gGo.transform.SetParent(Go.transform, false);
                gGo.transform.localScale = new Vector3(1.4f, 1.4f, 1f);
                var gs = gGo.AddComponent<SpriteRenderer>();
                gs.sprite = glow;
                gs.sortingOrder = 5;
                gs.color = new Color(OreColor.r, OreColor.g, OreColor.b, 0.5f);
            }
        }

        /// <summary>往返一趟返回运到的潮力（0 或 Haul）。</summary>
        public int Tick(float dt)
        {
            if (!Alive) { return 0; }
            LegTimer -= dt;
            float frac = 1f - Mathf.Clamp01(LegTimer / LegTime);
            Vector2 from = Loaded ? _minePos : _homePos;
            Vector2 to = Loaded ? _homePos : _minePos;
            Pos = Vector2.Lerp(from, to, frac);
            Go.transform.position = new Vector3(Pos.x, Pos.y, 0f);

            if (LegTimer <= 0f)
            {
                if (Loaded)
                {
                    Loaded = false;           // 倒矿，返空车
                    LegTimer = LegTime;
                    return Haul;
                }
                Loaded = true;                // 装矿，返程
                LegTimer = LegTime;
            }
            return 0;
        }

        public void Destroy()
        {
            Alive = false;
            if (Go != null) { Object.Destroy(Go); }
        }
    }
}
