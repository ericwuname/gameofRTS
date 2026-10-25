using System.Collections.Generic;
using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 破潮海图：节点 + 连线 + 经济。设计依据 @trace MAP-001 ECON-001 UNIT-001
    /// MVP 简化：单级供应链（矿直接产潮力，精炼为增益建筑），自由画线 LATER。
    /// </summary>
    public class NodeMap
    {
        public enum NodeType { Home, Mine, Refinery, FactoryNode, Sentry, Bastion }

        public class Node
        {
            public int Id;
            public NodeType Type;
            public Vector2 Pos;
            public bool Owned;                       // 我方占领
            public readonly List<int> Links = new(); // 相邻节点
            public readonly List<EnemyShip> Garrison = new(); // 驻军
            public int GarrisonQuota;                // 应有驻军数（增援人格补齐依据）@trace DEF-001
            public bool Ambushed;                    // 伏击节点：HUD 不显示真实驻军数
            public float BastionHp;                  // 仅 Bastion 用

            public string Name => Type switch
            {
                NodeType.Home => "母港",
                NodeType.Mine => "矿脉",
                NodeType.Refinery => "精炼所",
                NodeType.FactoryNode => "兵工厂",
                NodeType.Sentry => "哨线",
                _ => "要塞",
            };
        }

        public readonly List<Node> Nodes = new();
        public int BastionId = -1;

        private System.Random _rng;
        public readonly string Seed;

        public NodeMap(string seed)
        {
            // @trace MAP-002 同 seed 同图：seed 派生 RNG，生成序列确定性
            Seed = seed;
            _rng = new System.Random(SeedHash(seed));
            Build();
        }

        private static int SeedHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s) { h = h * 31 + c; }
                return h;
            }
        }

        public static string RollSeed()
        {
            return "TB-" + System.DateTime.Now.ToString("yyyyMMdd") + "-" +
                   UnityEngine.Random.Range(0, 0xFFFF).ToString("X4");
        }

        /// <summary>
        /// 双线拓扑 v2 @trace MAP-001：14 节点（反馈「地图太小布局感不足」）。
        /// 南北双通道 + 中路枢纽，多路取舍空间比单线 v1 大一倍。
        /// </summary>
        private void Build()
        {
            var template = new (NodeType t, float x, float y, bool rich)[]
            {
                (NodeType.Home,       -16f, -7.0f, false),  // 0
                (NodeType.Mine,       -11f, -4.0f, false),  // 1
                (NodeType.Sentry,      -6f, -1.5f, false),  // 2
                (NodeType.Mine,        -2f, -5.5f, false),  // 3
                (NodeType.FactoryNode,  0f,  0.5f, false),  // 4 中路枢纽
                (NodeType.Sentry,       4f, -2.5f, false),  // 5
                (NodeType.Mine,         8f, -5.5f, true ),  // 6 富矿
                (NodeType.Sentry,       8f,  2.5f, false),  // 7
                (NodeType.Mine,         2f,  5.5f, false),  // 8
                (NodeType.Sentry,      12f,  4.0f, false),  // 9
                (NodeType.Bastion,     16f,  6.5f, false),  // 10
                (NodeType.Mine,        -8f,  4.5f, false),  // 11
                (NodeType.Sentry,      -3f,  2.5f, false),  // 12
                (NodeType.Mine,        13f,  0.5f, true ),  // 13 富矿（要塞前哨）
            };
            // 抖动：同 seed 结果确定；幅度小保证不破坏可读拓扑
            for (int i = 0; i < template.Length; i++)
            {
                var t = template[i];
                float jx = (float)(_rng.NextDouble() - 0.5) * 1.0f;
                float jy = (float)(_rng.NextDouble() - 0.5) * 1.0f;
                var n = new Node { Id = i, Type = t.t, Pos = new Vector2(t.x + jx, t.y + jy) };
                if (t.t == NodeType.Bastion) { BastionId = i; n.BastionHp = 260f; }
                Nodes.Add(n);
            }
            int[][] linkPairs =
            {
                // 南线
                new[]{0,1}, new[]{1,2}, new[]{2,4}, new[]{3,4}, new[]{0,3},
                new[]{4,5}, new[]{5,6}, new[]{5,9}, new[]{6,9}, new[]{9,10},
                // 北线
                new[]{1,11}, new[]{11,12}, new[]{12,4}, new[]{12,8}, new[]{8,7},
                new[]{7,9}, new[]{7,10}, new[]{6,13}, new[]{13,10},
            };
            foreach (var p in linkPairs)
            {
                Nodes[p[0]].Links.Add(p[1]);
                Nodes[p[1]].Links.Add(p[0]);
            }
            // 驻军配额：富矿/要塞重，母港无
            foreach (var n in Nodes)
            {
                n.GarrisonQuota = n.Type switch
                {
                    NodeType.Mine => n.Pos.x > 5f ? 3 : 2,
                    NodeType.Sentry => 2,
                    NodeType.Refinery => 2,
                    NodeType.FactoryNode => 2,
                    NodeType.Bastion => 4,
                    _ => 0,
                };
            }
        }

        public Node Get(int id) => Nodes[id];

        /// <summary>补给半径内最近的我方节点（气紧判定 @trace UNIT-001）。</summary>
        public bool NearFriendly(Vector2 pos, float r)
        {
            foreach (var n in Nodes)
            {
                if (n.Owned && (n.Pos - pos).sqrMagnitude <= r * r) { return true; }
            }
            return false;
        }

        /// <summary>波末收入 @trace ECON-001。</summary>
        public int WaveIncome()
        {
            int income = 0;
            foreach (var n in Nodes)
            {
                if (!n.Owned) { continue; }
                if (n.Type == NodeType.Mine) { income += n.Pos.x > 5f ? 12 : 8; }
                else if (n.Type == NodeType.Refinery) { income += 6; }
            }
            return income;
        }
    }
}
