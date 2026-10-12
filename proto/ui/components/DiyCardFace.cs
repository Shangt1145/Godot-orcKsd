using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>Draws a custom definition using only the illustration from an existing printed card.</summary>
public partial class DiyCardFace : Control
{
    public UiCardDefinition Definition { get; set; } = null!;
    public UiCardView? View { get; set; }
    public Texture2D? IllustrationSource { get; set; }
    public override void _Draw() => Paint(this, Definition, View, IllustrationSource);

    internal static void Paint(Control canvas, UiCardDefinition definition, UiCardView? view, Texture2D? texture)
    {
        var size = canvas.Size; var font = canvas.GetThemeDefaultFont();
        var paper = new Color("d4ccb2"); var ink = new Color("34392e");
        var nation = definition.Set switch { "av76" => new Color("825747"), "USG" => new("506783"), "UN" => new("4f645b"), _ => new("686053") };
        Rect2 Box(float x, float y, float w, float h) => new(size.X * x, size.Y * y, size.X * w, size.Y * h);
        void Text(string text, Rect2 box, int point, Color color, bool centered = true)
        {
            var width = font.GetStringSize(text, fontSize: point).X;
            canvas.DrawString(font, new(box.Position.X + (centered ? Math.Max(0, (box.Size.X - width) / 2) : 0), box.Position.Y + point),
                text, HorizontalAlignment.Left, box.Size.X, point, color);
        }
        var number = (int)Math.Clamp(size.X * .115f, 11, 32);
        var body = (int)Math.Clamp(size.X * .060f, 8, 18);
        canvas.DrawRect(Box(.025f, .025f, .95f, .95f), paper);
        canvas.DrawRect(Box(.03f, .03f, .94f, .105f), nation);
        canvas.DrawRect(Box(.03f, .03f, .15f, .105f), ink);
        Text((view?.EffectiveCost ?? definition.Cost)?.ToString() ?? "—", Box(.03f, .03f, .15f, .105f), number, paper);
        Text(definition.Name, Box(.20f, .045f, .69f, .08f), body + 1, paper, false);
        var art = Box(.03f, .145f, .94f, .49f);
        if (texture is not null)
        {
            var original = texture.GetSize();
            canvas.DrawTextureRectRegion(texture, art, new(original.X * .027f, original.Y * .143f, original.X * .946f, original.Y * .518f));
        }
        else { canvas.DrawRect(art, nation.Darkened(.15f)); Text("DIY", Box(.1f, .31f, .8f, .12f), number, paper); }
        if (definition.CardType == "unit")
        {
            canvas.DrawRect(Box(.05f, .65f, .20f, .09f), ink); canvas.DrawRect(Box(.75f, .65f, .20f, .09f), ink);
            Text((view?.EffectiveAttack ?? definition.BaseAttack)?.ToString() ?? "—", Box(.05f, .65f, .20f, .09f), number, paper);
            Text((view?.Health ?? definition.BaseDefense)?.ToString() ?? "—", Box(.75f, .65f, .20f, .09f), number, paper);
            var unitType = UnitTypeLabel(definition.UnitType);
            Text($"{unitType} · 行动 {view?.EffectiveOpCost ?? definition.BaseOpCost}", Box(.27f, .667f, .46f, .08f), body, ink);
        }
        else Text("指令", Box(.1f, .65f, .8f, .08f), body + 1, ink);
        var keywords = view?.Keywords ?? definition.Keywords;
        var values = view?.KeywordValues ?? definition.KeywordValues;
        var rule = string.Join(" · ", keywords.Select(k => CardControl.KeywordLabel(k, values)));
        if (rule.Length > 0) rule += "\n";
        rule += definition.Text;
        var rules = Box(.07f, .77f, .86f, .19f); var line = ""; var y = rules.Position.Y + body;
        foreach (var ch in rule)
        {
            if (ch == '\n' || font.GetStringSize(line + ch, fontSize: body).X > rules.Size.X)
            {
                canvas.DrawString(font, new(rules.Position.X, y), line, fontSize: body, modulate: ink);
                line = ""; y += body + 2;
                if (y > rules.End.Y) return;
            }
            if (ch != '\n') line += ch;
        }
        if (line.Length > 0) canvas.DrawString(font, new(rules.Position.X, y), line, fontSize: body, modulate: ink);
    }
    private static string UnitTypeLabel(string type) => type switch
    { "infantry" => "步兵", "tank" => "坦克", "artillery" => "火炮", "fighter" => "战斗机", "bomber" => "轰炸机", _ => type };
}
