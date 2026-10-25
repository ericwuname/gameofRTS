using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 舰队单位（MVP：单体舰=可移动的血量+火力）。
    /// 设计依据 @trace UNIT-001：炮舰22/登舰16/工程20，编队上限 6。
    /// MVP 简化披露：舰只承受交战损耗（敌节点半径内持续掉血），逐弹命中 LATER。
    /// </summary>
    public class Fleet
    {
        public enum Kind { Gunner, Boarder, Engineer }

        public Kind Type;
        public float Hp;
        public float MaxHp;
        public Vector2 Pos;
        public bool Alive = true;
        public GameObject Go;

        public int Cost => Type switch { Kind.Boarder => 16, Kind.Engineer => 20, _ => 22 };
        public int Upkeep => 2; // @trace ECON-001 MVP 统一维护费
        public float Damage => Type switch { Kind.Boarder => 18, Kind.Engineer => 2, _ => 6 };
        public float Speed => Type switch { Kind.Boarder => 8f, Kind.Engineer => 6f, _ => 5f };
        public float FireInterval => Type switch { Kind.Boarder => 1.6f, Kind.Engineer => 2.2f, _ => 0.9f };

        private float _fireTimer;
        private SpriteRenderer _sr;

        public static Fleet Spawn(Kind kind, Vector2 pos, Transform parent,
            Sprite hull, Sprite glow)
        {
            var go = new GameObject("Fleet-" + kind);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(pos.x, pos.y, 0f);
            var f = new Fleet
            {
                Type = kind,
                Pos = pos,
                MaxHp = kind switch { Kind.Boarder => 12f, Kind.Engineer => 15f, _ => 20f },
                Go = go,
            };
            f.Hp = f.MaxHp;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = hull;
            sr.sortingOrder = 7;
            f._sr = sr;
            if (glow != null)
            {
                var gGo = new GameObject("glow");
                gGo.transform.SetParent(go.transform, false);
                gGo.transform.localPosition = Vector3.zero;
                gGo.transform.localScale = new Vector3(1.6f, 1.6f, 1f);
                var gs = gGo.AddComponent<SpriteRenderer>();
                gs.sprite = glow;
                gs.sortingOrder = 6;
                gs.color = kind switch
                {
                    Kind.Boarder => new Color32(0xFF, 0x8A, 0x5C, 0x99),
                    Kind.Engineer => new Color32(0xB4, 0xFF, 0xE8, 0x88),
                    _ => new Color32(0x7F, 0xF0, 0xFF, 0x88),
                };
            }
            return f;
        }

        /// <summary>朝目标节点推进 + 自动索敌开火。dt=0 时仅开火（战略暂停不冻火力输出结算）。</summary>
        public void Tick(float dt, Vector2 dest, bool moving, BulletField bullets, Color col)
        {
            if (!Alive) { return; }
            if (moving)
            {
                Vector2 dir = dest - Pos;
                float dist = dir.magnitude;
                float step = Speed * dt;
                if (dist <= step) { Pos = dest; }
                else { Pos += dir.normalized * step; }
            }
            Go.transform.position = new Vector3(Pos.x, Pos.y, 0f);

            // 自动索敌：舰队火力是攻城主力 @trace LOOP-001
            _fireTimer -= dt;
            if (_fireTimer <= 0f && bullets != null)
            {
                _fireTimer = FireInterval;
                bullets.SpawnPlayerBullet(Pos + Vector2.up * 0.3f, Vector2.right,
                    12f, Mathf.RoundToInt(Damage), col);
            }
        }

        /// <summary>交战损耗与维修。返回 false 表示沉没。</summary>
        public bool ApplyAttrition(float dps, float dt, float repair)
        {
            if (!Alive) { return false; }
            Hp += (repair - dps) * dt;
            if (_sr != null)
            {
                float f = Mathf.Clamp01(Hp / MaxHp);
                _sr.color = new Color(1f, 0.35f + 0.6f * f, 0.35f + 0.6f * f, 1f);
            }
            if (Hp <= 0f)
            {
                Alive = false;
                if (Go != null) { Object.Destroy(Go); }
                return false;
            }
            return true;
        }
    }
}
