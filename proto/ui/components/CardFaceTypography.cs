using Godot;
using System.Text.Json;

namespace Kards.Ui;

internal sealed record AstralTypeStyle
{
    public string[] Fonts { get; init; } = [];
    public float Size { get; init; }
    public float[] Baseline { get; init; } = [];
    public float HorizontalScale { get; init; } = 1;
    public int Tracking { get; init; }
    public float Leading { get; init; }
    public float[] Color { get; init; } = [];
}
internal sealed record AstralTypeCatalog
{
    public Dictionary<string, Dictionary<string, AstralTypeStyle>> Masters { get; init; } = [];
}
internal sealed record AstralFieldMetrics(string Field, string FontPath, Vector2 Baseline, float FontSize,
    float Leading, float Width, int Lines, bool Truncated);

/// <summary>Draws PSD point text from extracted baselines, fonts and leading. No Label minimum-size drift.</summary>
public sealed partial class CardFaceTypography : Control
{
    public float NumericBaselineOffset { get; set; }
    private const string Root = "res://proto/assets/cardface/fonts/";
    private static readonly Dictionary<string, Font> Fonts = [];
    private static readonly Dictionary<(string Path, int Spacing), FontVariation> Variations = [];
    private static readonly Lazy<AstralTypeCatalog> Catalog = new(() => JsonSerializer.Deserialize<AstralTypeCatalog>(
        Godot.FileAccess.GetFileAsString("res://proto/assets/cardface/astral/typography.json"),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
    private sealed record Run(TextParagraph Paragraph, AstralTypeStyle Style, float Scale, float Leading, int VisibleLines, bool Numeric);
    private readonly List<Run> _runs = [];
    private readonly List<AstralFieldMetrics> _metrics = [];
    internal IReadOnlyList<AstralFieldMetrics> Metrics => _metrics;
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; ClipContents = true; }
    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }
    public override void _ExitTree() => ClearRuns();
    private void ClearRuns() { foreach (var run in _runs) run.Paragraph.Dispose(); _runs.Clear(); _metrics.Clear(); }

