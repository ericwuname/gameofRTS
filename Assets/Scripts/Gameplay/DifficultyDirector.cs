using UnityEngine;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 自适应难度（DDA）。
    ///
    /// 为什么需要它：原来刷怪节奏是写死的（`0.5 - wave*0.045` 秒一次），
    /// 结果第 2 波的扇形弹敌人对新玩家来说直接劝退，对熟练玩家又不够看。
    /// 硬编码的曲线没法同时满足两种人。
    ///
    /// 做法：持续评估「玩家当前表现」，换算成一个 pressure 系数（0.65~1.55），
    /// 所有生成参数都乘它。打得顺就加压，快死了就减压。
    ///
    /// 三条硬约束（这些比算法本身重要）：
    /// 1. **开局宽限**：前 8 秒强制低压，让玩家先爽一下、学会操作。
    /// 2. **最后一命保护**：剩 1 条命时压力上限降到 1.05，不落井下石。
    /// 3. **变化要慢**：pressure 每秒最多变 0.3，避免难度跳变被察觉。
    /// </summary>
    public sealed class DifficultyDirector
    {
        public const float MinPressure = 0.65f;
        public const float MaxPressure = 1.55f;

        private const float GracePeriod = 8f;
        // 评估窗口从 4 秒放宽到 8 秒。
        // 4 秒太短：击杀数波动大，算出来的 pressure 像过山车一样抖，
        // 玩家能直接察觉「难度在变」，公平感受到损。
        // 8 秒才能反映真实水平。受伤仍有 NoteHurt 的即时减压兜底，
        // 所以窗口变长不会让「快死了还加压」。
        private const float WindowSeconds = 8f;
        private const float AdaptSpeed = 0.3f;
        private const float LastLifeCap = 1.05f;

        /// <summary>当前压力系数。1.0 为基准。</summary>
        public float Pressure { get; private set; } = MinPressure;

        /// <summary>最近一个窗口内的表现评分（0=很差，1=很好）。用于调试与外部观察。</summary>
        public float Performance { get; private set; }

        public float TargetPressure { get; private set; } = MinPressure;

        /// <summary>是否处于开局宽限期。</summary>
        public bool InGrace => _time < GracePeriod;

        private float _time;
        private float _windowTimer;
        private int _killsInWindow;
        private int _hurtsInWindow;

        /// <summary>上一窗口的统计，用于外部读取。</summary>
        public int LastWindowKills { get; private set; }
        public int LastWindowHurts { get; private set; }

        public void Reset()
        {
            _time = 0f;
            _windowTimer = 0f;
            _killsInWindow = 0;
            _hurtsInWindow = 0;
            LastWindowKills = 0;
            LastWindowHurts = 0;
            Pressure = MinPressure;
            TargetPressure = MinPressure;
            Performance = 0f;
        }

        public void NoteKill()
        {
            _killsInWindow++;
        }

        public void NoteHurt()
        {
            _hurtsInWindow++;

            // 受伤要**立刻**减压，不能等下一个 8 秒评估窗口。
            // 否则玩家感受到的是「我越受伤，敌人越凶」，那是最糟的体验。
            // 这里给一个即时下探，之后再由窗口评估慢慢调整回去。
            Pressure = Mathf.Max(MinPressure, Pressure - 0.18f);
            TargetPressure = Mathf.Min(TargetPressure, Pressure);
        }

        /// <summary>记一次受伤但**不**触发即时减压。
        /// 专供残血状态：残血爆发的意义是绝地反击，
        /// 如果 DDA 同时减压，玩家感受到的是「游戏在照顾我」而不是「我在殊死一搏」。
        /// 窗口统计仍然记录（长期趋势不变），只是拿掉那一下即时下探。</summary>
        public void NoteHurtNoRelief()
        {
            _hurtsInWindow++;
        }

        public void Tick(float dt, int lives, int maxLives)
        {
            if (dt <= 0f)
            {
                return;
            }
            _time += dt;
            _windowTimer += dt;

            if (_windowTimer >= WindowSeconds)
            {
                EvaluateWindow(lives, maxLives);
                LastWindowKills = _killsInWindow;
                LastWindowHurts = _hurtsInWindow;
                _killsInWindow = 0;
                _hurtsInWindow = 0;
                _windowTimer = 0f;
            }

            // 平滑逼近目标，避免难度跳变
            Pressure = Mathf.MoveTowards(Pressure, TargetPressure, AdaptSpeed * dt);
        }

        private void EvaluateWindow(int lives, int maxLives)
        {
            float health = maxLives <= 0 ? 1f : Mathf.Clamp01(lives / (float)maxLives);

            // 每秒击杀，2.5/s 视为满负荷（速率口径，与窗口长度无关）
            float killsPerSec = _killsInWindow / WindowSeconds;
            float killScore = Mathf.Clamp01(killsPerSec / 2.5f);

            // 一个窗口（8 秒）内受伤 3 次视为吃紧
            float hurtScore = Mathf.Clamp01(_hurtsInWindow / 3f);

            Performance = Mathf.Clamp01(health * 0.45f + killScore * 0.40f + (1f - hurtScore) * 0.15f);

            float target = Mathf.Lerp(MinPressure, MaxPressure, Performance);

            // 约束 1：开局宽限
            if (_time < GracePeriod)
            {
                target = MinPressure + 0.1f;
            }

            // 约束 2：最后一命不落井下石
            if (lives <= 1)
            {
                target = Mathf.Min(target, LastLifeCap);
            }

            TargetPressure = target;
        }
    }
}
