using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>One complete imported card with live numerical overlays only.</summary>
public partial class AlternateCardFace : Control
{
    public Texture2D? ArtOverride { get; set; }
    public CardSkin? Skin { get; set; }
    public UiCardDefinition? Definition { get; set; }
    public UiCardView? View { get; set; }
    public TextureCache Textures { get; set; } = null!;
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; Resized += QueueRedraw; }
    public override void _Draw()
    {
        if (Skin is { } skin && Definition is { } definition) Paint(this, skin, definition, View, Textures, artOverride: ArtOverride);
    }

    public static void Paint(Control canvas, CardSkin skin, UiCardDefinition definition, UiCardView? view, TextureCache textures, bool compact = false, Texture2D? artOverride = null)
    {
        var size = canvas.Size;
        Rect2 Box(SkinRect b) => new(b.X * size.X, b.Y * size.Y, b.Width * size.X, b.Height * size.Y);
        var ink = new Color(skin.Ink); var paper = new Color(skin.Paper);
        var font = canvas.GetThemeDefaultFont();
        void Text(string value, SkinRect region, float proportion, bool multiline = false)
        {
            var rect = Box(region);
            var fontSize = Math.Max(7, (int)(size.X * proportion));
            if (!multiline)
                while (fontSize > 6 && (font.GetStringSize(value, fontSize: fontSize).X > rect.Size.X - 4 || font.GetHeight(fontSize) > rect.Size.Y)) fontSize--;
            canvas.DrawRect(rect, paper);
            using var paragraph = new TextParagraph();
            paragraph.Width = Math.Max(1, rect.Size.X - 4);
            paragraph.Alignment = multiline ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            paragraph.AddString(value, font, fontSize);
            paragraph.MaxLinesVisible = Math.Max(1, (int)(rect.Size.Y / Math.Max(1, font.GetHeight(fontSize))));
            paragraph.Draw(canvas.GetCanvasItem(), rect.Position + new Vector2(2, Math.Max(0, (rect.Size.Y - paragraph.GetSize().Y) / 2)), ink);
        }
        // Complete-card import: preserve the entire bitmap; never crop or reconstruct its frame/text.
        if ((artOverride ?? textures.Get(skin.Art)) is { } art)
            canvas.DrawTextureRect(art, new(Vector2.Zero, size), false);
        Text((view?.EffectiveCost ?? definition.Cost)?.ToString() ?? "—", skin.CostBox, .10f);
        if (definition.CardType == "unit")
        {
            Text((view?.EffectiveAttack ?? definition.BaseAttack)?.ToString() ?? "—", skin.AttackBox, .10f);
            Text((view?.Health ?? view?.EffectiveDefense ?? definition.BaseDefense)?.ToString() ?? "—", skin.DefenseBox, .10f);
            Text((view?.EffectiveOpCost ?? definition.BaseOpCost)?.ToString() ?? "—", skin.OpBox, .065f);
        }
        if (view?.IsSuppressed == true) canvas.DrawRect(new(Vector2.Zero, size), new Color(.12f, .14f, .12f, .35f));
        if (compact && view is { } status)
        {
            var flags = new List<string>();
            if (status.IsVeteran) flags.Add("★");
            if (status.IsCovert) flags.Add("隐蔽");
            if (status.IsSilenced) flags.Add("抑制");
            if (status.IsSuppressed) flags.Add("压制");
            if (status.Keywords.Count > 0) flags.Add("◆");
            if (flags.Count > 0) Text(string.Join(" ", flags), new(.08f, .17f, .84f, .10f), .06f);
        }
    }
}
