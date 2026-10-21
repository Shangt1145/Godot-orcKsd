using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyWorkshopRoutes()
    {
        try { await VerifyWorkshopRoutesAsync(); GetTree().Quit(); }
        catch (Exception ex) { GD.PushError(ex.ToString()); GetTree().Quit(1); }
    }
    private async Task VerifyWorkshopRoutesAsync()
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var originals = new[] { "user://diy-cards.json", "user://diy-packs.json" }.Select(ProjectSettings.GlobalizePath).Where(System.IO.File.Exists)
            .ToDictionary(p => p, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(p))));
        var folder = ProjectSettings.GlobalizePath("user://verification/all-workshop-routes/" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(folder);
        async Task Capture(string name)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (DisplayServer.GetName() == "headless") return;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); GetViewport().GetTexture().GetImage().SavePng(folder + "/" + name + ".png");
        }
        Show("editor"); await MenuSettledAsync();
        if(!_diy.Cards.Any(c => c.PackId == "pack/builtin" && c.SourceCardId.Length > 0))
        {
            var original = _catalog.Cards.First(c => c.CardId.StartsWith("pack/builtin/", StringComparison.Ordinal));
            if (!_workshopLibrary.Packs.Any(p => p.Id == "pack/builtin"))
                _workshopLibrary.Save(BuiltInWorkshopCatalog.Packs(_workshopLibrary).Single(p => p.Id == "pack/builtin"));
            _diy.Save(BuiltInWorkshopCatalog.Edit(original,"pack/builtin",RuntimeGameData.Directory("nations"))); RefreshDiy();
        }
        if (_workshopLibrary.Packs.All(p => p.IsBuiltIn))
        {
            var seed = new DiyPackDraft { Name = "自建卡包验证" }; _workshopLibrary.Save(seed);
            _diy.Save(new DiyCardDraft { Name = "自建步兵", PackId = seed.Id }); RefreshDiy();
        }
        var packs = BuiltInWorkshopCatalog.Packs(_workshopLibrary).ToArray();
        // Include a legacy expansion even when the user's current library has no saved expansion.
        var parent = _workshopLibrary.Packs.FirstOrDefault(p => !p.IsExpansion && !p.IsBuiltIn);
        if (parent is not null)
        {
            var expansion = new DiyPackDraft { Name = "扩展入口验证", BasePackId = parent.Id }; _workshopLibrary.Save(expansion);
            _diy.Save(new DiyCardDraft { Name = "扩展步兵", PackId = expansion.Id }); RefreshDiy(); packs = BuiltInWorkshopCatalog.Packs(_workshopLibrary).ToArray();
        }
        var checkedCards = 0;
        foreach (var pack in packs)
        {
            _workshop.OpenPack(pack); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(_workshop.UsesUnifiedWorkspace, "Pack did not use unified workspace: " + pack.Name);
            var face = _workshop.FindChildren("*", "Control", true, false).OfType<CardControl>().FirstOrDefault(c => c.IsVisibleInTree());
            if (face is null)
            {
                var create = _workshop.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.IsVisibleInTree() && b.Text == "＋ 新建卡牌"); await DeckClickAsync(create);
                _diyEditor.CardName.Text = "空卡包创建验证"; _diyEditor.CardName.EmitSignal(LineEdit.SignalName.TextChanged, _diyEditor.CardName.Text); await DeckClickAsync(_diyEditor.SaveButton);
                Check(_diy.Cards.Any(c => c.PackId == pack.Id && c.Name == "空卡包创建验证"), "Empty pack could not create a card: " + pack.Name); checkedCards++; await Capture("empty-pack"); continue;
            }
            var id = face.Definition.CardId; await DeckClickAsync(face);
            var previews = _workshop.FindChildren("*", "Control", true, false).OfType<CardFace>().Where(c => c.IsVisibleInTree()).ToArray();
            Check(previews.Any(c => c.Size.Y >= 390), "Pack used compact legacy preview: " + pack.Name);
            Check(!_diyEditor.Cards.IsVisibleInTree(), "Pack exposed the legacy sidebar: " + pack.Name); checkedCards++;
            if (!pack.IsBuiltIn && _diy.Cards.FirstOrDefault(c => c.RuntimeId == id) is { } before)
            {
                _diyEditor.CardName.Text = before.Name + "·修改验证"; _diyEditor.CardName.EmitSignal(LineEdit.SignalName.TextChanged, _diyEditor.CardName.Text); await DeckClickAsync(_diyEditor.SaveButton);
                var after = _diy.Cards.Single(c => c.Id == before.Id);
                Check(after.Name.EndsWith("·修改验证", StringComparison.Ordinal) && after.PackId == before.PackId, "Compatible form did not save to its existing library");
                Check(System.Text.Json.JsonSerializer.Serialize(before.OrcEffects) == System.Text.Json.JsonSerializer.Serialize(after.OrcEffects) && before.Keywords.SequenceEqual(after.Keywords), "Compatible editing lost existing effects or keywords");
            }
            if (pack.Id == "source") await Capture("space-war");
            else if (pack.IsExpansion) await Capture("expansion");
            else if (pack.Name == "机制示范包") await Capture("mechanics");
            else if (!pack.IsBuiltIn) await Capture("custom-" + checkedCards);
        }
        _workshop.OpenNativePack("builtin"); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Check(_workshop.UsesUnifiedWorkspace, "Native manager failed to open an existing overlay");
        await Capture("overlay-library");
        // Reproduce a saved draft, switching away, and opening the same native package again.
        _workshop.OpenNativePack("orc.bulwark");
        var asset = _nativeWorkshop.Assets().First(a => a.Kind == "Card"); var document = new NativeCardDocument(_nativeWorkshop.Read(asset.Path)); document.Rename("恢复验证卡牌"); _nativeWorkshop.Commit(asset.Path, document.Json);
        _nativeWorkshop.Suspend(); _workshop.ShowHome();
        _workshop.OpenNativePack("orc.bulwark"); Check(_nativeWorkshop.RecoveredDraft, "Interrupted native draft was not recovered");
        var restored = _workshop.FindChildren("*", "Control", true, false).OfType<CardControl>().Single(c => c.IsVisibleInTree() && c.Definition.Name == "恢复验证卡牌"); await DeckClickAsync(restored); await Capture("recovered");
        _nativeWorkshop.Suspend();
        foreach(var original in originals) Check(original.Value == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(original.Key))), "Original user library changed during verification");
        GD.Print($"ALL_WORKSHOP_ROUTES_VERIFY_OK packs={packs.Length} cards={checkedCards} user-library-copy original-files-unchanged source custom mechanics expansion native recovered large-preview no-legacy-sidebar " + folder);
    }
}
