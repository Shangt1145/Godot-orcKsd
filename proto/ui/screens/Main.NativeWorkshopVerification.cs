using System.Text.Json.Nodes;
using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyNativeWorkshop()
    {
        try { await VerifyNativeWorkshopAsync(); GetTree().Quit(); }
        catch (Exception ex) { GD.PushError(ex.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
    private async Task VerifyNativeWorkshopAsync()
    {
        void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }
        var artifacts = ProjectSettings.GlobalizePath("user://verification/native-workshop/" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(artifacts);
        async Task Capture(string file)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (DisplayServer.GetName() == "headless") return;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(artifacts, file));
        }
        async Task WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!condition()) { if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Shipped workshop route timed out"); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        }
        // Exercise the ordinary home route, not the native manager shortcut.
        Show("editor"); await MenuSettledAsync(); _workshop.ShowHome();
        await WaitFor(() => _workshop.ChoicePhase == "ready"); await DeckClickAsync(_workshop.PackChoice);
        await WaitFor(() => GodotObject.IsInstanceValid(_workshop.ModifyChoice) && _workshop.ModifyChoice.IsVisibleInTree() && _workshop.ChoicePhase == "ready");
        await DeckClickAsync(_workshop.ModifyChoice); await WaitFor(() => GodotObject.IsInstanceValid(_workshop.ModifyPick) && _workshop.ConfirmPack.IsVisibleInTree());
        DiyEditorScreen.Select(_workshop.ModifyPick, "pack/orc.bulwark"); _workshop.ModifyPick.EmitSignal(OptionButton.SignalName.ItemSelected, (long)_workshop.ModifyPick.Selected);
        await DeckClickAsync(_workshop.ConfirmPack);
        Check(_nativeWorkshop.SessionPackage == "orc.bulwark", "Shipped package used the legacy workspace");
        await Capture("shipped-library.png");
        await DeckClickAsync(_workshop.EffectLibraryButton);
        var effectDialog = _workshop.FindChildren("*", "AcceptDialog", true, false).OfType<AcceptDialog>().Single(d => d.Visible && d.Title == "词条与效果");
        await DeckClickAsync(effectDialog.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "抢修"));
        var effectLabels = string.Join("\n", effectDialog.FindChildren("*", "Label", true, false).OfType<Label>().Where(l => l.IsVisibleInTree()).Select(l => l.Text));
        Check(!new[] { "mainTrigger", "wrappers", "judicatorWrappers", ".prefab.json", "keyword.repair" }.Any(effectLabels.Contains), "Effect library leaked backend fields");
        await Capture("effect-library.png");
        await DeckClickAsync(effectDialog.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "＋ 新建效果"));
        var effectNameDialog = _workshop.FindChildren("*", "ConfirmationDialog", true, false).OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "新建效果");
        effectNameDialog.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single().Text = "补给训练"; await DeckClickAsync(effectNameDialog.GetOkButton());
        await DeckClickAsync(effectDialog.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.IsVisibleInTree() && b.Text == "编辑规则…"));
        var effectComposer = _workshop.FindChildren("*", "ConfirmationDialog", true, false).OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "编排效果");
        var effectInspector = effectComposer.FindChildren("*", "Control", true, false).OfType<NativeEffectInspector>().Single();
        Check(!effectInspector.NewNativeEffectButton.IsVisibleInTree(), "Low-level native editor was exposed");
        var timing = (OptionButton)effectInspector.FriendlyControls["/effects/0/trigger"]; DiyEditorScreen.Select(timing, "deploy"); timing.EmitSignal(OptionButton.SignalName.ItemSelected, (long)timing.Selected);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        effectInspector.EnsureFriendlyVisible(effectInspector.FriendlyControls["/effects/0/actions#add"]); await DeckClickAsync(effectInspector.FriendlyControls["/effects/0/actions#add"]);
        await Capture("effect-action-picker.png");
        effectInspector.PickFriendlyAction("drawOne"); await DeckClickAsync(effectInspector.PickerAddButton);
        await Capture("effect-composer.png"); await DeckClickAsync(effectComposer.GetOkButton());
        Check(_nativeWorkshop.Effects().Any(e => e.Name == "补给训练" && e.Rules is { Length: 1 }), "Human effect composer did not save native source");
        await DeckClickAsync(effectDialog.GetOkButton());
        var shippedFace = _workshop.FindChildren("*", "Control", true, false).OfType<CardControl>().First(c => c.IsVisibleInTree() && c.Definition.CardId.StartsWith("pack/orc.bulwark/", StringComparison.Ordinal));
        Check(shippedFace.Definition.ArtPath.Length > 0, "Shipped artwork missing in native library"); await DeckClickAsync(shippedFace);
        Check(_workshop.NativeSaveButton.IsVisibleInTree(), "Shipped card did not open native authoring"); await Capture("shipped-editor.png");
        _nativeWorkshop.Discard(); _workshop.ShowHome();
        _workshop.OpenPack(BuiltInWorkshopCatalog.Packs(_workshopLibrary).Single(p => p.Id == "pack/builtin"));
        Check(_nativeWorkshop.SessionPackage == "builtin", "Direct shipped OpenPack used the legacy workspace");
        _nativeWorkshop.Discard(); _workshop.ShowHome();
        var producer = new NativeWorkshop(System.IO.Path.Combine(artifacts, "producer"), RuntimeGameData.Directory("kards"));
        producer.Create("beta4.ui.probe", "原生工坊验收", OrcEngineHost.PackDescriptors.First().Id);
        var original = producer.CreateCard("probe.infantry");
        var source = JsonNode.Parse(producer.Read(original.Path))!.AsObject(); source["name"] = "导入预览步兵";
        source["futureExtension"] = new JsonObject { ["retain"] = true };
        producer.Commit(original.Path, source.ToJsonString());
        var image = Image.CreateEmpty(240, 320, false, Image.Format.Rgba8); image.Fill(new Color("557a86"));
        producer.SetArtwork(original.Id, image.SavePngToBuffer(), false); producer.Promote();
        var zip = System.IO.Path.Combine(artifacts, "native-pack.zip"); producer.Export("beta4.ui.probe", zip);
        var imported = _nativeWorkshop.Import(zip); ReloadPacks(); RefreshDiy();
        Check(imported.Pack.Cards == 1 && _nativeWorkshop.Import(zip).Unchanged, "ZIP installation or idempotence failed");
        Check(_catalog.Cards.Any(c => c.CardId == original.AppId), "Installed card missing from catalog");
        var texturePath = _catalog.Cards.Single(c => c.CardId == original.AppId).ArtPath;
        Check(texturePath.StartsWith("packart://", StringComparison.Ordinal) && _textures.Get(texturePath) is not null, "Portable package artwork missing");
        Show("editor"); await MenuSettledAsync();
        _workshop.ShowNativePackages();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _workshop.OpenNativePack("beta4.ui.probe");
        for (var i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await Capture("native-library.png");
        var face = _workshop.FindChildren("*", "Control", true, false).OfType<CardControl>().Single(c => c.Definition.CardId == original.AppId && c.IsVisibleInTree());
        await DeckClickAsync(face);
        var type = _workshop.FindChildren("*", "OptionButton", true, false).OfType<OptionButton>().Single(p => p.IsVisibleInTree() && p.ItemCount == 3 && p.GetItemMetadata(0).AsString() == "unit");
        var preview = _workshop.FindChildren("*", "Control", true, false).OfType<CardFace>().Single(c => c.IsVisibleInTree());
        foreach (var item in new[] { (1, CardFaceMaster.Order), (2, CardFaceMaster.Counter), (0, CardFaceMaster.Unit) })
        { type.Select(item.Item1); type.EmitSignal(OptionButton.SignalName.ItemSelected, (long)item.Item1); Check(preview.ActiveMaster == item.Item2, "Card master did not follow native type"); }
        var nation = _workshop.NativeNationPicker;
        var createNation = _workshop.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.IsVisibleInTree() && b.Text == "＋ 自定义国家…"); await DeckClickAsync(createNation);
        var nationDialog = _workshop.FindChildren("*", "ConfirmationDialog", true, false).OfType<ConfirmationDialog>().Single(d => d.Visible && d.Title == "创建国家");
        var nationName = nationDialog.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(); nationName.Text = "自定义国家";
        await DeckClickAsync(nationDialog.GetOkButton()); var customNation = _nativeWorkshop.DraftNations.Single(n => n.Label == "自定义国家");
        Check(nation.GetItemMetadata(nation.Selected).AsString() == customNation.Id, "Created nation did not update the form"); await DeckClickAsync(_workshop.NativeSaveButton);
        Check(JsonNode.Parse(_nativeWorkshop.Read(original.Path))!["components"]!.AsArray().OfType<JsonObject>().Single(c => c["component"]!.ToString() == "factionCost")["faction"]!.ToString() == customNation.Id, "Custom faction was not persisted");
        Check(_nativeWorkshop.PreviewCards().Any(c => c.CardId == original.AppId), "Custom faction card failed native admission");
        DiyEditorScreen.Select(nation, "germany"); nation.EmitSignal(OptionButton.SignalName.ItemSelected, (long)nation.Selected);
        var name = _workshop.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(c => c.Text == "导入预览步兵" && c.IsVisibleInTree());
        name.Text = "原生草稿已修改"; name.EmitSignal(LineEdit.SignalName.TextChanged, name.Text); await DeckClickAsync(_workshop.NativeSaveButton);
        Check(JsonNode.Parse(_nativeWorkshop.Read(original.Path))!["name"]!.GetValue<string>() == "原生草稿已修改", "Form did not commit to native draft");
        Check(_catalog.Cards.Single(c => c.CardId == original.AppId).Name == "导入预览步兵", "Draft leaked into formal pool");
        Check(JsonNode.Parse(_nativeWorkshop.Read(original.Path))!["futureExtension"]!["retain"]!.GetValue<bool>(), "Native editor dropped unknown data");
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(artifacts, "native-editor.png"));
        }
        var tabs = _workshop.FindChildren("*", "TabContainer", true, false).OfType<TabContainer>().Single(c => c.IsVisibleInTree());
        for (var page = 1; page < 4; page++) { tabs.CurrentTab = page; await Capture("native-page-" + page + ".png"); }
        tabs.CurrentTab = 0;
        await DeckClickAsync(_workshop.NativePromoteButton);
        Check(_catalog.Cards.Single(c => c.CardId == original.AppId).Name == "原生草稿已修改", "Promotion did not refresh catalog");
        var reexport = System.IO.Path.Combine(artifacts, "promoted.zip"); _nativeWorkshop.Export("beta4.ui.probe", reexport);
        var reopened = new NativeWorkshop(System.IO.Path.Combine(artifacts, "reimport"), RuntimeGameData.Directory("kards"));
        Check(reopened.Import(reexport).Pack.Cards == 1, "Promoted export failed to reimport");
        using var native = await OrcMatchSession.CreateFromCatalogAsync(_catalog.Cards, 31, "native-workshop-ui", "unused",
            artLookup: id => _artById.GetValueOrDefault(id, ""), openingHand: [original.AppId], sourceDirectory: RuntimeGameData.Directory("nations"), firstPlayerIndex: 0);
        native.DriveOpponent = false; await native.SettleMulliganAsync(false);
        var actor = native.View.SelfHand.Single(c => c.CardId == original.AppId);
        Check(actor.Definition.Name == "原生草稿已修改", "Match used stale formal source");
        Check(await native.SubmitAsync(new PlayCard(actor.Uid)) == UiSubmitOutcome.Applied, "Imported card could not deploy");
        native.SettleProjection(); Check(native.View.SelfLine.Any(c => c.Uid == actor.Uid), "Imported card absent from native field");
        var battle = new BattleCard { Size = new(160, 224) }; AddChild(battle); battle.Bind(actor, BattleCardMode.Inspect, _textures);
        Check(battle.UsesGeneratedFace, "Imported illustration did not use shared card face"); battle.QueueFree();
        GD.Print("NATIVE_WORKSHOP_VERIFY_OK shipped-home-route shipped-art zip-install idempotent portable-art native-form custom-faction three-masters four-pages commit-draft promote-refresh unknown-fields export-reimport real-native-deploy shared-face " + artifacts);
    }
}
