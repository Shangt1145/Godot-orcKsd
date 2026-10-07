using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// Card -> UI value objects. Two read paths, per the product decision:
///   - cards in hand/deck read the card definition only (no runtime unit state exists yet);
///   - units on the board read runtime data components plus the modifier chain.
/// Headquarters is a third path: it is a <see cref="Hq"/>, not a <see cref="CardBase"/>, so it carries no
/// definition of its own and exposes only health.
/// No rule is inferred here: availability flags come from the engine's query surfaces, not from cost math.
/// </summary>
public sealed class OrcCardReader
{
    private readonly CardLibrary _library;
    private readonly Func<string, string>? _artLookup;
    internal IReadOnlyDictionary<string, UiCardDefinition>? SourceDefinitions { get; set; }

    /// <param name="artLookup">
    /// Maps a card id to a UI art path. The engine has no art concept, so the UI supplies its own
    /// table; without it cards render with the text fallback rather than an invented path.
    /// </param>
    public OrcCardReader(CardLibrary library, Func<string, string>? artLookup = null)
    {
        _library = library; _artLookup = artLookup;
    }

    /// <summary>
    /// Art lookup key. The engine carries no art path: the definition id is the registration key,
    /// so the UI resolves id -> texture with its own table. Name is the fallback.
    /// </summary>
    public string ArtIdOf(CardBase card)
        => _library.TryGetRegisteredId(card.Definition, out var id) ? id : card.Definition.Name;

    public UiCardDefinition ReadDefinition(CardBase card)
    {
        var definition = card.Definition;
        var cardId = _library.TryGetRegisteredId(definition, out var id) ? id : definition.Name;
        if (SourceDefinitions?.TryGetValue(cardId, out var source) == true) return source;
        return new UiCardDefinition
        {
            CardId = cardId,
            Name = definition.Name,
            CardType = definition.Category switch
            {
                CardCategory.Counter => "counter",
                CardCategory.Command => "order",
                _ => "unit"
            },
            UnitType = definition.UnitTypes.Count > 0 ? definition.UnitTypes[0].ToString() : "",
            Set = definition.Faction.ToString(),
            Rarity = definition.Rarity.ToString(),
            ArtPath = _artLookup?.Invoke(cardId) ?? "",
            Cost = definition.DeployCost,
            BaseAttack = definition.Attack,
            BaseDefense = definition.Defense,
            BaseOpCost = definition.OperateCost,
            IsToken = false
        };
    }

    /// <summary>Headquarters has no engine definition; the UI needs one to draw its map card.</summary>
    private static UiCardDefinition HqDefinition(Hq hq) => new()
    {
        CardId = "hq",
        Name = hq.Name,
        CardType = "hq",
        UnitType = "",
        Set = "",
        Rarity = "common",
        ArtPath = "",
        Cost = null,
        BaseAttack = null,
        BaseDefense = hq.Health,
        BaseOpCost = null,
        IsToken = false
    };

    public UiCardView Read(Card card, Player viewer, string zone, int slotIndex)
    {
        var typed = card as CardBase;
        var hq = card as Hq;
        var definition = hq is not null ? HqDefinition(hq) : ReadDefinition(typed!);
        var owner = hq?.Owner ?? typed?.Owner;
        var side = owner is null || owner.Index == viewer.Index ? "self" : "enemy";
        var unitized = card.TryGetData<UnitStateData>(out var state);

        var attack = hq is not null ? null : unitized ? Effective(typed!, CardStatFields.Attack) : definition.BaseAttack;
        var defense = hq is not null ? hq.Health : unitized ? Effective(typed!, CardStatFields.Defense) : definition.BaseDefense;
        var opCost = hq is not null ? null : unitized ? Effective(typed!, CardStatFields.OperateCost) : definition.BaseOpCost;

        return new UiCardView
        {
            Uid = OrcRefs.KeyOf(card),
            Definition = definition,
            EffectiveCost = hq is not null ? null : Effective(typed!, CardStatFields.DeployCost) ?? definition.Cost,
            EffectiveAttack = attack,
            EffectiveDefense = defense,
            EffectiveOpCost = opCost,
            Health = hq?.Health ?? (unitized && state is not null ? state.Defense : definition.BaseDefense),
            Zone = zone,
            SlotIndex = slotIndex,
            OwnerSide = side,
            // Board cards are public; the hand is filtered by the match reader, never here.
            Visibility = Visibility.Full,
            IsHq = hq is not null,
            CanPlayCard = false, // supplied by UiBattleActions; the UI does not evaluate legality.
            CanAttack = unitized && card.TryGetData<CommandData>(out var command) && command.CanAttack,
            CanMoveAndAttack = unitized && card.TryGetData<CommandData>(out var move) && move.CanMove,
            CanBeTargeted = side == "enemy",
            IsSuppressed = Keywords(card).Has(KeywordIds.Suppressed),
            IsSilenced = Keywords(card).Has(KeywordIds.Inhibited),
            IsCounterArmed = card.TryGetData<CounterActivationData>(out var armed) && armed.IsActive
        };
    }

    /// <summary>HQ is read as a board card with its own health domain.</summary>
    public UiCardView ReadHq(Hq hq, Player viewer, int slotIndex) => Read(hq, viewer, "support", slotIndex);

    private static KeywordManager Keywords(Card card)
        => card is CardBase typed ? typed.Keywords : ((Hq)card).Keywords;

    private static int? Effective(CardBase card, string field)
    {
        try { return card.Modifiers.GetEffectiveValue(field); }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
