namespace Kards.Ui.Contracts;

/// <summary>Definition-only catalogue data. Null stats mean unknown/not applicable, never zero.</summary>
public sealed record UiCardDefinition : IDtoKind
{
    public DtoKind Kind => DtoKind.CardDefinition;
    public required string CardId
    {
        get; init;
    }
    public string Name { get; init; } = "";
    public string NameEn { get; init; } = "";
    public string CardType { get; init; } = "unknown";
    public string UnitType { get; init; } = "";
    public string Set { get; init; } = "";
    public string Rarity { get; init; } = "common";
    public string ArtPath { get; init; } = "";
    public string Text { get; init; } = "";
    public string Flavor { get; init; } = "";
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, int> KeywordValues { get; init; } = new Dictionary<string, int>();
    public int? Cost
    {
        get; init;
    }
    public int? BaseAttack
    {
        get; init;
    }
    public int? BaseDefense
    {
        get; init;
    }
    public int? BaseOpCost
    {
        get; init;
    }
    public bool IsToken
    {
        get; init;
    }
    public bool HasVariableStats
    {
        get; init;
    }
}

/// <summary>Engine projection only. All Effective* values include modifiers and auras.</summary>
public sealed record UiCardView : IDtoKind
{
    public DtoKind Kind => DtoKind.CardInstance;
    public required string Uid
    {
        get; init;
    }
    public required UiCardDefinition Definition
    {
        get; init;
    }
    public string CardId => Definition.CardId;
    public int? EffectiveCost
    {
        get; init;
    }
    public int? EffectiveAttack
    {
        get; init;
    }
    public int? EffectiveDefense
    {
        get; init;
    }
    public int? EffectiveOpCost
    {
        get; init;
    }
    public int? Health
    {
        get; init;
    }
    public string Zone { get; init; } = "hand";
    public int SlotIndex { get; init; } = -1;
    public string OwnerSide { get; init; } = "self";
    public Visibility Visibility { get; init; } = Visibility.Full;
    public bool IsHq
    {
        get; init;
    }
    public bool IsVeteran
    {
        get; init;
    }
    public bool CanPlayCard
    {
        get; init;
    }
    public bool CanAttack
    {
        get; init;
    }
    public bool CanMoveAndAttack
    {
        get; init;
    }
    public bool CanBeTargeted
    {
        get; init;
    }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, int> KeywordValues { get; init; } = new Dictionary<string, int>();
    public bool IsSuppressed
    {
        get; init;
    }
    public bool IsSilenced
    {
        get; init;
    }
    public bool IsCounterArmed { get; init; }
    public bool IsToken
    {
        get; init;
    }
}

public sealed record UiMatchView
{
    public required string MatchId
    {
        get; init;
    }
    public int Turn
    {
        get; init;
    }
    public string SelfPlayerName { get; init; } = "我方";
    public string EnemyPlayerName { get; init; } = "对手";
    public string Phase { get; init; } = "mulligan";
    public string? ResultTitle { get; init; }
    public string ActivePlayerSide { get; init; } = "self";
    public int SelfKredits
    {
        get; init;
    }
    public int SelfMaxKredits
    {
        get; init;
    }
    public int? EnemyKredits
    {
        get; init;
    }
    public int? EnemyMaxKredits
    {
        get; init;
    }
    public int SelfHandCount
    {
        get; init;
    }
    public int SelfDeckCount
    {
        get; init;
    }
    public int? EnemyHandCount { get; init; }
    public int? EnemyDeckCount { get; init; }
    public int SelfCounterCount
    {
        get; init;
    }
    public IReadOnlyList<UiCardView> SelfHand { get; init; } = Array.Empty<UiCardView>();
    public IReadOnlyList<UiCardView> SelfLine { get; init; } = Array.Empty<UiCardView>();
    public UiCardView? SelfHq
    {
        get; init;
    }
    public IReadOnlyList<UiCardView> EnemyLine { get; init; } = Array.Empty<UiCardView>();
    public UiCardView? EnemyHq
    {
        get; init;
    }
    public UiCardView? SelectedTarget
    {
        get; init;
    }
    public IReadOnlyList<UiCardView> PendingChoices { get; init; } = Array.Empty<UiCardView>();
    /// <summary>Prompt text supplied with pending choices. The responder still decides what the selection means.</summary>
    public string? PendingChoiceCaption { get; init; }
}

public abstract record UiCommand;
public sealed record PlayCard(string Uid, int? SupportIndex = null) : UiCommand;
public sealed record MoveUnit(string Uid, string ToZone) : UiCommand;
public sealed record AttackUnit(string AttackerUid, string DefenderUid) : UiCommand;
public sealed record RetreatUnit(string Uid) : UiCommand;
public sealed record EndTurn : UiCommand;
public sealed record ChooseMulligan(IReadOnlyList<string> KeepUids) : UiCommand;
public sealed record ChooseTarget(string Uid) : UiCommand;