    public void SetCard(UiCardDefinition card)
    {
        ClearRuns();
        var unit = card.CardType == "unit";
        var styles = Catalog.Value.Masters[unit ? "unit" : "order"];
        var keywords = string.Join("  ", card.Keywords.Select(k => CardControl.KeywordLabel(k, new Dictionary<string, int>())
            .TrimStart('◆', '➜', '▣', '◈', '✦', '◇', '★', ' ') + (card.KeywordValues.TryGetValue(k, out var n) ? n.ToString() : "")));
        var hasKeywords = keywords.Length > 0;
        var twoDigitCost = card.Cost >= 10;
        if (card.Cost is not null)
        {
            Add("cost", card.Cost.Value.ToString(), styles[twoDigitCost ? "costDouble" : "cost"], 66);
            Add("k", "K", styles[twoDigitCost ? "kDouble" : "k"], 26);
        }
        if (unit && card.BaseOpCost is not null)
            Add("opCost", card.BaseOpCost.Value.ToString(), styles[twoDigitCost || card.BaseOpCost >= 10 ? "opCostDouble" : "opCost"], 25);
        Add("name", card.Name, styles[hasKeywords ? "name" : "namePlain"], unit ? 314 : 442);
        if (unit)
        {
            if (card.BaseAttack is not null) AddStat("attack", card.BaseAttack.Value, styles);
            if (card.BaseDefense is not null) AddStat("defense", card.BaseDefense.Value, styles);
        }
        if (hasKeywords) Add("keyword", keywords, styles["keyword"], 442);
        Add("body", card.Text, styles[hasKeywords ? "body" : "bodyPlain"], 442, body: true);
        QueueRedraw();
    }
    private void AddStat(string key, int value, Dictionary<string, AstralTypeStyle> styles)
    {
        var style = styles[key];
        if (value >= 10)
        {
            var compact = styles["defenseDouble"];
            style = compact with { Baseline = [style.Baseline[0], compact.Baseline[1]] };
        }
        Add(key, value.ToString(), style, 72);
    }
    private static string FontPath(string postscript) => Root + (postscript switch
    {
        "Roboto-Black" => "Roboto-Black.ttf", "SourceHanSansCN-Normal" => "SourceHanSansCN-Normal.otf",
        "SourceHanSansCN-Bold" => "SourceHanSansCN-Bold.otf", "FranklinGothic-DemiCond" => "FRADMCN.TTF",
        "FranklinGothic-MediumCond" => "FRAMDCN.TTF", _ => throw new InvalidDataException("Unknown PSD font: " + postscript)
    });
    private static Font FontFor(AstralTypeStyle style, float size)
    {
        var path = FontPath(style.Fonts[0]);
        if (!Fonts.TryGetValue(path, out var font))
        {
            font = ResourceLoader.Load<Font>(path) ?? throw new InvalidDataException("Missing template font: " + path);
            if (style.Fonts.Length > 1)
                font.Fallbacks = new Godot.Collections.Array<Font>(style.Fonts.Skip(1).Select(n => ResourceLoader.Load<Font>(FontPath(n))));
            Fonts[path] = font;
        }
        // Render at the original PSD text scale (2x design size), then apply its 0.5 transform.
        var spacing = Mathf.RoundToInt(size * 2 * style.Tracking / 1000);
        if (!Variations.TryGetValue((path, spacing), out var variation))
            Variations[(path, spacing)] = variation = new FontVariation { BaseFont = font, SpacingGlyph = spacing };
        return variation;
    }
    private static TextParagraph Shape(string text, Font font, float size, float width, bool wrap)
    {
        var p = new TextParagraph { Width = width, Alignment = HorizontalAlignment.Center,
            BreakFlags = TextServer.LineBreakFlag.Mandatory | (wrap ? TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.Adaptive : 0),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        p.AddString(text.Replace("\r\n", "\n").Replace('\r', '\n'), font, Mathf.RoundToInt(size * 2), "zh");
        return p;
    }
    private void Add(string field, string text, AstralTypeStyle style, float width, bool body = false)
    {
        if (text.Length == 0) return;
        float size = style.Size, fit = 1, leading = style.Leading;
        var font = FontFor(style, size);
        var p = Shape(text, font, size, width * 2 / style.HorizontalScale, body);
        int visible = 1, count;
        if (body)
        {
            while (true)
            {
                leading = style.Leading * size / style.Size;
                visible = Math.Max(1, (int)Math.Floor((666 - style.Baseline[1] - p.GetLineDescent(0) / 2) / leading) + 1);
                if (p.GetLineCount() <= visible || size <= 12) break;
                p.Dispose(); size = Math.Max(12, size - 1); font = FontFor(style, size);
                p = Shape(text, font, size, width * 2 / style.HorizontalScale, true);
            }
        }
        else
        {
            var measured = font.GetStringSize(text, fontSize: Mathf.RoundToInt(size * 2)).X * style.HorizontalScale / 2;
            fit = Mathf.Clamp(width / Math.Max(1, measured), .55f, 1);
            p.Dispose(); p = Shape(text, font, size, width * 2 / (style.HorizontalScale * fit), false);
        }
        count = p.GetLineCount(); p.MaxLinesVisible = visible;
        _runs.Add(new(p, style, fit, leading, Math.Min(count, visible), field is "cost" or "opCost" or "attack" or "defense"));
        _metrics.Add(new(field, FontPath(style.Fonts[0]), new(style.Baseline[0], style.Baseline[1]), size * fit,
            leading, width, Math.Min(count, visible), count > visible || (!body && p.GetLineWidth(0) * fit * style.HorizontalScale / 2 > width + 1)));
    }
    public override void _Draw()
    {
        var ratio = Mathf.Min(Size.X / 500, Size.Y / 701);
        if (ratio <= 0) return;
        var origin = (Size - CardFace.Design * ratio) / 2;
        foreach (var r in _runs)
        {
            var color = new Color(r.Style.Color[0], r.Style.Color[1], r.Style.Color[2], r.Style.Color[3]);
            for (var i = 0; i < r.VisibleLines; i++)
            {
                var at = new Vector2(r.Style.Baseline[0], r.Style.Baseline[1] + i * r.Leading + (r.Numeric ? NumericBaselineOffset : 0));
                DrawSetTransform(origin + at * ratio, 0, new Vector2(ratio * r.Scale * r.Style.HorizontalScale / 2, ratio * r.Scale / 2));
                var server = TextServerManager.GetPrimaryInterface();
                var line = r.Paragraph.GetLineRid(i);
                server.ShapedTextDraw(line, GetCanvasItem(), new Vector2(-(float)server.ShapedTextGetWidth(line) / 2, 0), -1, -1, color);
            }
        }
        DrawSetTransform(Vector2.Zero);
    }
}
