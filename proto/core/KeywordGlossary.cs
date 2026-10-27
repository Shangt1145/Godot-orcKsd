namespace Kards.Ui.Core;

/// <summary>
/// 内建词条的一句话释义。
///
/// 引擎**没有**释义字段：<c>KeywordRegistry</c> 只登记"标识 → 组件工厂"，内置内容在
/// <c>Orc.Plugins.BuiltIn</c>，投影里也只有 id 与参值。所以这张表由界面侧维护。
///
/// 收录规则：**只收核对过的**。每条的核对依据写在本行注释里（引擎源码位置）；没核对的词条
/// 不写进来，界面会显示"引擎未提供这个词条的释义文本"——宁可缺一条，也不编一句听起来对的话。
/// DIY 词条的释义不在这张表里，取作者在工坊填写的说明。
/// </summary>
public static class KeywordGlossary
{
    public static readonly IReadOnlyDictionary<string, string> BuiltIn = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // KeywordComponents.cs · ArmorKeywordComponent
        ["armor"] = "受到的对战伤害 -X（攻击与反击都算；指令伤害不减）。X 最多 3。",
        // KeywordComponents.cs · BlitzKeywordComponent（unit.deployed 时置位 CanMove/CanAttack）
        ["blitz"] = "部署当回合就能移动与攻击。",
        // KeywordComponents.cs · FuryKeywordComponent（首次攻击后仍可攻，二次后不可；回合恢复清零）
        ["fury"] = "本回合首次攻击后还能再攻击一次，之后不能再攻；回合恢复时清零。",
        // KeywordComponents.cs · AmbushKeywordComponent（造成攻击伤害时先判资格与条件）
        ["ambush"] = "结算攻击伤害时先判定资格与条件。",
        // KeywordComponents.cs · MobilizeKeywordComponent（友方回合开始 +1/+1；受实际伤害后失去）
        ["mobilize"] = "友方回合开始时 +1/+1 累积；受到实际伤害后失去（已得的加成保留）。",
        // KeywordComponents.cs · PincerKeywordComponent（部署收尾配对，双方 +2 攻击，一方离场另一方失去）
        ["pincer"] = "部署后可指定一名己方在场单位配对，双方 +2 攻击；任一方离场则另一方失去。",
        // KeywordComponents.cs · SuppressedKeywordComponent（不能移动或攻击；拥有者回合结束时解除）
        ["suppressed"] = "不能移动或攻击；在施加后首先到来的拥有者回合结束时解除。",
        // KeywordComponents.cs · ImmuneKeywordComponent（伤害归零；不限制索敌）
        ["immune"] = "不会受到伤害（伤害归零）；不影响被选中与非伤害效果。",
        // KeywordComponents.cs · IntelligenceKeywordComponent（被使用时触发；明牌环节尚未接通）
        ["intelligence"] = "具有情报的卡被使用时触发；明牌环节引擎尚未接通。",
        // SmokeScreenRules.cs（支援线单位受保护；前进到前线或攻击后失去）
        ["smokescreen"] = "支援线单位受到保护；前进到前线或攻击后失去。",
        // CommandManager.MaintainGuardForLine（同线相邻单位受守护；守护者自身不被守护）
        ["guard"] = "同线相邻单位受其保护，攻击方须先处理守护者。",
    };

    /// <summary>取内建释义；没有核对过的词条返回 null（界面据此显示"未提供"，不猜）。</summary>
    public static string? Describe(string keyword) => BuiltIn.GetValueOrDefault(keyword);
}
