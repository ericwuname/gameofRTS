using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 表现层：冲击波环、粒子、镜头震动。
    /// 「爽感」的大头在这里——命中要有火花，死亡要有向外扩张的环，
    /// 大爆炸要抖屏。缺了这些，弹幕再多也只是「一堆点在移动」。
    /// </summary>
    public sealed class FxLayer : MonoBehaviour
    {
        private const int WaveCapacity = 32;

        private struct Wave
        {
            public Vector2 Pos;
            public float Age;
            public float Duration;
            public float MaxRadius;
            public Color Color;
            public bool Alive;
        }

        private ParticleFx _particles;
        private BatchMesh _waves;
        private Wave[] _wavePool = new Wave[WaveCapacity];
        private int _waveCursor;

        private float _shakeRemaining;
        private float _shakeDuration;
        private float _shakeMagnitude;

        public static FxLayer Instance { get; private set; }

        public void Initialize(Material material)
        {
            Instance = this;

            var pGo = new GameObject("Particles");
            pGo.transform.SetParent(transform, false);
            _particles = pGo.AddComponent<ParticleFx>();
            _particles.Initialize(material);

            var wGo = new GameObject("Shockwaves");
            wGo.transform.SetParent(transform, false);
            _waves = wGo.AddComponent<BatchMesh>();
            _waves.Initialize(material, 12);
        }

        public void Tick(float dt)
        {
            _particles?.Tick(dt);
            TickWaves(dt);
            TickShake(dt);
        }

        private void TickWaves(float dt)
        {
            if (_waves == null)
            {
                return;
            }
            _waves.Clear();

            for (int i = 0; i < WaveCapacity; i++)
            {
                if (!_wavePool[i].Alive)
                {
                    continue;
                }
                Wave w = _wavePool[i];
                w.Age += dt;
                if (w.Age >= w.Duration)
                {
                    w.Alive = false;
                    _wavePool[i] = w;
                    continue;
                }
                _wavePool[i] = w;

                float t = w.Age / w.Duration;
                float radius = Mathf.Lerp(0.2f, w.MaxRadius, Mathf.Sqrt(t));
                float alpha = Mathf.Pow(1f - t, 1.6f);
                float thickness = Mathf.Lerp(0.28f, 0.06f, t);

                // 用一圈小方块拼出冲击环，比贴图更可控也更省
                int segments = 28;
                for (int s = 0; s < segments; s++)
                {
                    float a = (s / (float)segments) * Mathf.PI * 2f;
                    Vector2 p = w.Pos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
                    float tangent = a * Mathf.Rad2Deg + 90f;
                    Color c = w.Color;
                    c.a *= alpha;
                    _waves.Add(p, new Vector2(thickness * 2f, thickness), tangent, c);
                }
            }

            _waves.Commit();
        }

        private void TickShake(float dt)
        {
            if (_shakeRemaining <= 0f)
            {
                return;
            }
            _shakeRemaining = Mathf.Max(0f, _shakeRemaining - dt);
        }

        public Vector2 ShakeOffset()
        {
            if (_shakeRemaining <= 0f || _shakeMagnitude <= 0f || _shakeDuration <= 0f)
            {
                return Vector2.zero;
            }
            float k = _shakeRemaining / _shakeDuration;
            float amp = _shakeMagnitude * k;
            return new Vector2(Random.Range(-amp, amp), Random.Range(-amp, amp));
        }

        /// <summary>爆炸：冲击环 + 碎屑 + 抖屏。</summary>
        public void Explosion(Vector2 pos, float radius, Color color, bool big)
        {
            SpawnWave(pos, radius, color);

            if (_particles != null)
            {
                _particles.Burst(pos, big ? 28 : 12, color,
                    2f, big ? 14f : 8f,
                    0.22f, big ? 0.8f : 0.45f,
                    0.12f, big ? 0.34f : 0.22f, 3.4f);
                _particles.Burst(pos, big ? 12 : 6, Color.white,
                    1f, big ? 9f : 5f,
                    0.12f, 0.3f,
                    0.1f, 0.2f, 5f);
            }

            Shake(big ? 0.34f : 0.14f, big ? 0.5f : 0.16f);
        }

        /// <summary>命中：小火花 + 极短抖屏。</summary>
        public void Spark(Vector2 pos, Vector2 dir, Color color, bool crit)
        {
            if (_particles != null)
            {
                // 粒子数 4 → 7。命中反馈是「我在造成伤害」的唯一即时证据，
                // 4 颗在满屏弹幕里基本看不见；而看不见的命中等于没命中。
                _particles.Spray(pos, dir, 50f, crit ? 12 : 7, color,
                    2f, crit ? 7f : 4.5f, 0.1f, 0.2f, 0.08f, 0.14f, 6f);
            }
            if (crit)
            {
                Shake(0.08f, 0.1f);
            }
        }

        /// <summary>玩家出生 / 复活：一圈向外扩散的大环。</summary>
        public void SpawnPulse(Vector2 pos, Color color)
        {
            SpawnWave(pos, 2.6f, color);
        }

        private void SpawnWave(Vector2 pos, float radius, Color color)
        {
            for (int i = 0; i < WaveCapacity; i++)
            {
                int idx = (_waveCursor + i) % WaveCapacity;
                if (_wavePool[idx].Alive)
                {
                    continue;
                }
                _wavePool[idx] = new Wave
                {
                    Pos = pos,
                    Age = 0f,
                    Duration = 0.38f,
                    MaxRadius = radius,
                    Color = color,
                    Alive = true
                };
                _waveCursor = (idx + 1) % WaveCapacity;
                return;
            }
        }

        public void Shake(float duration, float magnitude)
        {
            if (duration > _shakeRemaining || magnitude > _shakeMagnitude)
            {
                _shakeRemaining = Mathf.Max(_shakeRemaining, duration);
                _shakeDuration = Mathf.Max(_shakeDuration, duration);
                _shakeMagnitude = Mathf.Max(_shakeMagnitude, magnitude);
            }
        }

        public void ClearAll()
        {
            for (int i = 0; i < WaveCapacity; i++)
            {
                _wavePool[i].Alive = false;
            }
            _particles?.ClearAll();
            _shakeRemaining = 0f;
            _shakeMagnitude = 0f;
        }
    }
}
