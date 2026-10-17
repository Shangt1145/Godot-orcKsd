using Kards.Ui.Core;
using Kards.Ui.Contracts;

namespace Kards.Ui.OrcBridge;

/// <summary>Exercises every admitted package definition and mounted effect in real engine matches.</summary>
public static class PackPlayabilityVerification
{
    public static async Task<int> VerifyAsync(IReadOnlyList<UiCardDefinition> source, string sourceDirectory)
    {
        var packs = PackSet.FromSnapshot(OrcEngineHost.Snapshot());
        var ids = packs.Definitions.Select(c => c.Id).ToArray();
        foreach (var chunk in ids.Chunk(20))
        {
            var deck = new ConstructedDeck { Name = "卡包完整装载检查", CardIds = chunk,
                PackIds = chunk.Select(DeckCardPacks.Of).Distinct().ToArray(),
                Options = new DeckOptions { UnrestrictedNations = true, UnrestrictedSize = true } };
            using var session = await OrcMatchSession.CreateFromCatalogAsync(source, 31, "pack-check", "unused",
                sourceDirectory: sourceDirectory, constructedDeck: deck, packs: packs);
            var actual = session.Host.Match.Players[0].Deck.Concat(session.View.SelfHand.Select(c => c.CardId)).ToArray();
            if (!actual.OrderBy(x => x).SequenceEqual(chunk.OrderBy(x => x)))
                throw new InvalidDataException("卡包实例未按完整卡表进入引擎。");
        }
        return ids.Length;
    }
}
