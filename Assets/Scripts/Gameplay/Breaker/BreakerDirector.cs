using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    public enum BreakerState { Layout, Wave, Over }

    /// <summary>
    /// 破潮主导演：双层循环 @trace LOOP-001 LOOP-002，经济结算 @trace ECON-001 ECON-002，
    /// 指挥官人格 @trace DEF-001，埋点 @trace DATA-001。
    /// </summary>
    public class BreakerDirector : MonoBehaviour
    {
        public BreakerState State { get; private set; } = BreakerState.Layout;
        public NodeMap Map { get; private set; }
        public int Tide { get; private set; }             // 潮力 @trace ECON-001
        public int Wave { get; private set; }
        public float WaveTimer { get; private set; }
        public float WaveLength { get; private set; } = 24f;
        public bool Victory { get; private set; }
        public string EndReason { get; private set; } = "";
        public string CommanderName { get; private set; } = "汐庭·无主";
        public string CommanderRule { get; private set; } = "无特别调度";
        public int NegativeStreak { get; private set; }   // 连续收支为负 @trace ECON-002
        public int LastIncome { get; private set; }
        public int LastUpkeep { get; private set; }
        public int SelectedNode { get; private set; } = 0; // 舰队目的地
        public bool Escorting { get; set; } = true;         // 旗舰护航（AI 玩/新手开，玩家按键即接管）
        public bool Building => _buildQueue.Count > 0;      // @trace UNIT-001 生产队列
        public float BuildProgress => Building ? 1f - _buildTimer / BuildTime : 0f;

        public PlayerShip Player;
        public BulletField Bullets;
        public EnemyManager Enemies;
        public FxLayer Fx;

        public const int MaxFleet = 6;                    // @trace UNIT-001
        public const float BuildTime = 3.0f;              // 生产节拍（红警式心跳，非瞬发）
        public readonly List<Fleet> FleetUnits = new();
        private readonly List<Fleet> _dead = new();
        private readonly Queue<Fleet.Kind> _buildQueue = new();
        private float _buildTimer;
        private readonly List<Harvester> _harvesters = new();  // @trace ECON-001 矿车经济
        private readonly Dictionary<EnemyShip, NodeMap.Node> _garrisonIndex = new();
        private bool _reinforce;                          // 增援人格 @trace DEF-001
        private int _lastNet;
        private string _logPath;
        private float _startTime;

        private static readonly Color FleetColor = new Color32(0x7F, 0xF0, 0xFF, 0xFF);

        public void Initialize(Camera cam, PlayerShip player, BulletField bullets,
            EnemyManager enemies, FxLayer fx, Material mat)
        {
            Player = player;
            Bullets = bullets;
            Enemies = enemies;
            Fx = fx;

            Map = new NodeMap(NodeMap.RollSeed());
            Tide = 60;                                    // @trace ECON-001
            Wave = 0;
            _startTime = Time.time;
            _logPath = Path.Combine(Application.persistentDataPath, "breaker_telemetry.jsonl");

            // 指挥官人格抽取 @trace DEF-001
            int roll = UnityEngine.Random.Range(0, 10);
            if (roll < 4) { _reinforce = true; CommanderName = "汐庭·铁浪"; CommanderRule = "增援：每波未占领节点补充 1 名驻军"; }
            else if (roll < 7)
            {
                CommanderName = "汐庭·暗礁"; CommanderRule = "伏击：某节点藏有额外驻军";
                var cand = Map.Nodes.FindAll(n => n.GarrisonQuota > 0 && n.Type != NodeMap.NodeType.Bastion);
                var amb = cand[UnityEngine.Random.Range(0, cand.Count)];
                amb.Ambushed = true;
                amb.GarrisonQuota += 2;
            }

            Player.Died += OnPlayerDied;
            Bullets.PlayerHit += OnPlayerHit;
            Bullets.EnemyHit += OnEnemyHit;
            SpawnWaveGarrison(true);

            LogEvent("run_start", $"seed={Map.Seed},cmd={CommanderName}");
            Sfx.WaveStart();
        }

        private void OnDestroy()
        {
            if (Player != null) { Player.Died -= OnPlayerDied; }
            if (Bullets != null) { Bullets.PlayerHit -= OnPlayerHit; Bullets.EnemyHit -= OnEnemyHit; }
            LogEvent("run_end", $"result={(Victory ? "win" : "lose")},wave={Wave},reason={EndReason},t={(Time.time - _startTime):F1}");
        }

        // ---------- 主循环 ----------

        private void Update()
        {
            if (State == BreakerState.Over) { return; }

            if (State == BreakerState.Layout)
            {
                // 波间：战术层冻结，但守军弹幕继续飘（压迫感）；旗舰不可操作
                Bullets.Tick(Time.deltaTime * 0.35f);
                Fx.Tick(Time.deltaTime);
                if (Input.GetKeyDown(KeyCode.Space)) { StartWave(); }
                return;
            }

            // Wave：全速运行 @trace LOOP-001
            float dt = Time.deltaTime;
            WaveTimer -= dt;
            TickEscort(dt);
            Player.Tick(dt, Bullets);
            Bullets.Tick(dt);
            Enemies.Tick(dt);
            TickFleets(dt);
            TickHarvesters(dt);
            TickBuildQueue(dt);
            Fx.Tick(dt);

            if (WaveTimer <= 0f) { EndWave(); }
        }

        /// <summary>生产队列 @trace UNIT-001：红警式心跳，扣款即时、舰体到时下水。</summary>
        private void TickBuildQueue(float dt)
        {
            if (!Building) { return; }
            _buildTimer -= dt;
            if (_buildTimer > 0f) { return; }
            var kind = _buildQueue.Dequeue();
            var home = Map.Nodes[0];
            FleetUnits.Add(Fleet.Spawn(kind,
                home.Pos + Vector2.right * UnityEngine.Random.Range(-0.5f, 0.5f),
                transform, SpriteFactory.Triangle(72, FleetColor, Color.white),
                SpriteFactory.Glow(96, FleetColor)));
            Sfx.LevelUp();
            FloatText.Push(home.Pos + Vector2.up * 1.2f, "舰 体 就 绪",
                new Color32(0xB4, 0xFF, 0xE8, 0xFF), true);
        }

        /// <summary>矿车节拍 @trace ECON-001：经济从结算数字变成看得见的流动。</summary>
        private void TickHarvesters(float dt)
        {
            // 占领矿脉 → 自动出车；失守/升级精炼 → 旧车退役
            foreach (var n in Map.Nodes)
            {
                if (n.Type != NodeMap.NodeType.Mine && n.Type != NodeMap.NodeType.Refinery)
                { continue; }
                bool has = _harvesters.Exists(h => h.Alive && h.SourceId == n.Id);
                if (n.Owned && !has)
                {
                    var h = new Harvester(n.Pos, Map.Nodes[0].Pos, transform,
                        SpriteFactory.Diamond(48, new Color32(0xFF, 0xD2, 0x4D, 0xFF), Color.white),
                        SpriteFactory.Glow(64, new Color32(0xFF, 0xD2, 0x4D, 0xFF)));
                    h.SourceId = n.Id;
                    _harvesters.Add(h);
                    FloatText.Push(n.Pos + Vector2.up * 0.6f, "矿 车 出 动",
                        new Color32(0xFF, 0xD2, 0x4D, 0xFF), false);
                }
            }
            for (int i = _harvesters.Count - 1; i >= 0; i--)
            {
                var h = _harvesters[i];
                var src = Map.Nodes[h.SourceId];
                if (!src.Owned || src.Type == NodeMap.NodeType.Sentry) { h.Destroy(); _harvesters.RemoveAt(i); continue; }
                int haul = h.Tick(dt) * (src.Type == NodeMap.NodeType.Refinery ? 2 : 1);
                if (haul > 0)
                {
                    Tide += haul;
                    FloatText.Push(Map.Nodes[0].Pos + Vector2.up * 0.8f, "+" + haul,
                        new Color32(0xFF, 0xD2, 0x4D, 0xFF), false);
                }
            }
        }

        /// <summary>旗舰护航：跟随舰队重心 + 基础避弹。玩家按任意移动键即接管。</summary>
        private void TickEscort(float dt)
        {
            if (!Escorting || !Player.IsAlive) { return; }
            if (Input.GetAxisRaw("Horizontal") != 0f || Input.GetAxisRaw("Vertical") != 0f)
            { Escorting = false; Player.ExternalInput = null; return; }

            Vector2 anchor = Map.Nodes[SelectedNode].Owned || LivingFleet() == 0
                ? Map.Nodes[SelectedNode].Pos
                : FleetCenter();
            Vector2 to = anchor - Player.Position;
            Vector2 drive = to.sqrMagnitude > 1.2f ? to.normalized : Vector2.zero;

            // 基础避弹 AI：最近敌弹 <2.5 单位时垂直逃离（权重压过跟随）。
            // 没有它，护航旗舰站桩吃扇形弹 12 秒内必沉——Agnes 首局已验证。
            Vector2? threat = Bullets.NearestEnemyBullet(Player.Position, 2.5f);
            if (threat.HasValue)
            {
                Vector2 away = Player.Position - threat.Value;
                drive = (drive * 0.35f + away.normalized * 1.2f).normalized;
            }
            Player.ExternalInput = drive;
        }

        private Vector2 FleetCenter()
        {
            Vector2 c = Vector2.zero; int k = 0;
            foreach (var f in FleetUnits)
            {
                if (!f.Alive) { continue; }
                c += f.Pos; k++;
            }
            return k > 0 ? c / k : Player.Position;
        }

        private void StartWave()
        {
            Wave++;
            // 首波短而弱：红警调研结论「时间-快乐转换效率」——
            // 玩家要在 2 分钟内尝到第一次推平哨线的甜头
            WaveTimer = Wave <= 1 ? 16f : WaveLength;
            State = BreakerState.Wave;

            // 增援人格 @trace DEF-001
            if (_reinforce)
            {
                foreach (var n in Map.Nodes)
                {
                    if (!n.Owned && n.Type != NodeMap.NodeType.Home &&
                        n.Garrison.Count < n.GarrisonQuota)
                    {
                        n.GarrisonQuota = Mathf.Min(n.GarrisonQuota, n.Garrison.Count + 1);
                    }
                }
            }
            SpawnWaveGarrison(false);
            Fx?.Shake(0.2f, 0.15f);
            Sfx.WaveStart();
        }

        private void EndWave()
        {
            // 波末结算 @trace ECON-001
            int income = Map.WaveIncome();
            int upkeep = 2;
            foreach (var f in FleetUnits)
            {
                if (f.Alive) { upkeep += f.Upkeep; }
            }
            upkeep += CountOwnedLinks() / 2;
            Tide += income - upkeep;
            LastIncome = income;
            LastUpkeep = upkeep;
            _lastNet = income - upkeep;

            // 破产判定 @trace ECON-002
            if (Tide < 0) { NegativeStreak++; } else { NegativeStreak = 0; }
            if (NegativeStreak >= 2)
            {
                LogEvent("bankruptcy", $"gap={-Tide},upkeep={upkeep},income={income},fleet={LivingFleet()}");
                GameOver(false, $"破 产（第 {Wave} 波）\n收入 {income} − 维护 {upkeep} = {_lastNet:+#;-#;0}\n" +
                    (LivingFleet() > 3 ? "主因：舰队过大，维护费失控" : "主因：收入线被截断"));
                return;
            }
            State = BreakerState.Layout;
            FloatText.Push(Player.Position, $"结算 {income - upkeep:+#;-#;0}",
                _lastNet >= 0 ? new Color32(0xB4, 0xFF, 0xE8, 0xFF) : new Color32(0xFF, 0x8A, 0x5C, 0xFF), true);
        }

        private int CountOwnedLinks()
        {
            int links = 0;
            foreach (var n in Map.Nodes)
            {
                if (!n.Owned) { continue; }
                foreach (int l in n.Links)
                {
                    if (Map.Nodes[l].Owned && l > n.Id) { links++; }
                }
            }
            return links;
        }

        public int LivingFleet()
        {
            int c = 0;
            foreach (var f in FleetUnits) { if (f.Alive) { c++; } }
            return c;
        }

        // ---------- 驻军 ----------

        private void SpawnWaveGarrison(bool initial)
        {
            foreach (var n in Map.Nodes)
            {
                if (n.Type == NodeMap.NodeType.Home || n.Owned) { continue; }
                while (n.Garrison.Count < n.GarrisonQuota)
                {
                    // 环弹塔只给要塞区（全屏弹幕太密），其余哨戒塔 @trace UNIT-002
                    EnemyKind kind = (n.Type == NodeMap.NodeType.Bastion && n.Garrison.Count == 0)
                        ? EnemyKind.Spinner : EnemyKind.Shooter;
                    Vector2 off = new Vector2(
                        UnityEngine.Random.Range(-1.6f, 1.6f), UnityEngine.Random.Range(-1.2f, 1.2f));
                    // 首波全线 -1：开局正反馈提速，别让玩家第一波就吃满配驻军
                    int quota = n.GarrisonQuota - (Wave <= 1 && n.Type != NodeMap.NodeType.Bastion ? 1 : 0);
                    if (n.Garrison.Count >= quota) { break; }
                    EnemyShip e = Enemies.Spawn(kind, n.Pos + off, Mathf.Max(1, Wave), 0.8f);
                    if (e == null) { break; }
                    e.HoldStation = true;             // @trace UNIT-002 静止化守军
                    e.Died += OnGarrisonDied;
                    n.Garrison.Add(e);
                    _garrisonIndex[e] = n;
                    if (initial && n.Type == NodeMap.NodeType.Bastion)
                    {
                        n.BastionHp = 260f;
                    }
                }
                if (n.Type == NodeMap.NodeType.Bastion && n.Garrison.Count > 0)
                {
                    n.BastionHp = n.Garrison[0].Hp + 220f; // 要塞血量挂主塔
                }
            }
        }

        private void OnGarrisonDied(EnemyShip e)
        {
            if (!_garrisonIndex.TryGetValue(e, out var n)) { return; }
            _garrisonIndex.Remove(e);
            n.Garrison.Remove(e);
            Tide += 2;                                // 击杀收割 @trace ECON-001

            // 节点占领：驻军清空且舰队在场 @trace LOOP-001
            if (!n.Owned && n.Garrison.Count == 0 && n.Type != NodeMap.NodeType.Home &&
                FleetAt(n))
            {
                Capture(n);
            }
            // 要塞主塔死亡 = 胜利 @trace LOOP-002
            if (n.Type == NodeMap.NodeType.Bastion && n.Garrison.Count == 0)
            {
                GameOver(true, "潮 汐 平 息\n要 塞 陷 落");
            }
        }

        private bool FleetAt(NodeMap.Node n)
        {
            foreach (var f in FleetUnits)
            {
                if (f.Alive && (f.Pos - n.Pos).sqrMagnitude < 4f) { return true; }
            }
            return false;
        }

        private void Capture(NodeMap.Node n)
        {
            n.Owned = true;
            if (n.Type == NodeMap.NodeType.Bastion) { return; }
            Tide += 25;                               // 占领掠夺 @trace ECON-001
            Fx?.SpawnPulse(n.Pos, new Color32(0x7F, 0xF0, 0xFF, 0xFF));
            FloatText.Push(n.Pos + Vector2.up * 0.8f, n.Name + " 已占领",
                new Color32(0xB4, 0xFF, 0xE8, 0xFF), true);
            Sfx.LevelUp();
            LogEvent("node_capture", $"node={n.Id},type={n.Type},wave={Wave}");
        }

        // ---------- 舰队 ----------

        public bool BuildFleet(Fleet.Kind kind)
        {
            // @trace UNIT-001 造价与上限；生产改为队列（红警式心跳）
            if (State != BreakerState.Layout || LivingFleet() + _buildQueue.Count >= MaxFleet)
            { return false; }
            var probe = new Fleet { Type = kind };
            int cost = probe.Cost;
            if (Tide < cost) { return false; }
            Tide -= cost;
            _buildQueue.Enqueue(kind);
            if (!Building) { _buildTimer = BuildTime; }
            Sfx.Pickup();
            return true;
        }

        /// <summary>AI 指挥官提前开波（等价于按空格）。</summary>
        public void ForceStartWave()
        {
            if (State == BreakerState.Layout) { StartWave(); }
        }

        private void TickFleets(float dt)
        {
            _dead.Clear();
            var dest = Map.Get(SelectedNode);
            foreach (var f in FleetUnits)
            {
                if (!f.Alive) { _dead.Add(f); continue; }
                bool moving = !dest.Owned && State == BreakerState.Wave;
                f.Tick(dt, dest.Pos, moving, Bullets, FleetColor);

                // 气紧：脱离我方补给圈掉血 @trace UNIT-001
                bool nearFriendly = Map.NearFriendly(f.Pos, 3.2f);
                bool inHostile = !dest.Owned && (f.Pos - dest.Pos).sqrMagnitude < 9f;
                // 交战损耗（MVP 简化披露见冻结清单）
                float dps = inHostile ? 2.2f : (nearFriendly ? 0f : 0.8f);
                // 工程舰自修
                float repair = f.Type == Fleet.Kind.Engineer && !inHostile ? 1.5f : 0f;
                if (!f.ApplyAttrition(dps, dt, repair))
                {
                    FloatText.Push(f.Pos, "舰 沉", new Color32(0xFF, 0x4D, 0x5E, 0xFF), true);
                }
            }
            foreach (var d in _dead) { FleetUnits.Remove(d); }
        }

        /// <summary>点选节点为舰队目的地（Anomaly 节点式）。</summary>
        public void TrySetDestination(int nodeId)
        {
            var n = Map.Get(nodeId);
            if (n.Owned) { SelectedNode = nodeId; return; }
            // 目的地必须与我方控制区相邻（沿连线推进）
            foreach (int l in n.Links)
            {
                if (Map.Nodes[l].Owned) { SelectedNode = nodeId; return; }
            }
        }

        /// <summary>节点当前是否可作为舰队目的地（可达性标记 @trace UI-001，QA 条件 1）。</summary>
        public bool CanAttack(NodeMap.Node n)
        {
            if (n.Owned) { return false; }
            foreach (int l in n.Links)
            {
                if (Map.Nodes[l].Owned) { return true; }
            }
            return false;
        }

        /// <summary>波间在已占领节点建精炼所（经济投资）。</summary>
        public bool BuildRefinery(int nodeId)
        {
            var n = Map.Get(nodeId);
            if (State != BreakerState.Layout || !n.Owned || n.Type != NodeMap.NodeType.Mine ||
                n.Type == NodeMap.NodeType.Refinery || Tide < 30)
            {
                return false;
            }
            Tide -= 30;
            n.Type = NodeMap.NodeType.Refinery;       // 矿脉升级为精炼所
            FloatText.Push(n.Pos, "精 炼 所", new Color32(0xFF, 0xD2, 0x4D, 0xFF), true);
            return true;
        }

        // ---------- 战斗事件 ----------

        private void OnEnemyHit(EnemyShip e, Vector2 at)
        {
            Fx?.Spark(at, Vector2.up, new Color32(0xFF, 0xE9, 0xA8, 0xFF), false);
        }

        private void OnPlayerHit(Vector2 at)
        {
            Player.TakeHit();
            Fx?.Shake(0.3f, 0.4f);
            Fx?.SpawnPulse(at, new Color32(0xFF, 0x4D, 0x5E, 0xFF));
        }

        private void OnPlayerDied()
        {
            GameOver(false, "旗 舰 沉 没\n（第 " + Wave + " 波）");
        }

        private void GameOver(bool win, string reason)
        {
            if (State == BreakerState.Over) { return; }
            Victory = win;
            EndReason = reason;
            State = BreakerState.Over;
            Fx?.Shake(0.8f, 0.8f);
            Sfx.LevelUp();
            if (win) { LogEvent("fort_breach", $"wave={Wave},t={(Time.time - _startTime):F1}"); }
        }

        // ---------- 埋点 @trace DATA-001 ----------

        private void LogEvent(string evt, string detail)
        {
            try
            {
                File.AppendAllText(_logPath,
                    $"{{\"t\":\"{System.DateTime.Now:HH:mm:ss}\",\"evt\":\"{evt}\",\"{detail}\"}}\n");
            }
            catch { /* 埋点失败不阻塞玩法，但按规范此处为显式静默：IO 异常不影响核心循环 */ }
        }
    }
}
