using System.Collections.Generic;
using UnityEngine;

namespace TideShot.Gameplay
{
    /// <summary>单个升级的定义。</summary>
    public struct UpgradeDef
    {
        public int Id;
        public string Name;
        public string Desc;

        /// <summary>最多叠几层。满层之后不再出现在选项里——
        /// 给玩家一个点了没用的选择，是很劣质的体验。</summary>
        public int MaxStacks;
    }

    /// <summary>
    /// 升级池：数值型强化，可叠加，每次三选一从池里**随机**摇三个。
    ///
    /// 为什么必须是随机抽而不是固定列表：固定的三选一在第二次就没有悬念了。
    /// 「不知道这次会遇到什么组合」正是老虎机让人上瘾的核心——可变比率强化
    /// （Variable Ratio Reinforcement），行为心理学里成瘾性最强的奖励时间表。
    /// 固定奖励是固定比率，随机奖励才是可变比率。
    ///
    /// 12 个升级抽 3 个，理论组合数 C(12,3)=220，再叠加各自层数，实际分支远多于此。
    /// 这是「每局 Build 不同」的来源，也是重玩动力。
    /// </summary>
    public static class UpgradePool
    {
        public static readonly UpgradeDef[] All =
        {
            new UpgradeDef { Id = 0,  Name = "急速", Desc = "射速 +12%",          MaxStacks = 5 },
            new UpgradeDef { Id = 1,  Name = "重弹", Desc = "子弹伤害 +3",        MaxStacks = 5 },
            new UpgradeDef { Id = 2,  Name = "锋锐", Desc = "弹速 +10%",          MaxStacks = 3 },
            new UpgradeDef { Id = 3,  Name = "贯穿", Desc = "子弹穿透 +1",        MaxStacks = 3 },
            new UpgradeDef { Id = 4,  Name = "散射", Desc = "额外弹 +1",          MaxStacks = 3 },
            new UpgradeDef { Id = 5,  Name = "引力", Desc = "磁吸范围 +25%",      MaxStacks = 3 },
            new UpgradeDef { Id = 6,  Name = "轻身", Desc = "移动速度 +8%",       MaxStacks = 3 },
            new UpgradeDef { Id = 7,  Name = "护盾", Desc = "抵挡 1 次伤害",      MaxStacks = 2 },
            new UpgradeDef { Id = 8,  Name = "生机", Desc = "生命上限 +1 并回满", MaxStacks = 1 },
            new UpgradeDef { Id = 9,  Name = "会心", Desc = "暴击率 +10%",        MaxStacks = 3 },
            new UpgradeDef { Id = 10, Name = "余韵", Desc = "连击窗口 +0.4 秒",   MaxStacks = 2 },
            new UpgradeDef { Id = 11, Name = "财运", Desc = "能量球掉落 +8%",     MaxStacks = 3 },

            // ---- 机制型：改变的是「怎么打」，不是「打多痛」----
            // 数值升级是加法（多 12% 射速），机制升级是乘法（多一种战术选择）。
            // 用户的原话是「技能就是加子弹加速度，没有一点创意」——
            // 这四个就是对着这句话写的。

            new UpgradeDef { Id = 12, Name = "潮旋", Desc = "子弹走弧形弹道",     MaxStacks = 2 },
            new UpgradeDef { Id = 13, Name = "回响", Desc = "击杀时发出冲击波",   MaxStacks = 3 },
            new UpgradeDef { Id = 14, Name = "镜像", Desc = "幻影同步开火",       MaxStacks = 2 },
            new UpgradeDef { Id = 15, Name = "逆潮", Desc = "受伤时肃清身边敌弹", MaxStacks = 2 },
        };

        public const int Count = 16;

        /// <summary>按 id 取定义。越界返回默认值而不是抛异常——
        /// HUD 和 AI 玩家都会查这个，一个坏 id 不该让整局游戏崩掉。</summary>
        public static UpgradeDef Get(int id)
        {
            if (id < 0 || id >= Count)
            {
                return new UpgradeDef { Id = -1, Name = "?", Desc = "", MaxStacks = 0 };
            }
            return All[id];
        }
    }
}
