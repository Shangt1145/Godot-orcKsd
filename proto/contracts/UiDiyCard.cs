namespace Kards.Ui.Contracts;

/// <summary>Editable values only. Compilation, selection and effect execution belong to the bridge/engine.</summary>
public sealed record DiyEffectDraft
{
    public string Op { get; init; } = "draw";
    public string Target { get; init; } = "self";
    public int Amount { get; init; } = 1;
    public int Attack { get; init; } = 1;
    public int Defense { get; init; } = 1;
    public string Until { get; init; } = "";
}

public sealed record DiyCardDraft
{
    public string Id { get; init; } = "diy/" + Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "新卡牌";
    public string Nation { get; init; } = "av76";
    public string Rarity { get; init; } = "iron";
    public string CardType { get; init; } = "unit";
    public string UnitType { get; init; } = "infantry";
    public int Cost { get; init; } = 1;
    public int OpCost { get; init; } = 1;
    public int Attack { get; init; } = 2;
    public int Defense { get; init; } = 3;
    public string[] Keywords { get; init; } = [];
    public int Armor { get; init; } = 1;
    public string ArtCardId { get; init; } = "";
    public string Flavor { get; init; } = "";
    public DiyEffectDraft[] Effects { get; init; } = [];
    public DiyCardDraft Snapshot() => this with { Keywords = Keywords.ToArray(), Effects = Effects.ToArray() };
}

public sealed record UiDiyChoice(string Id, string Label);
public sealed record UiDiyValidation(UiCardDefinition? Card, string? EngineCardJson, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0 && Card is not null && EngineCardJson is not null;
}
