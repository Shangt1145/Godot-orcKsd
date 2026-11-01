using Godot;

namespace Kards.Ui;

/// <summary>
/// 积木配色。**跟着 MenuStyles 走**，不是 Scratch 原版色板。
/// <para>
/// Scratch 那套（#4c97ff／#ffab19／#ffbf00）是给浅色儿童界面配的高饱和色，放在本项目这套
/// 军绿＋黄铜＋羊皮纸的深色牌面上会炸眼。所以只借它的**形状**（缺口／凸榫／C 形／贴合内容），
/// 颜色换成同一色系的低饱和土色：暗砖红、暗青、暗橄榄、黄铜……全部压到能衬住羊皮纸字的亮度。
/// </para>
/// </summary>
public static class BlockTheme
{
    // ── 分组色：按意图分，色相拉开但饱和度压在 25~40%，亮度都在羊皮纸字之下 ──
    // 「造成与消灭」与「控制流」是两组最常出现的块，必须一眼分得开：
    // 前者压成偏冷的深红（血色），后者取主题主按钮那一族的烧赭并提亮一档（结构性块该更亮）。
    public static readonly Color Combat = new("6f2f28");       // 造成与消灭：深血红
    public static readonly Color Cards = new("2f5a52");        // 手牌与牌堆：暗青
    public static readonly Color Attributes = new("4d4066");   // 属性与词条：暗紫
    public static readonly Color Battlefield = new("4f5c36");  // 战场位置：暗橄榄
    public static readonly Color Resources = new("6b5c22");    // 资源：黄铜（偏黄绿）
    public static readonly Color Interaction = new("38536a");  // 交互：钢蓝
    public static readonly Color Control = new("95561f");      // 控制流：烧赭（Scratch 里对应橙）
    public static readonly Color Variables = new("5e3a48");    // 变量：暗赭梅
    public static readonly Color Flow = new("3c4850");         // 流程：石板灰
    // 查询：青碧。刻意离开动作节点的全部色相（红/赭/蓝/青绿/紫/橄榄/黄铜/梅），
    // 因为 unitsOf 不是动作——它不改变世界，只是问一句（SPEC §12.10）。
    public static readonly Color Query = new("2a6068");
    public static readonly Color Advanced = new("3a403c");     // 高级：中性

    /// <summary>帽子块。用主题里唯一的高光色（黄铜），一眼看出「这段逻辑挂在哪」。</summary>
    public static readonly Color Hat = new("c8a35c");

    /// <summary>校验失败的块。要比分组色更扎眼，但仍然留在土色系里。</summary>
    public static readonly Color Error = new("a8402f");

    // ── 输入芯片：做旧纸张。深色块上必须比块亮才看得见，纯白又太跳，羊皮纸正好 ──
    public static readonly Color ChipFill = new("d6cfba");
    public static readonly Color ChipHover = new("e6e0cd");
    public static readonly Color ChipPressed = new("c2baa4");
    public static readonly Color ChipLine = new("a89f86");
    public static readonly Color ChipInk = new("2a251b");
    public static readonly Color ChipMuted = new("6f6952");

    public static Color ForGroup(string group) => group switch
    {
        "造成与消灭" => Combat,
        "属性与词条" => Attributes,
        "手牌与牌堆" => Cards,
        "战场位置" => Battlefield,
        "资源" => Resources,
        "交互" => Interaction,
        "查询" => Query,
        "控制流" => Control,
        "变量" => Variables,
        "流程" => Flow,
        "高级" => Advanced,
        _ => Advanced,
    };

