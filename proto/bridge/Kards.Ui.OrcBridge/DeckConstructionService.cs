using Kards.Ui.Core;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

public sealed class DeckConstructionService
{
    private readonly IReadOnlyList<UiCardDefinition> _sourceCards;
    private readonly IReadOnlySet<string> _reviewedIds;
    private IReadOnlyList<UiCardDefinition> _packCards = [];
    private IReadOnlyList<UiCardDefinition> _imported = [];
    public IReadOnlyList<UiCardDefinition> Cards { get; private set; }
    public IReadOnlyList<UiCardDefinition> CustomCards { get; private set; } = [];
    public IReadOnlyList<UiCardDefinition> PackCards => _packCards;
    public IReadOnlyDictionary<string, string> CustomErrors { get; private set; } = new Dictionary<string, string>();
    public IReadOnlySet<string> PlayableIds { get; private set; }
    public UiCardPoolReport Report { get; }
    public DeckConstructionService(IReadOnlyList<UiCardDefinition> cards, string sourceDirectory)
    {
        _sourceCards = cards.Where(c => !c.CardId.StartsWith("diy/", StringComparison.Ordinal)).ToArray();
        Cards = _sourceCards;
        var (entries, report, _) = VerifiedCardPool.Compile(_sourceCards, sourceDirectory);
        _reviewedIds = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        PlayableIds = _reviewedIds;
        Report = report;
    }
    public void UpdateCustom(IReadOnlyList<DiyCardDraft> drafts)
    {
        var custom = new List<UiCardDefinition>(); var errors = new Dictionary<string, string>();
        foreach (var draft in drafts)
        {
            var result = DiyCardCompiler.Validate(draft, _sourceCards);
            if (!result.Success) { errors[draft.Id] = string.Join("；", result.Errors); continue; }
            if (custom.Any(c => c.CardId == draft.Id)) throw new InvalidDataException("DIY 卡牌编号重复");
            custom.Add(result.Card!);
        }
        CustomCards = custom.AsReadOnly(); CustomErrors = errors;
        Rebuild(custom);
    }

    /// <summary>Installs pack projections into the buildable pool. Pack cards are engine-admitted by
    /// construction (engine cardpack pipeline precheck), so every projection is playable.</summary>
    public void UpdatePacks(IReadOnlyList<UiCardDefinition> packCards)
    {
        _packCards = packCards.ToArray();
        Rebuild(CustomCards);
    }

    /// <summary>Installs OrC batch-import projections (complete rows only) into the buildable pool.
    /// Admission already happened at import compile time; callers pass the compiled projections.</summary>
    public void UpdateImported(IReadOnlyList<UiCardDefinition> imported)
    {
        _imported = imported.ToArray();
        Rebuild(CustomCards);
    }

    private void Rebuild(IReadOnlyList<UiCardDefinition> custom)
    {
        Cards = _sourceCards.Concat(_packCards).Concat(custom).Concat(_imported).ToArray();
        PlayableIds = _reviewedIds.Concat(_packCards.Select(c => c.CardId)).Concat(custom.Select(c => c.CardId))
            .Concat(_imported.Select(c => c.CardId))
            .ToHashSet(StringComparer.Ordinal);
    }
    public IReadOnlyList<string> Validate(ConstructedDeck deck) => ConstructionRules.Validate(deck, Cards, PlayableIds);
    public ConstructedDeck Starter(int size = 39, int seed = 20261009)
    {
        var random = new Random(seed);
        // The automatic opponent keeps using reviewed source cards.
        var choices = _sourceCards.Where(c => _reviewedIds.Contains(c.CardId))
            .SelectMany(c => Enumerable.Repeat(c.CardId, ConstructionRules.CopyLimit(c.Rarity))).ToArray();
        random.Shuffle(choices);
        if (size < 5 || choices.Length < size) throw new InvalidDataException("已支持的卡牌不足以组成此张数的卡组");
        return new ConstructedDeck { Name = "混编初始卡组", Options = new DeckOptions { UnrestrictedNations = true, TotalCards = size + 1 }, CardIds = choices.Take(size).ToArray() };
    }
}
