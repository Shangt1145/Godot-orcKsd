using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

public sealed record WorkshopQuickEffect(string Action = "draw", int Amount = 1, int Defense = 1)
{
    public string Description(string cardType)
    {
        var action = Action switch
        {
            "draw" => $"抽 {Amount} 张牌",
            "damage" => $"对 1 个敌方单位造成 {Amount} 点伤害",
            "buff" => $"使 1 个友方单位获得 +{Amount}+{Defense}",
            _ => throw new InvalidDataException("未知效果")
        };
        return (cardType == "unit" ? "部署：" : "") + action + "。";
    }

    public OrcEffectDraft Compile(string cardId, string cardType, IReadOnlyList<UiCardDefinition> catalog)
    {
        if (cardType is not ("unit" or "order"))
            throw new InvalidDataException("快捷效果支持单位和指令；反制请用高级积木。");
        var result = OrcInspector.CompileText(cardId, Description(cardType), cardType, catalog);
        if (!result.ParseComplete || !result.AllCompiled || result.Parse.Effects.Count != 1)
            throw new InvalidDataException("效果无法应用：" + string.Join("；", result.Problems));
        var draft = OrcEffectEditing.ToDraft(result.Parse.Effects.Single());
        return draft with { QuickEffect = new(Action, Amount, Defense) };
    }

    public static WorkshopQuickEffect? From(OrcEffectDraft draft) =>
        draft.QuickEffect is { } value ? new(value.Action, value.Amount, value.Defense) : null;
}
