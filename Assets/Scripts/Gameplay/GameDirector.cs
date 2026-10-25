using System;
using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    public enum ShotState
    {
        Menu = 0,
        Playing = 1,
        Dead = 2
    }

    /// <summary>
    /// 单局流程与爽感核心。
    /// 「爽」主要由三件事撑起来：
    /// 1) 连杀不断 → 分数倍率增长，屏幕上数字一直跳；
    /// 2) 敌人死亡会连锁引爆附近的（最多 3 跳），一发子弹能炸掉一整片；
    /// 3) 每次爆炸都有冲击环 + 抖屏 + 顿帧，反馈密度极高。
    /// </summary>
    public sealed class GameDirector : MonoBehaviour
    {
        public ShotState State { get; private set; } = ShotState.Menu;
        public int Score { get; private set; }
        public int BestScore { get; private set; }
        public int Combo { get; private set; }
        public float ComboTimer01 => _comboDuration <= 0f ? 0f : Mathf.Clamp01(_comboTimer / _comboDuration);

        /// <summary>连击窗口剩余秒数。暴露出来是为了让外部观察者能直接验证窗口机制，
        /// 不用去猜「刚才那下到底算不算断连」。</summary>
        public float ComboRemaining => _comboTimer;

        public float ComboWindowDuration => _comboDuration;
        public int Wave { get; private set; }
        public int TotalKills { get; private set; }

        /// <summary>下一次连击回血需要的连击数（20, 35, 50...），用于外部观察。</summary>
        public int NextHealCombo => _nextHealCombo;
        public PlayerShip Player { get; private set; }
        public EnemyManager Enemies { get; private set; }
        public BulletField Bullets { get; private set; }
        public FxLayer Fx { get; private set; }
        public Camera Cam { get; private set; }

        public event Action<ShotState> StateChanged;
        public event Action RunStarted;

        private const float ComboWindow = 2.6f;

        /// <summary>连击达到这个数才值得为它付出顿帧。
        /// 低于此值的击杀不给打击感——否则密集弹幕下几乎每帧都在命中，
        /// 顿帧会叠成持续的操作延迟，手感从「爽」变成「糊」。</summary>
        private const int KeyKillCombo = 10;

        /// <summary>过载期间连击窗口的衰减系数。0.35 = 窗口从 2.6 秒有效延长到约 7.4 秒，
        /// 刚好够在射速减半的 3 秒里多打一轮把连击续上。</summary>
        private const float ComboOverloadGrace = 0.35f;

        private float _comboTimer;
        private float _comboDuration = ComboWindow;
        private float _spawnTimer;
        private int _waveRemaining;
        private int _waveIndex;
        private int _chainDepth;
        private float _hitstop;
        private float _runTime;
        private int _nextHealCombo = 20;
        private bool _wasLastStand;
        private Starfield _starfield;
        private EnergyOrbField _orbs;
        private RewardField _rewards;
        private readonly DifficultyDirector _difficulty = new DifficultyDirector();

        // ---------------- 收缩边界 ----------------

        /// <summary>距下次收缩还剩多少秒。</summary>
        public float ShrinkCountdown => _shrinkTimer;

        /// <summary>收缩是否正在进行（灼烧带可见）。</summary>
        public bool ShrinkActive => _shrinkRemaining > 0f;

        /// <summary>收缩进行时的剩余秒数，HUD 画倒计时用。</summary>
        public float ShrinkRemaining => _shrinkRemaining;

        /// <summary>是否处于收缩前的预警期：四边开始闪烁，但活动区还没真的收。
        /// 分开这个状态是为了让玩家提前看到「要收了」，而不是突然被推离角落。</summary>
        public bool ShrinkWarning => _shrinkRemaining <= 0f && _shrinkTimer <= ShrinkWarningSeconds;

        /// <summary>当前玩家允许的活动范围（收缩时收紧）。外部只读，用于 HUD 画边界。</summary>
        public Vector2 PlayBounds => _playBounds;

        /// <summary>每多少秒触发一次收缩。</summary>
        private const float ShrinkInterval = 25f;

        /// <summary>收缩持续秒数。</summary>
        private const float ShrinkDuration = 6f;

        /// <summary>收缩时从四边各收进多少单位。1.2 ≈ 屏幕宽度的 9%，
        /// 刚好让「贴边守角」站不住，又不至于把活动区压到难受。</summary>
        private const float ShrinkInset = 1.6f;

        /// <summary>收缩开始前的预警秒数。没有预警的话，玩家会莫名其妙被推离角落，
        /// 学不到任何东西——这和 Charger 冲撞是同一个教训。</summary>
        private const float ShrinkWarningSeconds = 1.5f;

        /// <summary>玩家踩进灼烧带后，多久内不重复触发过载。
        /// 没有这个冷却，站在边界上会每帧刷新过载，等于永久射速减半。</summary>
        private const float BurnCooldown = 1.0f;

        private float _shrinkTimer;
        private float _shrinkRemaining;
        private float _burnCooldown;
        private Vector2 _playBounds = new Vector2(16.4f, 9.0f);

        // ---------------- 潮涌（连击里程碑） ----------------

        /// <summary>下一个潮涌里程碑的连击数。</summary>
        public int NextSurgeCombo => _nextSurgeCombo;

        /// <summary>首次触发潮涌所需的连击数。100 → 50。
        /// 实测最近一局连击峰值只有 78，100 的门槛玩家整局都摸不到——
        /// 一个从未被触发的「爽点」等于不存在。50 连击约 20~25 秒可达。</summary>
        private const int FirstSurgeCombo = 50;

        /// <summary>首次之后每多少连击再触发一次。
        /// 实测 122 秒能打到 361 连击，固定 50 一次会触发 7 回，里程碑就不「里程碑」了。
        /// 所以首次 50（保证人人见到）、之后每 100（保持稀缺）。</summary>
        private const int SurgeIntervalCombo = 100;

        private int _nextSurgeCombo = FirstSurgeCombo;

        // ---------------- 火力成长 ----------------

        /// <summary>每多少秒提升一级武器（封顶 3 级）。
        /// 20 秒：实测对局长度 54~123 秒，20 秒一级意味着开局双发 → 20 秒三发，
        /// 玩家在中段就能摸到满级并开始攒进化球。原来这个定时器整段没接线，
        /// PowerLevel 永远停在 startPower，进化成了永远摸不到的东西。</summary>
        private const float PowerUpInterval = 20f;

        // ---------------- 赌徒模式（Double or Nothing） ----------------

        /// <summary>赌局是否正在进行。</summary>
        public bool GambleActive => _gambleActive;

        /// <summary>赌局剩余秒数。未进行时返回 0。</summary>
        public float GambleRemaining => _gambleActive ? _gambleTimer : 0f;

        /// <summary>距下次可以开盘还剩多少秒。</summary>
        public float GambleCooldownRemaining => Mathf.Max(0f, _gambleCooldown);

        /// <summary>赌局时长。10 秒约等于「两波多」，够打一小轮也够出事。</summary>
        private const float GambleDuration = 10f;

        /// <summary>赌局冷却。8 秒，防止把它当成常驻buff反复刷。</summary>
        private const float GambleCooldown = 8f;

        /// <summary>开盘所需最低连击。10 连以下倍率才 3.5，押上去没意义，
        /// 只会诱导玩家在毫无积累时瞎赌——那是劝退而不是博弈。</summary>
        private const int GambleMinCombo = 10;

        private bool _gambleActive;
        private float _gambleTimer;
        private float _gambleCooldown;

        private float _powerTimer;

        private static readonly Color EvolutionReadyColor = new Color32(0xFF, 0xD2, 0x4D, 0xFF);
        private static readonly Color ShrinkWarnColor = new Color32(0xFF, 0x6B, 0x4D, 0xFF);
        private static readonly Color SurgeColor = new Color32(0x7F, 0xF0, 0xFF, 0xFF);
        private static readonly Color PowerUpColor = new Color32(0xB4, 0xFF, 0xE8, 0xFF);
        private static readonly Color ShieldColor = new Color32(0x9F, 0xFF, 0xE0, 0xFF);
        private static readonly Color GambleWinColor = new Color32(0xFF, 0xD2, 0x4D, 0xFF);
        private static readonly Color GambleLoseColor = new Color32(0xFF, 0x4D, 0x5E, 0xFF);
        private static readonly Color GrazeColor = new Color32(0xB4, 0xFF, 0xE8, 0xFF);

        /// <summary>自适应难度控制器，外部可读取当前压力值用于观察/调试。</summary>
        public DifficultyDirector Difficulty => _difficulty;

        /// <summary>能量球场。外部控制接口（AI 玩家）需要知道球在哪，
        /// 否则 AI 只会躲不会捡——而「躲」是这个游戏里最没有价值的单一行为。</summary>
        public EnergyOrbField Orbs => _orbs;

        /// <summary>最近一次玩家武器形态变化的原因（升级/进化），供外部观察。</summary>
        public string LastPowerNote { get; private set; } = "";

        public void Setup(Camera cam, PlayerShip player, BulletField bullets,
            EnemyManager enemies, FxLayer fx, Starfield starfield, EnergyOrbField orbs,
            RewardField rewards)
        {
            Cam = cam;
            Player = player;
            Bullets = bullets;
            Enemies = enemies;
            Fx = fx;
            _starfield = starfield;
            _orbs = orbs;
            _rewards = rewards;

            Bullets.EnemyHit += OnBulletHitEnemy;
            Bullets.PlayerHit += OnBulletHitPlayer;
            Player.Damaged += OnPlayerDamaged;
            Player.Died += OnPlayerDied;
            Player.ShieldBroken += OnShieldBroken;
            Player.FullyUpgraded += OnFullyUpgraded;
            Player.CounterTideTriggered += OnCounterTide;
            Bullets.Grazed += OnGraze;
            if (_rewards != null)
            {
                _rewards.Opened += OnChestOpened;
            }

            SetState(ShotState.Menu);
        }

        public void StartRun()
        {
            Score = 0;
            Combo = 0;
            TotalKills = 0;
            _comboTimer = 0f;
            Wave = 1;
            _waveIndex = 0;
            _waveRemaining = WaveSize(1);
            _spawnTimer = 1.2f;
            _runTime = 0f;
            _hitstop = 0f;
            _nextHealCombo = 20;
            _wasLastStand = false;
            _difficulty.Reset();
            _orbs?.ClearAll();
            _rewards?.ClearAll();
            _shrinkTimer = ShrinkInterval;
            _shrinkRemaining = 0f;
            _burnCooldown = 0f;
            _playBounds = new Vector2(16.4f, 9.0f);
            _nextSurgeCombo = FirstSurgeCombo;
            _powerTimer = 0f;
            _gambleActive = false;
            _gambleTimer = 0f;
            _gambleCooldown = 0f;
            _tension = 0;
            _grazeCount = 0;
            RollMutations();
            RollModifier();
            Enemies.BossHalved = Modifier == RunModifier.TwinMoons;
            Enemies.BulletRain = Modifier == RunModifier.BulletRain;
            Enemies.TideOut = Modifier == RunModifier.TideOut;
            _modifierAnnounce = Modifier == RunModifier.None ? 0f : 4.5f;
            if (Modifier == RunModifier.LoneBet)
            {
                // 孤注：生命压到 1。ResetForNewRun 之后调用才生效。
                Player.SetLives(1);
            }

            Bullets.ClearAll();
            Enemies.ClearAll();
            Fx.ClearAll();
            Player.ResetForNewRun();
            // 上一局死亡时隐藏过飞船，新一局要显回来
            Player.SetVisible(true);
            Fx.SpawnPulse(Player.Position, new Color32(0x7F, 0xF0, 0xFF, 0xFF));

            SetState(ShotState.Playing);
            RunStarted?.Invoke();
        }

        public void ReturnToMenu()
        {
            Bullets.ClearAll();
            Enemies.ClearAll();
            Fx.ClearAll();
            SetState(ShotState.Menu);
        }

        private void SetState(ShotState next)
        {
            if (State == next)
            {
                return;
            }
            State = next;
            StateChanged?.Invoke(next);
        }

        /// <summary>手动驱动：为 true 时 Update 不再推进，由外部调 TickFrame（无人值守验证用）。</summary>
        public bool ManualDrive { get; set; }

        private void Update()
        {
            if (ManualDrive)
            {
                return;
            }
            TickFrame(Time.deltaTime > 0f ? Time.deltaTime : 0.016f);
            if (Input.GetKeyDown(KeyCode.Escape) && State == ShotState.Playing)
            {
                ReturnToMenu();
            }
        }

        /// <summary>推进一帧。公开是为了让验证脚本能在 player loop 不运行时手动快进。</summary>
        public void TickFrame(float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            TickCamera(dt);
            _starfield?.Tick(dt);
            Fx?.Tick(dt);
            FloatText.Tick(dt);

            if (State != ShotState.Playing)
            {
                return;
            }

            // 进化或随机升级选择中：冻结全部玩法，但星空/特效/HUD 继续跑（它们在上面）。
            // 完全冻死画面会让玩家以为游戏卡了，而不是「在做选择」。
            if (Player.AnyChoicePending)
            {
                return;
            }

            _runTime += dt;

            // 顿帧：只冻玩法，粒子与震屏继续跑
            float gameDt = dt;
            if (_hitstop > 0f)
            {
                _hitstop = Mathf.Max(0f, _hitstop - dt);
                gameDt = dt * 0.08f;
            }

            // 玩家操作**永不受顿帧影响**。
            // 顿帧是为了「打击有手感」，但如果连玩家移动一起冻住，
            // 密集击杀时（几乎每帧都在命中）会变成持续的操作延迟，
            // 手感从「爽」直接变成「失控」——AI 测试里表现为「飞船冻结」。
            Player.Tick(dt, Bullets);
            Bullets.Tick(gameDt);
            Enemies.Tick(gameDt);

            // 能量球：拾取转给玩家，攒够就触发进化（下一帧起冻结等选择）
            if (_orbs != null)
            {
                int picked = _orbs.Tick(dt, Player.Position, Player.IsAlive);
                if (picked > 0)
                {
                    if (Player.AddEnergyOrbs(picked))
                    {
                        // 刚弹出选择：当帧就给强反馈，不让玩家自己琢磨「是不是该选东西了」
                        Fx?.SpawnPulse(Player.Position, EvolutionReadyColor);
                        FloatText.Push(Player.Position + Vector2.up * 1.2f,
                            Player.EvolutionPending ? "武 器 进 化" : "选 择 强 化",
                            EvolutionReadyColor, true);
                        Sfx.LevelUp();
                    }
                    Sfx.Pickup();
                }
            }

            if (_modifierAnnounce > 0f)
            {
                _modifierAnnounce = Mathf.Max(0f, _modifierAnnounce - dt);
            }
            TickShrink(dt);
            TickPowerUp(dt);
            // 连击窗口随「余韵」升级变长。每帧重算而不是在升级事件里设——
            // 升级入口以后会变多（进化、随机升级、事件奖励），漏一个就表现为
            // 「买了没生效」，而这种 bug 极难复现。
            _comboDuration = ComboWindow + Player.ComboWindowBonus;

            // 宝箱：定时刷新 + 拾取开启
            _rewards?.Tick(dt, Player.Position, Player.IsAlive);
            if (_rewards != null)
            {
                _rewards.Halved = Modifier == RunModifier.RichTide;
            }
            // 难度评估必须用**真实时间**，不能用 gameDt。
            // gameDt 受 hitstop 缩放，密集击杀时会被压到 8%，
            // 结果就是「宽限期被无限拖长」——AI 测试里表现为 8.2 秒还显示在宽限期内。
            _difficulty.Tick(dt, Player.Lives, Player.MaxLives);
            TickCombo(dt);
            TickGamble(dt);
            TickSpawner(gameDt);

            // 残血爆发触发瞬间给一次强反馈，让玩家意识到「状态变了」
            if (Player != null)
            {
                if (Player.LastStand && !_wasLastStand)
                {
                    Fx?.SpawnPulse(Player.Position, new Color32(0xFF, 0x4D, 0x5E, 0xFF));
                    Fx?.Shake(0.35f, 0.3f);
                    FloatText.Push(Player.Position + Vector2.up * 1.1f, "残 灯 爆 发",
                        new Color32(0xFF, 0x4D, 0x5E, 0xFF), true);
                }
                _wasLastStand = Player.LastStand;
            }
        }

        private void TickCamera(float dt)
        {
            if (Cam == null)
            {
                return;
            }
            // 轻微跟随玩家横移，增加空间感；纵轴固定
            float targetX = Player != null ? Player.Position.x * 0.18f : 0f;
            Vector3 pos = Cam.transform.position;
            pos.x = Mathf.Lerp(pos.x, targetX, 1f - Mathf.Exp(-4f * dt));
            pos.y = 0f;
            pos.z = -10f;

            Vector2 shake = Fx != null ? Fx.ShakeOffset() : Vector2.zero;
            Cam.transform.position = pos + new Vector3(shake.x, shake.y, 0f);
        }

        /// <summary>
        /// 收缩边界：每 25 秒把活动区四边各收 1.2 单位，持续 6 秒。
        ///
        /// 为什么必须有它：录屏显示玩家长时间死守左下角——那里是舞台上唯一
        /// 「两面有墙」的位置，安全且自动开火覆盖广。任何不改变空间结构的
        /// 数值调整都治不了这个，因为守角的期望收益就是最高的。
        /// 收缩边界物理上禁止守角，同时天然造出节奏波峰。
        ///
        /// 惩罚选「受过载」而非「掉一条命」：掉命是终极惩罚，不该和
        /// 「踩了个节奏事件」同权；而复用过载意味着零新机制、零新 UI，
        /// 玩家早就认识这个反馈（橙条 + 射速减半）。
        /// </summary>
        /// <summary>
        /// 赌徒模式（Double or Nothing）。按 Shift 或 J 押上当前倍率：
        /// 10 秒内连击不断 → 倍率 ×2（封顶 16）；断了 → 倍率归零。
        ///
        /// 为什么这能治「博弈性不足」：它是整个游戏里第一个**由玩家主动发起、
        /// 且后果立竿见影**的决策。此前所有操作都只是「减少损失」（躲弹、捡球），
        /// 而这个是「用已知风险换未知收益」——损失厌恶与可变奖励同时被激活，
        /// 这正是赌博类机制让人上瘾的原因，也是本作此前最缺的东西。
        ///
        /// 放在 TickCombo 之后调用：必须等本帧的连击断裂判定完成，
        /// 否则断连那一帧赌局还在跑，玩家会看到「明明断了还显示进行中」。
        /// </summary>
        private void TickGamble(float dt)
        {
            bool wantStart = Input.GetKeyDown(KeyCode.LeftShift)
                             || Input.GetKeyDown(KeyCode.RightShift)
                             || Input.GetKeyDown(KeyCode.J);

            if (_gambleActive)
            {
                _gambleTimer -= dt;
                bool broken = Combo <= 0;
                if (broken || _gambleTimer <= 0f)
                {
                    _gambleActive = false;
                    _gambleCooldown = GambleCooldown;
                    if (broken)
                    {
                        // 赌输：Combo 已经是 0，倍率自然归零，这里补仪式感
                        FloatText.Push(Player.Position + Vector2.up * 1.5f,
                            "赌 局 失 败", GambleLoseColor, true);
                        Fx?.Shake(0.3f, 0.3f);
                        Sfx.Hurt();
                    }
                    else
                    {
                        FloatText.Push(Player.Position + Vector2.up * 1.5f,
                            "赌 局 成 功  倍率 ×2", GambleWinColor, true);
                        Fx?.SpawnPulse(Player.Position, GambleWinColor);
                        Fx?.Shake(0.2f, 0.18f);
                        Sfx.LevelUp();
                    }
                }
                return;
            }

            if (_gambleCooldown > 0f)
            {
                _gambleCooldown -= dt;
                return;
            }

            if (wantStart && Combo >= GambleMinCombo)
            {
                _gambleActive = true;
                _gambleTimer = GambleDuration;
                FloatText.Push(Player.Position + Vector2.up * 1.5f,
                    "赌 局 开 始", GambleWinColor, true);
                Sfx.WaveStart();
            }
        }

        /// <summary>
        /// 宝箱开启：发奖。每个分支都要给足反馈——
        /// 「开了箱子但不知道得了什么」和「没开」在体验上没区别。
        ///
        /// 注意 Life 分支：满命时开出一条命是最劣质的体验（收益为零），
        /// 所以 fallback 成能量球。宝箱必须「开了就有东西」。
        /// </summary>
        private void OnChestOpened(ChestReward reward, Vector2 pos)
        {
            Vector2 at = pos + Vector2.up * 0.9f;
            switch (reward)
            {
                case ChestReward.Orbs:
                    _orbs?.Spawn(pos, 6 + (Modifier == RunModifier.RichTide ? 4 : 0));
                    FloatText.Push(at, "能量球 ×6", EvolutionReadyColor, false);
                    break;

                case ChestReward.Life:
                    if (Player != null && Player.Heal(1))
                    {
                        FloatText.Push(at, "+1 命", PowerUpColor, true);
                        Fx?.SpawnPulse(Player.Position, PowerUpColor);
                    }
                    else
                    {
                        // 满命 fallback：宝箱不许空开
                        _orbs?.Spawn(pos, 6 + (Modifier == RunModifier.RichTide ? 4 : 0));
                        FloatText.Push(at, "生命已满 → 能量球", EvolutionReadyColor, false);
                    }
                    break;

                case ChestReward.Invuln:
                    Player?.GrantInvuln(3f);
                    FloatText.Push(at, "无 敌  3 秒", ShieldColor, true);
                    Fx?.SpawnPulse(Player.Position, ShieldColor);
                    break;

                case ChestReward.Frenzy:
                    Player?.GrantFrenzy(10f);
                    FloatText.Push(at, "狂 热  10 秒", EvolutionReadyColor, true);
                    Fx?.SpawnPulse(Player.Position, EvolutionReadyColor);
                    Fx?.Shake(0.2f, 0.18f);
                    break;

                case ChestReward.Nuke:
                    int cleared = Bullets.ClearAllEnemyBullets();
                    // 只清弹不清怪的话，爽感只有一半——屏幕上东西还在压过来。
                    // 顺带给全屏敌人来一下，让「肃清」两个字名副实。
                    Enemies.ForEachNear(Vector2.zero, 999f, e =>
                    {
                        if (e != null && e.IsAlive)
                        {
                            e.TakeDamage(30);
                        }
                    });
                    Fx?.SpawnPulse(Player.Position, SurgeColor);
                    Fx?.Shake(0.45f, 0.45f);
                    _hitstop = Mathf.Max(_hitstop, 0.1f);
                    FloatText.Push(at, "全 屏 肃 清", SurgeColor, true);
                    break;
            }
            Sfx.Pickup();
        }

        /// <summary>
        /// 升级池全满。球折算成分数发下去——**资源绝不允许被浪费**，
        /// 否则玩家捡了一路球却什么也没发生，会直接判定「这游戏的资源系统是坏的」。
        /// 同时给足仪式感：完形是一种成就，值得一个奖杯式的反馈。
        /// </summary>
        private void OnFullyUpgraded()
        {
            int bonus = 5000;
            Score += bonus;
            if (Score > BestScore)
            {
                BestScore = Score;
            }
            Fx?.SpawnPulse(Player.Position, EvolutionReadyColor);
            Fx?.Shake(0.2f, 0.18f);
            FloatText.Push(Player.Position + Vector2.up * 1.4f,
                "已 臻 完 形   +" + bonus.ToString("N0"), EvolutionReadyColor, true);
            Sfx.LevelUp();
        }

        // ---------------- 擦弹（Graze）与张力 ----------------

        /// <summary>张力满值。满则释放一次潮涌。</summary>
        private const int TensionFull = 50;

        /// <summary>当前张力值（0~TensionFull）。外部可读，HUD 与 AI 玩家都用它。</summary>
        public int Tension => _tension;

        /// <summary>本局累计擦弹次数。</summary>
        public int GrazeCount => _grazeCount;

        private int _tension;
        private int _grazeCount;

        // ---------------- 局间 Modifier（Seed 因子） ----------------

        /// <summary>本局规则。None 表示基线局（30% 概率）。</summary>
        public RunModifier Modifier { get; private set; } = RunModifier.None;

        /// <summary>是否有 Boss 提前规则。</summary>
        public bool BossRushed => Modifier == RunModifier.TwinMoons;

        /// <summary>开局规则公告剩余秒数。0 = 已过公告期。</summary>
        public float ModifierAnnounce => _modifierAnnounce;
        private float _modifierAnnounce;

        /// <summary>敌机变异（每局随机 0-2 条，与 Modifier 独立抽取）。
        /// 这是敌人剧本层的 Seed：让「这一局的敌人」与上一局不同。
        /// 黎明前20分钟被批「小怪只有3种、玩法没特色」——本作 4 种敌机同病，
        /// 变异让同一种敌机在不同局里有不同威胁模型。</summary>
        public bool MutationSplit { get; private set; }     // Drifter 死亡分裂
        public bool MutationPair { get; private set; }      // Charger 成对冲锋
        public bool MutationSwift { get; private set; }     // Shooter 射速加快

        private void RollMutations()
        {
            MutationSplit = UnityEngine.Random.value < 0.35f;
            MutationPair = UnityEngine.Random.value < 0.25f;
            MutationSwift = UnityEngine.Random.value < 0.30f;
        }

        /// <summary>开局抽取本局规则。
        /// 30% 基线局是刻意的：规则局多了，「规则」会沦为默认设定。</summary>
        private void RollModifier()
        {
            if (UnityEngine.Random.value < 0.30f)
            {
                Modifier = RunModifier.None;
                return;
            }
            // 1~5 均匀抽取（不含 None）
            Modifier = (RunModifier)UnityEngine.Random.Range(1, 6);
        }

        /// <summary>
        /// 擦弹：敌弹贴身掠过但未命中。
        ///
        /// 为什么这是本作最重要的机制：在此之前，玩家的移动只有「躲弹」一个目的，
        /// 而躲弹是**负价值**——它只让你少受损失，不创造任何收益。
        /// 于是蹲角落成了理性选择（AI 玩家里也独立得出同一结论）。
        ///
        /// 擦弹把「贴近危险」变成正收益：贴得够近但没被命中，系统给你奖励。
        /// 这是东方Project 三十年前就给出的答案，本作直到现在才补上。
        /// 操作性质由此从「避害」反转为「趋利」。
        /// </summary>
        private void OnGraze(Vector2 pos)
        {
            _grazeCount++;
            _tension++;

            // 火花要小：擦弹一局会发生几十次，抢戏的特效会让玩家忽略真正的威胁
            Fx?.Spark(pos, (pos - (Vector2)Player.Position).normalized, GrazeColor, false);

            if (_tension >= TensionFull)
            {
                _tension = 0;
                TriggerSurge();
                FloatText.Push(Player.Position + Vector2.up * 1.5f,
                    "张 力 释 放", GrazeColor, true);
            }
        }

        /// <summary>
        /// 逆潮：受伤瞬间肃清身边敌弹。
        /// 与护盾同理——没掉命也要有可见反馈，否则升级等于白买。
        /// 清了几颗弹要报数：数字是「这波不亏」的直接证据。
        /// </summary>
        private void OnCounterTide()
        {
            int layers = Player.CounterTide;
            float radius = 2.5f + 0.8f * layers;
            Vector2 pos = Player.Position;
            int cleared = Bullets.ClearEnemyBulletsNear(pos, radius);
            if (cleared > 0)
            {
                Fx?.SpawnPulse(pos, GrazeColor);
                FloatText.Push(pos + Vector2.up * 1.1f,
                    "逆 潮  肃清 " + cleared, GrazeColor, true);
                Sfx.Hit(true);
            }
        }

        private void TickShrink(float dt)
        {
            if (_burnCooldown > 0f)
            {
                _burnCooldown -= dt;
            }

            if (_shrinkRemaining > 0f)
            {
                _shrinkRemaining = Mathf.Max(0f, _shrinkRemaining - dt);
                if (_shrinkRemaining <= 0f)
                {
                    _playBounds = new Vector2(16.4f, 9.0f);
                    _shrinkTimer = ShrinkInterval;
                }
            }
            else
            {
                _shrinkTimer -= dt;
                if (_shrinkTimer <= 0f)
                {
                    _shrinkRemaining = ShrinkDuration;
                    _playBounds = new Vector2(16.4f - ShrinkInset, 9.0f - ShrinkInset);
                    // 收缩开始时，在被灼烧的边缘区撒一圈能量球。
                    // 这把「惩罚区」翻转为「博弈区」：站在中心安全但零收益，
                    // 冲进灼烧带捡球是拿风险换资源——而边缘空间狭窄，
                    // 单体高伤流派在这里反而有相对优势，顺手打破「扇幕唯一解」。
                    if (_orbs != null && Player != null)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            float ang = UnityEngine.Random.value * Mathf.PI * 2f;
                            Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                            Vector2 edge = new Vector2(
                                Mathf.Clamp(dir.x * 12f, -10.6f, 10.6f),
                                Mathf.Clamp(dir.y * 9.8f, -9.0f, 9.0f));
                            _orbs.Spawn(edge, 1);
                        }
                    }
                    // 复用波次开始的低鸣当收缩警报：音效库里没有更合适的，
                    // 而「低频长鸣」在听感上就是警报的通用语言。
                    Sfx.WaveStart();
                    FloatText.Push(new Vector2(0f, 7.0f), "边 界 收 缩", ShrinkWarnColor, true);
                }
            }

            if (Player.IsAlive && _shrinkRemaining > 0f && _burnCooldown <= 0f)
            {
                Vector2 p = Player.Position;
                if (Mathf.Abs(p.x) > _playBounds.x || Mathf.Abs(p.y) > _playBounds.y)
                {
                    Player.ApplyBurn();
                    _burnCooldown = BurnCooldown;
                    FloatText.Push(p + Vector2.up * 0.9f, "灼 烧", ShrinkWarnColor, false);
                }
            }
        }

        /// <summary>
        /// 火力成长：每 20 秒 +1 级（封顶 3）。
        /// 满级是进化的前提，所以这条曲线直接决定玩家多久能见到武器进化——
        /// 而「什么时候能变强」是这类游戏最核心的盼头。
        /// 帧序列里看到玩家 54 秒一局全程停在双发，就是因为它没接线。
        /// </summary>
        private void TickPowerUp(float dt)
        {
            if (Player.Form != WeaponForm.Standard || Player.PowerLevel >= 3)
            {
                return;
            }
            _powerTimer += dt;
            if (_powerTimer < PowerUpInterval)
            {
                return;
            }
            _powerTimer = 0f;
            if (Player.AddPower())
            {
                Fx?.SpawnPulse(Player.Position, PowerUpColor);
                FloatText.Push(Player.Position + Vector2.up * 1.0f,
                    Player.PowerLevel >= 3 ? "火 力 满 级" : "火 力 提 升",
                    PowerUpColor, Player.PowerLevel >= 3);
                // 满级这一刻额外抖一下：这是「可以攒球进化了」的发令枪
                if (Player.PowerLevel >= 3)
                {
                    Fx?.Shake(0.18f, 0.16f);
                }
            }
        }

        private void TickCombo(float dt)
        {
            if (Combo <= 0)
            {
                return;
            }
            // 过载期间连击窗口衰减降到 35%。
            //
            // 为什么：受伤已经付过代价了——一条命 + 3 秒射速减半。
            // 如果连击再因此断掉，就是同一件事罚两遍。实测链条是
            // 「受伤 → 射速减半 → 打不死 → 连击断 → 倍率从 8.00 掉到 2.75
            //  → 更打不死 → 再受伤」，玩家一旦掉进去就爬不出来。
            // 射速那一项已经罚过，连击这一项不再追加惩罚，
            // 残局才保得住「拼一把扳回来」的可能——这正是残血爆发的设计意图。
            float decayScale = Player != null && Player.OverloadRemaining > 0f
                ? ComboOverloadGrace
                : 1f;
            _comboTimer -= dt * decayScale;
            if (_comboTimer <= 0f)
            {
                Combo = 0;
                _comboTimer = 0f;
            }
        }

        public float ComboMultiplier
        {
            get
            {
                float baseMul = Mathf.Min(8f, 1f + Combo * 0.25f);
                // 赌局期间倍率 ×2，且封顶从 8 抬到 16。
                // 抬封顶是必须的——不抬的话高连击时 ×2 会被 8 的帽子切掉，
                // 赌局在玩家最需要它的时候反而失效，那是个假机制。
                return _gambleActive ? Mathf.Min(16f, baseMul * 2f) : baseMul;
            }
        }

        // ---------------- 生成 ----------------

        private static int WaveSize(int wave) => 14 + wave * 6;

        private void TickSpawner(float dt)
        {
            _spawnTimer -= dt;
            if (_spawnTimer > 0f)
            {
                return;
            }

            if (_waveRemaining <= 0)
            {
                _waveIndex++;
                Wave++;
                _waveRemaining = WaveSize(Wave);
                _spawnTimer = 1.2f;
                Fx.Shake(0.2f, 0.12f);
                // 每 5 波一个 Boss。为什么是 5：一局约 120~290 秒、17 波左右，
                // 5 波一个意味着玩家一局能见到 2~3 次 Boss——足够形成期待，
                // 又不至于多到麻木。
                if (Wave == FinalWave)
                {
                    SpawnFinalBoss();
                    return;
                }
                int bossAt = BossRushed ? 3 : BossWaveInterval;
                if (Wave % bossAt == 0)
                {
                    SpawnBoss();
                }
                return;
            }

            // 生成节奏由自适应难度接管：pressure 高则刷得更密
            float baseInterval = Mathf.Max(0.11f, 0.42f - Wave * 0.04f);
            _spawnTimer = Mathf.Max(0.10f, baseInterval / Mathf.Max(0.1f, _difficulty.Pressure));

            EnemyKind kind = RollKind(Wave, _difficulty.Pressure);
            Vector2 pos = new Vector2(UnityEngine.Random.Range(-16.0f, 16.0f), 10.8f);
            EnemyShip e = Enemies.Spawn(kind, pos, Wave, _difficulty.Pressure);
            if (e != null)
            {
                e.Died += OnEnemyDied;
                // 成对变异：Charger 双生出现——两只同点位冲脸，
                // 逼玩家真正用上预警窗口而不是只靠反应。
                if (MutationPair && kind == EnemyKind.Charger)
                {
                    Vector2 twinPos = pos + new Vector2(2.2f, 0f);
                    EnemyShip twin = Enemies.Spawn(kind, twinPos, Wave, _difficulty.Pressure);
                    if (twin != null) { twin.Died += OnEnemyDied; _waveRemaining--; }
                }
            }
            _waveRemaining--;
        }

        /// <summary>每多少波出一个 Boss。</summary>
        private const int BossWaveInterval = 5;

        /// <summary>终局波。潮主在此登场，击破即通关。
        /// Brotato 玩家最常见的遗憾：「好不容易凑出无敌 build 就结束了」——
        /// 终局给 build 一个值得奔赴的考场，也给整局一条弧线。</summary>
        public const int FinalWave = 20;

        /// <summary>潮主是否已在场。</summary>
        public bool FinalBossLive { get; private set; }

        /// <summary>
        /// Boss 登场。复用 Spinner 放大，但反馈要拉满：
        /// 抖屏 + 低频警报 + 双行大字。
        /// 为什么反馈要这么重：Boss 是整局唯一的「节奏高潮」，
        /// 如果它像普通敌人一样默默滑进屏幕，玩家根本不会意识到
        /// 「关键时刻来了」，也就不会调整打法——那 Boss 就白设计了。
        /// </summary>
        private void SpawnBoss()
        {
            Vector2 pos = new Vector2(UnityEngine.Random.Range(-10.0f, 10.0f), 7.0f);
            EnemyShip boss = Enemies.SpawnBoss(pos, Wave);
            if (boss == null)
            {
                return;
            }
            boss.Died += OnEnemyDied;

            Fx?.Shake(0.5f, 0.6f);
            Sfx.Blackout();
            FloatText.Push(new Vector2(0f, 6.2f), "W A R N I N G",
                new Color32(0xFF, 0x4D, 0x5E, 0xFF), true);
            FloatText.Push(new Vector2(0f, 8.2f), "强 敌 登 场",
                new Color32(0xFF, 0xA6, 0x2B, 0xFF), true);
            _hitstop = Mathf.Max(_hitstop, 0.1f);
        }

        /// <summary>
        /// 终局 Boss「潮主」：比普通 Boss 更大更硬，半血后进入狂暴
        /// （弹幕频率 ×1.5）。击破后显示通关大字——
        /// 「打完这一局」必须是一个可到达的时刻，而不是无尽的延续。
        /// </summary>
        private void SpawnFinalBoss()
        {
            Vector2 pos = new Vector2(0f, 6.5f);
            EnemyShip boss = Enemies.SpawnFinalBoss(pos, Wave);
            if (boss == null)
            {
                return;
            }
            boss.Died += OnFinalBossDied;
            FinalBossLive = true;

            Fx?.Shake(0.8f, 0.9f);
            _hitstop = Mathf.Max(_hitstop, 0.16f);
            Sfx.Blackout();
            FloatText.Push(new Vector2(0f, 6.4f), "潮  主",
                new Color32(0xFF, 0x4D, 0x5E, 0xFF), true);
            FloatText.Push(new Vector2(0f, 8.4f), "最 后 的 强 敌",
                new Color32(0xFF, 0xA6, 0x2B, 0xFF), true);
        }

        /// <summary>潮主被击破：通关。</summary>
        private void OnFinalBossDied(EnemyShip boss)
        {
            FinalBossLive = false;
            // 复用死亡结算（球/分/反馈），再叠通关仪式
            OnEnemyDied(boss);
            Bullets.ClearAllEnemyBullets();
            _hitstop = Mathf.Max(_hitstop, 0.25f);
            Fx?.Shake(1.0f, 0.9f);
            FloatText.Push(new Vector2(0f, 4.5f), "潮  汐  平  息",
                new Color32(0xFF, 0xD2, 0x4D, 0xFF), true);
            FloatText.Push(new Vector2(0f, 7.5f),
                "通 关   分数 " + Score.ToString("N0"),
                new Color32(0xB4, 0xFF, 0xE8, 0xFF), true);
            Sfx.LevelUp();
            Sfx.LevelUp();
        }

        private static EnemyKind RollKind(int wave, float pressure)
        {
            float r = UnityEngine.Random.value;
            if (wave >= 5 && r < 0.16f)
            {
                return EnemyKind.Spinner;
            }
            // Shooter 是弹幕的来源，占比必须够高，否则屏幕会空。
            // 高压时才提比例，低压时给玩家喘息。
            float shooterShare = pressure >= 1.2f ? 0.62f : 0.48f;
            if (r < shooterShare)
            {
                return EnemyKind.Shooter;
            }
            // Charger 占比 18% → 13%。
            // 修追踪弹死代码之前，Charger 的实际威胁是 0（弹根本发不出来），
            // 18% 这个数是在那个前提下定的。现在它会发锁定弹了，
            // 占比不降的话 WAVE 4 的压力会比设计值高出一截——
            // 实测玩家正是在 WAVE 4 从 3 命掉到 1 命然后被打死。
            // 追踪弹本身不砍（那是 Charger 的设计核心），砍数量。
            if (wave >= 2 && r < shooterShare + 0.13f)
            {
                return EnemyKind.Charger;
            }
            return EnemyKind.Drifter;
        }

        // ---------------- 命中与死亡 ----------------

        private void OnBulletHitEnemy(EnemyShip enemy, Vector2 point)
        {
            if (enemy == null || !enemy.IsAlive)
            {
                return;
            }
            int dmg = Player != null ? PlayerFireDamage() : 12;
            enemy.TakeDamage(dmg);
            Color c = enemy.Kind == EnemyKind.Spinner
                ? new Color32(0xFF, 0xC9, 0x4D, 0xFF)
                : new Color32(0x7F, 0xF0, 0xFF, 0xFF);
            Fx?.Spark(point, (point - (Vector2)enemy.Position).normalized, c, false);
            // 命中火花保留（成本低、不冻帧），但顿帧只留给关键击杀。
            if (Combo >= KeyKillCombo)
            {
                _hitstop = Mathf.Max(_hitstop, 0.012f);
            }
        }

        private int PlayerFireDamage()
        {
            return 12;
        }

        /// <summary>
        /// 潮涌：连击里程碑奖励。清掉全部敌弹 + 顿帧 + 全屏冲击环。
        ///
        /// 为什么清敌弹而**不清敌人**：清敌人等于替玩家打完，那是剥夺成就感；
        /// 清敌弹是「把你眼前的威胁抹掉」，是纯粹的解围。
        /// 玩家自己的弹也一律保留——清场把自己火力也掐了的那种「奖励」其实是惩罚。
        ///
        /// 为什么需要里程碑：连击倍率 28 连击就封顶，之后多杀一个没有边际收益，
        /// 玩家会失去追求目标（帧序列里就看到玩家从「追连击」转为「求不死」）。
        /// 潮涌给出一个明确的短期目标：「撑到 100 连击」。
        /// </summary>
        private void TriggerSurge()
        {
            int cleared = Bullets.ClearAllEnemyBullets();
            _hitstop = Mathf.Max(_hitstop, 0.12f);
            Fx?.SpawnPulse(Player.Position, SurgeColor);
            Fx?.Shake(0.3f, 0.35f);
            FloatText.Push(new Vector2(0f, 6.5f), "潮  涌", SurgeColor, true);
            FloatText.Push(new Vector2(0f, 8.6f),
                "敌弹肃清 " + cleared, SurgeColor, false);
            Sfx.Blackout();
            _nextSurgeCombo = Combo + SurgeIntervalCombo;
        }

        private void OnEnemyDied(EnemyShip enemy)
        {
            if (enemy == null)
            {
                return;
            }
            enemy.Died -= OnEnemyDied;

            bool big = enemy.Kind == EnemyKind.Spinner || enemy.Radius >= 0.85f;
            Vector2 pos = enemy.Position;
            // 爆炸色跟随敌人的暖色带，不再有紫色残值
            Color color = enemy.Kind == EnemyKind.Spinner
                ? new Color32(0xFF, 0xA6, 0x2B, 0xFF)
                : new Color32(0xE0, 0x45, 0x7F, 0xFF);

            Fx?.Explosion(pos, big ? 4.2f : 2.4f, color, big);
            // 普通小怪不再顿帧（原来是 28ms）。密集击杀时这是帧率杀手，
            // 也是「手感糊」的直接来源。只有大怪和高连击才配得上打击感。
            _hitstop = Mathf.Max(_hitstop,
                big ? 0.07f : (Combo >= KeyKillCombo ? 0.028f : 0f));

            // 能量球掉落：普通 25%，大怪必掉且掉 3 个。
            // 25% 的由来：实测击杀率约 2.9/s，25% ≈ 0.73 球/秒，
            // 攒满进化所需的 12 个约 16 秒——「一波多」的尺度，不至于等到忘记这件事。
            // 大怪必掉是给「打大怪」一个额外理由，否则 Spinner 只是血多而已。
            // 掉落率 = 基础 25% + 「财运」升级每层 8%
            float dropRate = 0.25f + (Player != null ? Player.OrbDropBonus : 0f);
            if (Modifier == RunModifier.TideOut) { dropRate *= 2f; }
            int drop = big ? 3 : (UnityEngine.Random.value < dropRate ? 1 : 0);
            if (drop > 0)
            {
                _orbs?.Spawn(pos, drop);
            }

            // 连击与计分：连续击杀把倍率推上去，屏幕上数字一路跳
            Combo++;
            TotalKills++;
            _difficulty.NoteKill();
            _comboTimer = _comboDuration;
            float scoreMul = ComboMultiplier * (Modifier == RunModifier.LoneBet ? 3f : 1f);
            int gained = Mathf.RoundToInt(enemy.ScoreValue * scoreMul);
            Score += gained;
            if (Score > BestScore)
            {
                BestScore = Score;
            }
            FloatText.Push(pos, "+" + gained,
                Combo >= 8 ? new Color32(0xFF, 0xE9, 0xA8, 0xFF) : new Color32(0xE8, 0xF1, 0xF5, 0xFF),
                Combo >= 5);

            // 碎星：击杀时从玩家位置射出碎片（自动索敌）。
            // 召唤流的资源循环：杀得越多 → 碎片越多 → 杀得更多。
            // 碎片伤害故意压低（6）：它是「量」的来源而不是「质」，否则会盖过主武器。
            if (Player != null && Player.Craft == PlayerCraft.Star)
            {
                int shards = big ? 3 : 1;
                for (int i = 0; i < shards; i++)
                {
                    Bullets.SpawnHomingBullet(Player.Position + Vector2.up * 0.5f,
                        14f, 6, GrazeColor);
                }
            }

            // 分裂变异：Drifter 死亡裂成两只小的（半血半分）。
            // 用 Radius < 0.4 判定「已是小号」，防止无限递归分裂。
            if (MutationSplit && enemy.Kind == EnemyKind.Drifter && enemy.Radius >= 0.4f)
            {
                for (int i = 0; i < 2; i++)
                {
                    Vector2 spawnAt = pos + new Vector2(i == 0 ? -0.8f : 0.8f, 0f);
                    EnemyShip child = Enemies.Spawn(EnemyKind.Drifter, spawnAt, Wave, 0.5f);
                    if (child != null)
                    {
                        child.ScaleDown(0.6f);
                        child.Died += OnEnemyDied;
                    }
                }
            }

            // 潮涌：连击里程碑
            if (Combo >= _nextSurgeCombo)
            {
                TriggerSurge();
            }

            // Boss 击破：本作最大的单次回报。12 个球（够立刻再换一次升级）+
            // 回 1 命 + 大爆炸 + 大字。没有这个量级的回报，
            // 「打Boss」就只是个血多的普通怪，玩家不会为它调整打法。
            if (enemy.IsBoss)
            {
                _orbs?.Spawn(pos, 12);
                if (Player != null && Player.Heal(1))
                {
                    FloatText.Push(new Vector2(0f, 7f), "+1 命",
                        new Color32(0xB4, 0xFF, 0xE8, 0xFF), false);
                }
                _hitstop = Mathf.Max(_hitstop, 0.15f);
                Fx?.Shake(0.6f, 0.7f);
                FloatText.Push(new Vector2(0f, 5f), "强 敌 击 破",
                    new Color32(0xFF, 0xD2, 0x4D, 0xFF), true);
                Sfx.LevelUp();
            }

            // 连击回血：打到阈值就回 1 条命。
            // 这是「打得凶就有回报」的正反馈——残局不该只有逃命的选项。
            // 阈值递增（20, 35, 50...），避免靠刷连击无限回血。
            if (Combo >= _nextHealCombo && Player != null && Player.Heal(1))
            {
                _nextHealCombo += 15;
                Fx?.SpawnPulse(Player.Position, new Color32(0xFF, 0xE9, 0xA8, 0xFF));
                Fx?.Shake(0.22f, 0.18f);
                FloatText.Push(Player.Position + Vector2.up * 0.9f, "+1 命",
                    new Color32(0xFF, 0xE9, 0xA8, 0xFF), true);
            }

            // 连锁引爆：打死一个能带掉一片，这是最直接的爽感
            if (_chainDepth < 3)
            {
                _chainDepth++;
                int chainDamage = Mathf.Max(6, Mathf.RoundToInt(enemy.MaxHp * 0.55f));
                Enemies.ForEachNear(pos, 2.3f, other =>
                {
                    if (other != null && other.IsAlive)
                    {
                        other.TakeDamage(chainDamage);
                    }
                });
                _chainDepth--;
            }

            // 回响：每次击杀都发出冲击波，与连锁爆炸共享深度限制防递归。
            // 与连锁的区别：连锁是「死亡扩散」（被动），回响是「每次击杀主动输出」。
            // 和「驱赶聚怪」的打法天然协同——聚得越密，回响吃得越满。
            int echo = Player != null ? Player.EchoKills : 0;
            if (echo > 0 && _chainDepth < 3)
            {
                _chainDepth++;
                float echoRadius = 1.8f + 0.5f * echo;
                int echoDmg = 6 + 4 * echo;
                Enemies.ForEachNear(pos, echoRadius, other =>
                {
                    if (other != null && other.IsAlive)
                    {
                        other.TakeDamage(echoDmg);
                    }
                });
                // 潮爆（回响+财运）：冲击波把范围内的球直接吸给玩家。
                // 击杀 → 冲击波 → 顺便收球 → 攒升级，整条循环不断档。
                if (Player.SynergyTideBurst && _orbs != null)
                {
                    Player.AddEnergyOrbs(1);
                }
                _chainDepth--;
            }
        }

        private void OnBulletHitPlayer(Vector2 point)
        {
            if (Player != null && Player.TakeHit())
            {
                Fx?.Explosion(point, 2.2f, new Color32(0xFF, 0x4D, 0x5E, 0xFF), false);
                _hitstop = Mathf.Max(_hitstop, 0.05f);
            }
        }

        /// <summary>
        /// 护盾挡下一次伤害。**没掉命，但必须有强反馈。**
        /// 玩家是花了升级才换来这层盾的，如果挡刀时画面毫无变化，
        /// 他会得出「这升级没用」的结论而再也不选——那是升级设计失败，不是玩家瞎。
        /// </summary>
        private void OnShieldBroken()
        {
            Fx?.SpawnPulse(Player.Position, ShieldColor);
            Fx?.Shake(0.14f, 0.12f);
            FloatText.Push(Player.Position + Vector2.up * 0.9f, "护 盾", ShieldColor, false);
            // 棱镜（逆潮+护盾）：盾碎的一瞬把周围敌弹「折射」掉，并反噬敌机。
            // 命名取「碎镜折射」：防守资源在破碎瞬间转化为进攻——
            // 失败的瞬间变成反击的瞬间，这是负反馈转正反馈的经典手法。
            if (Player.SynergyPrism)
            {
                int cleared = Bullets.ClearEnemyBulletsNear(Player.Position, 4.5f);
                int dmg = 10 + 5 * Player.CounterTide;
                Enemies.ForEachNear(Player.Position, 3.5f, e =>
                {
                    if (e != null && e.IsAlive) { e.TakeDamage(dmg); }
                });
                Fx?.SpawnPulse(Player.Position, GrazeColor);
                FloatText.Push(Player.Position + Vector2.up * 1.3f,
                    "棱 镜  肃清 " + cleared, GrazeColor, true);
            }
        }

        private void OnPlayerDamaged()
        {
            // 残血时不再向 DDA 报「减压」。
            // 深度玩家评审指出的死结：受伤→DDA减压→残血时敌人反而变弱→
            // 「残血狂暴」沦为「游戏在照顾我」。残血爆发的意义是绝地反击，
            // 那压力就必须是真的——压力记入窗口统计，但即时减压跳过。
            if (Player.LastStand)
            {
                _difficulty.NoteHurtNoRelief();
            }
            else
            {
                _difficulty.NoteHurt();
            }
            Fx?.Shake(0.3f, 0.4f);
            Fx?.SpawnPulse(Player.Position, new Color32(0xFF, 0x4D, 0x5E, 0xFF));
        }

        private void OnPlayerDied()
        {
            Fx?.Explosion(Player.Position, 6f, new Color32(0xFF, 0x4D, 0x5E, 0xFF), true);
            Fx?.Shake(0.6f, 0.7f);
            // 死亡必须清场。原来不清的后果：TickFrame 里 `if (State != Playing) return;`
            // 排在 Fx/星空 Tick 之后，子弹和敌机从此不再更新，却仍在每帧渲染——
            // 结算界面的背景就是一帧冻结的游戏场景，子弹悬停、敌人悬停。
            // 玩家看到的是「游戏卡死了」而不是「我被打死了」，死亡没有结束感。
            // Fx 故意不清：爆炸冲击环还要播 0.38 秒，且它在状态判断之前 Tick，会自然衰减。
            Bullets.ClearAll();
            Enemies.ClearAll();
            // 保险：观测到 Boss 在死亡结算界面上残留，而日志零异常（不是崩溃），
            // 说明 SetActive(false) 有没覆盖到的路径。再强制禁一次 renderer，
            // 并把池里的一并禁掉，防止下一局复用瞬间闪一帧。
            Enemies.ForceHideAll();
            // 能量球也要清。上一轮的教训就是「清了子弹和敌人却忘了飞船自己」，
            // 这次不能再漏第三样——结算界面背景上冻着一排金色小球，
            // 玩家看到的是「游戏没结束」而不是「我死了」。
            _orbs?.ClearAll();
            // 宝箱也要清。不清的后果：死亡瞬间开了一半的箱子挂在结算界面上，
            // 和子弹/敌人/飞船残留是同一类错误——清场必须列全清单。
            _rewards?.ClearAll();
            // 飞船自己也要隐藏。少了这一行的后果：结算界面背景上挂着一个
            // 孤零零的三角形，清了子弹和敌人却忘了自己，玩家看到的是
            // 「没死透」而不是「结束了」。
            Player?.SetVisible(false);
            SetState(ShotState.Dead);
        }

        public float RunTime => _runTime;
    }
}
