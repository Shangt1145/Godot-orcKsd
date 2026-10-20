using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>在模板坐标中调整卡图槽；预览局部生效，保存后才提交。</summary>
public sealed partial class CardFaceTuner : Control
{
    public const string LayoutPath = "user://cardface-layout.json";
    internal string StoragePath { get; set; } = LayoutPath;
    private Rect2 _slot;
    private int _zoom;
    private float _stageRatio = 1;
    private CardFace _face = null!;
    private Control _stage = null!, _cardArea = null!, _handle = null!, _resizeGrip = null!;
    private Label _readout = null!;
    private SpinBox _x = null!, _y = null!, _w = null!, _h = null!, _zoomBox = null!;
    private bool _dragging, _resizing, _syncing;
    private Vector2 _pointerStart;
    private Rect2 _slotStart;
    private UiCardDefinition _definition = new() { CardId = "preview", Name = "卡图对齐" };
    private Texture2D? _art;
    private int _cardZoom = 100;
    private CardFaceMaster _master;
    public event Action? Saved;

    public override void _Ready()
    {
        Theme = MenuStyles.CreateTheme();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var shade = new ColorRect { Color = new Color(0, 0, 0, .86f), MouseFilter = MouseFilterEnum.Stop };
        AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = new MarginContainer(); AddChild(margin);
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride("margin_" + edge, 28);
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 28); margin.AddChild(row);
        _cardArea = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(_cardArea);
        _stage = new Control { MouseFilter = MouseFilterEnum.Ignore }; _cardArea.AddChild(_stage);
        _face = new CardFace(); _stage.AddChild(_face);
        _handle = new Control { Name = "ArtSlotHandle", MouseFilter = MouseFilterEnum.Stop };
        _stage.AddChild(_handle);
        _handle.Draw += () => _handle.DrawRect(new Rect2(Vector2.Zero, _handle.Size), new Color(.4f, 1f, .5f), false, 2);
        _handle.GuiInput += e => HandleInput(e, _handle, resize: false);
        _resizeGrip = new ColorRect { Color = new Color(.4f, 1f, .5f), Size = new(16, 16), MouseFilter = MouseFilterEnum.Stop };
        _handle.AddChild(_resizeGrip);
        _resizeGrip.GuiInput += e => HandleInput(e, _resizeGrip, resize: true);
        var panelScroll = new ScrollContainer { CustomMinimumSize = new(350, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        row.AddChild(panelScroll);
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; panelScroll.AddChild(panel);
        BuildPanel(panel);
        _cardArea.Resized += LayoutStage;
        Open(_definition, _art);
    }

    public void Open(UiCardDefinition definition, Texture2D? art)
    {
        _definition = definition; _art = art;
        _master = CardFace.MasterFor(definition.CardType);
        _slot = CardFace.ArtSlotFor(_master); _zoom = CardFace.ZoomFor(_master);
        _cardZoom = definition.ArtPath.StartsWith("diyart://", StringComparison.Ordinal) ? DiyArtStore.ZoomOf(definition.CardId) : 100;
        _dragging = _resizing = false;
        if (_face is null) return;
        _face.SetDefinition(definition);
        _face.SetIllustration(art, _cardZoom);
        UpdatePreview();
    }

    private void BuildPanel(PanelContainer panel)
    {
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 10); panel.AddChild(box);
        box.AddChild(MenuStyles.Label("卡图对齐", 23, MenuStyles.Gold));
        var hint = MenuStyles.Label("拖动绿框移动插画槽，拖右下角调整宽高。\n滚轮调整插画缩放；保存后更新模板。", 13, MenuStyles.Dim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(hint);
        var grid = new GridContainer { Columns = 2 }; box.AddChild(grid);
        _x = Spin(-200, 600); _y = Spin(-200, 800); _w = Spin(20, 800); _h = Spin(20, 800); _zoomBox = Spin(10, 300);
        foreach (var (name, control) in new[] { ("X", _x), ("Y", _y), ("宽", _w), ("高", _h), ("模板缩放 %", _zoomBox) })
        { grid.AddChild(MenuStyles.Label(name, 13, MenuStyles.Ink)); grid.AddChild(control); }
        _readout = MenuStyles.Label("", 12, MenuStyles.Dim); _readout.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(_readout);
        box.AddChild(MenuStyles.Button("保存模板对齐", Save));
        box.AddChild(MenuStyles.Button("恢复模板默认", () => { _slot = CardFace.DefaultArtSlotFor(_master); _zoom = 100; UpdatePreview(); }));
        box.AddChild(MenuStyles.Button("关闭", () => { Visible = false; _dragging = _resizing = false; }));
        var scope = MenuStyles.Label("这里只调整模板。当前卡牌的插画缩放仍由「卡图」页保存。未保存的模板调整在关闭后丢弃。", 12, MenuStyles.Dim);
        scope.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(scope);
    }

    private SpinBox Spin(double min, double max)
    {
        var spin = new SpinBox { MinValue = min, MaxValue = max, Step = 1, CustomMinimumSize = new(130, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        spin.ValueChanged += _ =>
        {
            if (_syncing) return;
            _slot = new Rect2((float)_x.Value, (float)_y.Value, (float)_w.Value, (float)_h.Value);
            _zoom = (int)_zoomBox.Value;
            UpdatePreview();
        };
        return spin;
    }

    private void LayoutStage()
    {
        if (_cardArea.Size.X <= 0 || _cardArea.Size.Y <= 0) return;
        _stageRatio = Mathf.Min(.95f, Mathf.Min(_cardArea.Size.X / CardFace.Design.X, _cardArea.Size.Y / CardFace.Design.Y));
        _stage.Size = CardFace.Design * _stageRatio;
        _stage.Position = (_cardArea.Size - _stage.Size) / 2;
        _face.Size = _stage.Size;
        _handle.Position = _slot.Position * _stageRatio;
        _handle.Size = _slot.Size * _stageRatio;
        _resizeGrip.Position = _handle.Size - _resizeGrip.Size;
        _handle.QueueRedraw();
    }

    private void UpdatePreview()
    {
        _syncing = true;
        _x.Value = _slot.Position.X; _y.Value = _slot.Position.Y;
        _w.Value = _slot.Size.X; _h.Value = _slot.Size.Y; _zoomBox.Value = _zoom;
        _syncing = false;
        _readout.Text = $"{CardFace.MasterName(_master)}模板 ({_slot.Position.X:0},{_slot.Position.Y:0}) · {_slot.Size.X:0}×{_slot.Size.Y:0}\n模板缩放 {_zoom}% · 当前卡牌缩放 {_cardZoom}%";
        _face.PreviewArtLayout(_slot, _zoom);
        LayoutStage();
    }

    private void HandleInput(InputEvent e, Control source, bool resize)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
        { ChangeZoom(wheel); return; }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _dragging = button.Pressed && !resize; _resizing = button.Pressed && resize;
            _pointerStart = PointerInStage(source, button.Position); _slotStart = _slot;
        }
        if (e is InputEventMouseMotion motion && (_dragging || _resizing))
        {
            var delta = PointerInStage(source, motion.Position) - _pointerStart;
            _slot = _resizing
                ? new Rect2(_slotStart.Position, (_slotStart.Size + delta).Clamp(new Vector2(20, 20), new Vector2(800, 800)))
                : new Rect2((_slotStart.Position + delta).Clamp(new Vector2(-200, -200), new Vector2(600, 800)), _slotStart.Size);
            UpdatePreview();
        }
        AcceptEvent();
    }

    private Vector2 PointerInStage(Control source, Vector2 local)
        => (_stage.GetGlobalTransformWithCanvas().AffineInverse() * (source.GetGlobalTransformWithCanvas() * local)) / _stageRatio;

    private void ChangeZoom(InputEventMouseButton wheel)
    {
        if (wheel.Pressed)
        {
            _zoom = Mathf.Clamp(_zoom + (wheel.ButtonIndex == MouseButton.WheelUp ? 5 : -5), 10, 300);
            UpdatePreview();
        }
        AcceptEvent();
    }
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel) ChangeZoom(wheel);
    }

    private void Save()
    {
        Godot.Collections.Dictionary Entry(CardFaceMaster master)
        {
            var rect = master == _master ? _slot : CardFace.ArtSlotFor(master);
            return new() { ["x"] = rect.Position.X, ["y"] = rect.Position.Y,
                ["w"] = rect.Size.X, ["h"] = rect.Size.Y, ["zoom"] = master == _master ? _zoom : CardFace.ZoomFor(master) };
        }
        var json = new Godot.Collections.Dictionary { ["version"] = 3, ["unit"] = Entry(CardFaceMaster.Unit), ["order"] = Entry(CardFaceMaster.Order), ["counter"] = Entry(CardFaceMaster.Counter) };
        using var file = Godot.FileAccess.Open(StoragePath, Godot.FileAccess.ModeFlags.Write);
        if (file is null) { _readout.Text = "保存失败：" + Godot.FileAccess.GetOpenError(); return; }
        file.StoreString(Json.Stringify(json, "\t")); file.Flush();
        CardFace.ApplyArtOverride(_slot, _zoom, _master);
        Saved?.Invoke();
        _readout.Text = CardFace.MasterName(_master) + "模板对齐已保存。";
    }

    public static void LoadSaved(string path = LayoutPath)
    {
        if (!Godot.FileAccess.FileExists(path)) return;
        try
        {
            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
            if (file is null) return;
            var parsed = Json.ParseString(file.GetAsText());
            if (parsed.VariantType != Variant.Type.Dictionary) return;
            var root = parsed.AsGodotDictionary();
            void Read(Godot.Collections.Dictionary d, CardFaceMaster master)
            {
                if (new[] { "x", "y", "w", "h", "zoom" }.Any(k => !d.ContainsKey(k))) return;
                var values = new[] { "x", "y", "w", "h", "zoom" }.Select(k => (float)d[k].AsDouble()).ToArray();
                if (values.Any(v => !float.IsFinite(v)) || values[2] < 20 || values[2] > 800 || values[3] < 20 || values[3] > 800
                    || values[0] < -200 || values[0] > 600 || values[1] < -200 || values[1] > 800 || values[4] < 10 || values[4] > 300) return;
                CardFace.ApplyArtOverride(new(values[0], values[1], values[2], values[3]), (int)values[4], master);
            }
            if (root.ContainsKey("unit") || root.ContainsKey("order") || root.ContainsKey("counter"))
            {
                foreach (var type in new[] { "unit", "order", "counter" })
                    if (root.TryGetValue(type, out var value) && value.VariantType == Variant.Type.Dictionary)
                        Read(value.AsGodotDictionary(), CardFace.MasterFor(type));
            }
            else Read(root, CardFaceMaster.Unit); // 兼容旧文件；旧版只包含单位模板坐标。
        }
        catch (Exception e) { GD.PushWarning($"卡图对齐参数读取失败：{e.Message}"); }
    }
}


