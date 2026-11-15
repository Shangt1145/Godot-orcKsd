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

    private sealed record EditorSnapshot(HandlerDocument Document, string? Parent);
    private readonly List<EditorSnapshot> _history = [];
    private int _historyAt;

    private VBoxContainer _blocks = null!;
    private ScrollContainer _canvas = null!;
    private VBoxContainer _issues = null!;
    private TextEdit _json = null!;
    private Label _summary = null!, _issueCount = null!, _cardLabel = null!;
    private Button _undo = null!, _redo = null!;

    private readonly ValueBlockChoices _valueMenu = new();
    private AcceptDialog _prompt = null!;
    private LineEdit _promptEdit = null!;
    private Action<string>? _promptAccept;

    private PanelContainer _toolbox = null!;
    private VBoxContainer _toolboxList = null!;
    private LineEdit _toolboxSearch = null!;
    private string _toolboxCategory = "全部分类";
    private readonly Dictionary<string, Button> _categoryButtons = [];
    private readonly HashSet<HandlerNode> _expandedOptions = [];
    private Label _toolboxTarget = null!;
    private Button _toolboxToggle = null!;
    private string _pendingLabel = "整段的最后";
    private Action<HandlerValue?>? _valueCommit;
    private HandlerValue? _valueCurrent;
    private List<ScopeVar> _valueScope = [];
    private List<HandlerValue> _readOptions = [];
    private List<string> _valueLiterals = [];
    private List<HandlerNode>? _pendingInsert;
    private HandlerNode? _pendingElseOwner;
    private bool _toolboxOpen = true;

    private HashSet<HandlerNode> _errorNodes = [];
    /// <summary>节点 → 它的那块积木。插入后要滚到它那儿，否则「点了没反应」（块落在折叠区外）。</summary>
    private readonly Dictionary<HandlerNode, Control> _nodeControls = [];

    /// <summary>保存出口（复杂 DIY）：返回 null＝当前没有打开的卡包编辑会话。</summary>
    private Func<HandlerDslAuthoring?>? _authoring;
    private Func<IReadOnlyDictionary<string, string>>? _effectNames;
    public string EffectTitle => _cardLabel.Text;
    /// <summary>上一次保存的结果（跨 Rebuild 保留：重画会清空检查结果列，作者的反馈不能被顺手抹掉）。</summary>
    private string? _saveNote;
    private bool _saveFailed;

    public event Action? Finished;
    public event Action? TextAuthoringRequested;
    public event Action<string>? Changed;
    public event Action<HandlerDslSaveResult>? Saved;
    private string? _targetEffect, _targetParent;

    private string _savedJson = "";
    private string? _savedParent;
    private Action? _afterSave;
    public void ConfigureTarget(string? effect, string? parent)
    {
        _targetEffect = effect; _targetParent = parent;
        var authoring = _authoring?.Invoke();
        _triggerParents = authoring?.DescribeParents() ?? [];
        _targetParent = _triggerParents.FirstOrDefault(p => p.ParentId == (parent ?? DefaultParentId))?.ParentId ?? _triggerParents.FirstOrDefault()?.ParentId;
        if (authoring is not null && CurrentTrigger is { } option) ApplyView(authoring, option, false);
        _history.Clear(); _historyAt = 0; _history.Add(new(_doc.Clone(), _targetParent));
        Rebuild();
        _savedJson = _doc.ToJsonText(_catalog);
        _savedParent = _targetParent;
        if (authoring is not null && effect is not null && !System.IO.File.Exists(System.IO.Path.Combine(authoring.PackRoot, "handler-dsl", effect + ".hdsl.json"))
            && authoring.ReadExistingDsl(effect) is null)
        {
            _saveNote = "此效果没有积木源文件，原有行为已保留。当前画布用于重新搭建；保存后才替换卡牌引用。";
            Rebuild();
        }
    }

    public void GuardLeaving(Action action)
    {
        if (_doc.ToJsonText(_catalog) == _savedJson && _targetParent == _savedParent) { action(); return; }
        var dialog = new ConfirmationDialog { Title = "积木尚未保存", DialogText = "保存当前积木后离开，或继续编辑。", OkButtonText = "保存后离开", CancelButtonText = "继续编辑" }; AddChild(dialog);
        dialog.Confirmed += () => { dialog.Hide(); dialog.QueueFree(); _afterSave = action; OpenSaveDialog(); };
        dialog.AddButton("不保存，离开", true, "discard");
        dialog.CustomAction += key => { if (key == "discard")
        { _savedJson = _doc.ToJsonText(_catalog); _savedParent = _targetParent; dialog.Hide(); dialog.QueueFree(); action(); } };
        dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }

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
    public void ConfigureEffectNames(Func<IReadOnlyDictionary<string, string>> names) => _effectNames = names;

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
        _history.Add(new(_doc.Clone(), _targetParent));
        _savedJson = _doc.ToJsonText(_catalog);
        _savedParent = _targetParent;
        Rebuild();
    }

    public void Open(string title, HandlerDocument? doc = null)
    {
        _pendingElseOwner = null;
        _expandedOptions.Clear();
        _expandedTargetSteps.Clear();
        SetToolboxCategory("全部分类"); _toolboxSearch.Text = "";
        _targetEffect = _targetParent = null;
        _triggerParents = _authoring?.Invoke()?.DescribeParents() ?? [];
        RestoreTrigger(_triggerParents.FirstOrDefault(p => p.ParentId == DefaultParentId)?.ParentId ?? _triggerParents.FirstOrDefault()?.ParentId);
        _savedParent = _targetParent;
        _afterSave = null;
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
                + "去 DIY 工坊 → 我的卡包 → 打开一个卡包，再从顶部「积木 Inspector」进来。";
            _saveFailed = true;
        }
        _history.Clear();
        _historyAt = 0;
        _history.Add(new(_doc.Clone(), _targetParent));
        _savedJson = _doc.ToJsonText(_catalog);
        _pendingInsert = _doc.Actions;
        _pendingLabel = "整段的最后";
        Rebuild();
    }

    // ── 骨架 ───────────────────────────────────────────────────────────────

    private Control BuildHeader()
    {
        var header = new VBoxContainer(); header.AddThemeConstantOverride("separation", 12);
        var head = new HBoxContainer();
        header.AddChild(head);
        head.AddThemeConstantOverride("separation", 10);

        var title = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddChild(MenuStyles.Label("积木效果工作台", 26, MenuStyles.Ink));
        _cardLabel = MenuStyles.Label("样例：打出时", 13, MenuStyles.Dim);
        title.AddChild(_cardLabel);
        head.AddChild(title);

        _undo = MenuStyles.Button("撤销", Undo);
        _redo = MenuStyles.Button("重做", Redo);
        _toolboxToggle = MenuStyles.Button("工具箱 ▾", () => SetToolboxOpen(!_toolboxOpen));
        _toolboxToggle.TooltipText = "所有能用的积木都在左栏，点一块就插进去";
        head.AddChild(_toolboxToggle);
        head.AddChild(MenuStyles.Button("自动解析器", () => GuardLeaving(() => TextAuthoringRequested?.Invoke())));
        head.AddChild(_undo);
        head.AddChild(_redo);
        var save = MenuStyles.Button("保存到卡包", OpenSaveDialog);
        save.TooltipText = "保存当前积木，更新正在编辑的卡牌效果或包内效果。";
        MenuStyles.Primary(save);
        head.AddChild(save);
        head.AddChild(MenuStyles.Button("完成", () => GuardLeaving(() => Finished?.Invoke())));
        return header;
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
        var canvasPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        canvasPanel.AddThemeStyleboxOverride("panel", MenuStyles.Box(new("151f1b"), new("405448"), 1, 18));
        var canvasContent = new VBoxContainer(); canvasContent.AddThemeConstantOverride("separation", 12); canvasPanel.AddChild(canvasContent);
        canvasContent.AddChild(MenuStyles.Label("执行顺序", 18, MenuStyles.Ink));
        canvasContent.AddChild(MenuStyles.Label("从左侧添加动作，按从上到下的顺序执行。", 13, MenuStyles.Dim));
        canvasContent.AddChild(scroll); body.AddChild(canvasPanel);
        body.AddChild(BuildSide());
        return body;
    }

    private Control BuildSide()
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(252, 0) };
        panel.AddThemeStyleboxOverride("panel", MenuStyles.Box(new("1c2822"), new("405448"), 1, 14));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        var head = new HBoxContainer();
        head.AddChild(MenuStyles.Label("效果检查", 18, MenuStyles.Ink));
        _summary = MenuStyles.Label("", 12, MenuStyles.Dim);
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        head.AddChild(_summary);
        box.AddChild(head);

        var source = new AcceptDialog { Title = "积木源文档", MinSize = new(800, 540) }; panel.AddChild(source);
        _json = new TextEdit
        {
            Editable = false,
            CustomMinimumSize = new Vector2(760, 480),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _json.AddThemeFontSizeOverride("font_size", 12);
        source.AddChild(_json);

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
        var guidance = MenuStyles.Label("完成后保存到卡包。\n返回卡牌预览，再试打确认实际效果。", 13, MenuStyles.Dim);
        guidance.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(guidance);
        box.AddChild(MenuStyles.Button("查看积木源文档…", () => source.PopupCentered()));
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
        _toolbox = new PanelContainer { CustomMinimumSize = new Vector2(244, 0) };
        _toolbox.AddThemeStyleboxOverride("panel", MenuStyles.Box(new("1c2822"), new("405448"), 1, 14));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        _toolbox.AddChild(box);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        head.AddChild(MenuStyles.Label("工具箱", 13, MenuStyles.Gold));
        var count = MenuStyles.Label(_catalog.Nodes.Count + " 种动作", 11, MenuStyles.Dim);
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
        _toolboxSearch.AddThemeFontSizeOverride("font_size", 14);
        _toolboxSearch.TextChanged += _ => RefreshToolbox();
        box.AddChild(_toolboxSearch);
        var categories = new HFlowContainer(); categories.AddThemeConstantOverride("h_separation", 4);
        foreach (var category in new[] { "全部分类", "触发事件", "触发条件", "取值与运算" }.Concat(_catalog.Nodes.Select(n => n.Group).Distinct()))
        {
            var button = MenuStyles.Button(category, () => SetToolboxCategory(category));
            button.AddThemeFontSizeOverride("font_size", 11); button.SetMeta("block_category", category);
            _categoryButtons[category] = button; categories.AddChild(button);
        }
        box.AddChild(categories);

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
        AppendStructuralToolbox(query);
        var groups = _catalog.Nodes
            .Where(spec => Matches(spec, query))
            .Where(spec => _toolboxCategory == "全部分类" || spec.Group == _toolboxCategory)
            .GroupBy(spec => spec.Group)
            .OrderBy(group => GroupRank(group.Key))
            .ToList();

        if (groups.Count == 0 && _toolboxList.GetChildCount() == 0)
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
        var block = PaletteBlock(spec.Title + (spec.IsPreview ? " · 尚未开放运行" : ""), spec.IsPreview ? BlockTheme.Advanced : BlockTheme.ForGroup(spec.Group), BlockShape.Form.Stack, () => Insert(spec.Name));
        block.SetMeta("action_node", spec.Name);
        block.TooltipText = spec.IsPreview ? "还不能运行：" + (spec.Problem ?? "") : PinSummary(spec);
        return block;
    }

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
        var node = NewNode(op);
        Edit(() => InsertAtSelection(node));
        // 新块可能落在折叠区外，不滚过去就像「点了没反应」。
        // 要等**两帧**：第一帧容器才按新块的高度重排，这一帧拿到的是旧矩形，滚了也没用。
        if (_nodeControls.TryGetValue(node, out var control)) Reveal(control);
    }

    private void InsertAtSelection(HandlerNode node)
    {
        if (_pendingElseOwner is { } owner)
        {
            if (FindParentList(owner) is null) ResetInsertTarget();
            else owner.Bodies["else"] = _pendingInsert!;
            _pendingElseOwner = null;
        }
        (_pendingInsert ?? _doc.Actions).Add(node);
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
        _blocks.AddChild(TriggerHat());
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
            MinSize = new Vector2I(680, 500),
        };
        AddChild(dialog);

        var page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 10);
        var formScroll = new ScrollContainer { CustomMinimumSize = new(640, 400), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        dialog.AddChild(formScroll); formScroll.AddChild(page);

        var effectName = new LineEdit { Text = _cardLabel.Text, PlaceholderText = "给效果起个名字", MaxLength = 80 };
        DiyEditorScreen.Field(page, "效果名称", effectName);

        // 父是**模板**（R39）：104 个「信号 × 守卫档」的模板，成员恒为 onHook。
        // 缺省选「打出时」——卡牌效果里最常见的一档，省一次点击。
        var option = parents.FirstOrDefault(p => p.ParentId == _targetParent);
        if (option is null) { dialog.QueueFree(); ShowSaveNote("请先在画布中选择触发事件积木。", true); return; }
        page.AddChild(TriggerSummary(option));
        page.AddChild(MenuStyles.Button("返回画布修改触发积木", () => { _afterSave = null; dialog.Hide(); dialog.QueueFree(); }));

        var replace = new CheckBox
        {
            Text = "用这份积木重写一个已有效果",
            TooltipText = "更新选中的效果，使用它的卡牌也会更新；原始版本保留。",
        };
        page.AddChild(replace);

        // 目标与父是两个不同的 id：父是模板（往哪填），目标是效果（替换谁）。
        var targets = authoring.DescribeReplaceableEffects().ToList();
        if (_targetEffect is { } selectedEffect && targets.All(t => t.EffectId != selectedEffect))
            targets.Add(new HandlerReplaceTarget(selectedEffect, 0));
        var target = new BlockShelf
        {
            Name = "ReplaceTargetPicker",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Disabled = true,
        };
        var names = _effectNames?.Invoke();
        foreach (var item in targets)
            target.AddItem((names?.GetValueOrDefault(item.EffectId) is { } name && name != item.EffectId ? name : $"效果 {target.ItemCount + 1}")
                + (item.Cards > 0 ? $"（{item.Cards} 张卡在用）" : "（暂无卡引用）"));
        for (var i = 0; i < targets.Count; i++) target.SetItemMetadata(i, targets[i].EffectId);
        if (_targetEffect is not null) target.Select(Math.Max(0, targets.FindIndex(t => t.EffectId == _targetEffect)));
        if (targets.Count == 0) target.AddItem("（包里没有可替换的效果）");
        DiyEditorScreen.Field(page, "替换哪个效果（只有它会被改引用）", target);

        var product = new LineEdit { Name = "ProductId", Text = NewProductId() };
        var advanced = new VBoxContainer { Visible = false }; page.AddChild(advanced);
        DiyEditorScreen.Field(advanced, "效果标识（自动生成）", product);
        page.AddChild(MenuStyles.Button("高级保存设置…", () => advanced.Visible = !advanced.Visible));

        var hint = MenuStyles.Label(NewEffectHint, 12, MenuStyles.Dim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        page.AddChild(hint);

        // 选了哪个模板，画布的「视图字段」就跟着换成它的——作者能读什么由模板声明决定，
        // 而不是由这份界面猜（猜错的代价是「校验通过、装载期报字段不存在」）。
        replace.Toggled += _ => SyncReplaceMode(authoring, replace, target, targets, product, hint);
        target.ItemSelected += _ => SyncReplaceMode(authoring, replace, target, targets, product, hint);
        if (_targetEffect is not null) replace.ButtonPressed = true;

        var result = MenuStyles.Label("", 12, MenuStyles.Dim);
        result.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        page.AddChild(result);

        dialog.Confirmed += () =>
        {
            if (string.IsNullOrWhiteSpace(effectName.Text)) { result.Text = "请填写效果名称。"; return; }
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
                var message = saved.Success ? "已保存到草稿。返回卡牌后点击“应用修改”，用于构筑和新对局。" : saved.Describe();
                result.Text = message; result.TooltipText = saved.Describe();
                result.AddThemeColorOverride("font_color", saved.Success ? UiStyles.Green : new Color("e0705f"));
                ShowSaveNote(message, failed: !saved.Success);
                if (saved.Success)
                {
                    _cardLabel.Text = effectName.Text.Trim();
                    Saved?.Invoke(saved);
                    _targetEffect = saved.ProductId; _targetParent = option.ParentId;
                    Rebuild();
                    _savedJson = _doc.ToJsonText(_catalog);
                    _savedParent = _targetParent;
                    dialog.Hide(); dialog.QueueFree();
                    var after = _afterSave; _afterSave = null; after?.Invoke();
                }
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
        dialog.Canceled += () => { _afterSave = null; dialog.QueueFree(); };
        dialog.PopupCentered();
    }

    /// <summary>缺省父模板：打出时。模板库没有它才退回第一个。</summary>
    private const string DefaultParentId = "template.card.played.any";

    private const string NewEffectHint =
        "保存到当前卡包草稿。返回卡牌后可继续预览与试打，应用修改后用于新对局。";

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
        CheckBox replace,
        BlockShelf target,
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

        // Loading belongs to opening the editor. Toggling save options must never replace unsaved blocks.
        var existing = authoring.ReadExistingDsl(picked.EffectId);
        if (existing is null)
        {
            hint.Text = "这个效果还没有积木版本。保存后，使用原效果的卡牌会改用这份积木；原始版本保留。";
            return;
        }

        try
        {
            hint.Text = "保存当前积木并更新选中的效果，使用它的卡牌也会更新。之后可从效果列表重新打开。";
        }
        catch (FormatException error)
        {
            hint.Text = "上次的图读不出来：" + error.Message + "；保存会用当前画布替换它。";
        }
    }

    /// <summary>
    /// 把可读写字段切换到当前触发模板；没有字段时也清除上一模板的字段。
    /// </summary>
    private void ApplyView(HandlerDslAuthoring authoring, HandlerParentOption option, bool rebuild = true)
    {
        try
        {
            var fields = authoring.ViewFieldsOf(option.ParentId, option.Member);
            _view = fields;
            if (rebuild) Rebuild();
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
            if (TargetSequenceAt(list, i) is { } compound && !_expandedTargetSteps.Contains(list[i + 1]))
            {
                var loop = list[i + 1];
                DeclareBindings(node, local);
                var innerScope = new List<ScopeVar>(local) { new(loop.Text("as")!, "entity") };
                var group = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
                var rendered = RenderNode(compound.Action, basePath + "[" + (i + 1) + "].actions[0]", innerScope);
                _nodeControls[node] = _nodeControls[loop] = rendered;
                group.AddChild(rendered);
                if (_errorNodes.Contains(node) || _errorNodes.Contains(loop))
                    group.AddChild(MenuStyles.Label("目标条件需要修正，请展开选目标步骤查看。", 13, BlockTheme.Error));
                group.AddChild(SoftButton("展开选目标步骤…", BlockTheme.Interaction, "编辑完整的选择、筛选和遍历积木", () => { _expandedTargetSteps.Add(loop); Rebuild(); }));
                box.AddChild(group); i++; continue;
            }
            box.AddChild(RenderNode(node, basePath + "[" + i + "]", local));
            if (TargetSequenceAt(list, i) is not null)
            {
                var loop = list[i + 1];
                box.AddChild(SoftButton("收起选目标步骤", BlockTheme.Interaction, "用单张动作积木显示常用效果", () => { _expandedTargetSteps.Remove(loop); Rebuild(); }));
            }
            DeclareBindings(node, local);
        }

        box.AddChild(WithTopMargin(AddBlockButton(list, depth, where), 6));
        return box;
    }

    private void DeclareBindings(HandlerNode node, List<ScopeVar> local)
    {
            var spec = _catalog.Find(node.Op);
            if (node.Op == "let" && !string.IsNullOrEmpty(node.Text("name")))
                local.Add(new ScopeVar(node.Text("name")!, GuessType(node.Value("value"), local)));
            else if (node.Op == "ask" && !string.IsNullOrEmpty(node.Text("bind")))
                local.Add(new ScopeVar(node.Text("bind")!, "list<entity>"));
            else if (spec is { Outputs.Count: 1 } && node.Text("bind") is { Length: > 0 } bound)
                local.Add(new ScopeVar(bound, spec.Outputs[0].Type));
            else if (spec is { Outputs.Count: > 1 })
                foreach (var output in spec.Outputs)
                    if (node.OutputBinds.GetValueOrDefault(output.Name) is { Length: > 0 } name) local.Add(new ScopeVar(name, output.Type));
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
    private Button AddBlockButton(List<HandlerNode> target, int depth, string where, string? text = null, HandlerNode? elseOwner = null)
    {
        var active = ReferenceEquals(_pendingInsert, target);
        var tint = active ? MenuStyles.Gold : depth == 0 ? BlockTheme.Hat : BlockTheme.Advanced;
        var button = SoftButton(text ?? "＋ 添加积木", tint,
            active ? "工具箱里点一块，就插到这里" : "选这个位置，然后去左边工具箱点一块",
            () =>
            {
                _pendingInsert = target;
                _pendingElseOwner = elseOwner;
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
            CustomMinimumSize = new(340, 0),
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
                    var draft = ReferenceEquals(_pendingElseOwner, node) ? _pendingInsert! : elseList ?? [];
                    inner.AddChild(WithTopMargin(
                        AddBlockButton(draft, 1, where, "＋ 添加「不然」", node), 2));
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
        box.AddThemeConstantOverride("separation", 10);
        margin.AddChild(box);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(MenuStyles.Label(spec?.Title ?? node.Op, 18, ink));
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

        var optional = new VBoxContainer(); optional.AddThemeConstantOverride("separation", 10);
        foreach (var pin in spec.Inputs)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var label = MenuStyles.Label(spec.PinLabel(pin.Name) + (pin.Required ? "" : "（可选）"), 14,
                new Color(ink.R, ink.G, ink.B, 0.9f));
            label.CustomMinimumSize = new Vector2(82, 0);
            row.AddChild(label);
            var input = pin.Type == "entity" ? EntityTargetChip(node, pin.Name, scope) : pin.Kind switch
            {
                HandlerPinKind.Bind => TextChip(node.Text(pin.Name), "命名…", v => SetText(node, pin.Name, v), "绑定名"),
                HandlerPinKind.Text when node.Op == "script" && pin.Name == "body" => ScriptChip(node),
                HandlerPinKind.Text => TextChip(node.Text(pin.Name), "填写…", v => SetText(node, pin.Name, v), "文案"),
                HandlerPinKind.Field => TextChip(node.Text(pin.Name), "选字段…", v => SetText(node, pin.Name, v),
                    "视图字段", WritableFields(), HandlerVocabulary.ViewFieldLabel),
                HandlerPinKind.Enum => EnumChip(node, pin),
                _ => ValueChip(node.Value(pin.Name), v => SetValue(node, pin.Name, v), scope, !pin.Required,
                    pin.EnumValues),
            };
            input.SetMeta("handler_pin", pin.Name); row.AddChild(input);
            var filled = node.Value(pin.Name) is not null || !string.IsNullOrWhiteSpace(node.Text(pin.Name));
            if (pin.Required || filled) box.AddChild(row); else optional.AddChild(row);
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
            if (string.IsNullOrWhiteSpace(node.Text("bind"))) optional.AddChild(row); else box.AddChild(row);
        }
        else if (spec.Pin("bind") is null && spec.Outputs.Count > 1)
        {
            // 多输出的名字是**按输出名填**的，不是随便起几个：漏一个就是「那一路结果没名字」，
            // 校验器按 bind-scope 拒绝（想全部忽略就整段不写 bind）。所以这里一个输出一行，不给「加一个名字」的入口。
            foreach (var pin in spec.Outputs)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                var label = MenuStyles.Label(spec.PinLabel(pin.Name), 12, new Color(ink.R, ink.G, ink.B, 0.9f));
                label.CustomMinimumSize = new Vector2(82, 0);
                label.TooltipText = "输出 " + pin.Name + "（" + pin.Type + "）：给这一路结果起个名字，下游才能引用";
                row.AddChild(label);
                row.AddChild(TextChip(
                    node.OutputBinds.GetValueOrDefault(pin.Name), "命名…",
                    v => SetOutputBind(node, pin.Name, v), "绑定名"));
                if (node.OutputBinds.Count > 0) box.AddChild(row); else optional.AddChild(row);
            }
        }

        if (optional.GetChildCount() > 0)
        {
            optional.Visible = _expandedOptions.Contains(node);
            Button? toggle = null;
            toggle = SoftButton(optional.Visible ? "收起附加设置 ▾" : "附加设置 ▸", fill, "可选参数与结果命名", () =>
            {
                optional.Visible = !optional.Visible;
                if (optional.Visible) _expandedOptions.Add(node); else _expandedOptions.Remove(node);
                toggle!.Text = optional.Visible ? "收起附加设置 ▾" : "附加设置 ▸";
            });
            box.AddChild(toggle); box.AddChild(optional);
        }
        else optional.QueueFree();
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
        button.AddThemeFontSizeOverride("font_size", 14);
        button.AddThemeColorOverride("font_color", new Color(ink.R, ink.G, ink.B, 0.75f));
        button.AddThemeColorOverride("font_hover_color", ink);
        button.AddThemeStyleboxOverride("normal", UiStyles.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 4, 2));
        button.AddThemeStyleboxOverride("hover", UiStyles.Box(new Color(MenuStyles.Ink, 0.16f), new Color(0, 0, 0, 0), 4, 2));
        button.AddThemeStyleboxOverride("pressed", UiStyles.Box(new Color(MenuStyles.Ink, 0.26f), new Color(0, 0, 0, 0), 4, 2));
        button.Pressed += action;
        return button;
    }

    // ── 输入控件（做旧纸张的椭圆芯片）────────────────────────────────────

    private static Button InputChip(string text, bool empty)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new(36, 36),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        button.AddThemeFontSizeOverride("font_size", 14);
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
        return FixedLiteralBlock(current is null ? "未放入积木" : EnumLabel(pin.Name, current),
            pin.EnumValues ?? [], value => EnumLabel(pin.Name, value), value => SetText(node, pin.Name, value));
    }

    private Control FixedLiteralBlock(string label, IReadOnlyList<string> values, Func<string, string> caption, Action<string> commit)
    {
        return PaletteBlock(label, BlockTheme.Query, BlockShape.Form.Reporter, () =>
            ShowFixedBlocks("可拼入的积木", values.Select(value => (caption(value), (Action)(() => commit(value))))));
    }

    private void ShowFixedBlocks(string title, IEnumerable<(string Caption, Action Apply)> blocks)
    {
        var dialog = new AcceptDialog { Title = title, OkButtonText = "返回" }; AddChild(dialog);
        var scroll = new ScrollContainer { CustomMinimumSize = new(480, 360), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; box.AddThemeConstantOverride("separation", 8); scroll.AddChild(box);
        foreach (var (caption, apply) in blocks)
            box.AddChild(PaletteBlock(caption, BlockTheme.Query, BlockShape.Form.Reporter, () => { dialog.Hide(); dialog.QueueFree(); apply(); }));
        if (box.GetChildCount() == 0) box.AddChild(MenuStyles.Label("当前触发事件没有开放可用字段。", 14, MenuStyles.Dim));
        dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }

    private void SetToolboxCategory(string category)
    {
        _toolboxCategory = category;
        foreach (var (name, button) in _categoryButtons) button.Modulate = name == category ? Colors.White : new Color(.7f, .7f, .7f);
        RefreshToolbox();
    }

    /// <summary>取值集里的裸值 → 中文。只用于**枚举型**引脚，别拿它翻 read.of。</summary>
    private static string LiteralLabel(string value) => HandlerVocabulary.LiteralLabel(value);

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
        _ => LiteralLabel(value),
    };

    /// <summary>
    /// 文字芯片。<paramref name="label"/> 只翻**显示**（视图字段 <c>Defense</c> → 防御力），
    /// 存进文档的仍是原名——DSL 里不能出现中文别名（SPEC §0.4）。
    /// </summary>
    private Control TextChip(string? value, string placeholder, Action<string?> commit, string title,
        IReadOnlyList<string>? choices = null, Func<string, string>? label = null)
    {
        var shown = string.IsNullOrEmpty(value) ? placeholder : label?.Invoke(value!) ?? (title == "绑定名" ? HandlerVocabulary.BindingLabel(value!) : value!);
        if (choices is not null) return FixedLiteralBlock(shown, choices, v => label?.Invoke(v) ?? v, v => commit(v));
        var chip = InputChip(shown, string.IsNullOrEmpty(value));
        chip.Pressed += () =>
        {
            Prompt(title, title == "绑定名" ? HandlerVocabulary.BindingLabel(value ?? "") : value ?? "", text =>
                commit(string.IsNullOrWhiteSpace(text) ? null : title == "绑定名"
                    ? text == HandlerVocabulary.BindingLabel(value ?? "") ? value : HandlerVocabulary.BindingId(text) : text));
        };
        return chip;
    }

    private IReadOnlyList<string> WritableFields() =>
        _view.Values.Where(f => f.Writable).Select(f => f.Name).ToArray();

    // ── 值芯片（表达式树）────────────────────────────────────────────────

    private Control ValueChip(HandlerValue? value, Action<HandlerValue?> commit, List<ScopeVar> scope,
        bool optional = false, IReadOnlyList<string>? literals = null)
    {
        var text = value is null ? (optional ? "不用填" : "还没填")
            : value.Kind == HandlerValueKind.Operator ? HandlerVocabulary.OperatorLabel(value.Name ?? "?")
            : value is { Kind: HandlerValueKind.Literal, Text: { } literal } && literals is { Count: > 0 }
                ? LiteralLabel(literal) : value.Describe();
        if (literals is { Count: > 0 } && value is { Kind: HandlerValueKind.Literal, Text: not null })
            return PaletteBlock(text, BlockTheme.Query, BlockShape.Form.Reporter, () => OpenValueMenu(value, commit, scope, literals));
        var chip = InputChip(text, value is null);
        chip.Pressed += () => OpenValueMenu(value, commit, scope, literals);
        if (value is null || value.Kind == HandlerValueKind.Literal) return chip;

        var predicate = IsPredicate(value, scope);
        var block = new BlockShape { Shape = predicate ? BlockShape.Form.Predicate : BlockShape.Form.Reporter,
            Fill = BlockTheme.Query, ShowNotch = false, ShowTab = false, Name = "ValueExpressionBlock" };
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8); row.AddChild(chip);
        if (value.Kind == HandlerValueKind.Operator)
            for (var i = 0; i < value.Args.Count; i++)
            {
                var index = i;
                row.AddChild(ValueChip(value.Args[i], arg => commit(value.WithArg(index, arg ?? HandlerValue.Num(0))), scope));
            }
        if (value is { Kind: HandlerValueKind.Read, ReadTarget: { } target })
            row.AddChild(ValueChip(target, changed => { if (changed is not null) commit(HandlerValue.ReadFrom(changed, value.Name!)); }, scope));
        block.SetBody(BlockPadding(row));
        return block;
    }

    private void OpenValueMenu(HandlerValue? value, Action<HandlerValue?> commit, List<ScopeVar> scope,
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
        _valueMenu.AddItem("是", 3); _valueMenu.AddItem("否", 4);
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
                _valueMenu.AddItem("引用 ‹" + HandlerVocabulary.BindingLabel(scope[i].Name) + "›" + hint, 100 + i);
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

        ShowValueBlockPalette();
    }

    private void OnValuePicked(long id)
    {
        var commit = _valueCommit;
        if (commit is null) return;

        if (id == 999) { commit(null); return; }
        if (id is 3 or 4) { commit(HandlerValue.Bool(id == 3)); return; }
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
            while (args.Count < arity) args.Add(op is "and" or "or" or "not" ? HandlerValue.Bool(false) : HandlerValue.Num(0));
            commit(HandlerValue.Call(op, args.ToArray()));
        }
    }

    // ── 编辑 ───────────────────────────────────────────────────────────────

    private void Edit(Action? mutate = null)
    {
        mutate?.Invoke();
        _history.RemoveRange(_historyAt + 1, _history.Count - _historyAt - 1);
        _history.Add(new(_doc.Clone(), _targetParent));
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
        _doc = _history[_historyAt].Document.Clone();
        RestoreTrigger(_history[_historyAt].Parent);
        ResetInsertTarget();
        Rebuild();
    }

    private void Redo()
    {
        if (_historyAt >= _history.Count - 1) return;
        _historyAt++;
        _doc = _history[_historyAt].Document.Clone();
        RestoreTrigger(_history[_historyAt].Parent);
        ResetInsertTarget();
        Rebuild();
    }

    /// <summary>
    /// 撤销／重做／换文档都会把 <c>_doc</c> 换成**克隆体**，之前记住的插入目标就指向了旧对象树。
    /// 不重置的话，下一步插入会掉进一个已经不在画面上的列表里（点了没反应，最难查的那种）。
    /// </summary>
    private void ResetInsertTarget()
    {
        _pendingElseOwner = null;
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
        var selection = SelectionFor(node);
        var list = selection?.Parent ?? FindParentList(node);
        if (list is null) return;
        var index = selection?.SelectorIndex ?? list.IndexOf(node);
        var width = TargetSequenceAt(list, index) is not null ? 2 : 1;
        if (direction < 0 && index == 0 || direction > 0 && index + width >= list.Count) return;
        var next = direction < 0 ? index - (TargetSequenceAt(list, index - 2) is not null ? 2 : 1)
            : index + (TargetSequenceAt(list, index + width) is not null ? 2 : 1);
        Edit(() => { var moved = list.GetRange(index, width); list.RemoveRange(index, width); list.InsertRange(next, moved); });
    }

    private void RemoveNode(HandlerNode node)
    {
        if (SelectionFor(node) is { } selection) { Edit(() => selection.Parent.RemoveRange(selection.SelectorIndex, 2)); return; }
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
