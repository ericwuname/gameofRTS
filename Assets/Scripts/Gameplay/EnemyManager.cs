using System;
using System.Collections.Generic;
using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 敌机池与查询。同屏敌机控制在几十个，所以用 GameObject + SpriteRenderer，
    /// 只有子弹走批量渲染。池化保证战斗中不产生 Instantiate 抖动。
    /// </summary>
    public sealed class EnemyManager : MonoBehaviour
    {
        private readonly List<EnemyShip> _active = new List<EnemyShip>(64);
        private readonly Stack<EnemyShip> _idle = new Stack<EnemyShip>(64);
        private BulletField _bullets;
        private PlayerShip _player;
        private Transform _root;
    private BatchMesh _teleMesh;

        private Sprite _sprDiamond;
        private Sprite _sprHex;
        private Sprite _sprCircle;
        private Sprite _sprTriangle;
        private Sprite _sprGlow;

        public int ActiveCount => _active.Count;
        public IReadOnlyList<EnemyShip> Active => _active;

        public void Initialize(BulletField bullets, PlayerShip player, Material teleMaterial)
        {
            _bullets = bullets;
            _player = player;
            _root = new GameObject("EnemyRoot").transform;
            _root.SetParent(transform, false);

            _sprDiamond = SpriteFactory.Diamond(96, Color.white, Color.white);
            _sprHex = SpriteFactory.Hexagon(112, Color.white, Color.white);
            _sprCircle = SpriteFactory.Circle(96, Color.white);
            _sprTriangle = SpriteFactory.Triangle(96, Color.white, Color.white);
            _sprGlow = SpriteFactory.Glow(128, Color.white);

            // 弹道预警层：单独一个 BatchMesh，sortingOrder 压在敌机之下、背景之上。
            // 独立成层的理由：预警是「将发生」的信息，不是「已发生」的效果，
            // 混进特效层会让玩家分不清哪些是威胁哪些是装饰。
            if (teleMaterial != null)
            {
                var teleGo = new GameObject("TelegraphMesh");
                teleGo.transform.SetParent(transform, false);
                _teleMesh = teleGo.AddComponent<BatchMesh>();
                _teleMesh.Initialize(teleMaterial, 4);
            }
        }

        public EnemyShip Spawn(EnemyKind kind, Vector2 pos, int wave, float pressure = 1f)
        {
            EnemyShip ship = Acquire();
            ship.gameObject.SetActive(true);
            ship.transform.SetParent(_root, false);

            // 避免两架敌机几乎重叠生成：重叠会让玩家看到一个「怪」，实际判定是两架，
            // 击杀时的连锁爆炸也会显得莫名其妙。最多重试 6 次，仍冲突就接受。
            Vector2 spawnPos = pos;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (!HasEnemyNear(spawnPos, 1.25f))
                {
                    break;
                }
                spawnPos = new Vector2(UnityEngine.Random.Range(-16.0f, 16.0f), pos.y);
            }

            EnemySpawnSpec spec = BuildSpec(kind, spawnPos, wave);
            // 血量随压力温和浮动：pressure^0.6 而非线性，
            // 避免难度落差过大导致「肉到打不动」或「一碰就碎」
            spec.Hp *= Mathf.Pow(Mathf.Max(0.1f, pressure), 0.6f);
            Sprite body = BodySprite(kind);
            if (BulletRain) { spec.Hp *= 0.75f; spec.BulletSpeed *= 1.25f; }
            if (TideOut) { spec.StartVelocity *= 1.3f; spec.BulletSpeed *= 1.3f; }
            if (MutationSwift && kind == EnemyKind.Shooter) { spec.FireInterval *= 0.7f; }
            ship.Launch(spec, _bullets, _player, body, _sprGlow);
            _active.Add(ship);
            return ship;
        }

        /// <summary>
        /// 生成 Boss。复用 Spinner 的几何与配色，但放大 2.2 倍半径、HP ×8。
        ///
        /// 为什么复用而不是新做一套：Boss 的「仪式感」来自尺寸、血量、弹幕脚本
        /// 和登场反馈，不来自新美术。复用能在零新资产的前提下做出
        /// 「这是个大东西」的认知——而本项目的美术约束就是零资产。
        ///
        /// 用六边形而不是 Spinner 的圆形：圆环弹幕 + 圆 Boss 在视觉上会糊成一团，
        /// 六边形的硬朗轮廓能让玩家在满屏自己的弹幕里仍然锁定目标。
        /// </summary>
        public EnemyShip SpawnBoss(Vector2 pos, int wave)
        {
            EnemyShip ship = Acquire();
            ship.gameObject.SetActive(true);
            ship.transform.SetParent(_root, false);

            EnemySpawnSpec spec = BuildSpec(EnemyKind.Spinner, pos, wave);
            spec.Radius *= 2.2f;
            spec.Hp *= 8f;
            if (BulletRain) { spec.Hp *= 0.75f; spec.BulletSpeed *= 1.25f; }
            if (BossHalved) { spec.Hp *= 0.5f; }
            spec.Score = 5000;
            spec.IsBoss = true;
            spec.BulletSpeed *= 0.85f;
            spec.SpinSpeed = 35f;
            spec.StartVelocity = new Vector2(0f, -1.6f);

            ship.Launch(spec, _bullets, _player, _sprHex, _sprGlow);
            _active.Add(ship);
            return ship;
        }

        /// <summary>从池里取一个敌机，池空则新建。抽出来是为了避免
        /// Spawn 与 SpawnBoss 各写一遍相同的池逻辑。</summary>
        private EnemyShip Acquire()
        {
            while (_idle.Count > 0)
            {
                var candidate = _idle.Pop();
                if (candidate != null)
                {
                    return candidate;
                }
            }
            var go = new GameObject("Enemy");
            go.transform.SetParent(_root, false);
            return go.AddComponent<EnemyShip>();
        }

        /// <summary>
        /// 生成终局 Boss「潮主」：普通 Boss 的再放大（半径×1.4、HP×2.5），
        /// 弹幕更密（间隔 ×0.7）。多阶段简化为血量阈值驱动的狂暴——
        /// EnemyShip.TickBossFire 里已按 _bossPhase 交替弹形，
        /// 半血加速用 spec.FireInterval 在 Launch 后由 Boss 自行处理。
        /// </summary>
        public EnemyShip SpawnFinalBoss(Vector2 pos, int wave)
        {
            EnemyShip ship = Acquire();
            ship.gameObject.SetActive(true);
            ship.transform.SetParent(_root, false);

            EnemySpawnSpec spec = BuildSpec(EnemyKind.Spinner, pos, wave);
            spec.Radius *= 3.1f;
            spec.Hp *= 20f;
            spec.Score = 20000;
            spec.IsBoss = true;
            spec.BulletSpeed *= 0.9f;
            spec.SpinSpeed = 25f;
            spec.StartVelocity = new Vector2(0f, -1.2f);
            if (BulletRain) { spec.Hp *= 0.75f; spec.BulletSpeed *= 1.25f; }

            ship.Launch(spec, _bullets, _player, _sprHex, _sprGlow);
            _active.Add(ship);
            return ship;
        }

        /// <summary>「双月」规则：Boss 血量减半。由 GameDirector 每局开局设置。</summary>
        public bool BossHalved { get; set; }

        /// <summary>「弹雨」规则：敌机 HP -25%、敌弹速 +25%。</summary>
        public bool BulletRain { get; set; }

        /// <summary>「潮退」规则：敌机速度 +30%。</summary>
        public bool TideOut { get; set; }

        /// <summary>变异：Shooter 射速 +43%（间隔×0.7）。</summary>
        public bool MutationSwift { get; set; }

        private bool HasEnemyNear(Vector2 pos, float radius)
        {
            float r2 = radius * radius;
            for (int i = 0; i < _active.Count; i++)
            {
                EnemyShip e = _active[i];
                if (e == null || !e.IsAlive)
                {
                    continue;
                }
                if ((e.Position - pos).sqrMagnitude < r2)
                {
                    return true;
                }
            }
            return false;
        }

        private Sprite BodySprite(EnemyKind kind)
        {
            switch (kind)
            {
                case EnemyKind.Shooter: return _sprHex;
                case EnemyKind.Spinner: return _sprCircle;
                case EnemyKind.Charger: return _sprTriangle;
                default: return _sprDiamond;
            }
        }

        private EnemySpawnSpec BuildSpec(EnemyKind kind, Vector2 pos, int wave)
        {
            float w = Mathf.Max(1, wave);
            var spec = new EnemySpawnSpec
            {
                Kind = kind,
                StartPos = pos,
                StartVelocity = new Vector2(0f, -2.4f),
                HoverY = 5.5f,
                Score = 100,
                Radius = 0.62f,
                SpinSpeed = 0f,
                BulletSpeed = 7.5f + w * 0.22f,
                FireInterval = 0f,
                FirstFireDelay = 1.1f,
                FanCount = 5
            };

            switch (kind)
            {
                case EnemyKind.Drifter:
                    spec.Hp = 14f + w * 4f;
                    spec.Score = 100;
                    spec.Radius = 0.58f;
                    // 色相纪律：敌人一律锁在暖色带。
                    // 原来是紫色，和玩家的青色在色环上只隔一段，高速横飞时会混。
                    spec.Color = new Color32(0xE0, 0x45, 0x7F, 0xFF);
                    spec.BulletColor = new Color32(0xFF, 0x6B, 0x9E, 0xFF);
                    spec.SpinSpeed = 90f;
                    break;

                case EnemyKind.Shooter:
                    spec.Hp = 30f + w * 8f;
                    spec.Score = 250;
                    spec.Radius = 0.75f;
                    spec.Color = new Color32(0xFF, 0x3D, 0x4D, 0xFF);
                    spec.BulletColor = new Color32(0xFF, 0x5E, 0x6B, 0xFF);
                    // 发弹间隔压到 1.1 秒以内，屏幕才会被弹幕填满
                    spec.FireInterval = Mathf.Max(0.6f, 1.15f - w * 0.05f);
                    spec.HoverY = 3.2f + UnityEngine.Random.Range(-1.2f, 2.2f);
                    spec.StartVelocity = new Vector2(0f, -4.2f);
                    break;

                case EnemyKind.Spinner:
                    spec.Hp = 60f + w * 12f;
                    spec.Score = 400;
                    spec.Radius = 0.9f;
                    spec.Color = new Color32(0xFF, 0xA6, 0x2B, 0xFF);
                    spec.BulletColor = new Color32(0xFF, 0xC9, 0x4D, 0xFF);
                    spec.FireInterval = 1.7f;
                    spec.HoverY = 4.5f + UnityEngine.Random.Range(-1f, 2.5f);
                    spec.FirstFireDelay = 0.8f;
                    break;

                case EnemyKind.Charger:
                    spec.Hp = 22f + w * 5f;
                    spec.Score = 200;
                    spec.Radius = 0.62f;
                    // 原来是青绿色——和玩家的青色撞车，是色相纪律里最大的漏洞。
                    spec.Color = new Color32(0xFF, 0x6B, 0x2D, 0xFF);
                    spec.BulletColor = new Color32(0xFF, 0x8A, 0x4D, 0xFF);
                    // 曾经是 0f，导致 EnemyShip.TickFire 第一行的早退判断直接返回，
                    // 追踪弹分支成了永远进不去的死代码——Charger 设计上的
                    // 「冲脸 + 锁定弹」实际只做到了冲脸。
                    // 2.8 秒一发（原 2.4）：追踪弹的转向是渐进 Lerp，玩家跑速 9.5 大于弹速 7.5+
                    // 理论上甩得掉，所以它真正的压力来自「同时有几发在追」而不是单发威力。
                    // 间隔放宽让同屏锁定弹不超过 2 发，保留「逼你移动」的设计意图，
                    // 不至于和 Shooter 的扇形弹糊成一团。
                    spec.FireInterval = 2.8f;
                    spec.StartVelocity = new Vector2(0f, -3.4f);
                    break;
            }
            return spec;
        }

        public void Tick(float dt)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                EnemyShip e = _active[i];
                if (e == null)
                {
                    _active.RemoveAt(i);
                    continue;
                }
                e.Tick(dt);

                // 撞到玩家：算玩家受伤，敌人同归于尽
                if (e.IsAlive && _player != null && _player.CanBeHit)
                {
                    float reach = e.Radius + _player.HitRadius;
                    if ((e.Position - _player.Position).sqrMagnitude <= reach * reach)
                    {
                        _player.TakeHit();
                        e.ForceKill();
                    }
                }

                if (!e.IsAlive)
                {
                    _active.RemoveAt(i);
                    e.ResetForPool();
                    e.gameObject.SetActive(false);
                    e.transform.SetParent(_root, false);
                    _idle.Push(e);
                }
            }

            DrawTelegraphs();
        }

        /// <summary>找与给定圆相交的第一个敌机（玩家子弹命中检测）。</summary>
        public EnemyShip FindHit(Vector2 pos, float radius)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                EnemyShip e = _active[i];
                if (e == null || !e.IsAlive)
                {
                    continue;
                }
                float reach = e.Radius + radius;
                if ((e.Position - pos).sqrMagnitude <= reach * reach)
                {
                    return e;
                }
            }
            return null;
        }

        /// <summary>找最近的存活敌机（「碎星」机体的碎片索敌用）。</summary>
        public EnemyShip FindNearest(Vector2 pos)
        {
            EnemyShip best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < _active.Count; i++)
            {
                EnemyShip e = _active[i];
                if (e == null || !e.IsAlive) { continue; }
                float d = (e.Position - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        /// <summary>遍历范围内敌机（连锁爆炸用）。用回调避免每帧分配 List。</summary>
        public void ForEachNear(Vector2 pos, float radius, Action<EnemyShip> action)
        {
            float r2 = radius * radius;
            for (int i = 0; i < _active.Count; i++)
            {
                EnemyShip e = _active[i];
                if (e == null || !e.IsAlive)
                {
                    continue;
                }
                if ((e.Position - pos).sqrMagnitude <= r2)
                {
                    action(e);
                }
            }
        }

        /// <summary>每帧收集所有敌机的弹道预警并绘制到预警层。</summary>
        private void DrawTelegraphs()
        {
            if (_teleMesh == null)
            {
                return;
            }
            _teleMesh.Clear();
            for (int i = 0; i < _active.Count; i++)
            {
                var e = _active[i];
                if (e != null && e.IsAlive)
                {
                    e.DrawTelegraph(_teleMesh);
                }
            }
            _teleMesh.Commit();
        }

        public void ClearAll()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                EnemyShip e = _active[i];
                if (e == null)
                {
                    continue;
                }
                e.ResetForPool();
                e.gameObject.SetActive(false);
                _idle.Push(e);
            }
            _active.Clear();
        }

        /// <summary>
        /// 强制隐藏所有敌机——含 renderer，且**池里的也一并禁**。
        /// ClearAll 的保险：观测到 Boss 在死亡结算界面残留（日志零异常），
        /// 说明 SetActive(false) 有没覆盖到的路径。
        /// 池里那份也要禁，否则下一局复用时可能闪一帧。
        /// </summary>
        public void ForceHideAll()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                var e = _active[i];
                if (e != null)
                {
                    e.ForceHide();
                }
            }
            foreach (var e in _idle)
            {
                if (e != null)
                {
                    e.ForceHide();
                }
            }
        }
    }
}
