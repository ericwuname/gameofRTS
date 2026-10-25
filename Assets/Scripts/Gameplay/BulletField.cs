using System;
using System.Collections.Generic;
using UnityEngine;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 弹幕系统。上千颗子弹跑在一个数组里，全部由 BatchMesh 一次绘制。
    /// 碰撞在这里一并处理，通过事件通知外部，避免遍历顺序在两处各写一遍。
    /// </summary>
    public sealed class BulletField : MonoBehaviour
    {
        private const int Capacity = 1024;

        /// <summary>穿透弹命中后的无判定时间。0.12s × 弹速约 34 ≈ 4 单位，
        /// 足够飞出任何敌人的判定半径，又不会漏掉排成一串的敌人。</summary>
        private const float PierceImmunity = 0.12f;

        private struct Bullet
        {
            public Vector2 Pos;
            public Vector2 Vel;
            public float Radius;
            public float Length;
            public float Life;
            public Color Color;
            public int Damage;
            public bool FromPlayer;
            public bool Alive;
            public bool Homing;
            /// <summary>剩余可穿透敌人数。-1 = 普通弹，命中即消失。</summary>
            public int PierceHits;
            /// <summary>命中后的无判定剩余时间。
            /// 没有它的话，穿透弹穿过敌人时敌人不会立刻离开判定半径，
            /// 同一颗弹会每帧都算一次命中，一个敌人被打成十几刀。</summary>
            public float PierceCooldown;
            /// <summary>这颗敌弹是否已经擦过玩家。每颗只擦一次——
            /// 否则同一颗弹贴着玩家飞的那几帧会连擦十几次，张力瞬间满。</summary>
            public bool Grazed;

            // ---- 潮旋（曲线弹道）----
            /// <summary>正弦横移幅度（世界单位/秒）。「潮旋」升级驱动。
            /// 0 = 直线。曲线弹道让火力从「一条线」变成「一片扇」，且可绕过前排敌人。</summary>
            public float WaveAmp;
            public float WaveFreq;
            public float WavePhase;
            public float Age;
            /// <summary>剩余反弹次数（「回壁」机体）。0 = 出界即销毁。</summary>
            public int Bounces;
            /// <summary>玩家侧追踪弹（「碎星」机体的碎片）：追最近的敌机。</summary>
            public bool HomingEnemy;
        }

        /// <summary>「碎星」机体：发射一枚追踪最近敌机的碎片。</summary>
        public void SpawnHomingBullet(Vector2 pos, float speed, int damage, Color color)
        {
            for (int tries = 0; tries < Capacity; tries++)
            {
                int idx = (_cursor + tries) % Capacity;
                if (_bullets[idx].Alive) { continue; }
                _bullets[idx] = new Bullet
                {
                    Pos = pos,
                    Vel = UnityEngine.Random.insideUnitCircle.normalized * speed,
                    Radius = 0.11f, Length = 0.6f, Life = 4f,
                    Color = color, Damage = damage,
                    FromPlayer = true, HomingEnemy = true,
                    PierceHits = -1, PierceCooldown = 0f, Bounces = 0, Alive = true
                };
                _cursor = (idx + 1) % Capacity;
                return;
            }
        }

        /// <summary>发射带正弦弧线的玩家弹（「潮旋」升级）。
        /// amp 为横移幅度，freq 为频率。amp=0 退化为直线。</summary>
        public void SpawnWaveBullet(Vector2 pos, Vector2 dir, float speed, int damage, Color color,
            float amp, float freq)
        {
            for (int tries = 0; tries < Capacity; tries++)
            {
                int idx = (_cursor + tries) % Capacity;
                if (_bullets[idx].Alive)
                {
                    continue;
                }
                _bullets[idx] = new Bullet
                {
                    Pos = pos,
                    Vel = dir.normalized * speed,
                    Radius = 0.13f,
                    Length = 1.1f,
                    Life = 3.5f,
                    Color = color,
                    Damage = damage,
                    FromPlayer = true,
                    Homing = false,
                    PierceHits = -1,
                    PierceCooldown = 0f,
                    Grazed = false,
                    WaveAmp = amp,
                    WaveFreq = freq,
                    WavePhase = UnityEngine.Random.value * 6.28f,
                    Age = 0f,
                    Alive = true
                };
                _cursor = (idx + 1) % Capacity;
                return;
            }
        }

        /// <summary>擦弹判定半径：敌弹离玩家中心在这个距离内、但还没命中，算擦弹。
        /// 0.75 的选取：玩家判定半径 0.26 + 敌弹半径 0.2 = 0.46 才会命中，
        /// 所以 0.46~0.75 是一条约 0.29 单位宽的「贴身但安全」带。
        /// 太窄擦不到（没有奖励感），太宽会变成「随便走走就在擦」（稀释成就感）。</summary>
        private const float GrazeRadius = 0.75f;

        /// <summary>敌弹贴身掠过玩家但未命中。参数：擦弹位置。</summary>
        public event Action<Vector2> Grazed;

        private Bullet[] _bullets = new Bullet[Capacity];
        private int _cursor;
        private Core.BatchMesh _mesh;
        private EnemyManager _enemies;
        private PlayerShip _player;

        public int AliveCount { get; private set; }

        /// <summary>玩家子弹命中敌人。参数：敌人、命中点。</summary>
        public event Action<EnemyShip, Vector2> EnemyHit;

        /// <summary>敌弹命中玩家。</summary>
        public event Action<Vector2> PlayerHit;

        public void Initialize(Material material, EnemyManager enemies, PlayerShip player)
        {
            _enemies = enemies;
            _player = player;

            var go = new GameObject("BulletMesh");
            go.transform.SetParent(transform, false);
            _mesh = go.AddComponent<Core.BatchMesh>();
            _mesh.Initialize(material, 10);
        }

        public void ClearAll()
        {
            for (int i = 0; i < Capacity; i++)
            {
                _bullets[i].Alive = false;
            }
            AliveCount = 0;
            _mesh?.Clear();
            _mesh?.Commit();
        }

        // ---------------- 发射 ----------------

        public void SpawnPlayerBullet(Vector2 pos, Vector2 dir, float speed, int damage, Color color, int bounces = 0)
        {
            // 半径 0.13 / 弹道 1.1（原来是 0.085 / 0.7）。
            //
            // 为什么放大：敌弹半径 0.2 还带光晕，而玩家弹只有 0.085——
            // 比敌人小一半还多。帧序列里玩家的火力在视觉上被满屏敌弹
            // 完全压倒，「我打出去的东西毫无力量」正是「爽感不够」
            // 最直接的来源。玩家的弹必须比敌人的弹更有视觉重量，
            // 这不是夸张，是基本的可读性：先看清自己打了什么，才谈得上爽。
            Spawn(pos, dir.normalized * speed, damage, color, 0.13f, 1.1f, true, false, 3f, -1, bounces);
        }

        /// <summary>
        /// 穿透弹（武器进化「潮锋」用）。pierceHits = 能依次穿透几个敌人。
        /// 半径和弹道都比普通弹大——视觉上必须一眼看出「这不是一般的弹」，
        /// 否则玩家花了资源进化，却看不出任何变化。
        /// </summary>
        public void SpawnPierceBullet(Vector2 pos, Vector2 dir, float speed, int damage, Color color,
            int pierceHits)
        {
            Spawn(pos, dir.normalized * speed, damage, color, 0.15f, 1.6f, true, false, 4f, pierceHits);
        }

        public void SpawnEnemyBullet(Vector2 pos, Vector2 dir, float speed, int damage, Color color,
            float radius = 0.2f, bool homing = false)
        {
            Spawn(pos, dir.normalized * speed, damage, color, radius, 0f, false, homing, 6f);
        }

        /// <summary>环形弹幕：一次向四周均匀发射。</summary>
        public void SpawnRing(Vector2 pos, int count, float speed, int damage, Color color, float phaseDeg = 0f)
        {
            for (int i = 0; i < count; i++)
            {
                float a = (i / (float)count) * Mathf.PI * 2f + phaseDeg * Mathf.Deg2Rad;
                SpawnEnemyBullet(pos, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), speed, damage, color, 0.22f);
            }
        }

        /// <summary>扇形弹幕：朝向目标的一个锥形范围。</summary>
        public void SpawnFan(Vector2 pos, Vector2 target, int count, float spreadDeg, float speed,
            int damage, Color color)
        {
            Vector2 dir = target - pos;
            float baseAngle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            float step = count > 1 ? spreadDeg / (count - 1) : 0f;
            float start = baseAngle - spreadDeg * 0.5f;
            for (int i = 0; i < count; i++)
            {
                float a = (start + step * i) * Mathf.Deg2Rad;
                SpawnEnemyBullet(pos, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), speed, damage, color, 0.2f);
            }
        }

        private void Spawn(Vector2 pos, Vector2 vel, int damage, Color color,
            float radius, float length, bool fromPlayer, bool homing, float life, int pierceHits = -1, int bounces = 0)
        {
            for (int tries = 0; tries < Capacity; tries++)
            {
                int idx = (_cursor + tries) % Capacity;
                if (_bullets[idx].Alive)
                {
                    continue;
                }
                _bullets[idx] = new Bullet
                {
                    Pos = pos,
                    Vel = vel,
                    Radius = radius,
                    Length = length,
                    Life = life,
                    Color = color,
                    Damage = damage,
                    FromPlayer = fromPlayer,
                    Homing = homing,
                    PierceHits = pierceHits,
                    PierceCooldown = 0f,
                    Grazed = false,
                    Bounces = bounces,
                    Alive = true
                };
                _cursor = (idx + 1) % Capacity;
                return;
            }
            // 满了就顶掉游标位置那颗（保证新弹永远能发出去）
            _bullets[_cursor] = new Bullet
            {
                Pos = pos, Vel = vel, Radius = radius, Length = length, Life = life,
                Color = color, Damage = damage, FromPlayer = fromPlayer, Homing = homing,
                PierceHits = pierceHits, PierceCooldown = 0f, Grazed = false, Alive = true
            };
            _cursor = (_cursor + 1) % Capacity;
        }

        // ---------------- 每帧 ----------------

        public void Tick(float dt)
        {
            AliveCount = 0;
            _mesh.Clear();

            for (int i = 0; i < Capacity; i++)
            {
                if (!_bullets[i].Alive)
                {
                    continue;
                }

                Bullet b = _bullets[i];
                b.Life -= dt;
                b.Pos += b.Vel * dt;
                b.Age += dt;

                // 潮旋：正弦横移。用 Age 而不是 Life 是为了让每颗弹的波形独立。
                if (b.WaveAmp > 0f && b.FromPlayer)
                {
                    b.Pos += new Vector2(
                        Mathf.Sin(b.Age * b.WaveFreq + b.WavePhase) * b.WaveAmp * dt, 0f);
                }

                if (b.PierceCooldown > 0f)
                {
                    b.PierceCooldown -= dt;
                }

                // 追踪弹：敌方弹追玩家，玩家碎片（碎星）追最近敌机
                if (b.Homing && _player != null && _player.IsAlive)
                {
                    Vector2 toPlayer = (_player.Position - b.Pos).normalized;
                    float speed = b.Vel.magnitude;
                    b.Vel = Vector2.Lerp(b.Vel.normalized, toPlayer, 1.6f * dt).normalized * speed;
                }
                else if (b.HomingEnemy && _enemies != null)
                {
                    EnemyShip target = _enemies.FindNearest(b.Pos);
                    if (target != null)
                    {
                        Vector2 toE = (target.Position - b.Pos).normalized;
                        float speed = b.Vel.magnitude;
                        b.Vel = Vector2.Lerp(b.Vel.normalized, toE, 3.2f * dt).normalized * speed;
                    }
                }

                // 出界：有反弹次数的玩家弹（回壁机体）折返，否则销毁。
                // 反弹让「回壁」的火力变成一片驻场弹幕——它把自己的弹当墙用。
                if (Mathf.Abs(b.Pos.x) > 18.6f || Mathf.Abs(b.Pos.y) > 10.8f)
                {
                    if (b.FromPlayer && b.Bounces > 0)
                    {
                        var rb = _bullets[i];
                        if (Mathf.Abs(b.Pos.x) > 18.6f)
                        {
                            b.Vel.x = -b.Vel.x;
                            b.Pos.x = Mathf.Clamp(b.Pos.x, -18.4f, 18.4f);
                        }
                        if (Mathf.Abs(b.Pos.y) > 10.8f)
                        {
                            b.Vel.y = -b.Vel.y;
                            b.Pos.y = Mathf.Clamp(b.Pos.y, -10.6f, 10.6f);
                        }
                        rb.Bounces = b.Bounces - 1;
                        rb.Pos = b.Pos;
                        rb.Vel = b.Vel;
                        _bullets[i] = rb;
                        // 反弹不落入销毁分支，继续走绘制与碰撞
                    }
                    else
                    {
                        b.Alive = false;
                        _bullets[i] = b;
                        continue;
                    }
                }
                if (b.Life <= 0f)
                {
                    b.Alive = false;
                    _bullets[i] = b;
                    continue;
                }

                _bullets[i] = b;
                AliveCount++;
                Draw(b);

                // 擦弹判定：只对敌弹、只擦一次、且必须贴着命中带。
                // 放在 Draw 之后、ResolveCollision 之前：本帧先判擦弹再判命中，
                // 同一颗弹不会既擦到又命中（擦的判定带在命中判定之外）。
                if (!b.FromPlayer && !b.Grazed && _player != null && _player.IsAlive)
                {
                    float d = Vector2.Distance(b.Pos, _player.Position);
                    float hitDist = b.Radius + _player.HitRadius;
                    if (d < GrazeRadius && d > hitDist)
                    {
                        var gb = _bullets[i];
                        gb.Grazed = true;
                        _bullets[i] = gb;
                        Grazed?.Invoke(b.Pos);
                    }
                }

                ResolveCollision(i, b);
            }

            _mesh.Commit();
        }

        /// <summary>
        /// 视觉层级的量化定义。
        /// 弹幕游戏的生死线是「满屏特效时还能认出哪颗弹能杀我」。
        /// 做法：玩家弹走最亮阶（1.0）+ 锐利拖尾；敌弹压到 0.6 且光晕更淡。
        /// 用亮度差而不是色相差来区分——亮度差对色觉障碍玩家同样有效，
        /// 而「红 vs 青」在部分色觉类型下会糊成一片。
        /// </summary>
        private const float EnemyBulletDim = 0.6f;
        private const float EnemyHaloAlpha = 0.16f;

        /// <summary>低于这个已用图元数就停止画光晕，把容量留给核心圆。
        /// 一颗敌弹 = 核心圆 10 顶点 + 光晕圆 10 顶点，2048 容量下约 102 颗封顶；
        /// 提到 4096 后约 204 颗，再留 64 的余量给玩家弹的拖尾。</summary>
        private const int BulletHaloReserve = 4096 - 64;

        private void Draw(Bullet b)
        {
            if (b.FromPlayer)
            {
                // 粗拖尾 + 大圆头：读起来像一道激光而不是一颗子弹。
                // 线宽用半径的 3.0 倍（普通敌弹核心只有 1.05 倍半径），
                // 玩家火力在视觉重量上必须压过敌弹。
                Vector2 tail = b.Pos - b.Vel.normalized * b.Length;
                _mesh.AddLine(tail, b.Pos, b.Radius * 3.0f, b.Color);
                _mesh.AddCircle(b.Pos, b.Radius * 1.6f, new Color(1f, 1f, 1f, 1f));
            }
            else
            {
                // 敌弹：实心圆压暗 + 极淡外圈，且**不给拖尾**。
                // 不给拖尾是刻意的——拖尾是玩家弹的专属视觉语言，
                // 敌弹一旦也有，高速时满屏都是细线，玩家分不清是谁的。
                Color core = new Color(
                    b.Color.r * EnemyBulletDim,
                    b.Color.g * EnemyBulletDim,
                    b.Color.b * EnemyBulletDim,
                    b.Color.a);
                _mesh.AddCircle(b.Pos, b.Radius * 1.05f, core);
                // 光晕是可选装饰：容量紧张时优先保证核心圆画出来。
                // 少一圈光晕只是略硬，漏画整颗弹就是「子弹凭空消失」——
                // 后者会让玩家以为判定丢了，是更严重的公平性问题。
                if (_mesh.Count < BulletHaloReserve)
                {
                    _mesh.AddCircle(b.Pos, b.Radius * 1.9f,
                        new Color(core.r, core.g, core.b, EnemyHaloAlpha));
                }
            }
        }

        private void ResolveCollision(int index, Bullet b)
        {
            if (b.FromPlayer)
            {
                if (_enemies == null)
                {
                    return;
                }
                // 穿透弹的命中后无判定窗口（见 PierceCooldown 注释）
                if (b.PierceCooldown > 0f)
                {
                    return;
                }
                EnemyShip hit = _enemies.FindHit(b.Pos, b.Radius);
                if (hit == null)
                {
                    return;
                }
                EnemyHit?.Invoke(hit, b.Pos);

                var bb = _bullets[index];
                if (bb.PierceHits < 0)
                {
                    // 普通弹：命中即消失
                    bb.Alive = false;
                }
                else
                {
                    bb.PierceHits--;
                    bb.PierceCooldown = PierceImmunity;
                    if (bb.PierceHits < 0)
                    {
                        bb.Alive = false;
                    }
                }
                _bullets[index] = bb;
            }
            else
            {
                if (_player == null || !_player.IsAlive || !_player.CanBeHit)
                {
                    return;
                }
                if ((_player.Position - b.Pos).sqrMagnitude > (b.Radius + _player.HitRadius) * (b.Radius + _player.HitRadius))
                {
                    return;
                }
                var bb = _bullets[index];
                bb.Alive = false;
                _bullets[index] = bb;
                PlayerHit?.Invoke(b.Pos);
            }
        }

        /// <summary>清掉**全部**敌弹但保留玩家自己的弹。
        /// 潮涌奖励用：清场如果把玩家火力也掐了，那种「奖励」其实是惩罚。</summary>
        public int ClearAllEnemyBullets()
        {
            int cleared = 0;
            for (int i = 0; i < Capacity; i++)
            {
                if (!_bullets[i].Alive || _bullets[i].FromPlayer)
                {
                    continue;
                }
                _bullets[i].Alive = false;
                cleared++;
            }
            return cleared;
        }

        /// <summary>最近的威胁敌弹（破潮旗舰护航避弹用）。null = 半径内无敌弹。</summary>
        public Vector2? NearestEnemyBullet(Vector2 pos, float radius)
        {
            Vector2? best = null;
            float bestD = radius * radius;
            for (int i = 0; i < Capacity; i++)
            {
                if (!_bullets[i].Alive || _bullets[i].FromPlayer) { continue; }
                float d = (_bullets[i].Pos - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = _bullets[i].Pos; }
            }
            return best;
        }

        /// <summary>清掉指定区域内的敌弹（玩家放大招 / 无敌时用）。</summary>
        public int ClearEnemyBulletsNear(Vector2 center, float radius)        {
            int cleared = 0;
            float r2 = radius * radius;
            for (int i = 0; i < Capacity; i++)
            {
                if (!_bullets[i].Alive || _bullets[i].FromPlayer)
                {
                    continue;
                }
                if ((_bullets[i].Pos - center).sqrMagnitude <= r2)
                {
                    _bullets[i].Alive = false;
                    cleared++;
                }
            }
            return cleared;
        }

        /// <summary>
        /// 采样离给定点最近的若干敌弹位置。
        /// 给 MCP 控制接口用：AI 只需要知道「马上会打到我的那些弹」，
        /// 全量 300 颗会把状态包撑爆，也没必要。
        /// </summary>
        public IReadOnlyList<Vector2> SampleThreats(Vector2 origin, int max)
        {
            var result = new List<Vector2>(max);
            var dists = new List<float>(max);

            for (int i = 0; i < Capacity; i++)
            {
                if (!_bullets[i].Alive || _bullets[i].FromPlayer)
                {
                    continue;
                }
                Vector2 pos = _bullets[i].Pos;
                // 只把「玩家真的看得见」的子弹算作威胁。
                // 屏幕外的子弹对玩家不可见，报给观察者只会制造噪声与误判。
                if (Mathf.Abs(pos.x) > 6.5f || Mathf.Abs(pos.y) > 11.5f)
                {
                    continue;
                }
                float d = (pos - origin).sqrMagnitude;

                if (result.Count < max)
                {
                    result.Add(pos);
                    dists.Add(d);
                    continue;
                }

                // 找当前最远的一个替换掉
                int worst = 0;
                for (int j = 1; j < dists.Count; j++)
                {
                    if (dists[j] > dists[worst])
                    {
                        worst = j;
                    }
                }
                if (d < dists[worst])
                {
                    dists[worst] = d;
                    result[worst] = pos;
                }
            }

            return result;
        }
    }
}
