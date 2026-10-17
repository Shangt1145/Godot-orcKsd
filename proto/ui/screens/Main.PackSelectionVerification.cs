using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class Main
{
    private async Task VerifyPackSelectionAsync()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        Show("decklib"); await MenuSettledAsync();
        var before = _deckEditor.Draft;
        await DeckClickAsync(_deckLibrary.NewButton);
        Check(_deckPackPicker.Visible && _deckPackPicker.GetOkButton().Disabled,
            "Creating a deck did not require a pack selection.");
        Check(_deckEditor.Draft.Id == before.Id, "A deck was created before selecting packs.");
        if (OS.GetCmdlineUserArgs().Contains("--capture-decks"))
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var directory = ProjectSettings.GlobalizePath("res://artifacts/decks"); System.IO.Directory.CreateDirectory(directory);
            GetViewport().GetTexture().GetImage().SavePng(directory + "/select-packs.png");
        }
        await DeckClickAsync(_deckPackPicker.Choices["source"]);
        Check(!Kards.Ui.OrcBridge.OrcEngineHost.PackDescriptors.Where(p => p.BasePackId is not null)
            .Any(p => _deckPackPicker.Choices.ContainsKey("pack/" + p.Id)),
            "Expansion packages must not appear as independent choices.");
        await DeckClickAsync(_deckPackPicker.GetOkButton());
        await MenuSettledAsync();
        var excluded = _deckService.PackCards.First();
        Check(!_deckEditor.AddCard(excluded), "A card from an unselected pack could be added.");
        await DeckClickAsync(_deckEditor.PacksButton);
        await DeckClickAsync(_deckPackPicker.Choices["pack/builtin"]);
        await DeckClickAsync(_deckPackPicker.GetOkButton());
        await MenuSettledAsync();
        Check(_active == "deck" && _deckEditor.Draft.PackIds!.ToHashSet().SetEquals(["source", "pack/builtin"]),
            "Multi-pack selection did not reach the new deck.");
        var pool = _deckService.Pool(_deckEditor.Draft);
        Check(pool.Count == _deckService.PlayableCards.Count && pool.All(c => _deckService.PlayableIds.Contains(c.CardId)), "Selected pool contains unsupported or unselected cards.");
        Check(_deckService.PackCards.Where(c => Kards.Ui.Core.DeckCardPacks.RawOf(c.CardId) != "pack/builtin")
            .All(c => pool.Any(p => p.CardId == c.CardId)), "Choosing the parent cardpack did not include expansion content.");
        Check(_deckEditor.Grid.GetChildren().OfType<VBoxContainer>().SelectMany(b => b.GetChildren().OfType<CardControl>())
            .All(c => _deckService.PlayableIds.Contains(c.Definition.CardId)), "Builder rendered an unsupported card.");
        var unsupported = _catalog.Cards.First(c => !_deckService.PlayableIds.Contains(c.CardId));
        Check(!_deckEditor.AddCard(unsupported), "An unsupported card could be added programmatically.");
        await DeckClickAsync(_deckEditor.PacksButton);
        await DeckClickAsync(_deckPackPicker.Choices["source"]);
        await DeckClickAsync(_deckPackPicker.GetOkButton());
        Check(_deckService.Pool(_deckEditor.Draft).Count == _deckService.PackCards.Count, "Changing pack selection did not refresh the pool: "
            + string.Join(",", _deckEditor.Draft.PackIds!) + " count=" + _deckService.Pool(_deckEditor.Draft).Count);
        var roundTrip = DeckLibrary.Import(DeckLibrary.Export(_deckEditor.Draft));
        Check(roundTrip.PackIds!.SequenceEqual(_deckEditor.Draft.PackIds!), "Deck code lost selected packs.");
        var identity = _deckEditor.Draft.Id;
        await DeckClickAsync(_deckEditor.NewButton);
        await DeckClickAsync(_deckPackPicker.GetCancelButton());
        Check(_deckEditor.Draft.Id == identity, "Cancelling pack selection replaced the current draft.");
        _deckEditor.Load(_decks.ActiveDeck!);
        GD.Print("PACK_SELECTION_VERIFY_OK require-before-create multi-select strict-playable-pool reject-unsupported reject-unselected change-packs export-import cancel-preserves-draft");
    }
}
