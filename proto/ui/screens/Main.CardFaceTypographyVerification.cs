using Godot;
using System.Text.Json;

namespace Kards.Ui;

public partial class Main
{
    private async Task VerifyCardTypographyAsync(string directory)
    {
        using var data = JsonDocument.Parse(Godot.FileAccess.GetFileAsString("res://proto/assets/cardface/astral/typography.json"));
        var document = data.RootElement.GetProperty("masters");
        void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
        Rect2I Ink(Image image, Rect2I region)
        {
            var left = region.End.X; var top = region.End.Y; var right = region.Position.X; var bottom = region.Position.Y;
            for (var y = region.Position.Y; y < region.End.Y; y++)
                for (var x = region.Position.X; x < region.End.X; x++)
                    if (image.GetPixel(x, y).A > .2f) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1); }
            return right > left && bottom > top ? new(left, top, right - left, bottom - top) : new();
        }
        var results = new List<object>();
        foreach (var kind in new[] { "unit", "order", "counter" })
        {
            var unit = kind == "unit"; var styles = document.GetProperty(unit ? "unit" : "order");
            var viewport = new SubViewport { Size = new(500, 701), TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            AddChild(viewport);
            var typography = new CardFaceTypography { Size = CardFace.Design }; viewport.AddChild(typography);
            var card = new UiCardDefinition { CardId = "diy/typography-" + kind, CardType = kind,
                Name = styles.GetProperty(unit ? "name" : "namePlain").GetProperty("sample").GetString()!,
                Cost = unit ? 3 : 1, BaseOpCost = unit ? 2 : null, BaseAttack = unit ? 4 : null, BaseDefense = unit ? 4 : null,
                Keywords = unit ? ["fury"] : [], Text = styles.GetProperty(unit ? "body" : "bodyPlain").GetProperty("sample").GetString()! };
            typography.SetCard(card);
            async Task<Image> Snapshot(string suffix)
            {
                for (var i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var image = viewport.GetTexture().GetImage(); image.SavePng(directory + "/type-" + kind + suffix + ".png"); return image;
            }
            var shot = await Snapshot("-reference");
            var cost = typography.Metrics.Single(m => m.Field == "cost");
            Check(cost.FontPath.EndsWith("Roboto-Black.ttf") && cost.FontSize == 76, "Cost is not using the PSD Roboto Black 76px style");
            Check(typography.Metrics.Single(m => m.Field == "body").FontPath.EndsWith("SourceHanSansCN-Normal.otf"), "Body uses the wrong PSD font weight");
            var fields = unit ? new[] { "name", "cost", "opCost", "attack", "defense", "keyword", "body" } : new[] { "name", "cost", "body" };
            foreach (var field in fields)
            {
                var source = styles.GetProperty(field == "name" && !unit ? "namePlain" : field == "body" && !unit ? "bodyPlain" : field);
                var box = source.GetProperty("bbox").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                // Separate neighboring glyph fields while checking their actual rendered ink bounds.
                var roi = field switch
                {
                    "cost" => new Rect2I(10, 15, 54, 75), "opCost" => new Rect2I(63, 50, 25, 40),
                    "name" => unit ? new Rect2I(95, 15, 325, 77) : new Rect2I(29, 500, 442, 55),
                    "attack" => new Rect2I(88, 480, 85, 69), "defense" => new Rect2I(330, 480, 85, 69),
                    "keyword" => new Rect2I(29, 550, 442, 37), "body" => unit ? new Rect2I(29, 587, 442, 79) : new Rect2I(29, 558, 442, 108), _ => throw new Exception(field)
                };
                var actual = Ink(shot, roi); var expected = new Rect2I(box[0], box[1], box[2] - box[0], box[3] - box[1]);
                results.Add(new { kind, field, actual = new[] { actual.Position.X, actual.Position.Y, actual.Size.X, actual.Size.Y }, expected = new[] { expected.Position.X, expected.Position.Y, expected.Size.X, expected.Size.Y } });
                Check(actual.Size.X > 0 && actual.Size.Y > 0, $"Missing template glyphs: {kind}/{field}");
                Check(Math.Abs(actual.Position.Y - expected.Position.Y) <= 3 && Math.Abs(actual.End.Y - expected.End.Y) <= 3,
                    $"PSD baseline mismatch: {kind}/{field} actual={actual} expected={expected}");
                Check(Math.Abs(actual.Position.X - expected.Position.X) <= 6 && Math.Abs(actual.End.X - expected.End.X) <= 6,
                    $"PSD horizontal alignment mismatch: {kind}/{field} actual={actual} expected={expected}");
            }
            if (!unit)
            {
                typography.SetCard(card with { Name = "间谍", Keywords = ["情报"], KeywordValues = new Dictionary<string, int> { ["情报"] = 3 },
                    Text = styles.GetProperty("body").GetProperty("sample").GetString()! });
                await Snapshot("-keywords");
                Check(typography.Metrics.Single(m => m.Field == "name").FontSize == 46
                    && Math.Abs(typography.Metrics.Single(m => m.Field == "body").Baseline.Y - 621.4372f) < .01f,
                    "Keyword layout did not switch to the PSD text variant");
            }
            typography.SetCard(card with { Cost = 24, BaseOpCost = unit ? 9 : null, BaseAttack = unit ? 64 : null, BaseDefense = unit ? 24 : null });
            await Snapshot("-double");
            Check(typography.Metrics.Single(m => m.Field == "cost").FontSize == 51 && typography.Metrics.Single(m => m.Field == "k").FontSize == 21, "Two-digit costs did not switch PSD typography");
            if (unit) Check(typography.Metrics.Any(m => m.Field == "opCost"), "Operation cost 9 disappeared");
            typography.SetCard(card with { Name = "测试超长卡牌名称用于验证文字不会跑出模板边界以及覆盖费用区域", Cost = null, BaseAttack = null, BaseDefense = null, BaseOpCost = null, Keywords = [], Text = "" });
            shot = await Snapshot("-long-name");
            var nameInk = Ink(shot, new(0, 0, 500, 701));
            Check(nameInk.Position.X >= (unit ? 95 : 25) && nameInk.End.X <= (unit ? 420 : 477), "Long title escaped the template name area");
            typography.SetCard(card with { Name = "", Cost = null, BaseAttack = null, BaseDefense = null, BaseOpCost = null, Text = string.Concat(Enumerable.Repeat("部署时，随机对一个敌方单位造成2点伤害。", 50)) });
            shot = await Snapshot("-long-body");
            var bodyInk = Ink(shot, new(0, 0, 500, 701));
            Check(bodyInk.Position.X >= 25 && bodyInk.End.X <= 477 && bodyInk.End.Y <= 667, "Long body escaped the template text guide");
            Check(typography.Metrics.Single(m => m.Field == "body").Truncated, "Long body did not retain bounded overflow state");
            viewport.QueueFree();
        }
        File.WriteAllText(directory + "/typography-ink.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("ASTRAL_TYPOGRAPHY_VERIFY_OK PSD-fonts-baselines ink-bounds unit-order-counter single-double long-name long-body");
    }
}
