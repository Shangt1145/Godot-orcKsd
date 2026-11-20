using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private (UiCardDefinition Card, PackSet Packs)? _workshopTrial;
    private async Task StartWorkshopTrialAsync(UiCardDefinition card, PackSet packs)
    {
        StopRealMatch();
        _workshopTrial = (card, packs);
        _battleAppearanceDeck = null;
        var runner = new OrcMatchRunner
        {
            CatalogCards = _deckService.Cards.Where(item => !item.CardId.StartsWith("diy/", StringComparison.Ordinal)).ToArray(),
            CatalogSourceDirectory = RuntimeGameData.Directory("nations"),
            ArtLookup = id => id == card.CardId ? card.ArtPath : _artById.GetValueOrDefault(id, ""),
            OpeningHand = [card.CardId],
            WorkshopTrialCard = card.CardId,
            Packs = packs,
            DeckSeed = 37,
            FirstPlayerIndex = 0
        };
        Show("battle");
        BindRunner(runner);
        _battle.ShowWorkshopReturn(true);
        try
        {
            await runner.StartAsync();
            if (_runner == runner && runner.CurrentView is { } view)
                _workshopTrialOpening = (view.SelfHand.Single(item => item.CardId == card.CardId).Uid, view.SelfDeckCount, view.Turn);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_runner != runner) return;
            ReturnFromWorkshopTrial();
            _workshop.ReportTrialError(error.Message);
        }
    }

    private (string Uid, int DeckCount, int Turn)? _workshopTrialOpening;

    private void ReturnFromWorkshopTrial()
    {
        if (_workshopTrial is null) return;
        int? drawn = null;
        if (_workshopTrialOpening is { } opening && _runner?.CurrentView is { } view
            && view.Turn == opening.Turn && view.SelfLine.Any(unit => unit.Uid == opening.Uid))
            drawn = opening.DeckCount - view.SelfDeckCount;
        StopRealMatch();
        Show("editor");
        _workshop.FinishTutorialTrial(drawn);
    }
}
