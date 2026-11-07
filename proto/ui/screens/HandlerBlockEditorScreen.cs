using System.Globalization;
using Godot;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

/// <summary>
/// 积木式 handler 编辑面（契约 v1），Scratch 的**形状**、本项目的**配色**。
///
/// <para>
/// 形状由契约决定：DSL 是树、数组顺序即执行顺序、节点没有 id。所以这里一列积木就是一段
/// <c>actions[]</c>，容器块（如果／逐个处理／重复）是 C 形块，子体嵌在它的开口里，不做自由连线——
/// 也就不存在「画得出、存不下」的图（SPEC §2.7）。
/// </para>
/// <para>
/// 边界（FRONTEND-GUIDE §2）：本屏**不生成 csx**，只产出 <c>.hdsl.json</c> 文本；
/// 校验在本地跑一遍，装载期后端再判（最终裁定）。
/// </para>
/// </summary>
public partial class HandlerBlockEditorScreen : VBoxContainer
{
    // 目录来自引擎扫描（`HandlerCatalog.Default`）；扫描不可用时自动退回本地镜像，
    // 并在工具箱头部照实说明——不能让作者以为这就是引擎的全部能力。
    private HandlerCatalog _catalog = HandlerCatalog.Default;
    private HandlerDocument _doc = HandlerDocument.Sample();
    private IReadOnlyDictionary<string, HandlerViewField> _view = DemoView();

    private readonly List<HandlerDocument> _history = [];
    private int _historyAt;

    private VBoxContainer _blocks = null!;
    private ScrollContainer _canvas = null!;
    private VBoxContainer _issues = null!;
    private TextEdit _json = null!;
    private Label _summary = null!, _issueCount = null!, _cardLabel = null!;
    private Button _undo = null!, _redo = null!;

    private PopupMenu _valueMenu = null!;
    private AcceptDialog _prompt = null!;
    private LineEdit _promptEdit = null!;
    private Action<string>? _promptAccept;

    private PanelContainer _toolbox = null!;
    private VBoxContainer _toolboxList = null!;
    private LineEdit _toolboxSearch = null!;
    private Label _toolboxTarget = null!;
    private Button _toolboxToggle = null!;
    private string _pendingLabel = "整段的最后";
    private Action<HandlerValue?>? _valueCommit;
    private HandlerValue? _valueCurrent;
    private List<ScopeVar> _valueScope = [];
    private List<HandlerValue> _readOptions = [];
    private List<string> _valueLiterals = [];
    private List<HandlerNode>? _pendingInsert;
    private bool _toolboxOpen = true;

    private HashSet<HandlerNode> _errorNodes = [];
    /// <summary>节点 → 它的那块积木。插入后要滚到它那儿，否则「点了没反应」（块落在折叠区外）。</summary>
    private readonly Dictionary<HandlerNode, Control> _nodeControls = [];

    /// <summary>保存出口（复杂 DIY）：返回 null＝当前没有打开的卡包编辑会话。</summary>
    private Func<HandlerDslAuthoring?>? _authoring;
    /// <summary>上一次保存的结果（跨 Rebuild 保留：重画会清空检查结果列，作者的反馈不能被顺手抹掉）。</summary>
    private string? _saveNote;
    private bool _saveFailed;

    public event Action? Finished;
    public event Action<string>? Changed;

    /// <summary>
    /// 作用域里的一个名字 ＋ 它的静态类型。界面只拿类型来**过滤取值菜单**
    /// （例如「读取 ‹t›.防御」只对 <c>entity</c> 出现），真正的判定在校验器里。
    /// </summary>
    private sealed record ScopeVar(string Name, string Type)
    {
        public bool IsEntity => Type == "entity";
        public bool IsEntityList => Type == "list<entity>";
    }

    public HandlerDocument Document => _doc;
    public HandlerCatalog Catalog => _catalog;

    /// <summary>上一次保存的结果文本（null＝这次打开后还没保存过）。探针与调试用。</summary>
    public string? SaveNote => _saveNote;

    /// <summary>
    /// 接上「保存到卡包」的出口。**给的是取值的函数而不是实例**：会话会结束、会换包，
    /// 缓存下来的作者面会往上一个包里写。
    /// </summary>
    public void ConfigureSave(Func<HandlerDslAuthoring?> authoring) => _authoring = authoring;

    private static IReadOnlyDictionary<string, HandlerViewField> DemoView() =>
        new Dictionary<string, HandlerViewField>(StringComparer.Ordinal)
        {
            ["Card"] = new("Card", "entity", false),
            ["Defense"] = new("Defense", "number", true),
            ["Units"] = new("Units", "list<entity>", false),
        };

