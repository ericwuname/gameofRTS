using UnityEngine;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 局间 Modifier：每局开局随机抽一条「本局规则」。
    ///
    /// 对应 Run Variety 公式里的 **Seed** 因子——此前这个因子恒等于 1，
    /// 每局开局完全一样，是「无聊」的三大病根之一。
    /// 开局前先看到规则，玩家在按下开始的那一刻就开始做战略调整：
    /// 「孤注」该怎么打？「弹雨」该换什么构筑？期待在开局前就产生了。
    ///
    /// 抽取规则：70% 概率有一条规则、30% 无规则（基线局）。
    /// 保留基线局是刻意的：如果每局都有规则，「规则」会沦为默认设定；
    /// 偶尔的基线局让有规则的局显得特殊。
    /// </summary>
    public enum RunModifier
    {
        None = 0,

        /// <summary>潮退：能量球掉落 ×2，敌机速度 +30%。</summary>
        TideOut = 1,

        /// <summary>孤注：只有 1 条命，得分 ×3。极致的风险回报。</summary>
        LoneBet = 2,

        /// <summary>双月：Boss 提前到第 3 波，Boss 血量减半。前期高压高回报。</summary>
        TwinMoons = 3,

        /// <summary>丰潮：宝箱刷新减半，且开箱额外掉 4 球。</summary>
        RichTide = 4,

        /// <summary>弹雨：敌弹速 +25%，敌机 HP -25%。躲更难但杀更快。</summary>
        BulletRain = 5
    }

    /// <summary>Modifier 的名称与描述（HUD 显示用）。集中一处避免散落。</summary>
    public static class RunModifierInfo
    {
        public static string Name(RunModifier m)
        {
            switch (m)
            {
                case RunModifier.TideOut: return "潮 退";
                case RunModifier.LoneBet: return "孤 注";
                case RunModifier.TwinMoons: return "双 月";
                case RunModifier.RichTide: return "丰 潮";
                case RunModifier.BulletRain: return "弹 雨";
                default: return "";
            }
        }

        public static string Desc(RunModifier m)
        {
            switch (m)
            {
                case RunModifier.TideOut: return "能量球掉落 ×2 · 敌机速度 +30%";
                case RunModifier.LoneBet: return "只有 1 条命 · 得分 ×3";
                case RunModifier.TwinMoons: return "Boss 提前至第 3 波 · 血量减半";
                case RunModifier.RichTide: return "宝箱刷新减半 · 开箱额外 +4 球";
                case RunModifier.BulletRain: return "敌弹速 +25% · 敌机血量 -25%";
                default: return "";
            }
        }
    }
}
