using UnityEngine;

namespace TideShot.Core
{
    /// <summary>
    /// 轻量粒子系统。单 Mesh 批量绘制，最多 512 个粒子只占 1 个 draw call。
    /// 为什么不用 Unity 的 ParticleSystem：它需要配置预制体或运行时构建大量模块，
    /// 而这里只要「短命、纯色、受重力影响」的碎光，自己画更快也更可控。
    /// @trace AUD-001
    /// </summary>
    public sealed class ParticleFx : MonoBehaviour
    {
        private const int MaxParticles = 512;

        private struct Particle
        {
            public Vector2 Pos;
            public Vector2 Vel;
            public float Life;
            public float MaxLife;
            public float Size;
            public float Angle;
            public float Spin;
            public float Drag;
            public Color Color;
            public bool Alive;
        }

        private Particle[] _pool = new Particle[MaxParticles];
        private int _cursor;

        private Mesh _mesh;
        private MeshRenderer _renderer;
        private Vector3[] _verts;
        private Color32[] _colors;
        private int[] _tris;
        private int _activeCount;
        // 相机抖动叠在相机上，粒子本身不抖，否则手感会糊

        public void Initialize(Material material)
        {
            _pool = new Particle[MaxParticles];
            _cursor = 0;

            _verts = new Vector3[MaxParticles * 4];
            _colors = new Color32[MaxParticles * 4];
            _tris = new int[MaxParticles * 6];
            for (int i = 0; i < MaxParticles; i++)
            {
                int v = i * 4;
                int t = i * 6;
                _tris[t] = v;
                _tris[t + 1] = v + 1;
                _tris[t + 2] = v + 2;
                _tris[t + 3] = v;
                _tris[t + 4] = v + 2;
                _tris[t + 5] = v + 3;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "ParticleFxMesh" };
                _mesh.MarkDynamic();
            }
            var mf = gameObject.GetComponent<MeshFilter>();
            if (mf == null)
            {
                mf = gameObject.AddComponent<MeshFilter>();
            }
            mf.sharedMesh = _mesh;

