using System;
using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>宝箱能开出的东西。</summary>
    public enum ChestReward
    {
        /// <summary>一堆能量球（直接够一次升级）。</summary>
        Orbs = 0,
        /// <summary>+1 命。</summary>
        Life = 1,
        /// <summary>无敌 3 秒。</summary>
        Invuln = 2,
        /// <summary>狂热 10 秒（射速 ×2）。</summary>
        Frenzy = 3,
        /// <summary>全屏敌弹肃清。</summary>
        Nuke = 4
    }

    /// <summary>
    /// 宝箱：定时在屏幕上刷一个，玩家碰到即开，开出什么**开之前不知道**。
    ///
    /// 为什么需要它：这是「可变比率强化」的最小实现——不确定的奖励
    /// 是行为心理学里成瘾性最强的奖励时间表（老虎机就是这个）。
    /// 固定奖励给一次爽感，随机奖励给持续的「再开一个看看」。
    ///
    /// 0.5 秒开启动画不是装饰：揭晓前的那半秒悬念（光晕收缩 + 音效爬升）
    /// 正是「开箱」这件事的全部快感所在。瞬间到账等于没有开箱。
    /// </summary>
    public sealed class RewardField : MonoBehaviour
    {
        private const int MaxChests = 6;
        private const float SpawnInterval = 18f;

        /// <summary>「丰潮」规则：刷新间隔减半。做成字段而非构造参数——
        /// modifier 是每局开局才定的，构造发生在更早。</summary>
        public bool Halved { get; set; }
        private const float OpenDuration = 0.5f;
        private const float ChestRadius = 0.42f;
        private const float PickupRadius = 0.85f;

        private struct Chest
        {
            public Vector2 Pos;
            public float Age;
            public bool Alive;
            public bool Opening;
            public float OpenTimer;
            public ChestReward Reward;
        }

        private Chest[] _pool = new Chest[MaxChests];
        private int _cursor;
        private BatchMesh _mesh;
        private float _spawnTimer;

        /// <summary>宝箱被打开。参数：奖励类型、位置。</summary>
        public event Action<ChestReward, Vector2> Opened;

        public int AliveCount { get; private set; }

        public void Initialize(Material material)
        {
            _pool = new Chest[MaxChests];
            _cursor = 0;
            _spawnTimer = SpawnInterval;

            var go = new GameObject("ChestMesh");
            go.transform.SetParent(transform, false);
            _mesh = go.AddComponent<BatchMesh>();
            _mesh.Initialize(material, 11);
        }

        public void ResetTimer()
        {
            _spawnTimer = SpawnInterval;
        }

        public void Tick(float dt, Vector2 playerPos, bool playerAlive)
        {
            if (_mesh == null)
            {
                return;
            }

            if (playerAlive)
            {
                _spawnTimer -= dt;
                if (_spawnTimer <= 0f)
                {
                    _spawnTimer = Halved ? SpawnInterval * 0.5f : SpawnInterval;
                    SpawnOne();
                }
            }

            _mesh.Clear();
            int alive = 0;

            for (int i = 0; i < MaxChests; i++)
            {
                if (!_pool[i].Alive)
                {
                    continue;
                }
                Chest c = _pool[i];
                c.Age += dt;

                if (!c.Opening && playerAlive)
                {
                    float d = (playerPos - c.Pos).sqrMagnitude;
                    if (d < PickupRadius * PickupRadius)
                    {
                        // 碰到即进入开启动画，奖励在动画结束时才发放
                        c.Opening = true;
                        c.OpenTimer = OpenDuration;
                        Sfx.WaveStart();
                    }
                }

                if (c.Opening)
                {
                    c.OpenTimer -= dt;
                    if (c.OpenTimer <= 0f)
                    {
                        _pool[i].Alive = false;
                        Opened?.Invoke(c.Reward, c.Pos);
                        continue;
                    }
                }

                // 开启动画：光晕先胀后缩，制造「有什么要出来了」的预感
                float t = c.Opening ? 1f - Mathf.Clamp01(c.OpenTimer / OpenDuration) : 0f;
                float scale = c.Opening
                    ? Mathf.Lerp(1.5f, 2.4f, t) * (1f + Mathf.Sin(t * 22f) * 0.12f * (1f - t))
                    : 1f + Mathf.Sin(c.Age * 3.2f) * 0.08f;

                Color body = ChestBody;
                body.a = 0.95f;
                _mesh.AddCircle(c.Pos, ChestRadius * scale, body);

                // 外圈光晕：不开时缓慢呼吸，开启时急速收缩
                float haloR = c.Opening
                    ? Mathf.Lerp(ChestRadius * 2.6f, ChestRadius * 1.2f, t)
                    : ChestRadius * (2.1f + Mathf.Sin(c.Age * 2.4f) * 0.3f);
                _mesh.AddCircle(c.Pos, haloR,
                    new Color(body.r, body.g, body.b, c.Opening ? 0.5f * (1f - t) : 0.3f));

                _pool[i] = c;
                alive++;
            }

            AliveCount = alive;
            _mesh.Commit();
        }

        private static readonly Color ChestBody = new Color32(0xFF, 0xC8, 0x4D, 0xFF);

        private void SpawnOne()
        {
            // 优先复用空格，满了就顶掉最旧的
            int idx = -1;
            for (int t = 0; t < MaxChests; t++)
            {
                int c = (_cursor + t) % MaxChests;
                if (!_pool[c].Alive)
                {
                    idx = c;
                    break;
                }
            }
            if (idx < 0)
            {
                idx = _cursor;
            }

            _pool[idx] = new Chest
            {
                Pos = new Vector2(UnityEngine.Random.Range(-5f, 5f),
                    UnityEngine.Random.Range(-8.5f, 7.5f)),
                Age = 0f,
                Alive = true,
                Opening = false,
                OpenTimer = 0f,
                Reward = RollReward()
            };
            _cursor = (idx + 1) % MaxChests;
        }

        /// <summary>
        /// 摇奖励。权重刻意做成「好东西稀有但可达」：
        /// 全屏清屏最强所以最稀（8%），能量球最常见（40%），
        /// 命最稳（20%）——因为它收益确定但没有惊喜。
        /// </summary>
        private static ChestReward RollReward()
        {
            float r = UnityEngine.Random.value;
            if (r < 0.40f) return ChestReward.Orbs;
            if (r < 0.60f) return ChestReward.Life;
            if (r < 0.76f) return ChestReward.Invuln;
            if (r < 0.92f) return ChestReward.Frenzy;
            return ChestReward.Nuke;
        }

        public void ClearAll()
        {
            for (int i = 0; i < MaxChests; i++)
            {
                _pool[i].Alive = false;
            }
            AliveCount = 0;
            _spawnTimer = SpawnInterval;
        }
    }
}
