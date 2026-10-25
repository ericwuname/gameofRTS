using System;
using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    public enum EnemyKind
    {
        Drifter = 0,
        Shooter = 1,
        Spinner = 2,
        Charger = 3
    }

    /// <summary>
    /// 敌机。几何体 + 单色，靠形状区分威胁类型。
    /// 用 GameObject + SpriteRenderer（同屏只有几十个），
    /// 子弹才是几百上千的量级，所以只有子弹走批量渲染。
    /// </summary>
    public sealed class EnemyShip : MonoBehaviour
    {
        public EnemyKind Kind { get; private set; }
        public float Hp { get; private set; }
        public float MaxHp { get; private set; }
        public float Radius { get; private set; }
        public int ScoreValue { get; private set; }
        public bool IsAlive { get; private set; }
        public Vector2 Position => transform.position;

        /// <summary>
        /// 是否是 Boss。Boss 走自己的弹幕脚本、有自己的登场与死亡反馈，
        /// 不再受 FireInterval 那一套普通敌机逻辑约束。
        /// </summary>
        public bool IsBoss { get; private set; }

        /// <summary>开火预警窗口（秒）。Into the Breach 式完美信息：
        /// 敌人将做什么提前展示，「躲」从纯反应变成预判解谜，
        /// 同时消灭「毫无征兆的死亡」——公平死亡的第一条件是可预知。</summary>
        private const float TelegraphWindow = 0.5f;

        /// <summary>是否处于弹道预警窗口（即将开火的 0.5 秒内）。
        /// 判定条件与 TickFire 的开火前提保持一致，否则会出现
        /// 「预警了却不开火」或「开火了却没预警」的失信。</summary>
        public bool Telegraphing
        {
            get
            {
                if (!IsAlive || _bullets == null || _player == null)
                {
                    return false;
                }
                if (IsBoss)
                {
                    return _fireTimer < TelegraphWindow && _fireTimer > 0f;
                }
                if (_spec.FireInterval <= 0f)
                {
                    return false;
                }
                if (Kind == EnemyKind.Shooter && Position.y > _spec.HoverY + 0.5f)
                {
                    return false;
                }
                return _fireTimer < TelegraphWindow && _fireTimer > 0f;
            }
        }

        /// <summary>
        /// 把本帧的弹道预警画到预警层。
        /// 每种敌机画法不同：扇形画边线、环弹画预圈、追踪弹画指向线——
        /// 预警必须忠实于该敌机**真正的弹道形状**，画一个通用的感叹号毫无意义。
        /// </summary>
        public void DrawTelegraph(BatchMesh mesh)
        {
            if (!Telegraphing || mesh == null || _player == null)
            {
                return;
            }
            float frac = 1f - _fireTimer / TelegraphWindow;
            Color c = new Color32(0xFF, 0x4D, 0x5E, 0xFF);
            c.a = 0.08f + 0.30f * frac;   // 越接近开火越亮，形成「逼近感」
            float range = _spec.BulletSpeed * 1.6f;

            if (IsBoss)
            {
                // Boss：环弹为主，画一个大预圈 + 收缩内圈，读作「要爆了」
                mesh.AddCircle(Position, range * 0.55f, new Color(c.r, c.g, c.b, c.a * 0.5f));
                mesh.AddCircle(Position, range * 0.55f * (0.9f + 0.1f * frac),
                    new Color(c.r, c.g, c.b, c.a));
                return;
            }

            switch (Kind)
            {
                case EnemyKind.Shooter:
                {
                    // 扇形：画中线 + 两条边线（±23°），忠实于 5 发 46° 的真实形状
                    Vector2 baseDir = _player.Position - Position;
                    float ang = Mathf.Atan2(baseDir.y, baseDir.x) * Mathf.Rad2Deg;
                    for (int i = -1; i <= 1; i++)
                    {
                        float a = (ang + 23f * i) * Mathf.Deg2Rad;
                        Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                        mesh.AddLine(Position, Position + dir * range, 0.09f, c);
                    }
                    break;
                }
                case EnemyKind.Spinner:
                {
                    // 环弹：预警是收拢的圈，读作「即将向外爆环」
                    mesh.AddCircle(Position, range * 0.45f, new Color(c.r, c.g, c.b, c.a * 0.4f));
                    mesh.AddCircle(Position, range * 0.45f * (1.05f - 0.15f * frac),
                        new Color(c.r, c.g, c.b, c.a));
                    break;
                }
                case EnemyKind.Charger:
                {
                    // 追踪弹：画一条指向玩家当前位置的线（弹会转向，但预判价值不变）
                    Vector2 dir = (_player.Position - Position).normalized;
                    mesh.AddLine(Position, Position + dir * range * 0.8f, 0.09f, c);
                    break;
                }
            }
        }

        public event Action<EnemyShip> Died;
        public event Action<EnemyShip, int> Damaged;

        private SpriteRenderer _renderer;
        private SpriteRenderer _glow;
        private Vector2 _velocity;
        private float _life;
        private float _fireTimer;
        private float _flash;
        private Vector3 _baseScale;
        private float _phase;
        private EnemySpawnSpec _spec;
        private BulletField _bullets;
        private PlayerShip _player;
        private bool _chargeWarned;

        public void Launch(EnemySpawnSpec spec, BulletField bullets, PlayerShip player,
            Sprite bodySprite, Sprite glowSprite)
        {
            _spec = spec;
            _bullets = bullets;
            _player = player;

            Kind = spec.Kind;
            MaxHp = spec.Hp;
            Hp = spec.Hp;
            Radius = spec.Radius;
            ScoreValue = spec.Score;
            IsAlive = true;
            IsBoss = spec.IsBoss;
            _life = 0f;
            _flash = 0f;
            _chargeWarned = false;
            _fireTimer = spec.FirstFireDelay;
            _phase = UnityEngine.Random.value * 6.28f;
            _velocity = spec.StartVelocity;
            transform.position = new Vector3(spec.StartPos.x, spec.StartPos.y, 0f);

            // 从池里复用时必须恢复显隐。
            // ForceHide 会关掉 renderer，而 SetActive(true) 只激活 GameObject、
            // 不会重新打开已禁用的组件——后果是「第二局敌人全部隐身」，
            // 玩家打不到看不见的敌人，85 秒只杀 30 个（正常应 200+）。
            if (_renderer != null)
            {
                _renderer.enabled = true;
            }
            if (_glow != null)
            {
                _glow.enabled = true;
            }

            if (_renderer == null)
            {
                _renderer = gameObject.GetComponent<SpriteRenderer>();
                if (_renderer == null)
                {
                    _renderer = gameObject.AddComponent<SpriteRenderer>();
                }
            }
            _renderer.sprite = bodySprite;
            _renderer.color = spec.Color;
            _renderer.sortingOrder = 6;

            // 外发光：让敌机从深空里浮出来
            if (_glow == null)
            {
                var glowGo = new GameObject("Glow");
                glowGo.transform.SetParent(transform, false);
                _glow = glowGo.AddComponent<SpriteRenderer>();
                _glow.sortingOrder = 5;
            }
            _glow.sprite = glowSprite;
            _glow.color = new Color(spec.Color.r, spec.Color.g, spec.Color.b, 0.35f);

            float spriteWorld = bodySprite != null ? Mathf.Max(bodySprite.bounds.size.x, bodySprite.bounds.size.y) : 2f;
            _baseScale = Vector3.one * (spec.Radius * 2f / Mathf.Max(0.001f, spriteWorld));
            // 1.15 而不是 1.6：原来敌人个头过大，会把弹幕空间挤掉
            transform.localScale = _baseScale * 1.15f;

            // 发光贴图尺寸与世界尺寸的补偿
            if (_glow.sprite != null)
            {
                float glowWorld = Mathf.Max(_glow.sprite.bounds.size.x, 0.001f);
                float wanted = spec.Radius * 5f / Mathf.Max(0.001f, transform.localScale.x);
                _glow.transform.localScale = Vector3.one * (wanted / glowWorld);
            }
        }

        public void Tick(float dt)
        {
            if (!IsAlive)
            {
                return;
            }

            _life += dt;
            TickMove(dt);
            TickFire(dt);

            if (_flash > 0f)
            {
                _flash = Mathf.Max(0f, _flash - dt);
            }
            _renderer.color = _flash > 0f ? Color.white : _spec.Color;
            transform.localScale = _baseScale * (_flash > 0f ? 1.35f : 1.15f);

            transform.Rotate(0f, 0f, _spec.SpinSpeed * dt);
        }

        private void TickMove(float dt)
        {
            switch (Kind)
            {
                case EnemyKind.Drifter:
                    // 直线下落，轻微横摆
                    _velocity.x = Mathf.Sin(_life * 1.7f + _phase) * 1.6f;
                    break;

                case EnemyKind.Shooter:
                    // 下落到指定高度后停住横向漂移
                    if (Position.y > _spec.HoverY)
                    {
                        _velocity.y = -_spec.StartVelocity.magnitude;
                    }
                    else
                    {
                        _velocity.y = 0f;
                        _velocity.x = Mathf.Sin(_life * 1.1f + _phase) * 2.2f;
                    }
                    break;

                case EnemyKind.Spinner:
                    if (Position.y > _spec.HoverY)
                    {
                        _velocity.y = -_spec.StartVelocity.magnitude * 0.7f;
                    }
                    else
                    {
                        _velocity.y = Mathf.Sin(_life * 2.2f + _phase) * 0.8f;
                        _velocity.x = Mathf.Cos(_life * 1.6f + _phase) * 2.4f;
                    }
                    break;

                case EnemyKind.Charger:
                    // 先下坠蓄势，然后加速冲向玩家。
                    // 冲刺开始的一瞬间闪白一次（0.18s，比受击闪白的 0.06s 长得多）：
                    // Charger 的全部威胁在那 8.5 的冲脸速度上，而玩家缩在角落里
                    // 被追上就是死路——横向 ±5.8、纵向 ±11.5 的钳制让角落成为
                    // 唯一「两面有墙」的位置，也就最容易把人逼进去。
                    // 不给预警的话，玩家只觉得「莫名其妙被撞死」，学不到任何东西。
                    if (_life < 1.1f)
                    {
                        _velocity = new Vector2(0f, -2.2f);
                    }
                    else
                    {
                        if (!_chargeWarned)
                        {
                            _chargeWarned = true;
                            _flash = 0.18f;
                        }
                        if (PlayerPositionValid())
                        {
                            Vector2 to = (_player.Position - Position).normalized;
                            _velocity = Vector2.Lerp(_velocity, to * 8.5f, 2.6f * dt);
                        }
                    }
                    break;
            }

            transform.position += (Vector3)(_velocity * dt);

            // 横向钳制：Drifter/Spinner 的横摆速度会把敌机带出屏幕（实测能飘到 x=9.0，
            // 而屏幕只有 ±6.5），越界后玩家看不见却被判定命中，是明确的不公平来源。
            // 收到 ±5.8 而不是贴着 6.5：贴着边界会让敌机看起来"卡在边上不动"，
            // 视觉上比越界更像 bug（AI 与真人都会误判）。
            Vector2 clamped = transform.position;
            clamped.x = Mathf.Clamp(clamped.x, -16.4f, 16.4f);
            transform.position = clamped;

            if (Mathf.Abs(Position.y) > 12.0f)
            {
                // 飞出屏幕不计分，直接从池里回收
                IsAlive = false;
            }
        }

        private bool PlayerPositionValid()
        {
            return _player != null && _player.IsAlive;
        }

        private void TickFire(float dt)
        {
            if (_bullets == null || !PlayerPositionValid())
            {
                return;
            }

            // Boss 走自己的弹幕脚本：旋转环 + 追踪扇形交替。
            // 为什么两种交替：纯环弹可以绕着躲，纯扇形可以横向躲，
            // 交替出现才迫使玩家同时管两个维度——这是 Boss 战的起码要求。
            if (IsBoss)
            {
                TickBossFire(dt);
                return;
            }

            if (_spec.FireInterval <= 0f)
            {
                return;
            }
            if (Kind == EnemyKind.Shooter && Position.y > _spec.HoverY + 0.5f)
            {
                return;
            }

            _fireTimer -= dt;
            if (_fireTimer > 0f)
            {
                return;
            }
            _fireTimer = _spec.FireInterval;

            switch (Kind)
            {
                case EnemyKind.Shooter:
                    _bullets.SpawnFan(Position, _player.Position, 5, 46f, _spec.BulletSpeed, 8, _spec.BulletColor);
                    break;
                case EnemyKind.Spinner:
                    _bullets.SpawnRing(Position, 16, _spec.BulletSpeed, 8, _spec.BulletColor, _life * 40f);
                    break;
                case EnemyKind.Charger:
                    if (_life > 1.1f)
                    {
                        _bullets.SpawnEnemyBullet(Position, (_player.Position - Position), _spec.BulletSpeed,
                            10, _spec.BulletColor, 0.24f, true);
                    }
                    break;
            }
        }

        private int _bossPhase;

        /// <summary>Boss 弹幕：每第三次换成追踪扇形，其余是旋转环。</summary>
        private void TickBossFire(float dt)
        {
            _fireTimer -= dt;
            if (_fireTimer > 0f)
            {
                return;
            }
            _bossPhase++;

            if (_bossPhase % 3 == 0)
            {
                // 追踪扇形：朝玩家 7 发宽锥，逼玩家横向拉开
                _bullets.SpawnFan(Position, _player.Position, 7, 72f,
                    _spec.BulletSpeed * 0.95f, 10, _spec.BulletColor);
                _fireTimer = 2.1f;
            }
            else
            {
                // 旋转环：18 发，相位随时间转，形成螺旋压迫感
                _bullets.SpawnRing(Position, 18, _spec.BulletSpeed * 0.8f, 8,
                    _spec.BulletColor, _life * 85f);
                _fireTimer = 1.15f;
            }
        }

        public int TakeDamage(int amount)
        {
            if (!IsAlive)
            {
                return 0;
            }
            Hp -= amount;
            _flash = 0.06f;
            Damaged?.Invoke(this, amount);
            if (Hp <= 0f)
            {
                Hp = 0f;
                IsAlive = false;
                Died?.Invoke(this);
                return amount;
            }
            return amount;
        }

        public void ForceKill()
        {
            if (!IsAlive)
            {
                return;
            }
            Hp = 0f;
            IsAlive = false;
            Died?.Invoke(this);
        }

        /// <summary>分裂变异用：等比缩小并等比降血。</summary>
        public void ScaleDown(float factor)
        {
            Radius *= factor;
            _baseScale *= factor;
            transform.localScale = _baseScale;
            Hp *= factor;
        }

        public void ResetForPool()
        {
            Died = null;
            Damaged = null;
            IsAlive = false;
            IsBoss = false;
            _flash = 0f;
            _life = 0f;
            _chargeWarned = false;
            _bossPhase = 0;
        }

        /// <summary>
        /// 强制隐藏本体与光晕，并关掉整个 GameObject。
        /// 这是死亡清场的保险路径：观测到过 Boss 在死亡结算界面上残留
        /// （日志零异常，说明不是崩溃，而是某条路径没被 SetActive 覆盖到）。
        /// 结算界面必须是干净的——那是「这一局结束了」的视觉契约。
        /// </summary>
        public void ForceHide()
        {
            if (_renderer != null)
            {
                _renderer.enabled = false;
            }
            if (_glow != null)
            {
                _glow.enabled = false;
            }
            gameObject.SetActive(false);
        }
    }

    /// <summary>单个敌机的生成参数，由波次系统填。</summary>
    [Serializable]
    public struct EnemySpawnSpec
    {
        public EnemyKind Kind;
        public float Hp;
        public float Radius;
        public int Score;
        public Vector2 StartPos;
        public Vector2 StartVelocity;
        public float HoverY;
        public float FireInterval;
        public float FirstFireDelay;
        public float BulletSpeed;
        public float SpinSpeed;
        public Color Color;
        public Color BulletColor;

        /// <summary>是否 Boss。Boss 走独立弹幕脚本，且尺寸/血量/掉落都不同。</summary>
        public bool IsBoss;
    }
}