    public void Initialize()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 10);
        AddChild(BuildHeader());
        AddChild(BuildBody());
        _pendingInsert = _doc.Actions;
        SetToolboxOpen(_toolboxOpen);
        _history.Add(_doc.Clone());
        Rebuild();
    }

    public void Open(string title, HandlerDocument? doc = null)
    {
        _doc = doc?.Clone() ?? HandlerDocument.Sample();
        _cardLabel.Text = title;
        // 上一份文档的「已保存」不能留给下一份：作者会以为这份也存过了。
        _saveNote = null;
        _saveFailed = false;
        // 没有打开的卡包＝没有保存目标。这件事必须**一进来就说**，不能等作者搭完点保存才告知——
        // 那时候他搭的那一堆只能丢。画布本身仍然可用（可以搭、可以检查），所以只是提示，不拦。
        if (_authoring?.Invoke() is null)
        {
            _saveNote = "当前没有打开的卡包：可以搭、可以检查，但保存不可用。"
                + "去 DIY 工坊 → 我的卡包 → 打开一个卡包，再从顶部「新版 Inspector · 积木编辑」进来。";
            _saveFailed = true;
        }
        _history.Clear();
        _historyAt = 0;
        _history.Add(_doc.Clone());
        _pendingInsert = _doc.Actions;
        _pendingLabel = "整段的最后";
        Rebuild();
    }

    // ── 骨架 ───────────────────────────────────────────────────────────────

    private Control BuildHeader()
    {
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);

        var title = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddChild(MenuStyles.Label("效果搭建", 26, MenuStyles.Ink));
        _cardLabel = MenuStyles.Label("样例：打出时", 13, MenuStyles.Dim);
        title.AddChild(_cardLabel);
        head.AddChild(title);

        _undo = MenuStyles.Button("撤销", Undo);
        _redo = MenuStyles.Button("重做", Redo);
        _toolboxToggle = MenuStyles.Button("工具箱 ▾", () => SetToolboxOpen(!_toolboxOpen));
        _toolboxToggle.TooltipText = "所有能用的积木都在左栏，点一块就插进去";
        head.AddChild(_toolboxToggle);
        head.AddChild(_undo);
        head.AddChild(_redo);
        var save = MenuStyles.Button("保存到卡包", OpenSaveDialog);
        save.TooltipText = "把这段积木写成包内的一个效果（DSL 文档 ＋ 草案 ＋ 摊平产物）";
        MenuStyles.Primary(save);
        head.AddChild(save);
        head.AddChild(MenuStyles.Button("完成", () => Finished?.Invoke()));
        return head;
    }

    private Control BuildBody()
    {
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);

        body.AddChild(BuildToolbox());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _canvas = scroll;
        _blocks = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _blocks.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_blocks);
        body.AddChild(scroll);
        body.AddChild(BuildSide());
        return body;
    }

    private Control BuildSide()
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(370, 0) };
        panel.AddThemeStyleboxOverride("panel", UiStyles.Box(UiStyles.Panel, UiStyles.Line, 10, 10));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        var head = new HBoxContainer();
        head.AddChild(MenuStyles.Label("生成的内容", 12, MenuStyles.Dim));
        _summary = MenuStyles.Label("", 12, MenuStyles.Dim);
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        head.AddChild(_summary);
        box.AddChild(head);

        _json = new TextEdit
        {
            Editable = false,
            CustomMinimumSize = new Vector2(0, 300),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _json.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_json);

        var check = new HBoxContainer();
        check.AddChild(MenuStyles.Label("检查结果", 12, MenuStyles.Dim));
        _issueCount = MenuStyles.Label("", 12, MenuStyles.Green);
        _issueCount.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _issueCount.HorizontalAlignment = HorizontalAlignment.Right;
        check.AddChild(_issueCount);
        box.AddChild(check);

        var issueScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 150),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _issues = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _issues.AddThemeConstantOverride("separation", 4);
        issueScroll.AddChild(_issues);
        box.AddChild(issueScroll);
        return panel;
    }

    // ── 工具箱 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 左栏工具箱：**引擎支持的全部积木**都在这儿（含 <c>choice</c>、<c>sequence</c>），
    /// 按用途分组、能搜、每块用自己的分组色——先认颜色，再读字。
    ///
    /// <para>
    /// 为什么不是弹出菜单：37 块塞进一个 popup 要滚三屏，还看不出分组边界；
    /// 常驻一栏能边看画布边挑，挑完自动插到「当前插入位置」，不用每块都先选位置。
    /// </para>
    /// <para>
    /// 为什么每行是带色脊的横条而不是小积木：这里要的是**可扫读的清单**，不是形状比喻
    /// ——形状语言留在画布上，工具箱只负责「有什么、是什么颜色」。
    /// </para>
    /// </summary>
    private Control BuildToolbox()
    {
        _toolbox = new PanelContainer { CustomMinimumSize = new Vector2(286, 0) };
        _toolbox.AddThemeStyleboxOverride("panel", UiStyles.Box(UiStyles.Panel, UiStyles.Line, 10, 10));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        _toolbox.AddChild(box);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        head.AddChild(MenuStyles.Label("工具箱", 13, MenuStyles.Gold));
        var count = MenuStyles.Label(_catalog.Nodes.Count + " 块", 11, MenuStyles.Dim);
        count.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.TooltipText = HandlerCatalog.DefaultIsFallback
            ? "引擎目录读不出来，这份清单来自本地镜像：" + HandlerCatalog.DefaultFallbackReason
            : "引擎当前支持的全部积木（目录版本 " + _catalog.Revision + "）";
        head.AddChild(count);
        box.AddChild(head);

        // 退回镜像时必须说出来：作者会以为「工具箱里没有＝引擎做不到」，而真实原因是目录没读出来。
        if (HandlerCatalog.DefaultIsFallback)
            box.AddChild(MenuStyles.Label("目录来自本地镜像（引擎目录不可用）", 11, new Color("e0705f")));

        box.AddChild(MenuStyles.Label("点一块，就插到「插入到」写的位置。", 11, MenuStyles.Dim));

        _toolboxSearch = new LineEdit
        {
            PlaceholderText = "搜：伤害、抽牌、如果…",
            ClearButtonEnabled = true,
            CustomMinimumSize = new Vector2(0, 30),
        };
        _toolboxSearch.AddThemeFontSizeOverride("font_size", 12);
        _toolboxSearch.TextChanged += _ => RefreshToolbox();
        box.AddChild(_toolboxSearch);

        var targetRow = new HBoxContainer();
        targetRow.AddThemeConstantOverride("separation", 4);
        targetRow.AddChild(MenuStyles.Label("插入到", 11, MenuStyles.Dim));
        _toolboxTarget = MenuStyles.Label("", 11, MenuStyles.Gold);
        _toolboxTarget.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _toolboxTarget.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toolboxTarget.TooltipText = "在画布里点任意「＋ 添加积木」，就能改插入位置";
        targetRow.AddChild(_toolboxTarget);
        box.AddChild(targetRow);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _toolboxList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _toolboxList.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(_toolboxList);
        box.AddChild(scroll);

        RefreshToolbox();
        return _toolbox;
    }

    private void SetToolboxOpen(bool open)
    {
        _toolboxOpen = open;
        if (_toolbox is not null) _toolbox.Visible = open;
        if (_toolboxToggle is not null) _toolboxToggle.Text = open ? "工具箱 ▾" : "工具箱 ▸";
        if (open) RefreshToolbox();
    }

    private void RefreshToolbox()
    {
        if (_toolboxList is null) return;
        foreach (var child in _toolboxList.GetChildren()) { _toolboxList.RemoveChild(child); child.QueueFree(); }

        _toolboxTarget.Text = _pendingLabel;

        var query = _toolboxSearch.Text.Trim();
        var groups = _catalog.Nodes
            .Where(spec => Matches(spec, query))
            .GroupBy(spec => spec.Group)
            .OrderBy(group => GroupRank(group.Key))
            .ToList();

        if (groups.Count == 0)
        {
            _toolboxList.AddChild(MenuStyles.Label("没有这样的积木，换个说法试试。", 11, MenuStyles.Dim));
            return;
        }

        foreach (var group in groups)
        {
            _toolboxList.AddChild(ToolboxGroupHeader(group.Key));
            // 组内：先 alpha、后 preview（OrderBy 是稳定排序，同档内保持目录顺序）。
            // 不把 preview 抽出来单列一张表——那样它会离开自己的分组，作者反而找不到。
            foreach (var spec in group.OrderBy(spec => spec.Stage))
                _toolboxList.AddChild(ToolboxRow(spec));
        }
    }

    private static bool Matches(HandlerNodeSpec spec, string query) =>
        query.Length == 0
        || spec.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || spec.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || spec.Group.Contains(query, StringComparison.OrdinalIgnoreCase)
        || HandlerVocabulary.GroupHint(spec.Group).Contains(query, StringComparison.OrdinalIgnoreCase);

    private static int GroupRank(string group)
    {
        var at = Array.IndexOf(HandlerVocabulary.GroupOrder, group);
        return at < 0 ? 99 : at;
    }

    private static Control ToolboxGroupHeader(string group)
    {
        // 组名在上、一句说明在下：一行塞不下（11 组里最长的那句要 11 个字），
        // 硬切会看成掉字，省略号又看不清——两行最省事，也最清楚。
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 0);
        column.AddChild(MenuStyles.Label(group, 12, MenuStyles.Ink));
        var hint = MenuStyles.Label(HandlerVocabulary.GroupHint(group), 10, MenuStyles.Dim);
        hint.ClipText = true;
        hint.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        hint.TooltipText = HandlerVocabulary.GroupHint(group);
        column.AddChild(hint);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(new ColorRect
        {
            Color = BlockTheme.ForGroup(group),
            CustomMinimumSize = new Vector2(10, 10),
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        });
        row.AddChild(column);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_top", 9);
        margin.AddThemeConstantOverride("margin_bottom", 3);
        margin.AddChild(row);
        return margin;
    }

    private Control ToolboxRow(HandlerNodeSpec spec)
    {
        var color = BlockTheme.ForGroup(spec.Group);
        // 预览态：颜色降饱和、加角标、tooltip 摆原因。**照常可点**——契约要求「允许先搭图，
        // 保存前再拦」，藏起来或点不动都会让作者以为这版没这个能力（FRONTEND §5.5）。
        var preview = spec.IsPreview;
        var row = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = preview
                ? spec.Name + "　还不能用：" + (spec.Problem ?? "")
                : spec.Name + "　" + PinSummary(spec),
        };
        row.AddThemeStyleboxOverride("panel", ToolboxRowStyle(color, false, preview));
        row.MouseEntered += () => row.AddThemeStyleboxOverride("panel", ToolboxRowStyle(color, true, preview));
        row.MouseExited += () => row.AddThemeStyleboxOverride("panel", ToolboxRowStyle(color, false, preview));
        row.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                Insert(spec.Name);
        };

        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 6);
        line.AddChild(MenuStyles.Label(spec.Title, 12,
            preview ? new Color(BlockTheme.InkFor(color), 0.55f) : BlockTheme.InkFor(color)));
        // 右侧优先标「还不能用」——它比「带子块」重要得多：一个影响能不能跑，一个只影响点几下。
        var tail = MenuStyles.Label(
            preview ? "还不能用" : spec.IsContainer ? "带子块" : "", 10,
            preview ? new Color(MenuStyles.Gold, 0.85f) : BlockTheme.MutedInkFor(color));
        tail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        tail.HorizontalAlignment = HorizontalAlignment.Right;
        tail.ClipText = true;
        line.AddChild(tail);
        row.AddChild(line);
        return row;
    }

    private static StyleBoxFlat ToolboxRowStyle(Color color, bool hover, bool preview = false) => new()
    {
        // 预览态整体压暗：alpha 降一档，读起来是「灰的」，不是「换了个颜色」。
        BgColor = hover ? new Color(color, preview ? 0.45f : 0.95f) : new Color(color, preview ? 0.22f : 0.62f),
        BorderColor = hover ? MenuStyles.Gold : new Color(color.Lightened(0.35f), preview ? 0.45f : 0.85f),
        // 左边那道色脊是分组色的「标签」，一行扫过去就能认出这组是什么颜色。
        BorderWidthLeft = 3,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        CornerRadiusTopLeft = 3,
        CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 3,
        CornerRadiusBottomRight = 6,
        ContentMarginLeft = 7,
        ContentMarginRight = 8,
        ContentMarginTop = 5,
        ContentMarginBottom = 5,
        AntiAliasing = true,
    };

    private static string PinSummary(HandlerNodeSpec spec)
    {
        if (spec.Inputs.Count == 0) return "不用填";
        var required = spec.Inputs.Count(pin => pin.Required);
        return required > 0 ? "要填 " + required + " 格" : "都能留空";
    }

    /// <summary>
    /// 把一块积木插到「当前插入位置」的末尾。**插入位置不重置**：
    /// 连点几块就是从上往下的执行顺序，这正是作者想要的（数组顺序即执行顺序）。
    /// </summary>
    private void Insert(string op)
    {
        var target = _pendingInsert ??= _doc.Actions;
        var node = NewNode(op);
        Edit(() => target.Add(node));
        // 新块可能落在折叠区外，不滚过去就像「点了没反应」。
        // 要等**两帧**：第一帧容器才按新块的高度重排，这一帧拿到的是旧矩形，滚了也没用。
        if (_nodeControls.TryGetValue(node, out var control)) Reveal(control);
    }

    private void Reveal(Control control) => Callable.From(() => Callable.From(() =>
    {
        if (IsInstanceValid(_canvas) && IsInstanceValid(control)) _canvas.EnsureControlVisible(control);
    }).CallDeferred()).CallDeferred();

    // ── 渲染 ───────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        var issues = HandlerDslValidator.Validate(_doc, _catalog, _view);
        _errorNodes = issues.Where(i => i.Node is not null).Select(i => i.Node!).ToHashSet();

        foreach (var child in _blocks.GetChildren()) { _blocks.RemoveChild(child); child.QueueFree(); }
        _nodeControls.Clear();

        // Scratch 的「帽子」：说明这段逻辑挂在哪。触发时机属于预制体成员，DSL 里不写。
        _blocks.AddChild(Hat("这个效果被触发时，从上往下做"));
        _blocks.AddChild(RenderArray(_doc.Actions, "actions", [], 0, "整段的最后"));

        foreach (var child in _issues.GetChildren()) { _issues.RemoveChild(child); child.QueueFree(); }

        // 上一次保存的结果排在最前：作者点完「保存」后看到的第一句话应该是「存到哪儿了／为什么没存成」，
        // 而不是被一堆校验条目顶下去。
        if (_saveNote is not null)
        {
            var note = MenuStyles.Label(_saveNote, 12, _saveFailed ? new Color("e0705f") : UiStyles.Green);
            note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            note.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _issues.AddChild(note);
        }

        // 预览态提示（SPEC §12.11／FRONTEND §5.5）：**不是**校验错误——13 个错误码是封闭的，
        // 这里不加第 14 个。它是一条「能存但跑不起来」的警告：允许作者先把图搭完，保存前再拦。
        var previewOps = PreviewOpsIn(_doc.Actions);
        if (previewOps.Count > 0)
            _issues.AddChild(PreviewWarning(previewOps));

        if (issues.Count == 0)
        {
            if (previewOps.Count == 0)
                _issues.AddChild(MenuStyles.Label("都没问题，可以保存了。", 12, MenuStyles.Dim));
            _issueCount.Text = previewOps.Count == 0 ? "没问题" : previewOps.Count + " 块跑不起来";
            _issueCount.AddThemeColorOverride("font_color",
                previewOps.Count == 0 ? UiStyles.Green : MenuStyles.Gold);
        }
        else
        {
            foreach (var issue in issues) _issues.AddChild(IssueRow(issue));
            _issueCount.Text = issues.Count + " 处要改";
            _issueCount.AddThemeColorOverride("font_color", new Color("e0705f"));
        }

        _json.Text = _doc.ToJsonText(_catalog);
        _summary.Text = CountNodes(_doc.Actions) + " 块";
        _undo.Disabled = _historyAt <= 0;
        _redo.Disabled = _historyAt >= _history.Count - 1;
        RefreshToolbox();   // 插入位置可能在这次重画里变了（撤销、切换文档）
        Changed?.Invoke(_json.Text);
    }

    // ── 保存（复杂 DIY：DSL → 草案 → 摊平）────────────────────────────────

    /// <summary>
    /// 保存对话框。三个必填：**模板**（挂载、视图字段与守卫都从它继承）、**产物 id**
    /// （卡牌的 <c>effects.prefabs</c> 引用它），以及（覆盖模式下）**替换哪个效果**。
    ///
    /// <para>
    /// 为什么不让作者填挂载：模板已有的挂载会被摊平器带进产物，再声明一遍就是**挂两遍**
    /// （`draw 2` 变抽 4 张，产物却完全合法）。这里只提示「模板挂在哪个信号上」，
    /// 不给第二个输入框——少一个能写错的地方。
    /// </para>
    /// <para>
    /// 为什么模板与目标是两个下拉（R39）：模板声明「这里有一个空位」，效果是「卡正在用的那份实现」。
    /// 两者不再是同一个 id，硬塞进一个下拉会让「用哪套视图」和「改哪张卡的引用」绑死在一起。
    /// </para>
    /// </summary>
    private void OpenSaveDialog()
    {
        var authoring = _authoring?.Invoke();
        if (authoring is null)
        {
            ShowSaveNote("没保存：先在工坊里打开一个卡包——没有编辑会话就无处可存。", failed: true);
            return;
        }

        IReadOnlyList<HandlerParentOption> parents;
        try { parents = authoring.DescribeParents(); }
        catch (Exception error) { ShowSaveNote("没保存：读不出模板库——" + error.Message, failed: true); return; }

        if (parents.Count == 0)
        {
            // 模板库是引擎派生的（信号 × 守卫档），空表只可能是引擎信号表被裁掉了。
            ShowSaveNote("没保存：引擎没派生出任何模板（信号表为空？）——没有模板就没有可填的空位。", failed: true);
            return;
        }

        var dialog = new ConfirmationDialog
        {
            Title = "保存到卡包",
            OkButtonText = "保存",
            CancelButtonText = "取消",
            DialogHideOnOk = false,   // 失败要留在原地让作者改，不能一关就丢
            // 高度不能给 0：窗口会按 0 高布局，确认按钮落到可见区外，鼠标点不到（探针实测）。
            MinSize = new Vector2I(640, 260),
        };
        AddChild(dialog);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 10);
        dialog.AddChild(page);

        // 父是**模板**（R39）：104 个「信号 × 守卫档」的模板，成员恒为 onHook。
        // 缺省选「打出时」——卡牌效果里最常见的一档，省一次点击。
        var parent = new OptionButton { Name = "ParentPicker", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var option in parents) parent.AddItem(option.Label);
        for (var i = 0; i < parents.Count; i++) parent.SetItemMetadata(i, parents[i].ParentId);
        var defaultIndex = parents.ToList().FindIndex(option => option.ParentId == DefaultParentId);
        parent.Select(defaultIndex < 0 ? 0 : defaultIndex);
        DiyEditorScreen.Field(page, "模板（挂载、视图字段与守卫从它继承）", parent);
        ApplyView(authoring, parents[Math.Max(0, parent.Selected)]);

        var replace = new CheckBox
        {
            Text = "用这份积木重写一个已有效果",
            TooltipText = "产物落到 <效果 id>.dsl，包里引用它的卡自动改指产物；原效果保留为基线，可再次编辑",
        };
        page.AddChild(replace);

        // 目标与父是两个不同的 id：父是模板（往哪填），目标是效果（替换谁）。
        var targets = authoring.DescribeReplaceableEffects();
        var target = new OptionButton
        {
            Name = "ReplaceTargetPicker",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Disabled = true,
        };
        foreach (var item in targets) target.AddItem(item.Label);
        for (var i = 0; i < targets.Count; i++) target.SetItemMetadata(i, targets[i].EffectId);
        if (targets.Count == 0) target.AddItem("（包里没有可替换的效果）");
        DiyEditorScreen.Field(page, "替换哪个效果（只有它会被改引用）", target);

        var product = new LineEdit { Name = "ProductId", Text = NewProductId() };
        DiyEditorScreen.Field(page, "产物 id（卡牌的 effects.prefabs 引用它）", product);

        var hint = MenuStyles.Label(NewEffectHint, 12, MenuStyles.Dim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        page.AddChild(hint);

        // 选了哪个模板，画布的「视图字段」就跟着换成它的——作者能读什么由模板声明决定，
        // 而不是由这份界面猜（猜错的代价是「校验通过、装载期报字段不存在」）。
        parent.ItemSelected += index =>
        {
            ApplyView(authoring, parents[Math.Clamp((int)index, 0, parents.Count - 1)]);
            SyncReplaceMode(authoring, parent, replace, target, targets, product, hint);
        };
        replace.Toggled += _ => SyncReplaceMode(authoring, parent, replace, target, targets, product, hint);
        target.ItemSelected += _ => SyncReplaceMode(authoring, parent, replace, target, targets, product, hint);

        var result = MenuStyles.Label("", 12, MenuStyles.Dim);
        result.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        page.AddChild(result);

        dialog.Confirmed += () =>
        {
            var option = parents[Math.Max(0, parent.Selected)];
            var replaceId = replace.ButtonPressed && targets.Count > 0
                ? targets[Math.Clamp(target.Selected, 0, targets.Count - 1)].EffectId
                : null;

            if (replace.ButtonPressed && replaceId is null)
            {
                // 没目标就没法改引用：产物会是一份没人用的孤儿，作者却以为「覆盖成功」。
                const string why = "没保存：这个包里没有可替换的效果（卡牌没有引用任何效果组件）。";
                result.Text = why;
                result.AddThemeColorOverride("font_color", new Color("e0705f"));
                // 同时落到检查结果列：对话框不关，作者关掉它之后还得看得见原因。
                ShowSaveNote(why, failed: true);
                return;
            }

            try
            {
                var saved = authoring.Save(new HandlerDslSaveRequest(
                    option.ParentId, option.Member, product.Text.Trim(), _doc.ToJsonText(_catalog),
                    replace.ButtonPressed ? HandlerDslSaveMode.ReplaceExisting : HandlerDslSaveMode.NewEffect,
                    RetargetFromId: replaceId));
                result.Text = saved.Describe();
                result.AddThemeColorOverride("font_color", saved.Success ? UiStyles.Green : new Color("e0705f"));
                ShowSaveNote(saved.Describe(), failed: !saved.Success);
                if (saved.Success) dialog.QueueFree();
            }
            catch (Exception error)
            {
                // 引擎的拒绝理由（校验、命名、会话状态）本来就是写给作者看的，原话带出去。
                result.Text = "没保存成功：" + error.Message;
                result.AddThemeColorOverride("font_color", new Color("e0705f"));
                GD.PushWarning("积木保存：" + error.Message);
                // 同时落到检查结果列：对话框一关，作者还能看见刚才为什么没成。
                ShowSaveNote("没保存成功：" + error.Message, failed: true);
            }
        };
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }

    /// <summary>缺省父模板：打出时。模板库没有它才退回第一个。</summary>
    private const string DefaultParentId = "template.card.played.any";

    private const string NewEffectHint =
        "保存会写三份文件：DSL 文档、草案、摊平产物。产物是自包含的——之后改模板不会影响它。";

    private static string NewProductId() => "effect." + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 覆盖开关的联动：目标下拉的可用性、产物 id、提示、以及「载入上次的积木图」。
    /// <para>
    /// 载入这一条是覆盖模式能用的**前提**：不载入，作者第二次进来看到的是样例文档，
    /// 一保存就把上次的图换成样例——那不是编辑，是数据丢失。
    /// </para>
    /// <para>
    /// 产物 id 跟着**目标**走、不跟模板走：模板是 <c>template.card.played.any</c>，
    /// 拿它派生 id 会得到一份既看不懂、也无法与卡牌引用对上的产物。
    /// </para>
    /// </summary>
    private void SyncReplaceMode(
        HandlerDslAuthoring authoring,
        OptionButton parent,
        CheckBox replace,
        OptionButton target,
        IReadOnlyList<HandlerReplaceTarget> targets,
        LineEdit product,
        Label hint)
    {
        product.Editable = !replace.ButtonPressed;

        if (!replace.ButtonPressed)
        {
            target.Disabled = true;
            // 勾选期间 id 被改写成派生值；取消勾选＝回到「新建」，必须换回一个新 id，
            // 否则会以新建模式写出与覆盖模式同名的产物。
            product.Text = NewProductId();
            hint.Text = NewEffectHint;
            return;
        }

        if (targets.Count == 0)
        {
            target.Disabled = true;
            product.Text = "";
            hint.Text = "这个包里没有可替换的效果：卡牌没有引用任何效果组件，先用积木新建一个。";
            return;
        }

        target.Disabled = false;
        var picked = targets[Math.Clamp(target.Selected, 0, targets.Count - 1)];
        var productId = HandlerDslAuthoring.ReplaceProductIdOf(picked.EffectId);
        product.Text = productId;

        var existing = authoring.ReadExistingDsl(picked.EffectId);
        if (existing is null)
        {
            hint.Text = $"这个效果还没有积木版本：保存后包内引用 {picked.EffectId} 的卡会改用它，原效果保留。";
            return;
        }

        try
        {
            Open(_cardLabel.Text, HandlerDocument.FromJson(existing, _catalog));
            hint.Text = $"已载入上次的积木图（保存会覆盖 {productId}）。";
        }
        catch (FormatException error)
        {
            hint.Text = "上次的图读不出来：" + error.Message + "；保存会用当前画布替换它。";
        }
    }

    /// <summary>
    /// 把画布的视图换成这个父成员声明的视图字段（`read`／`write` 的合法范围）。
    /// <para>
    /// 读不出来（父没了、成员没了）＝**保持现状**并留一条警告，不清空：把视图清成空表会让画布上
    /// 每个读取都变红，看起来像「作者的图坏了」，其实是父效果没读到。
    /// </para>
    /// </summary>
    private void ApplyView(HandlerDslAuthoring authoring, HandlerParentOption option)
    {
        try
        {
            var fields = authoring.ViewFieldsOf(option.ParentId, option.Member);
            if (fields.Count == 0)
            {
                GD.PushWarning($"父效果 {option.ParentId}#{option.Member} 没有声明视图字段。");
                return;
            }
            _view = fields;
            Rebuild();
        }
        catch (Exception error)
        {
            GD.PushWarning("读不出父效果的视图字段：" + error.Message);
        }
    }

    private void ShowSaveNote(string note, bool failed)
    {
        _saveNote = note;
        _saveFailed = failed;
        Rebuild();
    }

    private Control IssueRow(HandlerIssue issue)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var code = MenuStyles.Label(HandlerVocabulary.IssueTitle(issue.Code), 11, new Color("e0705f"));
        code.CustomMinimumSize = new Vector2(92, 0);
        code.TooltipText = issue.Code;   // 原始错误码留给排查的人，界面上不摆给作者
        row.AddChild(code);
        var msg = MenuStyles.Label(
            HandlerVocabulary.FriendlyPath(issue.NodePath) + "：" + issue.Message, 11, MenuStyles.Dim);
        msg.TooltipText = issue.NodePath;
        msg.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        msg.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(msg);
        return row;
    }

    private Control Hat(string text)
    {
        var block = new BlockShape
        {
            Fill = BlockTheme.Hat,
            ShowNotch = false,
            ShowTab = true,
            TopRadius = 18,
            BottomRadius = 6,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            NotchColor = MenuStyles.Bg,
            TooltipText = "什么时候触发由卡牌设定决定，这里不用管。",
        };
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 9);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        margin.AddChild(MenuStyles.Label(text, 14, BlockTheme.InkFor(BlockTheme.Hat)));
        block.SetBody(margin);
        return block;
    }

    /// <summary>
    /// 渲染一段 <c>actions[]</c>。<paramref name="where"/> 是给工具箱看的**人话位置**
    /// （「整段的最后」「如果 里面」「选项2 里面」）——工具箱靠它告诉作者「下一块会插到哪」。
    /// </summary>
    private Control RenderArray(List<HandlerNode> list, string basePath, List<ScopeVar> scope, int depth,
        string where)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        // 间距 = 凸榫探出量，榫头正好填满缝、顶到下一块的缺口上。
        box.AddThemeConstantOverride("separation", 4);

        var local = new List<ScopeVar>(scope);
        for (var i = 0; i < list.Count; i++)
        {
            var node = list[i];
            box.AddChild(RenderNode(node, basePath + "[" + i + "]", local));

            // 声明在「本节点之后」才对兄弟可见（SPEC §8.2）。三处来源：let{name}、ask.bind、动作节点的 bind。
            var spec = _catalog.Find(node.Op);
            if (node.Op == "let" && !string.IsNullOrEmpty(node.Text("name")))
                local.Add(new ScopeVar(node.Text("name")!, GuessType(node.Value("value"), local)));
            else if (node.Op == "ask" && !string.IsNullOrEmpty(node.Text("bind")))
                local.Add(new ScopeVar(node.Text("bind")!, "list<entity>"));
            else if (spec is { Outputs.Count: 1 } && node.Text("bind") is { Length: > 0 } bound)
                local.Add(new ScopeVar(bound, spec.Outputs[0].Type));
        }

        box.AddChild(WithTopMargin(AddBlockButton(list, depth, where), 6));
        return box;
    }

    private static Control WithTopMargin(Control child, int top)
    {
        var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        margin.AddThemeConstantOverride("margin_top", top);
        margin.AddChild(child);
        return margin;
    }

    /// <summary>
    /// 画布上的「＋ 添加积木」：**只负责选插入位置**，不再弹菜单。
    /// 选中后按钮变成「↓ 就插在这儿」——工具箱里点哪块，就落在这个位置。
    /// </summary>
    private Button AddBlockButton(List<HandlerNode> target, int depth, string where, string? text = null)
    {
        var active = ReferenceEquals(_pendingInsert, target);
        var tint = active ? MenuStyles.Gold : depth == 0 ? BlockTheme.Hat : BlockTheme.Advanced;
        var button = SoftButton(text ?? "＋ 添加积木", tint,
            active ? "工具箱里点一块，就插到这里" : "选这个位置，然后去左边工具箱点一块",
            () =>
            {
                _pendingInsert = target;
                _pendingLabel = where;
                Rebuild();
            });
        if (active) button.Text = "↓ 就插在这儿";
        return button;
    }

    private static Button SoftButton(string text, Color tint, string tip, Action action)
    {
        var button = new Button
        {
            Text = text,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            TooltipText = tip,
        };
        button.AddThemeFontSizeOverride("font_size", 12);
        button.AddThemeColorOverride("font_color", new Color(MenuStyles.Ink, 0.9f));
        button.AddThemeStyleboxOverride("normal", UiStyles.Box(new Color(tint.R, tint.G, tint.B, 0.30f), new Color(MenuStyles.Ink, 0.16f), 6, 8));
        button.AddThemeStyleboxOverride("hover", UiStyles.Box(new Color(tint.R, tint.G, tint.B, 0.52f), MenuStyles.Gold, 6, 8));
        button.AddThemeStyleboxOverride("pressed", UiStyles.Box(new Color(tint.R, tint.G, tint.B, 0.72f), MenuStyles.Gold, 6, 8));
        button.Pressed += action;
        return button;
    }

    private Control RenderNode(HandlerNode node, string path, List<ScopeVar> scope)
    {
        var spec = _catalog.Find(node.Op);
        var fill = spec is null ? BlockTheme.Advanced : BlockTheme.ForGroup(spec.Group);
        if (_errorNodes.Contains(node)) fill = BlockTheme.Error;

        var shell = new BlockShape
        {
            Fill = fill,
            ShowTab = spec is null || !spec.IsContainer,
            // 容器块的头要和左侧竖脊接成一体，底角必须是方的，否则接缝处会掐出一道收腰。
            BottomRadius = spec is null || !spec.IsContainer ? 6 : 0,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            NotchColor = MenuStyles.Bg,
            // op 名和契约路径只留在 tooltip：作者认的是中文块名，排查的人才需要这两个。
            TooltipText = (spec?.Title ?? node.Op) + "　" + node.Op + "\n" + path,
        };
        shell.SetBody(BuildBlockContent(node, spec, scope, fill));
        _nodeControls[node] = shell;

        if (spec is null || !spec.IsContainer) return shell;

        // C 形块：头 + 左侧竖脊 + 脚。Scratch 的「如果／重复」就是这个形状。
        var wrapper = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        wrapper.AddThemeConstantOverride("separation", 0);
        wrapper.AddChild(shell);

        var bodyRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        bodyRow.AddThemeConstantOverride("separation", 0);
        bodyRow.AddChild(new ColorRect
        {
            Color = fill,
            CustomMinimumSize = new Vector2(14, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        });

        var inner = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        inner.AddThemeConstantOverride("separation", 4);
        var innerMargin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        innerMargin.AddThemeConstantOverride("margin_left", 10);
        innerMargin.AddThemeConstantOverride("margin_right", 14);
        innerMargin.AddThemeConstantOverride("margin_top", 4);
        innerMargin.AddThemeConstantOverride("margin_bottom", 4);
        innerMargin.AddChild(inner);
        bodyRow.AddChild(innerMargin);

        var innerScope = new List<ScopeVar>(scope);
        if (node.Op == "iterate" && !string.IsNullOrEmpty(node.Text("as")))
            innerScope.Add(new ScopeVar(node.Text("as")!, ElementOf(GuessType(node.Value("over"), scope))));

        if (node.Op == "choice")
        {
            RenderChoiceArms(node, path, innerScope, inner, fill);
        }
        else
        {
            foreach (var field in spec.Bodies)
            {
                var where = (spec.Title) + " 里面";
                if (field != "actions") where = (spec.Title) + " 的「" + BodyLabel(field) + "」里";

                if (field == "else" && (!node.Bodies.TryGetValue("else", out var elseList) || elseList.Count == 0))
                {
                    inner.AddChild(WithTopMargin(
                        AddBlockButton(node.Body("else"), 1, where, "＋ 添加「不然」"), 2));
                    continue;
                }
                if (field != "actions")
                    inner.AddChild(MenuStyles.Label(BodyLabel(field), 11, new Color(MenuStyles.Ink, 0.72f)));
                inner.AddChild(RenderArray(node.Body(field), path + "." + field, innerScope, 1, where));
            }
        }

        wrapper.AddChild(bodyRow);

        var foot = new BlockShape
        {
            Fill = fill,
            ShowNotch = false,
            ShowTab = true,
            TopRadius = 0,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            NotchColor = MenuStyles.Bg,
        };
        foot.SetBody(new Control { CustomMinimumSize = new Vector2(14, 12) });
        wrapper.AddChild(foot);
        return wrapper;
    }

    /// <summary>
    /// <c>choice</c> 的选项：每个选项是**一句给玩家看的话 ＋ 一串动作**。
    ///
    /// <para>
    /// 契约里 <c>options</c> 是 <c>[{label, actions}]</c>（SPEC §3），不是普通动作数组，
    /// 所以不能按 <c>Bodies</c> 走 <see cref="RenderArray"/> 那一套——这里每条臂自己一段，
    /// 各带自己的「添加积木」。把它想成「并列的几条分支」，而不是「一个列表」。
    /// </para>
    /// </summary>
    private void RenderChoiceArms(HandlerNode node, string path, List<ScopeVar> scope, VBoxContainer inner,
        Color fill)
    {
        var ink = BlockTheme.InkFor(fill);
        if (node.Options.Count == 0)
            inner.AddChild(MenuStyles.Label("还没有选项——玩家会看到一个空的选择框。", 11,
                BlockTheme.MutedInkFor(fill)));

        for (var i = 0; i < node.Options.Count; i++)
        {
            var option = node.Options[i];
            var index = i;

            if (i > 0)
            {
                // 一条细线分开相邻的臂：C 形脊柱已经把整块圈住了，这里只需要「断句」。
                // 直接挂到 inner 上（不套 MarginContainer），否则会被 ShrinkBegin 压成 0 宽看不见。
                inner.AddChild(new ColorRect
                {
                    Color = new Color(ink.R, ink.G, ink.B, 0.14f),
                    CustomMinimumSize = new Vector2(0, 1),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                });
            }

            var head = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            head.AddThemeConstantOverride("separation", 6);
            head.AddChild(MenuStyles.Label("选项 " + (i + 1), 11, BlockTheme.MutedInkFor(fill)));
            var chip = InputChip(string.IsNullOrEmpty(option.Label) ? "写上玩家看到的字…" : option.Label,
                string.IsNullOrEmpty(option.Label));
            chip.TooltipText = "玩家在选项按钮上看到的文字";
            chip.Pressed += () => Prompt("选项文字", option.Label,
                text => Edit(() => option.Label = text.Trim()));
            head.AddChild(chip);
            head.AddChild(BlockButton("✕", "删掉这个选项", ink, () => Edit(() => node.Options.Remove(option))));
            inner.AddChild(head);

            inner.AddChild(RenderArray(option.Actions, path + ".options[" + index + "].actions", scope, 1,
                "选项" + (i + 1) + " 里面"));
        }

        inner.AddChild(WithTopMargin(SoftButton("＋ 添加选项", fill,
            "再加一条玩家能选的路",
            () => Edit(() => node.Options.Add(new HandlerOption("选项" + (node.Options.Count + 1))))), 4));
    }

    private Control BuildBlockContent(HandlerNode node, HandlerNodeSpec? spec, List<ScopeVar> scope, Color fill)
    {
        var ink = BlockTheme.InkFor(fill);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 7);
        margin.AddThemeConstantOverride("margin_bottom", 10);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        margin.AddChild(box);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(MenuStyles.Label(spec?.Title ?? node.Op, 15, ink));
        // 画布上也标一次预览态：作者是在这块积木上做决定，不能只在右栏提一句。
        if (spec is { IsPreview: true })
        {
            var badge = MenuStyles.Label("还不能用", 10, new Color(MenuStyles.Gold, 0.9f));
            badge.TooltipText = spec.Problem;
            head.AddChild(badge);
        }
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(BlockButton("↑", "上移", ink, () => MoveNode(node, -1)));
        head.AddChild(BlockButton("↓", "下移", ink, () => MoveNode(node, 1)));
        head.AddChild(BlockButton("✕", "删除", ink, () => RemoveNode(node)));
        box.AddChild(head);

        if (spec is null)
        {
            box.AddChild(MenuStyles.Label("目录里没有这个节点，保存会被拒绝。", 12, MenuStyles.Ink));
            return margin;
        }

        foreach (var pin in spec.Inputs)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var label = MenuStyles.Label(spec.PinLabel(pin.Name) + (pin.Required ? "" : "（可选）"), 12,
                new Color(ink.R, ink.G, ink.B, 0.9f));
            label.CustomMinimumSize = new Vector2(82, 0);
            row.AddChild(label);
            row.AddChild(pin.Kind switch
            {
                HandlerPinKind.Bind => TextChip(node.Text(pin.Name), "命名…", v => SetText(node, pin.Name, v), "绑定名"),
                HandlerPinKind.Text => TextChip(node.Text(pin.Name), "填写…", v => SetText(node, pin.Name, v), "文案"),
                HandlerPinKind.Field => TextChip(node.Text(pin.Name), "选字段…", v => SetText(node, pin.Name, v),
                    "视图字段", WritableFields(), HandlerVocabulary.ViewFieldLabel),
                HandlerPinKind.Enum => EnumChip(node, pin),
                _ => ValueChip(node.Value(pin.Name), v => SetValue(node, pin.Name, v), scope, !pin.Required,
                    pin.EnumValues),
            });
            box.AddChild(row);
        }

        // bind 是**节点级字段**，不是引脚（SPEC §3）——目录里查不到，得单独画。
        // 形态由输出数决定，两种形态不能互换（SPEC §8.9）：
        //   单输出 → 字符串（一行「结果命名」）
        //   多输出 → 对象，键集**恰好等于**输出名（每个输出一行，全部留空＝不写 bind）
        if (spec.Pin("bind") is null && spec.Outputs.Count == 1)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var label = MenuStyles.Label("结果命名（可选）", 12, new Color(ink.R, ink.G, ink.B, 0.9f));
            label.CustomMinimumSize = new Vector2(82, 0);
            row.AddChild(label);
            row.AddChild(TextChip(node.Text("bind"), "命名…", v => SetText(node, "bind", v), "绑定名"));
            box.AddChild(row);
        }
        else if (spec.Pin("bind") is null && spec.Outputs.Count > 1)
        {
            // 多输出的名字是**按输出名填**的，不是随便起几个：漏一个就是「那一路结果没名字」，
            // 校验器按 bind-scope 拒绝（想全部忽略就整段不写 bind）。所以这里一个输出一行，不给「加一个名字」的入口。
            foreach (var pin in spec.Outputs)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                var label = MenuStyles.Label(pin.Name, 12, new Color(ink.R, ink.G, ink.B, 0.9f));
                label.CustomMinimumSize = new Vector2(82, 0);
                label.TooltipText = "输出 " + pin.Name + "（" + pin.Type + "）：给这一路结果起个名字，下游才能引用";
                row.AddChild(label);
                row.AddChild(TextChip(
                    node.OutputBinds.GetValueOrDefault(pin.Name), "命名…",
                    v => SetOutputBind(node, pin.Name, v), "绑定名"));
                box.AddChild(row);
            }
        }

        if (spec.Inputs.Count == 0 && !spec.IsContainer && spec.Outputs.Count != 1)
            box.AddChild(MenuStyles.Label("没有输入引脚", 11, new Color(ink.R, ink.G, ink.B, 0.7f)));

        return margin;
    }

    /// <summary>取 <c>list&lt;T&gt;</c> 的元素类型；不是列表就是 unknown。</summary>
    private static string ElementOf(string type) =>
        type.StartsWith("list<", StringComparison.Ordinal) ? type[5..^1] : "unknown";

    /// <summary>
    /// 界面侧的粗类型推断：**只用来给取值菜单分组**（决定「读取 ‹t›.防御」出不出现）。
    /// 判错不会漏错——真正的判定在校验器里，这里宁可保守。
    /// </summary>
    private string GuessType(HandlerValue? value, List<ScopeVar> scope)
    {
        switch (value?.Kind)
        {
            case null: return "unknown";
            case HandlerValueKind.Literal: return value.LiteralType;
            case HandlerValueKind.Binding:
                return scope.FirstOrDefault(s => s.Name == value.Name)?.Type ?? "unknown";
            case HandlerValueKind.View:
                return _view.TryGetValue(value.Name ?? "", out var field) ? field.Type : "unknown";
            case HandlerValueKind.Operator:
                return HandlerDslValidator.OperatorResult(value.Name ?? "");
            default: return "unknown";
        }
    }

    private static string BodyLabel(string field) => field switch
    {
        "then" => "那么",
        "else" => "不然",
        "actions" => "",
        "options" => "选项",
        _ => field,
    };

    private static Button BlockButton(string text, string tip, Color ink, Action action)
    {
        var button = new Button { Text = text, TooltipText = tip, CustomMinimumSize = new Vector2(24, 22) };
        button.AddThemeFontSizeOverride("font_size", 12);
        button.AddThemeColorOverride("font_color", new Color(ink.R, ink.G, ink.B, 0.75f));
        button.AddThemeColorOverride("font_hover_color", ink);
        button.AddThemeStyleboxOverride("normal", UiStyles.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 4, 2));
        button.AddThemeStyleboxOverride("hover", UiStyles.Box(new Color(MenuStyles.Ink, 0.16f), new Color(0, 0, 0, 0), 4, 2));
        button.AddThemeStyleboxOverride("pressed", UiStyles.Box(new Color(MenuStyles.Ink, 0.26f), new Color(0, 0, 0, 0), 4, 2));
        button.Pressed += action;
        return button;
    }

    // ── 输入控件（做旧纸张的椭圆芯片）────────────────────────────────────

    private static Button InputChip(string text, bool empty, bool dropdown = false)
    {
        var button = new Button
        {
            Text = dropdown ? text + "  ▾" : text,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        button.AddThemeFontSizeOverride("font_size", 12);
        button.AddThemeColorOverride("font_color", empty ? BlockTheme.ChipMuted : BlockTheme.ChipInk);
        button.AddThemeColorOverride("font_hover_color", BlockTheme.ChipInk);
        button.AddThemeStyleboxOverride("normal", UiStyles.Box(BlockTheme.ChipFill, BlockTheme.ChipLine, 11, 8));
        button.AddThemeStyleboxOverride("hover", UiStyles.Box(BlockTheme.ChipHover, MenuStyles.Gold, 11, 8));
        button.AddThemeStyleboxOverride("pressed", UiStyles.Box(BlockTheme.ChipPressed, BlockTheme.ChipLine, 11, 8));
        return button;
    }

    private Control EnumChip(HandlerNode node, HandlerPinSpec pin)
    {
        var current = node.Text(pin.Name);
        var button = InputChip(current is null ? "选择…" : EnumLabel(pin.Name, current), current is null, true);
        var values = pin.EnumValues ?? [];
        var menu = new PopupMenu();
        AddChild(menu);
        for (var i = 0; i < values.Count; i++) menu.AddItem(EnumLabel(pin.Name, values[i]), i);
        menu.IdPressed += id => SetText(node, pin.Name, values[(int)id]);
        menu.PopupHide += () => menu.QueueFree();
        button.Pressed += () =>
        {
            menu.Position = (Vector2I)button.GetScreenPosition() + new Vector2I(0, (int)button.Size.Y);
            menu.Popup();
        };
        return button;
    }

    /// <summary>取值集里的裸值 → 中文。只用于**枚举型**引脚，别拿它翻 read.of。</summary>
    private static string LiteralLabel(string value) => value switch
    {
        "self" => "己方",
        "enemy" => "敌方",
        "friendly" => "我方",
        "any" => "任意",
        "empty" => "选不到就跳过",
        "first" => "取第一个",
        "Permanent" => "永久",
        "TurnEnd" => "到回合结束",
        "NextOwnerTurnStart" => "到其下回合开始",
        _ => value,
    };

    private static string EnumLabel(string pin, string value) => (pin, value) switch
    {
        ("kind", "unit") => "单位",
        ("kind", "hq") => "总部",
        ("kind", "card") => "卡牌",
        ("side", "enemy") => "敌方",
        ("side", "friendly") => "我方",
        ("side", "any") => "任意",
        ("duration", "Permanent") => "永久",
        ("duration", "TurnEnd") => "到回合结束",
        ("duration", "NextOwnerTurnStart") => "到其下回合开始",
        _ => value,
    };

    /// <summary>
    /// 文字芯片。<paramref name="label"/> 只翻**显示**（视图字段 <c>Defense</c> → 防御力），
    /// 存进文档的仍是原名——DSL 里不能出现中文别名（SPEC §0.4）。
    /// </summary>
    private Control TextChip(string? value, string placeholder, Action<string?> commit, string title,
        IReadOnlyList<string>? choices = null, Func<string, string>? label = null)
    {
        var shown = string.IsNullOrEmpty(value) ? placeholder : label?.Invoke(value!) ?? value!;
        var chip = InputChip(shown, string.IsNullOrEmpty(value), choices is { Count: > 0 });
        chip.Pressed += () =>
        {
            if (choices is { Count: > 0 })
            {
                var menu = new PopupMenu();
                AddChild(menu);
                for (var i = 0; i < choices.Count; i++)
                    menu.AddItem(label?.Invoke(choices[i]) ?? choices[i], i);
                menu.IdPressed += id => commit(choices[(int)id]);
                menu.PopupHide += () => menu.QueueFree();
                menu.Position = (Vector2I)chip.GetScreenPosition() + new Vector2I(0, (int)chip.Size.Y);
                menu.Popup();
                return;
            }
            Prompt(title, value ?? "", text => commit(string.IsNullOrWhiteSpace(text) ? null : text));
        };
        return chip;
    }

    private IReadOnlyList<string> WritableFields() =>
        _view.Values.Where(f => f.Writable).Select(f => f.Name).ToArray();

    // ── 值芯片（表达式树）────────────────────────────────────────────────

    private Control ValueChip(HandlerValue? value, Action<HandlerValue?> commit, List<ScopeVar> scope,
        bool optional = false, IReadOnlyList<string>? literals = null)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);

        var text = value is null
            ? (optional ? "不用填" : "还没填")
            : value.Kind == HandlerValueKind.Operator ? HandlerVocabulary.OperatorLabel(value.Name ?? "?")
            : value is { Kind: HandlerValueKind.Literal, Text: { } literal } && literals is { Count: > 0 }
                ? LiteralLabel(literal)
                : value.Describe();
        var chip = InputChip(text, value is null);
        chip.Pressed += () => OpenValueMenu(chip, value, commit, scope, literals);
        row.AddChild(chip);

        if (value is { Kind: HandlerValueKind.Operator })
        {
            row.AddChild(MenuStyles.Label("(", 12, new Color(MenuStyles.Ink, 0.8f)));
            for (var i = 0; i < value.Args.Count; i++)
            {
                if (i > 0) row.AddChild(MenuStyles.Label(",", 12, new Color(MenuStyles.Ink, 0.8f)));
                var index = i;
                row.AddChild(ValueChip(value.Args[i], arg => commit(value.WithArg(index, arg ?? HandlerValue.Num(0))), scope));
            }
            row.AddChild(MenuStyles.Label(")", 12, new Color(MenuStyles.Ink, 0.8f)));
        }
        return row;
    }

    private void OpenValueMenu(Button anchor, HandlerValue? value, Action<HandlerValue?> commit, List<ScopeVar> scope,
        IReadOnlyList<string>? literals = null)
    {
        _valueCommit = commit;
        _valueCurrent = value;
        _valueScope = scope;

        _valueMenu.Clear();
        _valueLiterals = literals?.ToList() ?? [];
        if (_valueLiterals.Count > 0)
        {
            for (var i = 0; i < _valueLiterals.Count; i++)
                _valueMenu.AddItem(LiteralLabel(_valueLiterals[i]), 500 + i);
            _valueMenu.AddSeparator();
        }
        _valueMenu.AddItem("数字…", 1);
        _valueMenu.AddItem("文本…", 2);
        _valueMenu.AddSeparator();

        if (scope.Count == 0)
        {
            _valueMenu.AddItem("（这儿还没有名字可用）", 900);
            _valueMenu.SetItemDisabled(_valueMenu.ItemCount - 1, true);
        }
        else
        {
            for (var i = 0; i < scope.Count; i++)
            {
                var hint = scope[i].IsEntity || scope[i].IsEntityList
                    ? "（" + HandlerVocabulary.TypeLabel(scope[i].Type) + "）"
                    : "";
                _valueMenu.AddItem("引用 ‹" + scope[i].Name + "›" + hint, 100 + i);
            }
        }
        _valueMenu.AddSeparator();

        // 读取：目标分「我方／自己／每个可见的实体绑定」，每类只列**该种类**真有的 path。
        // 「自己.kredits」「我方.attack」这类无意义组合从此不再出现在菜单里（SPEC §12.8 折算表）。
        _readOptions = [];
        var readTargets = new List<(string Label, IReadOnlyList<string> Kinds, Func<string, HandlerValue> Make)>
        {
            ("我方", HandlerCatalog.KindsForLiteral("viewer"), path => HandlerValue.Read("viewer", path)),
            ("自己", HandlerCatalog.KindsForLiteral("self"), path => HandlerValue.Read("self", path)),
        };
        foreach (var bound in scope.Where(s => s.IsEntity))
        {
            var name = bound.Name;
            readTargets.Add(("‹" + name + "›", HandlerCatalog.UnitOrHq,
                path => HandlerValue.ReadFrom(HandlerValue.Var(name), path)));
        }

        foreach (var (label, kinds, make) in readTargets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in kinds)
            {
                if (!_catalog.Readables.TryGetValue(kind, out var table)) continue;
                foreach (var (path, type) in table.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!seen.Add(path)) continue;
                    _readOptions.Add(make(path));
                    _valueMenu.AddItem(
                        "读取 " + label + " 的" + HandlerVocabulary.ReadableLabel(path) +
                        "（" + HandlerVocabulary.TypeLabel(type) + "）",
                        200 + _readOptions.Count - 1);
                }
            }
        }
        _valueMenu.AddSeparator();

        var viewIndex = 0;
        foreach (var field in _view.Values)
            _valueMenu.AddItem(
                "取「" + HandlerVocabulary.ViewFieldLabel(field.Name) + "」（" +
                HandlerVocabulary.TypeLabel(field.Type) + "）", 300 + viewIndex++);
        _valueMenu.AddSeparator();

        var opIndex = 0;
        foreach (var op in HandlerDslValidator.OperatorNames)
            _valueMenu.AddItem("运算 · " + HandlerVocabulary.OperatorLabel(op), 400 + opIndex++);
        _valueMenu.AddSeparator();
        _valueMenu.AddItem("清除", 999);

        _valueMenu.Position = (Vector2I)anchor.GetScreenPosition() + new Vector2I(0, (int)anchor.Size.Y);
        _valueMenu.Popup();
    }

    private void OnValuePicked(long id)
    {
        var commit = _valueCommit;
        if (commit is null) return;

        if (id == 999) { commit(null); return; }
        if (id == 1)
        {
            var current = _valueCurrent is { Kind: HandlerValueKind.Literal } ? _valueCurrent.Number : 0;
            Prompt("数字", current.ToString(CultureInfo.InvariantCulture), text =>
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    commit(HandlerValue.Num(v));
            });
            return;
        }
        if (id == 2)
        {
            var current = _valueCurrent is { Kind: HandlerValueKind.Literal } ? _valueCurrent.Text ?? "" : "";
            Prompt("文本", current, text => commit(HandlerValue.Str(text)));
            return;
        }
        if (id is >= 100 and < 200)
        {
            commit(HandlerValue.Var(_valueScope[(int)id - 100].Name));
            return;
        }
        if (id is >= 500 and < 600)
        {
            commit(HandlerValue.Str(_valueLiterals[(int)id - 500]));
            return;
        }
        if (id is >= 200 and < 300)
        {
            commit(_readOptions[(int)id - 200]);
            return;
        }
        if (id is >= 300 and < 400)
        {
            commit(HandlerValue.ViewField(_view.Values.ElementAt((int)id - 300).Name));
            return;
        }
        if (id is >= 400 and < 500)
        {
            var op = HandlerDslValidator.OperatorNames.ElementAt((int)id - 400);
            var arity = HandlerDslValidator.OperatorArity(op);
            var args = new List<HandlerValue>();
            if (arity == 2 && _valueCurrent is not null) args.Add(_valueCurrent);
            while (args.Count < arity) args.Add(HandlerValue.Num(0));
            commit(HandlerValue.Call(op, args.ToArray()));
        }
    }

    // ── 编辑 ───────────────────────────────────────────────────────────────

    private void Edit(Action? mutate = null)
    {
        mutate?.Invoke();
        _history.RemoveRange(_historyAt + 1, _history.Count - _historyAt - 1);
        _history.Add(_doc.Clone());
        _historyAt = _history.Count - 1;
        Rebuild();
    }

    private void SetValue(HandlerNode node, string pin, HandlerValue? value) =>
        Edit(() => node.Set(pin, value));

    private void SetText(HandlerNode node, string pin, string? text) =>
        Edit(() => node.SetText(pin, text));

    /// <summary>多输出节点按输出名命名（空＝这一路不绑；全空＝整段不写 bind）。</summary>
    private void SetOutputBind(HandlerNode node, string output, string? name) =>
        Edit(() => node.SetOutputBind(output, name));

    private void Undo()
    {
        if (_historyAt <= 0) return;
        _historyAt--;
        _doc = _history[_historyAt].Clone();
        ResetInsertTarget();
        Rebuild();
    }

    private void Redo()
    {
        if (_historyAt >= _history.Count - 1) return;
        _historyAt++;
        _doc = _history[_historyAt].Clone();
        ResetInsertTarget();
        Rebuild();
    }

    /// <summary>
    /// 撤销／重做／换文档都会把 <c>_doc</c> 换成**克隆体**，之前记住的插入目标就指向了旧对象树。
    /// 不重置的话，下一步插入会掉进一个已经不在画面上的列表里（点了没反应，最难查的那种）。
    /// </summary>
    private void ResetInsertTarget()
    {
        _pendingInsert = _doc.Actions;
        _pendingLabel = "整段的最后";
    }

    private List<HandlerNode>? FindParentList(HandlerNode target)
    {
        List<HandlerNode>? found = null;
        void Walk(List<HandlerNode> list)
        {
            if (found is not null) return;
            if (list.Contains(target)) { found = list; return; }
            foreach (var n in list)
            {
                foreach (var body in n.Bodies.Values) Walk(body);
                // choice 的选项动作挂在 Options 上，不在 Bodies 里（SPEC §3）
                foreach (var option in n.Options) Walk(option.Actions);
            }
        }
        Walk(_doc.Actions);
        return found;
    }

    private void MoveNode(HandlerNode node, int direction)
    {
        var list = FindParentList(node);
        if (list is null) return;
        var index = list.IndexOf(node);
        var target = index + direction;
        if (target < 0 || target >= list.Count) return;
        Edit(() => (list[index], list[target]) = (list[target], list[index]));
    }

    private void RemoveNode(HandlerNode node)
    {
        var list = FindParentList(node);
        if (list is null) return;
        Edit(() => list.Remove(node));
    }

    // ── 新建节点 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 新插进来的块要**尽量一次就合法**：必填的枚举给个默认值、必填的数字给 1，
    /// 作者看到的就是「还差几个格子」，而不是一串报错。
    /// <para>
    /// 两处故意留空：<c>damage.target</c>（默认猜「打谁」一定是错的，猜错会报成「名字用错了」，
    /// 比留空报「还有地方没填」难懂）和 <c>choice</c> 的选项文字（那是给玩家看的话，不能替作者编）。
    /// </para>
    /// </summary>
    private HandlerNode NewNode(string op) => op switch
    {
        "ask" => new HandlerNode("ask").SetText("kind", "unit").SetText("prompt", "选择目标"),
        // script 的两个引脚数组是**必填**（没有引脚也要写 []），由工厂预置——导出时不补，
        // 否则读进来的漏写会被静默改写掉。
        "script" => HandlerNode.Script(""),
        "choice" => Choice(),
        "branch" => Branch(),
        "loop" => Loop(),
        "while" => While(),
        "iterate" => Iterate(),
        "let" => new HandlerNode("let").SetText("name", "n").Set("value", HandlerValue.Num(0)),
        "write" => new HandlerNode("write").SetText("field", WritableFields().FirstOrDefault() ?? "").Set("value", HandlerValue.Num(0)),
        "damage" => new HandlerNode("damage").Set("amount", HandlerValue.Num(1)),
        "draw" => new HandlerNode("draw").Set("count", HandlerValue.Num(1)),
        _ => new HandlerNode(op),
    };

    private static HandlerNode Choice()
    {
        var node = new HandlerNode("choice").SetText("prompt", "选一个");
        node.Options.Add(new HandlerOption(""));
        return node;
    }

    private static HandlerNode Branch()
    {
        var node = new HandlerNode("branch").Set("test", HandlerValue.Bool(true));
        node.Body("then").Add(new HandlerNode("draw").Set("count", HandlerValue.Num(1)));
        return node;
    }

    private static HandlerNode Loop()
    {
        var node = new HandlerNode("loop").Set("times", HandlerValue.Num(1));
        node.Body("actions");
        return node;
    }

    private static HandlerNode While()
    {
        var node = new HandlerNode("while")
            .Set("test", HandlerValue.Bool(true))
            .Set("limit", HandlerValue.Num(3));
        node.Body("actions");
        return node;
    }

    private static HandlerNode Iterate()
    {
        var node = new HandlerNode("iterate").SetText("as", "t");
        node.Body("actions");
        return node;
    }

    // ── 杂项 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 文档里用到的**预览态** op（SPEC §12.11）：能存、能生成 csx，但运行期跑不起来。
    /// <para>
    /// 与校验错误分开统计——预览态不是错误码，是「这版还没接通」的事实。
    /// 界面据此在检查结果里出一条警告，并在保存前拦（保存链路接上时用）。
    /// </para>
    /// </summary>
    private List<(string Op, string? Problem, int Count)> PreviewOpsIn(IReadOnlyList<HandlerNode> list)
    {
        var found = new Dictionary<string, (string? Problem, int Count)>(StringComparer.Ordinal);

        void Walk(IReadOnlyList<HandlerNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (_catalog.StageOf(node.Op) == HandlerNodeStage.Preview)
                {
                    var problem = _catalog.ProblemOf(node.Op);
                    found[node.Op] = found.TryGetValue(node.Op, out var at)
                        ? (at.Problem, at.Count + 1)
                        : (problem, 1);
                }

                foreach (var body in node.Bodies.Values) Walk(body);
                foreach (var option in node.Options) Walk(option.Actions);
            }
        }

        Walk(list);
        return found.Select(pair => (pair.Key, pair.Value.Problem, pair.Value.Count)).ToList();
    }

    private Control PreviewWarning(IReadOnlyList<(string Op, string? Problem, int Count)> ops)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 2);

        var total = ops.Sum(op => op.Count);
        box.AddChild(MenuStyles.Label("有 " + total + " 块现在还跑不起来", 11, MenuStyles.Gold));

        foreach (var (op, problem, count) in ops)
        {
            var line = MenuStyles.Label(
                "· " + HandlerVocabulary.NodeTitle(op) + (count > 1 ? " ×" + count : "") + "：" +
                (problem ?? "这版还跑不起来。"), 10, MenuStyles.Dim);
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            line.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            line.TooltipText = op;
            box.AddChild(line);
        }

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_bottom", 4);
        margin.AddChild(box);
        return margin;
    }

    private static int CountNodes(IEnumerable<HandlerNode> list)
    {
        var count = 0;
        foreach (var node in list)
        {
            count++;
            foreach (var body in node.Bodies.Values) count += CountNodes(body);
            foreach (var option in node.Options) count += CountNodes(option.Actions);
        }
        return count;
    }

    private void Prompt(string title, string current, Action<string> accept)
    {
        _prompt.Title = title;
        _promptEdit.Text = current;
        _promptAccept = accept;
        _prompt.PopupCentered(new Vector2I(360, 120));
        _promptEdit.GrabFocus();
        _promptEdit.SelectAll();
    }

    public override void _Ready()
    {
        _valueMenu = new PopupMenu();
        AddChild(_valueMenu);
        _valueMenu.IdPressed += OnValuePicked;

        _prompt = new AcceptDialog { Title = "输入", OkButtonText = "确定" };
        _promptEdit = new LineEdit { CustomMinimumSize = new Vector2(320, 0) };
        _prompt.AddChild(_promptEdit);
        _prompt.RegisterTextEnter(_promptEdit);
        _prompt.Confirmed += () =>
        {
            var accept = _promptAccept;
            _promptAccept = null;
            accept?.Invoke(_promptEdit.Text);
        };
        AddChild(_prompt);
    }
}
