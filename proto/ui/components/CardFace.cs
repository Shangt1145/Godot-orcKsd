using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public enum CardFaceMaster { Unit, Order, Counter }

/// <summary>
/// 真实卡面（AstralKards 模板，500×701 设计尺寸）。卡框是模板导出的真图层，
/// 其余元素按 PSD 图层 bbox 摆放：卡图 (7,94,486,435)｜部署费用 (22,24)-(61,80)｜
/// 行动费用 (65,56)-(80,79)｜卡名 (109,37)-(405,70)｜攻击 (114,493)-(146,535)｜
/// 防御 (355,491)-(387,533)｜词条 (221,556)-(280,585)｜效果描述 (108,588)-(397,611)。
/// <para>
/// 卡框、插画、文字及裁切区域统一从设计坐标换算；纹理原始尺寸不参与控件的最小尺寸。
/// 指令模板使用顶部插画、下方卡名的独立布局。
/// </para>
/// </summary>
public sealed partial class CardFace : Control
{
    /// <summary>设计尺寸（模板画布）。</summary>
    public static readonly Vector2 Design = new(500, 701);

    private const string MasterRoot = "res://proto/assets/cardface/astral/";
    public static CardFaceMaster MasterFor(string cardType) => cardType switch
    { "order" => CardFaceMaster.Order, "counter" => CardFaceMaster.Counter, _ => CardFaceMaster.Unit };
    public CardFaceMaster ActiveMaster => MasterFor(_definition?.CardType ?? "unit");
    public static string MasterName(CardFaceMaster master) => master switch
    { CardFaceMaster.Order => "指令", CardFaceMaster.Counter => "反制", _ => "单位" };
    public static string MasterKey(CardFaceMaster master) => master.ToString().ToLowerInvariant();

    private static readonly Rect2 ArtRect = new(7, 94, 486, 435);
    private static readonly Rect2 OrderArtRect = new(7, 17, 486, 471);

    /// <summary>对齐工具覆盖的卡图槽（null＝用模板常量）。</summary>
    private static Rect2? _artOverride;
    private static int _zoomOverride = 100;
    private static Rect2? _orderArtOverride;
    private static int _orderZoomOverride = 100;
    private static Rect2? _counterArtOverride;
    private static int _counterZoomOverride = 100;

    /// <summary>当前生效的卡图槽（对齐工具保存后由这里读回）。</summary>
    public static Rect2 CurrentArtSlot => _artOverride ?? ArtRect;
    public static Rect2 DefaultArtSlot => ArtRect;
    public static Rect2 ArtSlotFor(bool order) => ArtSlotFor(order ? CardFaceMaster.Order : CardFaceMaster.Unit);
    public static Rect2 ArtSlotFor(CardFaceMaster master) => master switch
    { CardFaceMaster.Order => _orderArtOverride ?? OrderArtRect, CardFaceMaster.Counter => _counterArtOverride ?? OrderArtRect, _ => CurrentArtSlot };
    public static Rect2 DefaultArtSlotFor(bool order) => order ? OrderArtRect : ArtRect;
    public static Rect2 DefaultArtSlotFor(CardFaceMaster master) => master == CardFaceMaster.Unit ? ArtRect : OrderArtRect;
    public static int ZoomFor(bool order) => ZoomFor(order ? CardFaceMaster.Order : CardFaceMaster.Unit);
    public static int ZoomFor(CardFaceMaster master) => master switch
    { CardFaceMaster.Order => _orderZoomOverride, CardFaceMaster.Counter => _counterZoomOverride, _ => CurrentZoom };
    private static event Action? ArtLayoutChanged;
    /// <summary>当前生效的卡图缩放百分比。</summary>
    public static int CurrentZoom => _zoomOverride;

