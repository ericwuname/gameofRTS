using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 能量球：敌人死亡掉落，玩家磁吸拾取，攒够数量触发武器进化。
    ///
    /// 为什么它是这一轮的核心：原来「守角」是唯一最优解——角落安全、
    /// 自动开火覆盖广、连击在角落也能续住，玩家没有任何理由离开那个位置。
    /// 加了一个「必须移动才能拿到」的资源之后，守角从「最优解」
    /// 变成「放弃成长」，决策才真正出现。
    ///
    /// 磁吸半径 4.0：够大，让「去捡球」这个行为不显得像苦力活；
    /// 又不够大，待在角落正中央是捡不到的——必须真的挪过去。
    /// </summary>
    public sealed class EnergyOrbField : MonoBehaviour
    {
        private const int MaxOrbs = 256;

        // 磁吸半径 4.0 → 2.2。4.0 是个数值错误：舞台只有 ±5.8 宽，
        // 玩家站在中心不动就能吸到 |x|<4 以内的球，而敌机只在 ±5.6 活动——
        // 等于「几乎全图自动吸球」。后果是「去捡球」这个动作根本不存在，
        // 玩家站在原地球就自己飞过来了，走位因此失去资源层面的驱动力，
        // 只剩「躲弹」一个目的，于是蹲角落成了理性选择。
        // 2.2 意味着玩家必须真的挪过去，移动才同时承载躲弹/捡球/（将来的）擦弹三个目的。
        private const float MagnetRadius = 2.2f;
        private const float CollectRadius = 0.6f;
        private const float DriftAccel = 2.6f;
        private const float MagnetAccel = 26f;
        private const float Damping = 2.4f;
        private const float Lifetime = 12f;

        /// <summary>磁吸范围系数。由玩家的「引力」升级驱动，默认 1。
        /// 做成公开字段而不是在 Tick 里传参：升级的入口以后可能变多
        /// （进化、随机升级、事件奖励），每加一个入口就多一个传参点，
        /// 漏一个就表现为「买了没生效」。</summary>
        public float MagnetScale = 1f;

        private struct Orb
        {
            public Vector2 Pos;
            public Vector2 Vel;
            public float Age;
            public bool Alive;
        }

        private Orb[] _pool = new Orb[MaxOrbs];
        private int _cursor;
        private BatchMesh _mesh;

        public int AliveCount { get; private set; }

        /// <summary>本帧被拾取的数量。由 GameDirector 读走并转给玩家。</summary>
        public int CollectedThisFrame { get; private set; }

        public void Initialize(Material material)
        {
            _pool = new Orb[MaxOrbs];
            _cursor = 0;

            var go = new GameObject("OrbMesh");
            go.transform.SetParent(transform, false);
            _mesh = go.AddComponent<BatchMesh>();
            _mesh.Initialize(material, 10);
        }

        public void Spawn(Vector2 pos, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int idx = -1;
                for (int t = 0; t < MaxOrbs; t++)
                {
                    int c = (_cursor + t) % MaxOrbs;
                    if (!_pool[c].Alive)
                    {
                        idx = c;
                        break;
                    }
                }
                // 环形覆盖：满了顶掉最旧的，保证不丢新球也不爆池
                if (idx < 0)
                {
                    idx = _cursor;
                }
                _pool[idx] = new Orb
                {
                    Pos = pos + UnityEngine.Random.insideUnitCircle * 0.35f,
                    Vel = UnityEngine.Random.insideUnitCircle * 2.2f,
                    Age = 0f,
                    Alive = true
                };
                _cursor = (idx + 1) % MaxOrbs;
            }
        }

        /// <summary>推进一帧。返回本帧拾取数。</summary>
        public int Tick(float dt, Vector2 playerPos, bool playerAlive)
        {
            CollectedThisFrame = 0;
            if (_mesh == null)
            {
                return 0;
            }
            _mesh.Clear();
            int alive = 0;

            for (int i = 0; i < MaxOrbs; i++)
            {
                if (!_pool[i].Alive)
                {
                    continue;
                }
                Orb o = _pool[i];
                o.Age += dt;

                if (playerAlive)
                {
                    Vector2 toPlayer = playerPos - o.Pos;
                    float dist = toPlayer.magnitude;
                    if (dist < CollectRadius)
                    {
                        _pool[i].Alive = false;
                        CollectedThisFrame++;
                        continue;
                    }
                    if (dist < MagnetRadius * MagnetScale)
                    {
                        // 越近吸力越强，但保留初速惯性——看起来是「被拽过去」而不是瞬移
                        float pull = 1f - dist / MagnetRadius;
                        o.Vel += toPlayer.normalized * (MagnetAccel * pull * dt);
                    }
                    else
                    {
                        o.Vel += Vector2.down * (DriftAccel * dt);
                    }
                }
                else
                {
                    o.Vel += Vector2.down * (DriftAccel * dt);
                }

                o.Vel *= 1f - Mathf.Clamp01(Damping * dt);
                o.Pos += o.Vel * dt;

                if (o.Age > Lifetime || o.Pos.y < -13f || Mathf.Abs(o.Pos.x) > 9f)
                {
                    _pool[i].Alive = false;
                    continue;
                }
                _pool[i] = o;
                alive++;

                // 出生 0.15 秒内放大：避免「凭空冒出来」的廉价感
                float grow = Mathf.Min(1f, o.Age / 0.15f);
                float r = 0.17f * grow;
                Color c = OrbColor;
                c.a = 0.95f * grow;
                _mesh.AddCircle(o.Pos, r, c);
                _mesh.AddCircle(o.Pos, r * 1.9f, new Color(c.r, c.g, c.b, 0.22f * grow));
            }

            AliveCount = alive;
            _mesh.Commit();
            return CollectedThisFrame;
        }

        private static readonly Color OrbColor = new Color32(0xFF, 0xD2, 0x4D, 0xFF);

        public void ClearAll()
        {
            for (int i = 0; i < MaxOrbs; i++)
            {
                _pool[i].Alive = false;
            }
            AliveCount = 0;
            CollectedThisFrame = 0;
        }

        /// <summary>
        /// 采样离给定点最近的若干个能量球位置。给外部控制接口（AI 玩家）用。
        /// 没有这个，AI 只知道「哪里有危险」而不知道「哪里有机会」，
        /// 于是只会躲不会打——而躲是这款游戏里最没有价值的单一行为。
        /// </summary>
        public System.Collections.Generic.List<Vector2> SampleNearest(Vector2 origin, int max)
        {
            var result = new System.Collections.Generic.List<Vector2>(max);
            var dists = new System.Collections.Generic.List<float>(max);
            for (int i = 0; i < MaxOrbs; i++)
            {
                if (!_pool[i].Alive)
                {
                    continue;
                }
                float d = (_pool[i].Pos - origin).sqrMagnitude;
                int insert = result.Count;
                for (int k = 0; k < result.Count; k++)
                {
                    if (d < dists[k])
                    {
                        insert = k;
                        break;
                    }
                }
                if (insert < max)
                {
                    result.Insert(insert, _pool[i].Pos);
                    dists.Insert(insert, d);
                    if (result.Count > max)
                    {
                        result.RemoveAt(max);
                        dists.RemoveAt(max);
                    }
                }
            }
            return result;
        }
    }
}