            _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer == null)
            {
                _renderer = gameObject.AddComponent<MeshRenderer>();
            }
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.sortingOrder = 20;
        }

        /// <summary>圆形扩散爆散。用于击杀与受击。</summary>
        public void Burst(Vector2 position, int count, Color color,
            float speedMin, float speedMax, float lifeMin, float lifeMax,
            float sizeMin, float sizeMax, float drag = 3.5f)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = Random.value * Mathf.PI * 2f;
                float speed = Random.Range(speedMin, speedMax);
                Spawn(position, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed,
                    Random.Range(lifeMin, lifeMax), Random.Range(sizeMin, sizeMax),
                    color, drag, Random.Range(-360f, 360f));
            }
        }

        /// <summary>定向锥形喷射。用于命中火花沿着射线方向飞出。</summary>
        public void Spray(Vector2 position, Vector2 direction, float spreadDeg, int count, Color color,
            float speedMin, float speedMax, float lifeMin, float lifeMax,
            float sizeMin, float sizeMax, float drag = 5f)
        {
            float baseAngle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            for (int i = 0; i < count; i++)
            {
                float a = (baseAngle + Random.Range(-spreadDeg, spreadDeg)) * Mathf.Deg2Rad;
                float speed = Random.Range(speedMin, speedMax);
                Spawn(position, new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * speed,
                    Random.Range(lifeMin, lifeMax), Random.Range(sizeMin, sizeMax),
                    color, drag, Random.Range(-540f, 540f));
            }
        }

        /// <summary>环形冲击：用于升级与波次开始。</summary>
        public void Ring(Vector2 position, int count, float radius, Color color,
            float life, float size)
        {
            for (int i = 0; i < count; i++)
            {
                float a = (i / (float)count) * Mathf.PI * 2f;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Spawn(position + dir * radius, dir * (radius * 0.8f), life, size, color, 1.2f, 0f);
            }
        }

        private void Spawn(Vector2 pos, Vector2 vel, float life, float size, Color color, float drag, float spin)
        {
            // 环形覆盖：满了就顶掉最旧的，保证永远不丢新粒子而不爆池
            for (int tries = 0; tries < MaxParticles; tries++)
            {
                int idx = (_cursor + tries) % MaxParticles;
                if (!_pool[idx].Alive)
                {
                    _pool[idx] = new Particle
                    {
                        Pos = pos,
                        Vel = vel,
                        Life = life,
                        MaxLife = life,
                        Size = size,
                        Angle = Random.value * 360f,
                        Spin = spin,
                        Drag = drag,
                        Color = color,
                        Alive = true
                    };
                    _cursor = (idx + 1) % MaxParticles;
                    return;
                }
            }
            int oldest = _cursor;
            _pool[oldest] = new Particle
            {
                Pos = pos, Vel = vel, Life = life, MaxLife = life, Size = size,
                Angle = 0f, Spin = spin, Drag = drag, Color = color, Alive = true
            };
            _cursor = (_cursor + 1) % MaxParticles;
        }

        public void Tick(float dt)
        {
            if (_mesh == null)
            {
                return;
            }

            _activeCount = 0;

            for (int i = 0; i < MaxParticles; i++)
            {
                if (!_pool[i].Alive)
                {
                    continue;
                }

                Particle p = _pool[i];
                p.Life -= dt;
                if (p.Life <= 0f)
                {
                    p.Alive = false;
                    _pool[i] = p;
                    continue;
                }

                float damp = 1f - Mathf.Clamp01(p.Drag * dt);
                p.Vel *= damp;
                p.Pos += p.Vel * dt;
                p.Angle += p.Spin * dt;
                _pool[i] = p;

                WriteQuad(p, _activeCount);
                _activeCount++;
            }

            if (_activeCount == 0)
            {
                if (_mesh.vertexCount > 0)
                {
                    _mesh.Clear();
                }
                return;
            }

            _mesh.Clear();
            _mesh.vertices = _verts;
            _mesh.colors32 = _colors;
            _mesh.triangles = _tris;
            _mesh.RecalculateBounds();
        }

        private void WriteQuad(Particle p, int slot)
        {
            float t = p.Life / p.MaxLife;
            // 前 20% 生命快速放大，之后缓慢收缩 —— 有起势才有冲击感
            float grow = t > 0.8f ? Mathf.Lerp(1.4f, 1f, (1f - t) / 0.2f) : t;
            float half = p.Size * Mathf.Max(0.08f, grow) * 0.5f;
            float rad = p.Angle * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);

            Color c = p.Color;
            c.a *= Mathf.Clamp01(t * 1.6f);

            int v = slot * 4;
            _verts[v] = new Vector3(p.Pos.x + (-half * cos - -half * sin), p.Pos.y + (-half * sin + -half * cos), 0f);
            _verts[v + 1] = new Vector3(p.Pos.x + (half * cos - -half * sin), p.Pos.y + (half * sin + -half * cos), 0f);
            _verts[v + 2] = new Vector3(p.Pos.x + (half * cos - half * sin), p.Pos.y + (half * sin + half * cos), 0f);
            _verts[v + 3] = new Vector3(p.Pos.x + (-half * cos - half * sin), p.Pos.y + (-half * sin + half * cos), 0f);

            Color32 c32 = c;
            _colors[v] = c32;
            _colors[v + 1] = c32;
            _colors[v + 2] = c32;
            _colors[v + 3] = c32;
        }

        public int ActiveCount => _activeCount;

        public void ClearAll()
        {
            for (int i = 0; i < MaxParticles; i++)
            {
                _pool[i].Alive = false;
            }
            _activeCount = 0;
            if (_mesh != null)
            {
                _mesh.Clear();
            }
        }
    }
}