    /// <summary>应用对齐工具给出的卡图槽与缩放（不落盘，落盘由工具自己做）。</summary>
    public static void ApplyArtOverride(Rect2 art, int zoom, bool order = false)
        => ApplyArtOverride(art, zoom, order ? CardFaceMaster.Order : CardFaceMaster.Unit);
    public static void ApplyArtOverride(Rect2 art, int zoom, CardFaceMaster master)
    {
        if (master == CardFaceMaster.Counter) { _counterArtOverride = art; _counterZoomOverride = zoom; }
        else if (master == CardFaceMaster.Order) { _orderArtOverride = art; _orderZoomOverride = zoom; }
        else { _artOverride = art; _zoomOverride = zoom; }
        ArtLayoutChanged?.Invoke();
    }

    /// <summary>清除覆盖，回到模板常量。</summary>
    public static void ClearArtOverride()
    {
        _artOverride = null;
        _zoomOverride = 100;
        _orderArtOverride = null;
        _orderZoomOverride = 100;
        _counterArtOverride = null;
        _counterZoomOverride = 100;
        ArtLayoutChanged?.Invoke();
    }

    private TextureRect _frame = null!;
    private Control _art = null!;
    private TextureRect _artInner = null!;
    private ColorRect _artBackdrop = null!;
    private ColorRect _unitTitleBackdrop = null!;
    private TextureRect _unitBadge = null!;
    private CardFaceTypography _typography = null!;

    private UiCardDefinition? _definition;
    private Texture2D? _illustration;
    private int _illustrationZoom = 100;
    private bool _explicitIllustration;
    private Rect2? _previewSlot;
    private int _previewZoom = 100;
    private float _ratio;
    private Vector2 _origin;
    private bool IsOrder => ActiveMaster != CardFaceMaster.Unit;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        ArtLayoutChanged += RefreshLayout;

        _frame = new TextureRect { Position = Vector2.Zero, Size = Design,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_frame);
        // 卡图区：外层裁剪到 (7,94,486,435)，内层纹理按缩放比例居中——和模板的插画槽一致。
        // 先垫一层纸色：没插画的卡不能露出深色空洞。
        _art = new Control { ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_art);
        _artBackdrop = new ColorRect { Color = new Color("e6e1d3"), MouseFilter = MouseFilterEnum.Ignore };
        _art.AddChild(_artBackdrop);
        _artInner = new TextureRect { StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore };
        _art.AddChild(_artInner);

