using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyCardFace()
    {
        try { await VerifyCardFaceAsync(); GetTree().Quit(); }
        catch (Exception e) { GD.PushError(e.ToString()); GetTree().Quit(1); }
    }
    private async Task VerifyCardFaceAsync()
    {
        var directory = ProjectSettings.GlobalizePath("res://artifacts/cardface-alignment/" + GetWindow().Size.X);
        Directory.CreateDirectory(directory);
        await VerifyCardTypographyAsync(directory);
        CardFace.ClearArtOverride();
        async Task Settle()
        { for (var i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task<Image> Capture(string name)
        {
            await Settle();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var result = GetViewport().GetTexture().GetImage(); result.SavePng(directory + "/" + name + ".png"); return result;
        }
        void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        void Pixel(Image shot, Vector2 point, Color expected, string why)
        {
            point *= new Vector2(shot.GetWidth(), shot.GetHeight()) / GetViewportRect().Size;
            var pixel = shot.GetPixel((int)point.X, (int)point.Y);
            Check(Math.Abs(pixel.R - expected.R) + Math.Abs(pixel.G - expected.G) + Math.Abs(pixel.B - expected.B) < .13f, why + $" at={point} got={pixel} expected={expected}");
        }
        var stripes = Image.CreateEmpty(972, 870, false, Image.Format.Rgba8);
        var colors = new[] { new Color("d92e2e"), new Color("e5b91b"), new Color("2fa058"), new Color("2b69cf"), new Color("9e44be") };
        for (var i = 0; i < 5; i++) stripes.FillRect(new Rect2I(i * 972 / 5, 0, (i + 1) * 972 / 5 - i * 972 / 5, 870), colors[i]);
        var texture = ImageTexture.CreateFromImage(stripes);
        var layer = new CanvasLayer { Layer = 35 }; AddChild(layer);
        var background = new ColorRect { Color = new Color("20251f") }; layer.AddChild(background); background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var faces = new List<CardFace>();
        var x = 35f;
        foreach (var height in new[] { 150f, 320f, 701f })
        {
            var f = new CardFace { Position = new(x, 50) }; f.FitTo(height); layer.AddChild(f);
            f.SetDefinition(new() { CardId = "diy/alignment", ArtPath = "diyart://probe.png", Name = "插画定位", CardType = "unit", Cost = 3, BaseAttack = 2, BaseDefense = 4 });
            f.SetIllustration(texture); faces.Add(f); x += f.Size.X + 42;
        }
        var shot = await Capture("01-scales");
        foreach (var f in faces)
        {
            var ratio = f.Size.Y / 701;
            for (var i = 0; i < 5; i++)
                Pixel(shot, f.GlobalPosition + (new Vector2(7, 94) + new Vector2(486 * (i + .5f) / 5, 435 * .4f)) * ratio,
                    colors[i], "Imported art does not cover its slot at every card size");
        }
        foreach (var zoom in new[] { 50, 150 })
        {
            foreach (var f in faces) f.SetIllustration(texture, zoom);
            shot = await Capture("02-zoom-" + zoom);
            foreach (var f in faces)
            {
                var point = f.GlobalPosition + (new Vector2(7, 94) + new Vector2(486 * .1f, 435 * .4f)) * (f.Size.Y / 701);
                Pixel(shot, point, zoom == 50 ? new Color("e6e1d3") : colors[1], "Zoom does not match across preview sizes");
            }
        }
        // A printed card is cropped before compositing: its old title/rules must not reappear in the illustration.
        var printed = Image.CreateEmpty(500, 701, false, Image.Format.Rgba8); printed.Fill(Colors.Red);
        printed.FillRect(new(13, 100, 474, 364), colors[2]);
        var printedFace = faces[^1];
        printedFace.SetDefinition(new() { CardId = "printed", ArtPath = "res://printed-probe.png", Name = "整卡裁切", CardType = "order" });
        printedFace.SetIllustration(ImageTexture.CreateFromImage(printed));
        shot = await Capture("03-printed-source");
        foreach (var offset in new[] { new Vector2(40, 130), new Vector2(460, 470) })
            Pixel(shot, printedFace.GlobalPosition + offset, colors[2], "Printed card decorations leaked into the illustration");
        Check(CardFace.ArtSlotFor(true) != CardFace.CurrentArtSlot, "Order and unit art slots were conflated");
        var types = new[] { "unit", "order", "counter" };
        for (var i = 0; i < faces.Count; i++)
        {
            var f = faces[i]; f.Position = new(45 + i * 480, 75); f.FitTo(580);
            f.SetDefinition(new() { CardId = "diy/master-" + types[i], CardType = types[i], UnitType = "infantry", ArtPath = "diyart://probe.png",
                Name = CardFace.MasterName(CardFace.MasterFor(types[i])) + "母版", Cost = 3, BaseOpCost = types[i] == "unit" ? 2 : null, BaseAttack = 2, BaseDefense = 4, Text = "按卡牌类别自动切换母版" });
            f.SetIllustration(texture);
            Check(f.ActiveMaster == CardFace.MasterFor(types[i]), "Rebinding a face failed to switch master");
        }
        shot = await Capture("03a-three-masters");
        var differences = 0;
        for (var py = 456; py < 496; py += 2)
            for (var px = 230; px < 270; px += 2)
            {
                var points = new[] { faces[1], faces[2] }.Select(f => (f.GlobalPosition + new Vector2(px, py) * (f.Size.Y / 701))
                    * new Vector2(shot.GetWidth(), shot.GetHeight()) / GetViewportRect().Size).ToArray();
                var a = shot.GetPixel((int)points[0].X, (int)points[0].Y); var b = shot.GetPixel((int)points[1].X, (int)points[1].Y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > .12f) differences++;
            }
        Check(differences > 20, "Order and counter still display the same PSD type emblem");
        var sample = _catalog.Cards.First(c => c.ArtPath.StartsWith("res://", StringComparison.Ordinal));
        for (var i = 0; i < faces.Count; i++)
        {
            faces[i].SetDefinition(new() { CardId = "diy/master-" + types[i], CardType = types[i], UnitType = "infantry", ArtPath = sample.ArtPath,
                Name = CardFace.MasterName(CardFace.MasterFor(types[i])) + "母版", Cost = 3, BaseOpCost = types[i] == "unit" ? 2 : null, BaseAttack = 2, BaseDefense = 4, Text = "按卡牌类别自动切换母版" });
            faces[i].SetIllustration(_textures.Get(sample.ArtPath));
        }
        await Capture("03b-three-masters-art");
        layer.QueueFree(); await Settle();

        Show("editor"); await MenuSettledAsync();
        var pack = new DiyPackDraft { Name = "卡图对齐验收" }; _workshopLibrary.Save(pack); RefreshDiy(); _workshop.OpenPack(pack);
        _diyEditor.Load(new DiyCardDraft { PackId = pack.Id, Name = "插画对齐测试", Cost = 0 });
        var sourceFile = directory + "/stripes.png"; stripes.SavePng(sourceFile);
        _diyEditor.OnArtImported(sourceFile); await Settle();
        await Capture("04-editor-imported");
        var editorFace = _diyEditor.FindChildren("*", "", true, false).OfType<CardFace>().Single(f => f.IsVisibleInTree());
        shot = await Capture("04a-editor-art-pixels");
        Pixel(shot, editorFace.GlobalPosition + new Vector2(250, 220) * (editorFace.Size.Y / 701), colors[2], "Editor failed to load diyart through TextureCache");
        _diyEditor.ArtZoom.Value = 150; await Settle();
        await DiyClickAsync(_diyEditor.SaveButton);
        var saved = _diy.Cards.Single(c => c.Id == _diyEditor.Draft.Id);
        _diyEditor.Load(saved); await Settle();
        Check(_diyEditor.ArtZoom.Value == 150, "Per-card art zoom did not survive reload");
        await DeckClickAsync(_diyEditor.AlignButton); await Settle();
        var tuner = _diyEditor.FindChildren("*", "", true, false).OfType<CardFaceTuner>().Single();
        tuner.StoragePath = directory + "/layout.json";
        var handle = tuner.FindChild("ArtSlotHandle", true, false) as Control ?? throw new Exception("No art handle");
        var toolFace = tuner.FindChildren("*", "", true, false).OfType<CardFace>().Single();
        var expected = new Rect2(toolFace.GlobalPosition + CardFace.DefaultArtSlot.Position * (toolFace.Size.Y / 701), CardFace.DefaultArtSlot.Size * (toolFace.Size.Y / 701));
        Check(handle.GetGlobalRect().Position.DistanceTo(expected.Position) < 1 && handle.Size.DistanceTo(expected.Size) < 1, "Green frame is not aligned on opening");
        Check(GetViewportRect().Encloses(toolFace.GetGlobalRect()), "Tuner card overflows viewport");
        await Capture("05-tuner-initial");
        var start = handle.GlobalPosition + handle.Size / 2;
        var ratioTool = toolFace.Size.Y / 701;
        var delta = new Vector2(24, 18) * ratioTool;
        GetViewport().PushInput(new InputEventMouseMotion { Position = start, GlobalPosition = start }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = start, GlobalPosition = start, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseMotion { Position = start + delta, GlobalPosition = start + delta, Relative = delta, ButtonMask = MouseButtonMask.Left }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = start + delta, GlobalPosition = start + delta, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Settle();
        Check(handle.GlobalPosition.DistanceTo(expected.Position + delta) < 1.5f, "Drag uses a different coordinate system from the art slot");
        var spins = tuner.FindChildren("*", "SpinBox", true, false).OfType<SpinBox>().ToArray();
        Check(spins[0].Value == 31 && spins[1].Value == 112, "Dragged slot did not reach numeric controls");
        GetViewport().PushInput(new InputEventMouseButton { Position = start + delta, GlobalPosition = start + delta, ButtonIndex = MouseButton.WheelUp, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = start + delta, GlobalPosition = start + delta, ButtonIndex = MouseButton.WheelUp, Pressed = false }, true); await Settle();
        Check(spins[4].Value == 105, "Wheel zoom was overwritten by stale numeric state");
        Check(CardFace.CurrentArtSlot == CardFace.DefaultArtSlot && CardFace.CurrentZoom == 100, "Unsaved tuning escaped its local preview");
        await Capture("06-tuner-adjusted");
        var buttons = tuner.FindChildren("*", "Button", true, false).OfType<Button>().ToArray();
        await DeckClickAsync(buttons.Single(b => b.Text == "关闭"));
        Check(!tuner.Visible, "Close button did not hide tuner");
        await DeckClickAsync(_diyEditor.AlignButton); await Settle();
        Check(tuner.Visible, "Tuner did not reopen");
        Check(spins[0].Value == 7 && spins[4].Value == 100, $"Closing failed to discard unsaved tuning: x={spins[0].Value} zoom={spins[4].Value}");
        spins[4].Value = 125;
        await DeckClickAsync(buttons.Single(b => b.Text == "保存模板对齐"));
        Check(CardFace.CurrentZoom == 125 && File.Exists(tuner.StoragePath), "Template save did not apply");
        CardFace.ClearArtOverride(); CardFaceTuner.LoadSaved(tuner.StoragePath);
        Check(CardFace.CurrentZoom == 125, "Saved template zoom did not survive reload");
        await DeckClickAsync(buttons.Single(b => b.Text == "关闭"));
        await Capture("07-editor-saved-template");
        CardFace.ClearArtOverride();
        // The selector must follow native CardType metadata, including saved original counters.
        var originalCounter = _catalog.Cards.First(c => c.CardType == "counter");
        var counterDraft = BuiltInWorkshopCatalog.Edit(originalCounter, Kards.Ui.Core.DeckCardPacks.RawOf(originalCounter.CardId), RuntimeGameData.Directory("nations"));
        _diyEditor.Load(counterDraft); await Settle();
        Check(DiyEditorScreen.Value(_diyEditor.CardType) == "counter", "Loading a counter changed its card type");
        for (var i = 0; i < types.Length; i++)
        {
            var index = Enumerable.Range(0, _diyEditor.CardType.ItemCount).Single(n => _diyEditor.CardType.GetItemMetadata(n).AsString() == types[i]);
            _diyEditor.CardType.Select(index); _diyEditor.CardType.EmitSignal(OptionButton.SignalName.ItemSelected, index); await Settle();
            var activeFace = _diyEditor.FindChildren("*", "", true, false).OfType<CardFace>().Single(f => f.IsVisibleInTree());
            Check(activeFace.ActiveMaster == CardFace.MasterFor(types[i]) && _diyEditor.Draft.CardType == types[i], "Editor selector and master diverged");
        }
        await Capture("09a-editor-counter");
        await DeckClickAsync(_diyEditor.SaveButton); await Settle();
        var storedCounter = _diy.Cards.Single(c => c.Id == counterDraft.Id);
        Check(storedCounter.CardType == "counter", "Saving a counter silently coerced its type");
        _diyEditor.Load(storedCounter); await Settle();
        await DeckClickAsync(_diyEditor.AlignButton); await Settle();
        Check(spins[1].Value == CardFace.DefaultArtSlotFor(CardFaceMaster.Counter).Position.Y, "Counter tuner used unit coordinates");
        spins[4].Value = 160;
        await DeckClickAsync(buttons.Single(b => b.Text == "保存模板对齐"));
        Check(CardFace.ZoomFor(CardFaceMaster.Counter) == 160 && CardFace.ZoomFor(true) == 100 && CardFace.CurrentZoom == 100, "Counter tuning overwrote another master");
        CardFace.ClearArtOverride(); CardFaceTuner.LoadSaved(tuner.StoragePath);
        Check(CardFace.ZoomFor(CardFaceMaster.Counter) == 160 && CardFace.ZoomFor(true) == 100, "Counter layout did not persist independently");
        await Capture("09b-tuner-counter");
        await DeckClickAsync(buttons.Single(b => b.Text == "关闭"));
        CardFace.ClearArtOverride();
        var builtIn = _catalog.Cards.First(c => c.CardType == "order" && c.ArtPath.StartsWith("res://", StringComparison.Ordinal) && !c.CardId.StartsWith("diy/", StringComparison.Ordinal));
        _diyEditor.Load(BuiltInWorkshopCatalog.Edit(builtIn, Kards.Ui.Core.DeckCardPacks.RawOf(builtIn.CardId), RuntimeGameData.Directory("nations"))); await Settle();
        await Capture("08-editor-built-in");
        await DeckClickAsync(_diyEditor.AlignButton); await Settle();
        Check(spins[1].Value == CardFace.DefaultArtSlotFor(true).Position.Y, "Order tuner opened with unit art coordinates");
        spins[4].Value = 140;
        await DeckClickAsync(buttons.Single(b => b.Text == "保存模板对齐"));
        Check(CardFace.ZoomFor(true) == 140 && CardFace.CurrentZoom == 100, "Order tuning changed the unit template");
        CardFace.ClearArtOverride(); CardFaceTuner.LoadSaved(tuner.StoragePath);
        Check(CardFace.ZoomFor(true) == 140 && CardFace.CurrentZoom == 100, "Independent template layouts did not survive reload");
        await DeckClickAsync(buttons.Single(b => b.Text == "恢复模板默认"));
        Check(spins[4].Value == 100 && CardFace.ZoomFor(true) == 140, "Reset escaped preview before saving");
        await Capture("09-tuner-order");
        await DeckClickAsync(buttons.Single(b => b.Text == "关闭"));
        CardFace.ClearArtOverride();
        var legacy = directory + "/legacy-layout.json";
        File.WriteAllText(legacy, "{\"x\":11,\"y\":98,\"w\":480,\"h\":430,\"zoom\":135}");
        CardFaceTuner.LoadSaved(legacy);
        Check(CardFace.CurrentZoom == 135 && CardFace.CurrentArtSlot.Position == new Vector2(11, 98)
            && CardFace.ZoomFor(true) == 100, "Legacy template migration changed the order layout");
        File.WriteAllText(directory + "/invalid-layout.json", "{\"x\":0,\"y\":0,\"w\":-1,\"h\":0,\"zoom\":999}");
        CardFaceTuner.LoadSaved(directory + "/invalid-layout.json");
        Check(CardFace.CurrentZoom == 135, "Invalid layout overwrote the previous valid layout");
        CardFace.ClearArtOverride();
        GD.Print("CARDFACE_VERIFY_OK pixel-alignment sizes=150,320,701 zoom=50,100,150 printed-crop imported-editor save-reload drag wheel cancel automatic-unit-order-counter distinct-PSD-emblems independent-layouts legacy-layout invalid-layout viewport=" + GetWindow().Size);
    }
}


