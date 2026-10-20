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
        // 以前只有 source+builtin 两个来源，"池子等于全部可玩卡"恰好成立；多出独立卡包后前提不再成立。
        // 真正要保证的是：池子里没有非法卡，且未选中的卡包没有泄漏进来。
        Check(pool.Count > 0 && pool.All(c => _deckService.PlayableIds.Contains(c.CardId)),
            "Selected pool contains unsupported cards.");
        Check(!pool.Any(c => Kards.Ui.Core.DeckCardPacks.RawOf(c.CardId) == "pack/orc.bulwark"),
            "Cards from an unselected card pack leaked into the pool.");
        // 只有真正的扩展包（BasePackId 非空）才该随父包一起进池；独立卡包（如 orc.bulwark）不该被带出来。
        var extensionIds = Kards.Ui.OrcBridge.OrcEngineHost.PackDescriptors
            .Where(p => p.BasePackId is not null)
            .Select(p => "pack/" + p.Id)
            .ToHashSet(StringComparer.Ordinal);
        Check(_deckService.PackCards.Where(c => extensionIds.Contains(DeckCardPacks.RawOf(c.CardId) ?? ""))
            .All(c => pool.Any(p => p.CardId == c.CardId)), "Choosing the parent cardpack did not include expansion content.");
        Check(_deckEditor.Grid.GetChildren().OfType<VBoxContainer>().SelectMany(b => b.GetChildren().OfType<CardControl>())
            .All(c => _deckService.PlayableIds.Contains(c.Definition.CardId)), "Builder rendered an unsupported card.");
        var unsupported = _catalog.Cards.First(c => !_deckService.PlayableIds.Contains(c.CardId));
        Check(!_deckEditor.AddCard(unsupported), "An unsupported card could be added programmatically.");
        await DeckClickAsync(_deckEditor.PacksButton);
        await DeckClickAsync(_deckPackPicker.Choices["source"]);
        await DeckClickAsync(_deckPackPicker.GetOkButton());
        // 改选择的语义是"池子必须随所选包变化"。原来用「等于全部卡包卡」判定，前提是没有独立卡包；
        // 现在 orc.bulwark 是独立包，未选中时本就不该在池里，所以改判"池子确实变了且仍全部合法"。
        var repool = _deckService.Pool(_deckEditor.Draft);
        Check(repool.Count > 0 && repool.Count != pool.Count
            && repool.All(c => _deckService.PlayableIds.Contains(c.CardId)), "Changing pack selection did not refresh the pool: "
            + string.Join(",", _deckEditor.Draft.PackIds!) + " count=" + repool.Count);
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
