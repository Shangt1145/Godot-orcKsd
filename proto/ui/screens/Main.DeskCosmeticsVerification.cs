using System.Text;
using Godot;
using Kards.Ui.Core;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyDeskCosmetics()
    {
        var local = AppearanceRuntime.LocalProfile; var quiet = _clock.ReducedMotion; var tilted = _clock.TiltedBattleView;
        try
        {
            _clock.SetReducedMotion(false);
            var directory = ProjectSettings.GlobalizePath("res://artifacts/desk-cosmetics"); Directory.CreateDirectory(directory);
            var image = Image.CreateEmpty(512, 512, false, Image.Format.Rgba8); image.Fill(new("204f59"));
            for (var i = 0; i < 512; i += 64)
            {
                image.FillRect(new(i, 0, 3, 512), new("759a89")); image.FillRect(new(0, i, 512, 3), new("759a89"));
            }
            var cloth = _cosmetics.Import("tablecloth", "验证·自定义桌布", image.SavePngToBuffer());
            var glb = AnimatedDeskFixture(); var hash = _cosmetics.CacheModel(glb);
            var capture = new DeskPropPreview { Position = new(-2048, 0), Size = new(512, 512) }; capture.Initialize(); GetTree().Root.AddChild(capture);
            CosmeticAsset model;
            try
            {
                capture.Bind("", new(Model: hash));
                if (!capture.HasProp || capture.Animations.Length == 0) throw new Exception("Animated GLB import failed");
                model = _cosmetics.ImportModel("验证·动画桌饰", glb, await capture.ThumbnailAsync());
            }
            finally { capture.QueueFree(); }
            var deck = _decks.ActiveDeck ?? throw new Exception("Fixture deck missing");
            OpenAppearance(deck);
            await _appearanceScreen.VerifyDeskEditorAsync(cloth, model);
            await ShotAsync(directory, "desk-editor");
            var library = new CosmeticLibrary(Path.GetDirectoryName(_cosmetics.ImagePath(cloth.Image))! + "/..");
            var profile = library.Resolve(deck.Id, deck.Options.MainNation);
            if (profile.Tablecloth != cloth.Image || profile.Prop.Model != hash) throw new Exception("Desk equipment did not survive reload");
            AppearanceRuntime.LocalProfile = () => profile;
            Show("battle");
            await _battle.VerifyDeskAppearanceAsync(directory, model, () => profile, p => profile = p);
            GD.Print("DESK_COSMETICS_VERIFY_OK tablecloth-import model-import animations thumbnail editor equip restart switch click flat tilted remove");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError("DESK_COSMETICS_VERIFY_FAIL " + error); GetTree().Quit(1); }
        finally { AppearanceRuntime.LocalProfile = local; _clock.SetReducedMotion(quiet); _clock.SetTiltedBattleView(tilted); }
    }

    private static byte[] AnimatedDeskFixture()
    {
        const string json = """
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"CustomAnimatedBox","mesh":0}],
         "buffers":[{"byteLength":200}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":96,"target":34962},{"buffer":0,"byteOffset":96,"byteLength":72,"target":34963},{"buffer":0,"byteOffset":168,"byteLength":8},{"buffer":0,"byteOffset":176,"byteLength":24}],
         "accessors":[{"bufferView":0,"componentType":5126,"count":8,"type":"VEC3","min":[-0.5,0,-0.5],"max":[0.5,1,0.5]},{"bufferView":1,"componentType":5123,"count":36,"type":"SCALAR"},{"bufferView":2,"componentType":5126,"count":2,"type":"SCALAR","min":[0],"max":[0.8]},{"bufferView":3,"componentType":5126,"count":2,"type":"VEC3"}],
         "materials":[{"pbrMetallicRoughness":{"baseColorFactor":[0.15,0.65,0.8,1],"metallicFactor":0.2,"roughnessFactor":0.5},"doubleSided":true}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1,"material":0}]}],
         "animations":[{"name":"ClickLift","samplers":[{"input":2,"output":3}],"channels":[{"sampler":0,"target":{"node":0,"path":"translation"}}]}]}
        """;
        var text = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67); writer.Write(2); writer.Write(28 + text.Length + 200); writer.Write(text.Length); writer.Write(0x4E4F534A); writer.Write(text);
        writer.Write(200); writer.Write(0x004E4942);
        foreach (var value in new float[] { -.5f,0,-.5f, .5f,0,-.5f, .5f,1,-.5f, -.5f,1,-.5f, -.5f,0,.5f, .5f,0,.5f, .5f,1,.5f, -.5f,1,.5f }) writer.Write(value);
        foreach (var index in new ushort[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 }) writer.Write(index);
        foreach (var value in new float[] { 0,.8f, 0,0,0, 0,.5f,0 }) writer.Write(value);
        return stream.ToArray();
    }
}
