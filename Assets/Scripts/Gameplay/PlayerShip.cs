using System;
using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 武器形态。Standard 是初始形态（随 PowerLevel 1/2/3 线性成长）；
    /// 另外三种是进化分支，用能量球换，三选一且不可逆。
    /// 三个分支刻意做成**不同打法**而不是不同数值——
    /// 否则「进化」只是数字变大，玩家感受不到自己做了选择。
    /// </summary>
    /// <summary>
    /// 开局机体。Path Variety 的地基：不同机体 = 不同的弹幕形状与打法 =
    /// 不同的升级优先级。三个机体刻意做成「没有谁是最优解」——
    /// 孤潮均衡、碎星单体爆发、回壁靠反弹堵路。
    /// </summary>
    public enum PlayerCraft
    {
        /// <summary>孤潮：均衡。双发直线弹。</summary>
        Lone = 0,
        /// <summary>碎星：单发高伤慢速，击杀掉落自动索敌碎片——召唤流。</summary>
        Star = 1,
        /// <summary>回壁：子弹撞墙反弹两次——用你自己的弹幕封路。</summary>
        Wall = 2
    }

    public enum WeaponForm
    {
        /// <summary>初始形态。</summary>
        Standard = 0,

        /// <summary>潮锋：单发粗弹穿透一串敌人。慢、贵、准，奖励精准走位到敌群中轴线。</summary>
        TidePierce = 1,

        /// <summary>扇幕：五发大扇形 + 高射速、低单发伤害。奖励贴近弹幕正面压制。</summary>
        FanBarrage = 2,

        /// <summary>回澜：前三后三双向开火。专治「背后被偷袭」这个纯亏场景，
        /// 让玩家敢把注意力放在一个方向上。</summary>
        EchoWave = 3
    }

    /// <summary>
    /// 玩家飞船。判定点远小于视觉尺寸——这是弹幕游戏的通行做法：
    /// 看起来擦着子弹飞过去，实际判定只有中心一个小点，惊险但公平。
    /// </summary>
    public sealed class PlayerShip : MonoBehaviour
    {
        public Vector2 Position { get; private set; }
        public bool IsAlive { get; private set; }
        public bool CanBeHit => IsAlive && _invuln <= 0f;
        public float HitRadius => 0.26f;
        public int Lives { get; private set; }
        public int PowerLevel { get; private set; }
        public int MaxLives => (_cfg != null ? _cfg.startLives : 3) + _maxLivesBonus;

        /// <summary>当前武器形态。</summary>
        public WeaponForm Form { get; private set; } = WeaponForm.Standard;

        /// <summary>已攒到的能量球数。只在 Standard 形态下累积——
        /// 进化之后再去捡球没有意义，不该让玩家做无用功。</summary>
        public int EnergyOrbs { get; private set; }

        /// <summary>触发一次进化需要的能量球数。
        /// 12 → 8：掉落率约 25%、击杀率约 2.9/s，即约 0.73 球/秒，
        /// 8 个约 11 秒——配合 20 秒满级，首次进化落在 30 秒左右，
        /// 正好卡在实测对局长度（54~123 秒）的前半段，不至于到死都没见到。</summary>
        public const int EvolutionCost = 8;

        /// <summary>是否满足进化条件（满级 + 球够）。</summary>
        public bool EvolutionReady =>
            Form == WeaponForm.Standard && PowerLevel >= 3 && EnergyOrbs >= EvolutionCost;

        /// <summary>已触发进化选择、正在等玩家拍板。此期间玩法冻结。</summary>
        public bool EvolutionPending { get; private set; }

        // ---------------- 随机升级（老虎机） ----------------

        /// <summary>已触发随机升级、正在等玩家拍板。此期间玩法冻结。</summary>
        public bool UpgradePending { get; private set; }

        /// <summary>两种选择任意一种挂着都该冻结玩法。</summary>
        public bool AnyChoicePending => EvolutionPending || UpgradePending;

        /// <summary>开局机体。StartRun 前由主菜单设定，局内不变。</summary>
        public PlayerCraft Craft { get; set; } = PlayerCraft.Lone;

        /// <summary>当前待选的三个升级 id。仅在 UpgradePending 为真时有意义。</summary>
        public int[] PendingChoices { get; private set; } = new int[3];

        private readonly int[] _upgrades = new int[UpgradePool.Count];
        private int _shield;
        private int _maxLivesBonus;
        private float _frenzy;

        /// <summary>某升级当前叠了几层。</summary>
        public int UpgradeStacks(int id)
        {
            return id >= 0 && id < UpgradePool.Count ? _upgrades[id] : 0;
        }

        /// <summary>剩余护盾层数。护盾是唯一能「主动选择变硬」的升级，
        /// 也是唯一让「往弹幕里钻」从纯吃亏变成可计算风险的升级。</summary>
        public int ShieldCount => _shield;

        /// <summary>暴击率（0~1）。</summary>
        public float CritChance => Mathf.Clamp01(_upgrades[9] * 0.10f);
        /// <summary>移速系数。</summary>
        public float MoveScale => 1f + _upgrades[6] * 0.08f;
        /// <summary>射速系数（&gt;1 表示更快）。</summary>
        public float FireRateScale => 1f + _upgrades[0] * 0.12f;
        /// <summary>额外伤害（加法）。</summary>
        public int DamageBonus => _upgrades[1] * 3;
        /// <summary>弹速系数。</summary>
        public float BulletSpeedScale => 1f + _upgrades[2] * 0.10f;
        /// <summary>穿透层数。</summary>
        public int PierceBonus => _upgrades[3];
        /// <summary>额外弹数。</summary>
        public int ExtraBullets => _upgrades[4];
        /// <summary>磁吸范围系数。</summary>
        public float MagnetScale => 1f + _upgrades[5] * 0.25f;
        /// <summary>连击窗口加成（秒）。</summary>
        public float ComboWindowBonus => _upgrades[10] * 0.4f;
        /// <summary>能量球掉落率加成。</summary>
        public float OrbDropBonus => _upgrades[11] * 0.08f;

        // ---- 机制型升级 ----

        /// <summary>潮旋：子弹正弦横移幅度。0 = 直线。</summary>
        public float BulletWave => _upgrades[12] * 1.1f;
        /// <summary>回响：击杀时冲击波的伤害加成层数。</summary>
        public int EchoKills => _upgrades[13];
        /// <summary>镜像：幻影数量（每层左右各一个）。</summary>
        public int MirrorCount => _upgrades[14];
        /// <summary>逆潮：受伤时肃清半径层数。</summary>
        public int CounterTide => _upgrades[15];

        /// <summary>
        /// 两个升级是否同时至少各有一层（协同判定）。
        ///
        /// Synergy 是 Variety 公式的第三个因子（Seed × Build × **Synergy**），
        /// 也是玩家口口相传一款游戏的原始动力——「两个从没配过的东西
        /// 突然组成引擎」的那个瞬间。设计上刻意只做「1+1 出新效果」，
        /// 不做数值加成：数值协同玩家感知不到，机制协同才会被讨论。
        /// </summary>
        public bool HasSynergy(int idA, int idB)
        {
            return _upgrades[idA] > 0 && _upgrades[idB] > 0;
        }

        /// <summary>漩涡双子（潮旋+镜像）：幻影弹也走弧线。</summary>
        public bool SynergyVortexTwins => HasSynergy(12, 14);
        /// <summary>棱镜（逆潮+护盾）：护盾破碎时反弹一圈敌弹。</summary>
        public bool SynergyPrism => HasSynergy(15, 7);
        /// <summary>潮爆（回响+财运）：冲击波直接吸收能量球。</summary>
        public bool SynergyTideBurst => HasSynergy(13, 11);
        /// <summary>穿心（会心+贯穿）：暴击时额外穿透 +2。</summary>
        public bool SynergyHeartPierce => HasSynergy(9, 3);

        /// <summary>受伤时触发逆潮。参数：受击位置。GameDirector 据此清弹。</summary>
        public event Action CounterTideTriggered;

        /// <summary>
        /// 残血爆发（背水一战）。只剩 1 条命时自动触发：
        /// 武器强制满级、射速更快、弹速更高。
        /// 设计意图：残血不该等于等死——给玩家一个「拼一把」的窗口，
        /// 而不是被压到只能逃命。
        /// </summary>
        public bool LastStand { get; private set; }
        public float InvulnRemaining => _invuln;

        /// <summary>过载剩余秒数。>0 表示射速减半中（受伤后的惩罚窗口）。</summary>
        public float OverloadRemaining => _overload;

        /// <summary>自动演示：不读输入，改为左右摆动（供无人值守截图验证用）。</summary>
        public bool AutoPilot { get; set; }

        /// <summary>
        /// 外部输入（由 MCP 控制服务器注入）。为 null 时读键盘。
        /// 支持 AI 通过工具调用操作飞船。
        /// </summary>
        public Vector2? ExternalInput { get; set; }

        public event Action Damaged;
        public event Action Died;

        /// <summary>护盾挡下一次伤害。没掉命，但玩家需要知道「刚才那下是盾吃的」。</summary>
        public event Action ShieldBroken;

        private SpriteRenderer _renderer;
        private Transform _visual;
        private Vector3 _visualBase;
        private float _invuln;
        private float _fireTimer;
        private Vector2 _velocity;
        private PlayerStatsConfig _cfg;
        private float _overload;

        /// <summary>过载持续秒数。受伤不再掉武器等级，改为短时间射速减半——
        /// 惩罚的是「这一下之后的反应窗口」，而不是永久能力。</summary>
        private const float OverloadSeconds = 3f;

        /// <summary>过载总时长（秒）。公开是为了让 HUD 画进度条时不用再写死一个 3——
        /// 两处各写一个常量，改一处忘另一处是必然的。</summary>
        public const float OverloadDuration = OverloadSeconds;

        private static readonly Color BulletColor = new Color32(0x7F, 0xF0, 0xFF, 0xFF);
        private static readonly Color CoreColor = new Color32(0xB4, 0xFF, 0xE8, 0xFF);
        /// <summary>残血时的灯色：从青绿转为炽白偏红，视觉上「烧起来了」。</summary>
        private static readonly Color LastStandColor = new Color32(0xFF, 0xC8, 0xB4, 0xFF);

        public void Initialize(PlayerStatsConfig cfg)
        {
            _cfg = cfg;
            Lives = cfg.startLives;
            PowerLevel = cfg.startPower;
            IsAlive = true;
            _invuln = cfg.spawnInvuln;
            _fireTimer = 0f;
            Position = new Vector2(0f, cfg.startY);

            var sprite = SpriteFactory.Triangle(128, CoreColor, new Color32(0x2E, 0xA8, 0xC8, 0xFF));

            if (_renderer == null)
            {
                _renderer = gameObject.GetComponent<SpriteRenderer>();
                if (_renderer == null)
                {
                    _renderer = gameObject.AddComponent<SpriteRenderer>();
                }
            }
            _renderer.sprite = sprite;
            _renderer.sortingOrder = 8;
            _visual = transform;
            _visualBase = Vector3.one * (cfg.shipSize / Mathf.Max(0.001f, sprite.bounds.size.x));
            transform.localScale = _visualBase;
        }

        /// <summary>
        /// 控制飞船渲染显隐。死亡时必须调 false：
        /// 结算界面如果还挂着一个孤零零的三角形，玩家看到的是「没死透」
        /// 而不是「这一局结束了」。清场要清干净——子弹、敌人、自己，一个都不能漏。
        /// </summary>
        public void SetVisible(bool visible)
        {
            if (_renderer != null)
            {
                _renderer.enabled = visible;
            }
        }

        /// <summary>所有升级已满层，再捡球也不再触发选择。
        /// 必须给玩家反馈——否则他会以为球白捡了，进而觉得资源设计有问题。</summary>
        public event Action FullyUpgraded;

        /// <summary>拾取能量球。返回是否因此弹出了选择界面。</summary>
        public bool AddEnergyOrbs(int count)
        {
            if (count <= 0)
            {
                return false;
            }

            // 已进化之后，能量球不再是「一次性换形态」，而是「持续换随机升级」。
            // 这一步是把进化从一次性事件变成持续构筑的关键——
            // 一次性事件给一次爽感，持续构筑才给重玩动力。
            if (Form != WeaponForm.Standard)
            {
                if (UpgradePending || EvolutionPending)
                {
                    return false;
                }
                EnergyOrbs += count;
                if (EnergyOrbs >= EvolutionCost)
                {
                    int[] choices = RollUpgradeChoices();

                    // 全部满层时 choices 全是 -1。此时**绝不能**进入选择状态——
                    // 否则玩家面对三张「已满层」的卡片，点哪个都没反应，
                    // UpgradePending 永远为真，玩法被永久冻结。
                    // 这是死锁，不是体验问题：实测一局 274 秒、645 连击
                    // 就是这么卡住的，玩家只能强杀进程。
                    bool anyValid = false;
                    for (int i = 0; i < choices.Length; i++)
                    {
                        if (choices[i] >= 0)
                        {
                            anyValid = true;
                            break;
                        }
                    }
                    if (!anyValid)
                    {
                        EnergyOrbs = 0;
                        FullyUpgraded?.Invoke();
                        return false;
                    }

                    EnergyOrbs -= EvolutionCost;
                    PendingChoices = choices;
                    UpgradePending = true;
                    return true;
                }
                return false;
            }

            // 未进化：满级后攒球触发武器进化（一次性，三选一固定分支）
            EnergyOrbs += count;
            if (EvolutionReady && !EvolutionPending)
            {
                EvolutionPending = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 摇出三个可选升级。只从「还没满层」的里摇——满层的还出现在选项里，
        /// 等于给玩家一个点了没用的选择，是很劣质的体验。
        /// Fisher-Yates 洗牌后取前三个。
        /// </summary>
        public int[] RollUpgradeChoices()
        {
            var pool = new System.Collections.Generic.List<int>(UpgradePool.Count);
            for (int i = 0; i < UpgradePool.Count; i++)
            {
                if (_upgrades[i] < UpgradePool.All[i].MaxStacks)
                {
                    pool.Add(i);
                }
            }
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                int t = pool[i];
                pool[i] = pool[j];
                pool[j] = t;
            }
            var result = new int[3];
            for (int i = 0; i < result.Length && i < pool.Count; i++)
            {
                result[i] = pool[i];
            }
            // 池子被摇空了（全满级）时用 -1 占位，HUD 会显示为禁用态
            for (int i = pool.Count; i < result.Length; i++)
            {
                result[i] = -1;
            }
            return result;
        }

        /// <summary>真正吃下一个升级。</summary>
        public void ApplyUpgrade(int id)
        {
            if (id < 0 || id >= UpgradePool.Count)
            {
                return;
            }
            if (_upgrades[id] >= UpgradePool.All[id].MaxStacks)
            {
                return;
            }
            _upgrades[id]++;

            // 有即时效果的特殊升级
            if (id == 7)
            {
                _shield++;
            }
            if (id == 8)
            {
                _maxLivesBonus++;
                Lives = MaxLives;
            }
            Sfx.LevelUp();
        }

        /// <summary>玩家在三个随机升级里拍板。</summary>
        public void ChooseUpgrade(int index)
        {
            if (!UpgradePending || index < 0 || index >= PendingChoices.Length)
            {
                return;
            }
            ApplyUpgrade(PendingChoices[index]);
            UpgradePending = false;
        }

        /// <summary>玩家在三个进化分支里拍板。未处于选择状态时调用无效。</summary>
        public void ChooseEvolution(WeaponForm form)
        {
            if (!EvolutionPending)
            {
                return;
            }
            if (form == WeaponForm.Standard)
            {
                return;
            }
            Form = form;
            EnergyOrbs -= EvolutionCost;
            EvolutionPending = false;
            Sfx.LevelUp();
        }

        /// <summary>被收缩边界的灼烧带命中：受过载，但**不掉命**。
        /// 掉命是这个游戏的终极惩罚，不该和「踩了个节奏事件」同权；
        /// 而复用过载意味着零新机制、零新 UI——玩家早就认识橙条 + 射速减半这套反馈。
        /// </summary>
        public void ApplyBurn()
        {
            _overload = OverloadSeconds;
            Sfx.Hurt();
        }

        // ---------------- 时效道具 ----------------

        /// <summary>狂热剩余秒数。>0 表示射速 ×2 中。</summary>
        public float FrenzyRemaining => _frenzy;

        /// <summary>当前射速总系数（含升级、残血、狂热、过载）。
        /// 集中成一个属性而不是散在各处算，是为了让「我现在到底打多快」
        /// 在代码里只有一处真相——AI 玩家和 HUD 都要读它。</summary>
        public float TotalFireRateScale
        {
            get
            {
                float s = FireRateScale;
                if (_frenzy > 0f) s *= 2f;
                if (_overload > 0f) s *= 0.5f;
                return s;
            }
        }

        /// <summary>开启狂热：射速 ×2。持续期间飞船染金——
        /// 时效道具必须看得见，否则玩家不知道自己在超频。</summary>
        public void GrantFrenzy(float seconds)
        {
            _frenzy = Mathf.Max(_frenzy, seconds);
        }

        /// <summary>给一段无敌。直接复用无敌帧机制：
        /// 玩家已经认识「闪烁=打不到」，不需要再学一套。</summary>
        public void GrantInvuln(float seconds)
        {
            _invuln = Mathf.Max(_invuln, seconds);
        }

        public void Tick(float dt, BulletField bullets)
        {
            if (!IsAlive)
            {
                return;
            }

            if (_invuln > 0f)
            {
                _invuln = Mathf.Max(0f, _invuln - dt);
            }
            if (_overload > 0f)
            {
                _overload = Mathf.Max(0f, _overload - dt);
            }
            if (_frenzy > 0f)
            {
                _frenzy = Mathf.Max(0f, _frenzy - dt);
            }

            TickMove(dt);
            TickFire(dt, bullets);
            TickVisual();

            // 残血判定放在最后，本帧的射击仍按上一帧状态走，避免同帧来回切换
            LastStand = IsAlive && Lives <= 1;
        }

        /// <summary>孤注 Modifier 用：直接设定生命数。
        /// 不走 Heal 是因为语义不同——Heal 是奖励，这是开局规则。</summary>
        public void SetLives(int value)
        {
            Lives = Mathf.Max(1, value);
        }

        /// <summary>回复生命（连击奖励用）。返回是否真的加上去了。</summary>
        public bool Heal(int amount)
        {
            if (!IsAlive || amount <= 0 || Lives >= MaxLives)
            {
                return false;
            }
            Lives = Mathf.Min(MaxLives, Lives + amount);
            return true;
        }

        private void TickMove(float dt)
        {
            // 键盘为主：弹幕游戏靠精确微操，鼠标在窗口边界会失效
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            Vector2 input = new Vector2(h, v);
            if (ExternalInput.HasValue)
            {
                input = ExternalInput.Value;
            }
            else if (AutoPilot)
            {
                // 无焦点时读不到输入，用正弦摆动让截图里能看到飞船在动
                input = new Vector2(Mathf.Sin(Time.time * 1.35f) * 0.9f,
                    Mathf.Sin(Time.time * 0.63f) * 0.35f);
            }
            if (input.sqrMagnitude > 1f)
            {
                input.Normalize();
            }

            _velocity = input * _cfg.moveSpeed * MoveScale;
            Position += _velocity * dt;

            // 限制在可视区域内
            float halfW = _cfg.boundX;
            float minY = -_cfg.boundY + 0.6f;
            float maxY = _cfg.boundY - 0.6f;
            Position = new Vector2(
                Mathf.Clamp(Position.x, -halfW + 0.6f, halfW - 0.6f),
                Mathf.Clamp(Position.y, minY, maxY));

            transform.position = new Vector3(Position.x, Position.y, 0f);
        }

        private void TickFire(float dt, BulletField bullets)
        {
            _fireTimer -= dt;
            if (_fireTimer > 0f)
            {
                return;
            }

            // 升级修正统一在这里算，各分支只管「弹幕形状」。
            // 分开算的后果是每个分支都要重抄一遍修正公式，改一处漏三处。
            bool isCrit = UnityEngine.Random.value < CritChance;
            int damage = _cfg.bulletDamage + DamageBonus;
            if (isCrit)
            {
                damage *= 2;
            }
            float speedScale = BulletSpeedScale;
            int pierce = PierceBonus;
            int extra = ExtraBullets;

            // 机体差异：三个机体三种打法，Path Variety 的地基。
            // 碎星=慢速重炮+碎片召唤；回壁=轻伤弹但驻场反弹。
            // 差异必须是「种」而不是「量」——数值上下浮动 10% 造不出流派。
            float craftInterval = 1f;
            int bounces = 0;
            _fireBounces = 0;
            switch (Craft)
            {
                case PlayerCraft.Star:
                    craftInterval = 1.8f;
                    damage = Mathf.Max(1, Mathf.RoundToInt(damage * 2f));
                    break;
                case PlayerCraft.Wall:
                    bounces = 2;
                    _fireBounces = 2;
                    damage = Mathf.Max(1, Mathf.RoundToInt(damage * 0.8f));
                    break;
            }

            float interval;
            float speed;

            switch (Form)
            {
                case WeaponForm.TidePierce:
                    // 慢而贵：单发换一串敌人，逼玩家走到敌群中轴线上。
                    // 半径和弹道比普通弹大（见 SpawnPierceBullet），进化必须看得见。
                    interval = _cfg.fireInterval * 1.35f;
                    speed = _cfg.bulletSpeed * 1.3f * speedScale;
                    bullets.SpawnPierceBullet(Position + Vector2.up * 0.55f, Vector2.up,
                        speed, damage, BulletColor, 3 + pierce + (isCrit && SynergyHeartPierce ? 2 : 0));
                    break;

                case WeaponForm.FanBarrage:
                    // 快而散：五发大扇形、低单发伤害，奖励正面压进弹幕。
                    // 「散射」升级直接加宽扇形，而不是加伤害——同一个升级
                    // 在不同形态下应该有不同体感，否则构筑就没有个性。
                    interval = _cfg.fireInterval * 0.62f;
                    speed = _cfg.bulletSpeed * 0.92f * speedScale;
                    int fanCount = 5 + extra;
                    Vector2 fanMuzzle = Position + Vector2.up * 0.55f;
                    for (int i = 0; i < fanCount; i++)
                    {
                        float a = Mathf.Lerp(-32f, 32f, i / (float)(fanCount - 1)) * Mathf.Deg2Rad;
                        bullets.SpawnPlayerBullet(fanMuzzle,
                            new Vector2(Mathf.Sin(a), Mathf.Cos(a)), speed, damage, BulletColor);
                    }
                    break;

                case WeaponForm.EchoWave:
                    // 前后双向：补上「背后被偷袭」这个纯亏场景，
                    // 让玩家敢把注意力集中在一个方向上。
                    interval = _cfg.fireInterval * 1.05f;
                    speed = _cfg.bulletSpeed * speedScale;
                    Vector2 echoMuzzle = Position + Vector2.up * 0.55f;
                    bullets.SpawnPlayerBullet(echoMuzzle, Vector2.up, speed, damage, BulletColor);
                    bullets.SpawnPlayerBullet(echoMuzzle + Vector2.left * 0.3f,
                        new Vector2(-0.22f, 1f), speed, damage, BulletColor);
                    bullets.SpawnPlayerBullet(echoMuzzle + Vector2.right * 0.3f,
                        new Vector2(0.22f, 1f), speed, damage, BulletColor);
                    bullets.SpawnPlayerBullet(echoMuzzle, Vector2.down,
                        speed * 0.8f, damage, BulletColor);
                    bullets.SpawnPlayerBullet(echoMuzzle + Vector2.left * 0.3f,
                        new Vector2(-0.18f, -1f), speed * 0.8f, damage, BulletColor);
                    bullets.SpawnPlayerBullet(echoMuzzle + Vector2.right * 0.3f,
                        new Vector2(0.18f, -1f), speed * 0.8f, damage, BulletColor);
                    // 「散射」在双向形态下表现为再加一对更偏的侧后弹
                    for (int i = 0; i < extra; i++)
                    {
                        float sign = (i % 2 == 0) ? 1f : -1f;
                        bullets.SpawnPlayerBullet(
                            echoMuzzle + Vector2.right * sign * 0.55f,
                            new Vector2(sign * 0.45f, 1f), speed, damage, BulletColor);
                        bullets.SpawnPlayerBullet(
                            echoMuzzle + Vector2.right * sign * 0.55f,
                            new Vector2(sign * 0.4f, -1f), speed * 0.8f, damage, BulletColor);
                    }
                    break;

                default:
                    interval = _cfg.fireInterval;
                    speed = _cfg.bulletSpeed * speedScale;
                    int power = LastStand ? 3 : PowerLevel;
                    Vector2 muzzle = Position + Vector2.up * 0.55f;

                    switch (power)
                    {
                        case 1:
                            FireBullet(bullets, muzzle, Vector2.up, speed, damage);
                            break;
                        case 2:
                            FireBullet(bullets, muzzle + Vector2.left * 0.26f, Vector2.up,
                                speed, damage);
                            FireBullet(bullets, muzzle + Vector2.right * 0.26f, Vector2.up,
                                speed, damage);
                            break;
                        default:
                            FireBullet(bullets, muzzle, Vector2.up, speed, damage);
                            FireBullet(bullets, muzzle + Vector2.left * 0.3f,
                                new Vector2(-0.22f, 1f), speed, damage);
                            FireBullet(bullets, muzzle + Vector2.right * 0.3f,
                                new Vector2(0.22f, 1f), speed, damage);
                            // 「散射」升级：在最外侧继续加弹，角度与偏移同步递增
                            for (int i = 0; i < extra; i++)
                            {
                                float sign = (i % 2 == 0) ? 1f : -1f;
                                float mag = 0.42f + 0.18f * (i / 2);
                                FireBullet(bullets,
                                    muzzle + Vector2.right * sign * (0.42f + 0.14f * (i / 2)),
                                    new Vector2(sign * mag, 1f), speed, damage);
                            }
                            break;
                    }
                    break;
            }

            // 机体节奏
            interval *= craftInterval;
            // 升级：射速
            interval /= FireRateScale;
            // 狂热：射速再 ×2。与残血/过载都是乘法叠加，不会出现负间隔
            if (_frenzy > 0f)
            {
                interval *= 0.5f;
            }
            // 残血加成对进化形态同样有效——那是残血爆发的核心，不该被形态取代
            if (LastStand)
            {
                interval *= 0.8f;
                speed *= 1.25f;
            }
            // 过载：射速减半。与残血加成是乘法叠加（残血+过载 = 0.8 × 2 = 1.6 倍间隔），
            // 不会出现负间隔这种荒唐组合。
            interval *= _overload > 0f ? 2f : 1f;

            _fireTimer = interval;

            // 镜像：在左右对称位置用同样的弹幕再打一遍（伤害 ×0.6）。
            // 用递归会重复计算残血/过载修正，所以这里只复制「发射形状」本身。
            int mirrors = MirrorCount;
            if (mirrors > 0 && Form != WeaponForm.TidePierce)
            {
                Vector2 center = Position;
                for (int m = 0; m < mirrors; m++)
                {
                    float off = 2.4f + 1.6f * m;
                    Vector2 leftPos = center + Vector2.left * off;
                    Vector2 rightPos = center + Vector2.right * off;
                    // 幻影位置可能出界，先夹回舞台
                    leftPos.x = Mathf.Clamp(leftPos.x, -16.4f, 16.4f);
                    rightPos.x = Mathf.Clamp(rightPos.x, -16.4f, 16.4f);
                    float sub = speed * 0.85f;
                    int subDmg = Mathf.Max(1, Mathf.RoundToInt(damage * 0.6f));

                    if (SynergyVortexTwins)
                    {
                        // 漩涡双子：幻影也走弧线，且左右相位对称——两条辫子
                        bullets.SpawnWaveBullet(leftPos + Vector2.up * 0.55f, Vector2.up,
                            sub, subDmg, BulletColor, BulletWave * 0.8f, 6.5f);
                        bullets.SpawnWaveBullet(rightPos + Vector2.up * 0.55f, Vector2.up,
                            sub, subDmg, BulletColor, -BulletWave * 0.8f, 6.5f);
                    }
                    else
                    {
                        bullets.SpawnPlayerBullet(leftPos + Vector2.up * 0.55f, Vector2.up,
                            sub, subDmg, BulletColor);
                        bullets.SpawnPlayerBullet(rightPos + Vector2.up * 0.55f, Vector2.up,
                            sub, subDmg, BulletColor);
                    }
                }
            }
        }

        private void TickVisual()
        {
            if (_visual == null)
            {
                return;
            }
            // 无敌帧闪烁 + 轻微摆动，让飞船「活着」
            float flicker = _invuln > 0f
                ? (Mathf.Repeat(Time.time, 0.12f) < 0.06f ? 0.25f : 1f)
                : 1f;
            float tilt = Mathf.Clamp(_velocity.x / Mathf.Max(0.01f, _cfg.moveSpeed), -1f, 1f) * -14f;
            // 残血心跳脉冲：频率更高、幅度更大，让玩家感觉到「在危墙边缘」
            float pulse = LastStand ? 1f + Mathf.Sin(Time.time * 9.5f) * 0.09f : 1f;
            _visual.localScale = _visualBase * pulse;
            _visual.localRotation = Quaternion.Euler(0f, 0f, tilt);
            if (_renderer != null)
            {
                Color c = LastStand ? LastStandColor : CoreColor;
                // 过载：灯色压暗并偏橙，与残血爆发的「炽白烧起来」在观感上明确区分，
                // 玩家扫一眼就知道自己现在是残血爆发还是过载罚站。
                if (_overload > 0f)
                {
                    c = new Color(c.r * 0.7f + 0.3f, c.g * 0.55f, c.b * 0.45f, c.a);
                }
                // 狂热：染金。时效道具必须看得见——玩家花了运气开出来的，
                // 如果画面毫无变化，他会以为没生效。
                if (_frenzy > 0f)
                {
                    c = new Color(1f, 0.85f, 0.35f, c.a);
                }
                c.a = flicker;
                _renderer.color = c;
            }
        }

        /// <summary>标准玩家弹发射口。带「潮旋」升级时走弧形弹道。
        /// 收拢到一个方法是为了让曲线逻辑只有一处真相——
        /// 散在各分支里的话，改波形要改四五处。</summary>
        private void FireBullet(BulletField bullets, Vector2 pos, Vector2 dir,
            float speed, int damage)
        {
            float wave = BulletWave;
            if (wave > 0f)
            {
                bullets.SpawnWaveBullet(pos, dir, speed, damage, BulletColor, wave, 6.5f);
            }
            else
            {
                bullets.SpawnPlayerBullet(pos, dir, speed, damage, BulletColor, _fireBounces);
            }
        }

        /// <summary>本帧发射的反弹次数。TickFire 开头按机体算好，发射时引用。</summary>
        private int _fireBounces;

        /// <summary>被敌弹或敌机撞到。返回 true 表示这一下真的结算了。</summary>
        public bool TakeHit()
        {
            if (!CanBeHit)
            {
                return false;
            }
            // 护盾先挡。这是「主动选择变硬」的唯一途径，
            // 也是唯一让「往弹幕里钻」从纯吃亏变成可计算风险的升级。
            if (_shield > 0)
            {
                _shield--;
                _invuln = _cfg.hitInvuln;
                Sfx.Hit(true);
                ShieldBroken?.Invoke();
                return false;
            }
            Lives--;
            _invuln = _cfg.hitInvuln;
            // 不再掉武器等级。
            // 掉级是「永久能力」惩罚，实测后果是玩家变得保守、不敢往弹幕密集区钻——
            // 而弹幕游戏的乐趣恰恰在密集区。这与设计目标直接冲突。
            // 改为 3 秒过载（射速减半）：惩罚一个反应窗口，不打断成长曲线。
            _overload = OverloadSeconds;
            Damaged?.Invoke();
            // 逆潮：受伤的瞬间肃清身边敌弹。
            // 给「被打」一个反击的出口——否则受伤的体验只有纯粹的损失，
            // 而纯粹的损失只会教玩家更怕死、更保守。
            if (CounterTide > 0)
            {
                CounterTideTriggered?.Invoke();
            }
            if (Lives <= 0)
            {
                IsAlive = false;
                Died?.Invoke();
            }
            return true;
        }

        /// <summary>
        /// 武器等级 +1（封顶 3）。返回是否真的升上去了——
        /// 调用方要据此决定是否播反馈，不能凭猜。
        ///
        /// 这个方法的调用点曾经整段漏掉，导致 PowerLevel 永远停在 startPower，
        /// 满级（进而武器进化）在一局游戏里根本不可能达成。
        /// 那类「机制写好了但没接线」的 bug 在录屏上看不出来——
        /// 玩家只会觉得「说好的成长呢」，而归因不到具体哪里。
        /// </summary>
        public bool AddPower()
        {
            if (PowerLevel >= 3)
            {
                return false;
            }
            PowerLevel++;
            Sfx.LevelUp();
            return true;
        }

        public void ResetForNewRun()
        {
            Lives = _cfg.startLives;
            PowerLevel = _cfg.startPower;
            IsAlive = true;
            _invuln = _cfg.spawnInvuln;
            _overload = 0f;
            _fireTimer = 0f;
            // 新一局从零开始攒球。进化是局内成长，不跨局保留——
            // 否则第一局进化完之后，后面每局都直接跳过最有意思的那段。
            Form = WeaponForm.Standard;
            EnergyOrbs = 0;
            EvolutionPending = false;
            UpgradePending = false;
            _shield = 0;
            _maxLivesBonus = 0;
            _frenzy = 0f;
            for (int i = 0; i < UpgradePool.Count; i++)
            {
                _upgrades[i] = 0;
            }
            Position = new Vector2(0f, _cfg.startY);
            transform.position = new Vector3(Position.x, Position.y, 0f);
        }
    }

    /// <summary>玩家参数。集中一处便于调手感。</summary>
    [Serializable]
    public sealed class PlayerStatsConfig
    {
        public int startLives = 3;
        /// <summary>开局武器等级。给 2 是为了让玩家一上手就有「双发」的手感，
        /// 1 级单发在弹幕密度下太无力（AI 测试反馈「开局即地狱难度」）。</summary>
        public int startPower = 2;
        public float moveSpeed = 9.5f;
        public float fireInterval = 0.055f;
        public float bulletSpeed = 26f;
        public int bulletDamage = 12;
        public float hitInvuln = 1.6f;
        public float spawnInvuln = 1.2f;
        public float shipSize = 1.15f;
        public float startY = -9f;
        public float boundX = 16.8f;
        public float boundY = 9.0f;
    }
}