        // 插画在原始边框下方；保留 PSD 的费用板、类型徽章、攻防盾形和透明孔位。
        MoveChild(_frame, GetChildCount() - 1);
        _unitTitleBackdrop = new ColorRect { Color = new Color("30332d"), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_unitTitleBackdrop);
        _unitBadge = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_unitBadge);

        _typography = new CardFaceTypography(); AddChild(_typography);

        Layout();
        if (_definition is not null) Apply(_definition);
    }

    public override void _ExitTree() => ArtLayoutChanged -= RefreshLayout;

    /// <summary>工具里的临时布局不会改写其它卡面；只有保存才提交全局设置。</summary>
    public void PreviewArtLayout(Rect2 slot, int zoom)
    {
        _previewSlot = slot;
        _previewZoom = zoom;
        RefreshLayout();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) Layout();
    }

    /// <summary>按目标高度布局（宽度按设计比例）。传 0 或负值＝沿用当前尺寸。</summary>
    public void FitTo(float targetHeight)
    {
        if (targetHeight <= 0) return;
        CustomMinimumSize = new Vector2(Design.X * targetHeight / Design.Y, targetHeight);
        Size = CustomMinimumSize;
        Layout();
    }

    /// <summary>绑定卡面数据（卡名/费用/攻防/词条/效果文本/卡图）。</summary>
    public void SetDefinition(UiCardDefinition definition)
    {
        _definition = definition;
        if (_typography is null) return; // 还没 _Ready
        Apply(definition);
    }

    /// <summary>布局参数变化后重排（对齐工具用）。</summary>
    public void RefreshLayout()
    {
        Layout();
        if (_definition is not null) Apply(_definition);
    }

    private void Layout()
    {
        if (_frame is null) return;
        if (Size.X <= 1 || Size.Y <= 1) return;
        var ratio = Mathf.Min(Size.X / Design.X, Size.Y / Design.Y);
        _ratio = ratio;
        _origin = (Size - Design * ratio) / 2;

        _frame.Position = _origin;
        _frame.Size = Design * ratio;
        Place(_art, _previewSlot ?? ArtSlotFor(ActiveMaster), ratio);
        Place(_unitTitleBackdrop, new Rect2(97, 15, 391, 75), ratio);
        Place(_unitBadge, new Rect2(Vector2.Zero, Design), ratio);

        Place(_typography, new Rect2(Vector2.Zero, Design), ratio);

        LayoutArt();
    }

    /// <summary>卡图区里的插画：按 zoomPercent 缩放并居中，超出部分由卡图槽裁掉。</summary>
    public void SetIllustration(Texture2D? texture, int zoomPercent = 100)
    {
        _illustration = texture;
        _explicitIllustration = true;
        _illustrationZoom = zoomPercent;
        if (_artInner is null) return;
        _artInner.Texture = IllustrationTexture(texture);
        LayoutArt();
    }

    /// <summary>按卡宽布局（高度按设计比例）。</summary>
    public void FitToWidth(float width)
    {
        if (width <= 0) return;
        FitTo(width * Design.Y / Design.X);
    }

    private void LayoutArt()
    {
        if (_artInner is null) return;
        if (_ratio <= 0) return;
        var slot = (_previewSlot ?? ArtSlotFor(ActiveMaster)).Size * _ratio;
        var zoom = Mathf.Clamp(_illustrationZoom, 50, 300) / 100f
            * (_previewSlot.HasValue ? _previewZoom : ZoomFor(ActiveMaster)) / 100f;
        _artBackdrop.Position = Vector2.Zero;
        _artBackdrop.Size = slot;
        _artInner.Size = slot * zoom;
        _artInner.Position = (slot - _artInner.Size) / 2f;
    }

    private void Place(Control node, Rect2 design, float ratio)
    {
        node.Position = _origin + design.Position * ratio;
        node.Size = design.Size * ratio;
    }


    private void Apply(UiCardDefinition d)
    {
        var order = IsOrder;
        var framePath = MasterRoot + MasterKey(ActiveMaster) + "-master.png";
        _frame.Texture = ResourceLoader.Exists(framePath) ? ResourceLoader.Load<Texture2D>(framePath) : null;
        // 插画优先用外部显式给的（DIY 卡走 DiyArtStore），否则回退到定义里的美术路径。
        if (!_explicitIllustration)
            _illustration = !string.IsNullOrEmpty(d.ArtPath) && ResourceLoader.Exists(d.ArtPath)
                ? ResourceLoader.Load<Texture2D>(d.ArtPath) : null;
        _artInner.Texture = IllustrationTexture(_illustration);
        LayoutArt();

        _unitTitleBackdrop.Visible = !order;
        _unitBadge.Visible = !order;
        var badgePath = MasterRoot + "unit-" + (d.UnitType.Length > 0 ? d.UnitType : "infantry") + ".png";
        _unitBadge.Texture = !order && ResourceLoader.Exists(badgePath) ? ResourceLoader.Load<Texture2D>(badgePath) : null;
        _typography.SetCard(d);
        Layout();
    }

    private Texture2D? IllustrationTexture(Texture2D? source)
    {
        // 素材库保存的是整张旧卡面；仅取插画，避免旧卡名、费用和规则再被贴进新模板。
        // diyart:// 已在导入时裁过，不能在这里重复裁切。
        if (source is null || (_definition?.ArtPath.StartsWith("res://", StringComparison.Ordinal) != true
            && _definition?.ArtPath.StartsWith("packfull://", StringComparison.Ordinal) != true))
            return source;
        var size = source.GetSize();
        return new AtlasTexture { Atlas = source,
            Region = new Rect2(size * new Vector2(.027f, .143f), size * new Vector2(.946f, .49f)),
            FilterClip = true };
    }

}