    public static float Luma(Color c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;

    /// <summary>深色块上用羊皮纸字，浅色块（只有帽子）上用近黑褐字——不是纯白配纯黑，跟主题走。</summary>
    public static Color InkFor(Color fill) => Luma(fill) > 0.55f ? new Color("2a2318") : MenuStyles.Ink;

    /// <summary>块内次级文字（op 名、可选标记）：同色系压低亮度，不用灰色，避免发灰。</summary>
    public static Color MutedInkFor(Color fill) =>
        new(InkFor(fill), Luma(fill) > 0.55f ? 0.62f : 0.66f);

    public static Color Shade(Color fill, float amount) =>
        new(fill.R * (1 - amount), fill.G * (1 - amount), fill.B * (1 - amount));
}

/// <summary>
/// 一块积木：圆角实心 + 上缘缺口 + 下缘凸榫。
/// <para>
/// 缺口只画在**自身矩形内**（<c>y: 0..NotchDepth</c>）。这一点必须守住：一旦把缺口画到 y&lt;0，
/// 它就会盖住上一块的凸榫，两块之间的接口会变成一团深色方块而不是榫卯。
/// </para>
/// <para>
/// 凸榫从底边往外探 <see cref="TabOverhang"/>，列表间距与之相等时，凸榫正好填满缝、下沿顶到
/// 下一块的缺口上。这是纯视觉，不承载任何语义（执行顺序仍然由数组位置决定）。
/// </para>
/// </summary>
public partial class BlockShape : Control
{
    private Color _fill = BlockTheme.Advanced;
    private Control? _body;

    public Color Fill
    {
        get => _fill;
        set { _fill = value; QueueRedraw(); }
    }

    /// <summary>缺口的颜色必须等于画布底色，否则会露出一块方形的补丁。</summary>
    public Color NotchColor { get; set; } = MenuStyles.Bg;

    public bool ShowNotch { get; set; } = true;
    public bool ShowTab { get; set; } = true;
    public int TopRadius { get; set; } = 5;
    public int BottomRadius { get; set; } = 5;

    /// <summary>缺口左边缘的 x（避开文字左内边距）。</summary>
    public int NotchX { get; set; } = 12;
    public int NotchWidth { get; set; } = 22;
    /// <summary>缺口咬进块内的深度。</summary>
    public int NotchDepth { get; set; } = 5;
    public int NotchRadius { get; set; } = 3;
    /// <summary>凸榫探出底边的长度。等于列表间距时榫卯严丝合缝。</summary>
    public int TabOverhang { get; set; } = 4;

    public void SetBody(Control body)
    {
        _body = body;
        AddChild(body);
        body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        UpdateMinimumSize();
        QueueRedraw();
    }

    public override Vector2 _GetMinimumSize() =>
        _body?.GetCombinedMinimumSize() ?? new Vector2(80, 28);

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }

    public override void _Draw()
    {
        // 一圈极淡的描边把块从同样偏暗的底上托起来——土色系亮度都低，不描边会糊在一起。
        var body = new StyleBoxFlat
        {
            BgColor = _fill,
            BorderColor = new Color(0, 0, 0, 0.35f),
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = TopRadius,
            CornerRadiusTopRight = TopRadius,
            CornerRadiusBottomLeft = BottomRadius,
            CornerRadiusBottomRight = BottomRadius,
            AntiAliasing = true,
        };
        DrawStyleBox(body, new Rect2(Vector2.Zero, Size));

        if (ShowNotch)
        {
            var notch = new StyleBoxFlat
            {
                BgColor = NotchColor,
                CornerRadiusBottomLeft = NotchRadius,
                CornerRadiusBottomRight = NotchRadius,
                AntiAliasing = true,
            };
            DrawStyleBox(notch, new Rect2(NotchX, 0, NotchWidth, NotchDepth));
        }

        if (ShowTab)
        {
            var tab = new StyleBoxFlat
            {
                BgColor = _fill,
                CornerRadiusBottomLeft = NotchRadius,
                CornerRadiusBottomRight = NotchRadius,
                AntiAliasing = true,
            };
            DrawStyleBox(tab, new Rect2(NotchX, Size.Y - 2, NotchWidth, TabOverhang + 2));
        }
    }
}
