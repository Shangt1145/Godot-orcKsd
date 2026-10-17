using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;
using Kards.Ui.OrcBridge.OrcText;

namespace Kards.Ui;

/// <summary>
/// 效果编辑器（独立屏幕，从 DIY 卡牌编辑器按钮进入）：左侧＝结构编辑面（触发/守卫/op 序列）
/// ＋简易效果行（旧卡兼容），最右侧＝原始 DSL（编译产物 JSON，实时刷新）。
/// 编辑实时经 <see cref="Applied"/> 写回主编辑器草稿；「完成」返回主编辑器。
/// </summary>
public partial class DiyEffectEditorScreen : VBoxContainer
{
    internal Button SaveButton = null!, FinishButton = null!, UndoButton = null!, RedoButton = null!, AddOrcButton = null!, AddEffectButton = null!;
    internal OptionButton RecipePick = null!;
    internal Label OrcNotice = null!, _trigger = null!;
    internal ScrollContainer EditorScroll = null!;
    internal IReadOnlyList<EffectRow> EffectRows => _effects;
    /// <summary>每次编辑实时上抛（主编辑器把结果并回草稿并刷新预览）。</summary>
    public event Action<OrcEffectDraft[], DiyEffectDraft[]>? Applied;
    /// <summary>「完成」返回主编辑器（Main 负责 Show 切屏）。</summary>
    public event Action? Finished;
    public event Action? DraftTouched;
    private string _cardId = "diy/none", _cardType = "unit";
    private bool _loading;
    private readonly List<OrcEffectRow> _orcRows = [];
    private readonly List<EffectRow> _effects = [];
    private VBoxContainer _orcBox = null!, _effectBox = null!;
    private TextEdit _dslView = null!;
    private Label _dslNotice = null!;
    private Godot.Timer _orcTimer = null!;
    private readonly List<string> _history = [];
    private int _historyPos = -1;
    private DiyCardDraft _openedDraft = new();
    internal NativeEffectInspector NativeInspector = null!;
    private TabContainer _editorTabs=null!;
    private bool _nativeSync;
    public Func<IReadOnlyList<UiCardDefinition>>? Catalog {get;set;}
    public Func<IReadOnlyList<DiyCardDraft>>? Drafts {get;set;}
    public event Action<DiyCardDraft>? InspectorSaved;
    public event Action<DiyCardDraft>? DraftEdited;


    public void Initialize()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 10);
        var heading = new HBoxContainer(); AddChild(heading);
        var legacyTools=new HBoxContainer();
        var title = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; heading.AddChild(title);
        title.AddChild(UiStyles.Label("效果编辑器", 26, UiStyles.Gold));
        title.AddChild(UiStyles.Label("先选发生时机，再组合动作。修改会保留在草稿中。", 13, UiStyles.Dim));
        UndoButton = UiStyles.Button("撤销", Undo); legacyTools.AddChild(UndoButton);
        RedoButton = UiStyles.Button("重做", Redo); legacyTools.AddChild(RedoButton);
        RecipePick = DiyEditorScreen.Option(new[] { new UiDiyChoice("", "从常见效果开始…") }
            .Concat(OrcEffectEditing.Recipes.Select((r, i) => new UiDiyChoice(i.ToString(), r.Name))).ToArray());
        legacyTools.AddChild(RecipePick);
        legacyTools.AddChild(UiStyles.Button("添加", () =>
        {
            if (RecipePick.Selected <= 0) { OrcNotice.Text = "先选一个常见效果。"; return; }
            AddOrc(OrcEffectEditing.Recipes[RecipePick.Selected - 1].Effect);
            OrcNotice.Text = "已添加「" + OrcEffectEditing.Recipes[RecipePick.Selected - 1].Name + "」。";
        }));
        FinishButton = UiStyles.Button("完成", () => {if(NativeInspector.CommitPendingRaw())Finished?.Invoke();}); heading.AddChild(FinishButton);
        SaveButton=UiStyles.Button("保存卡牌",SaveNative);heading.AddChild(SaveButton);
        _editorTabs=new TabContainer{TabsVisible=false,SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_editorTabs);
        NativeInspector=new NativeEffectInspector{Name="效果搭建"};NativeInspector.Initialize();_editorTabs.AddChild(NativeInspector);
        NativeInspector.Edited+=NativeChanged;
        NativeInspector.RawEdited+=()=>DraftTouched?.Invoke();

        var body = new HBoxContainer { Name="旧结构兼容", SizeFlagsVertical = SizeFlags.ExpandFill }; _editorTabs.AddChild(body);
        var editPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(editPanel);
        var margin = new MarginContainer();
        foreach (var (name, value) in new[] { ("margin_left", 12), ("margin_top", 8), ("margin_right", 12), ("margin_bottom", 12) })
            margin.AddThemeConstantOverride(name, value);
        editPanel.AddChild(margin);
        var editColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        margin.AddChild(editColumn);editColumn.AddChild(legacyTools);
        OrcNotice = UiStyles.Label("", 12, UiStyles.Dim);
        OrcNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart; AddChild(OrcNotice);
        var editScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        editColumn.AddChild(editScroll); EditorScroll = editScroll;
        var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        form.AddThemeConstantOverride("separation", 10); editScroll.AddChild(form);

        var structureSec = Section(form, "效果结构（Inspector，可编辑）");
        structureSec.AddChild(UiStyles.Label("结构是编译真源；规则文本不随结构修改回写（改卡面文字请回卡牌编辑器）。", 12, UiStyles.Dim));
        _orcBox = new VBoxContainer(); _orcBox.AddThemeConstantOverride("separation", 10); structureSec.AddChild(_orcBox);
        AddOrcButton = UiStyles.Button("＋ 再加一条效果", () => AddOrc(new OrcEffectDraft()));
        structureSec.AddChild(AddOrcButton);

        var legacySec = Section(form, "简易效果行（旧卡兼容）");
        legacySec.AddChild(UiStyles.Label("九宫格行编辑器，表达不了引擎 21 动作面；新效果请用上方结构编辑。", 12, UiStyles.Dim));
        _trigger = UiStyles.Label("", 14, UiStyles.Gold); legacySec.AddChild(_trigger);
        _effectBox = new VBoxContainer(); legacySec.AddChild(_effectBox);
        AddEffectButton = UiStyles.Button("＋ 添加效果", () => AddEffect(new()));
        legacySec.AddChild(AddEffectButton);

        // ── 最右侧：原始 DSL（编译产物，实时刷新）──
        var dslPanel = new PanelContainer { CustomMinimumSize = new(390, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(dslPanel);
        var dslMargin = new MarginContainer();
        foreach (var (name, value) in new[] { ("margin_left", 12), ("margin_top", 8), ("margin_right", 12), ("margin_bottom", 12) })
            dslMargin.AddThemeConstantOverride(name, value);
        dslPanel.AddChild(dslMargin);
        var dslColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        dslMargin.AddChild(dslColumn);
        dslColumn.AddChild(UiStyles.Label("原始 DSL（编译产物）", 16, UiStyles.Gold));
        dslColumn.AddChild(UiStyles.Label("当前结构的引擎效果 DSL，编辑结构即实时更新；可直接复制给引擎工具核对。", 12, UiStyles.Dim));
        _dslView = new TextEdit
        {
            Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new(360, 300)
        };
        dslColumn.AddChild(_dslView);
        _dslNotice = UiStyles.Label("", 12, UiStyles.Dim);
        _dslNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart; dslColumn.AddChild(_dslNotice);
        dslColumn.AddChild(UiStyles.Button("复制到剪贴板", () => DisplayServer.ClipboardSet(_dslView.Text)));
        _orcTimer = new Godot.Timer { OneShot = true, WaitTime = 0.5 }; AddChild(_orcTimer);
        _orcTimer.Timeout += ()=>{OrcCheck();RefreshDsl();};
    }

    /// <summary>打开编辑器：以主编辑器当前草稿为工作副本（结构＋简易行），重置历史。</summary>
    internal void ShowCompatibility(){_editorTabs.TabsVisible=true;_editorTabs.CurrentTab=1;}
    public void Open(DiyCardDraft draft)
    {
        _openedDraft=draft.Snapshot();
        _cardId = draft.Id; _cardType = draft.CardType;
        _loading = true;
        foreach (var row in _orcRows) { _orcBox.RemoveChild(row); row.QueueFree(); } _orcRows.Clear();
        foreach (var row in _effects) { _effectBox.RemoveChild(row); row.QueueFree(); } _effects.Clear();
        foreach (var orc in draft.OrcEffects) AddOrcRow(orc);
        foreach (var effect in draft.Effects) AddEffect(effect);
        RenumberOrc();
        _trigger.Text = _cardType == "unit" ? "部署完成时执行" : "打出时执行";
        _history.Clear(); _historyPos = -1;
        _loading = false; Snap();
        OrcCheck(); RefreshDsl();
        NativeInspector.Catalog=Catalog;_nativeSync=true;NativeInspector.Open(InspectorCardRecord.FromDraft(draft));_nativeSync=false;
        _editorTabs.TabsVisible=draft.OrcEffects.Any(e=>e.DesktopJson is null)||draft.Effects.Length>0;
        _editorTabs.CurrentTab=draft.OrcEffects.Any(e=>e.DesktopJson is null)||draft.Effects.Length>0?1:0;
    }

    private void NativeChanged()
    {
        if(_nativeSync)return;
        DraftTouched?.Invoke();
        try
        {
            var updated=InspectorCardRecord.ApplyRecord(_openedDraft,NativeInspector.Document.Element());
            // Old finite declarations stay in their dedicated compatibility tab until explicitly replaced.
            var retained=OrcDrafts().Where(e=>e.DesktopJson is null).ToArray();
            updated=updated with {OrcEffects=updated.OrcEffects.Concat(retained).ToArray(),Effects=LegacyDrafts()};
            _openedDraft=updated;_cardId=updated.Id;_cardType=updated.CardType;
            _loading=true;
            foreach(var row in _orcRows){_orcBox.RemoveChild(row);row.QueueFree();}_orcRows.Clear();
            foreach(var effect in updated.OrcEffects)AddOrcRow(effect);
            _loading=false;RenumberOrc();
            DraftEdited?.Invoke(updated);_orcTimer.Start();
        }
        catch(Exception e){OrcNotice.Text="声明未写回："+e.Message;}
    }
    internal void SaveNative()
    {
        try
        {
            if(!NativeInspector.CommitPendingRaw())throw new InvalidDataException("完整声明 JSON 尚未应用；请修正后再保存。");
            var current=_editorTabs.CurrentTab==0
                ? InspectorCardRecord.ApplyRecord(_openedDraft,NativeInspector.Document.Element()) with {OrcEffects=OrcDrafts(),Effects=LegacyDrafts()}
                : _openedDraft with {OrcEffects=OrcDrafts(),Effects=LegacyDrafts()};
            InspectorSaved?.Invoke(current);_openedDraft=current.Snapshot();
            var check=DiyCardCompiler.Validate(current,Catalog?.Invoke()??[]);
            OrcNotice.Text=check.Success?"卡牌已保存，可以用于对局。":"草稿已保存。部分效果暂不能用于对局，详细原因在高级工具中。";
        }
        catch(Exception e){OrcNotice.Text="保存失败，草稿仍保留："+e.Message;}
    }

    private static VBoxContainer Section(VBoxContainer parent, string title)
    {
        var panel = new PanelContainer(); parent.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var (name, value) in new[] { ("margin_left", 12), ("margin_top", 8), ("margin_right", 12), ("margin_bottom", 12) })
            margin.AddThemeConstantOverride(name, value);
        panel.AddChild(margin);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);
        box.AddChild(UiStyles.Label(title, 16, UiStyles.Gold));
        return box;
    }

    private void AddOrc(OrcEffectDraft draft)
    {
        // 指令卡的部署触发自动换打出触发（配方与空白效果同款口径）。
        if (_cardType == "order" && draft.Template == "deploy_basic")
            draft = draft with { Template = "played_basic", Hook = "card.played", Guard = "SelfCard" };
        AddOrcRow(draft);
        RenumberOrc();
        OrcChanged();
    }

    private void AddOrcRow(OrcEffectDraft draft)
    {
        var row = new OrcEffectRow(draft, OrcChanged, r =>
        {
            _orcRows.Remove(r); _orcBox.RemoveChild(r); r.QueueFree(); RenumberOrc(); OrcChanged();
        }, (r, offset) =>
        {
            var index = _orcRows.IndexOf(r); var target = Math.Clamp(index + offset, 0, _orcRows.Count - 1);
            _orcRows.RemoveAt(index); _orcRows.Insert(target, r); _orcBox.MoveChild(r, target); RenumberOrc(); OrcChanged();
        });
        _orcRows.Add(row); _orcBox.AddChild(row);
    }

    private void RenumberOrc()
    {
        for (var i = 0; i < _orcRows.Count; i++) _orcRows[i].SetIndex(i + 1);
        AddOrcButton.Disabled = _orcRows.Count >= 8;
    }

    /// <summary>结构编辑统一入口：快照＋防抖编译校验＋实时写回主编辑器。</summary>
    private void OrcChanged()
    {
        if (_loading) return;
        RenumberOrc(); Snap(); _orcTimer.Start();
        Applied?.Invoke(OrcDrafts(), LegacyDrafts());
        if(!_nativeSync){_nativeSync=true;NativeInspector.Open(InspectorCardRecord.FromDraft(_openedDraft with {OrcEffects=OrcDrafts()}));_nativeSync=false;}
        RefreshDsl();
    }

    private void LegacyChanged()
    {
        if (_loading) return;
        Applied?.Invoke(OrcDrafts(), LegacyDrafts());
    }

    /// <summary>防抖编译校验：逐条编译编辑面，状态行＋行级错误。</summary>
    private void OrcCheck()
    {
        var drafts = OrcDrafts();
        NativeInspector.RefreshSourceCheck();
        if (drafts.Length == 0) { OrcNotice.Text = ""; return; }
        var compiled = OrcEffectEditing.Compile(_cardId.Replace('/', '.'), drafts);
        var problems = OrcEffectEditing.Problems(compiled).ToList();
        if(_editorTabs.CurrentTab==0)
        {
            var admission=DiyCardCompiler.Validate(_openedDraft with {OrcEffects=drafts,Effects=LegacyDrafts()},Catalog?.Invoke()??[]);
            problems.AddRange(admission.Errors.Where(e=>!problems.Contains(e)));
        }
        for (var i = 0; i < _orcRows.Count; i++)
            _orcRows[i].SetError(i < compiled.Count ? compiled[i].Error : null);
        OrcNotice.Text = !NativeInspector.AdvancedVisible ? (problems.Count==0?"设置已通过规则检查，保存后即可用于对局。":"还有设置需要检查，或存在规则暂不支持的效果。草稿可以保留，详情见高级工具。") : problems.Count == 0
            ? $"编译通过：{drafts.Length} 条效果。回卡牌编辑器保存后进对局生效。"
            : $"{problems.Count} 项需要检查：{string.Join("；", problems.Take(3))}";
        OrcNotice.AddThemeColorOverride("font_color", problems.Count == 0 ? UiStyles.Dim : new Color("dca58b"));
    }

    private OrcEffectDraft[] OrcDrafts() => _orcRows.Select(r => r.Read()).ToArray();
    private DiyEffectDraft[] LegacyDrafts() => _effects.Select(r => r.Read()).ToArray();

    /// <summary>右侧原始 DSL 面板：当前结构的编译产物（逐条注释＋JSON；失败条目显错误）。</summary>
    private void RefreshDsl()
    {
        var drafts = OrcDrafts();
        if (drafts.Length == 0)
        {
            _dslView.Text = "// 还没有结构效果；先添加一条，或回卡牌编辑器用规则文本解析。";
            _dslNotice.Text = "";NativeInspector.SetEngineReport(_dslView.Text);
            return;
        }
        var compiled = OrcEffectEditing.Compile(_cardId.Replace('/', '.'), drafts);
        _dslView.Text = string.Join("\n\n", compiled.Select((e, i) =>
            e.Error is null
                ? $"// 效果 {i + 1}（{OrcEffectEditing.Templates.FirstOrDefault(t => t.Id == e.Draft.Template && t.Guard.ToString() == e.Draft.Guard)?.Label ?? e.Draft.Template}）\n{e.JsonText}"
                : $"// 效果 {i + 1} 编译失败：{e.Error}"));
        var problems = OrcEffectEditing.Problems(compiled);
        NativeInspector.SetEngineReport(_dslView.Text);
        _dslNotice.Text = problems.Count == 0 ? $"共 {compiled.Count} 条，编译通过。" : $"{problems.Count} 项编译失败，见上方注释。";
    }

    // ── 撤销 / 重做（对齐 Electron 检查器：结构快照差分，50 步上限）──
    private void Snap()
    {
        if (_loading) return;
        var state = System.Text.Json.JsonSerializer.Serialize(OrcDrafts());
        if (_historyPos >= 0 && _history[_historyPos] == state) return;
        if (_historyPos < _history.Count - 1) _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
        _history.Add(state);
        if (_history.Count > 50) _history.RemoveAt(0);
        _historyPos = _history.Count - 1;
        SyncHistory();
    }
    private void Undo()
    {
        if (_historyPos <= 0) return;
        _historyPos--; ApplyHistory();
    }
    private void Redo()
    {
        if (_historyPos >= _history.Count - 1) return;
        _historyPos++; ApplyHistory();
    }
    private void ApplyHistory()
    {
        var drafts = System.Text.Json.JsonSerializer.Deserialize<OrcEffectDraft[]>(_history[_historyPos]) ?? [];
        _loading = true;
        foreach (var row in _orcRows) { _orcBox.RemoveChild(row); row.QueueFree(); } _orcRows.Clear();
        foreach (var draft in drafts) AddOrcRow(draft);
        RenumberOrc();
        _loading = false;
        Applied?.Invoke(OrcDrafts(), LegacyDrafts()); OrcCheck(); SyncHistory(); RefreshDsl();
    }
    private void SyncHistory()
    {
        UndoButton.Disabled = _historyPos <= 0;
        RedoButton.Disabled = _historyPos >= _history.Count - 1;
    }

    internal EffectRow AddEffect(DiyEffectDraft effect)
    {
        var row = new EffectRow(effect, LegacyChanged, r =>
        {
            _effects.Remove(r); _effectBox.RemoveChild(r); r.QueueFree(); LegacyChanged();
        }, (r, offset) =>
        {
            var index = _effects.IndexOf(r); var target = Math.Clamp(index + offset, 0, _effects.Count - 1);
            _effects.RemoveAt(index); _effects.Insert(target, r); _effectBox.MoveChild(r, target); LegacyChanged();
        });
        _effects.Add(row); _effectBox.AddChild(row);
        if (!_loading) LegacyChanged();
        return row;
    }

    // ───────────────────────── 简易效果行（旧卡兼容） ─────────────────────────
    internal partial class EffectRow : PanelContainer
    {
        internal readonly OptionButton Op, Target, Until;
        internal readonly SpinBox Amount, Attack, Defense;
        private readonly Label _amountLabel, _attackLabel, _defenseLabel;
        private bool _updating;
        internal EffectRow(DiyEffectDraft draft, Action changed, Action<EffectRow> remove, Action<EffectRow, int> move)
        {
            var box = new VBoxContainer(); AddChild(box);
            var top = new HBoxContainer(); box.AddChild(top);
            Op = DiyEditorScreen.Option(DiyCardCompiler.Operations); top.AddChild(Op);
            Target = DiyEditorScreen.Option(DiyCardCompiler.Targets); top.AddChild(Target);
            top.AddChild(UiStyles.Button("↑", () => move(this, -1)));
            top.AddChild(UiStyles.Button("↓", () => move(this, 1)));
            top.AddChild(UiStyles.Button("×", () => remove(this)));
            var values = new HBoxContainer(); box.AddChild(values);
            _amountLabel = UiStyles.Label("数量", 12, UiStyles.Dim); values.AddChild(_amountLabel);
            Amount = DiyEditorScreen.Number(-20, 99); values.AddChild(Amount);
            _attackLabel = UiStyles.Label("攻击", 12, UiStyles.Dim); values.AddChild(_attackLabel);
            Attack = DiyEditorScreen.Number(-20, 20); values.AddChild(Attack);
            _defenseLabel = UiStyles.Label("防御", 12, UiStyles.Dim); values.AddChild(_defenseLabel);
            Defense = DiyEditorScreen.Number(-20, 20); values.AddChild(Defense);
            Until = DiyEditorScreen.Option(DiyCardCompiler.Durations); values.AddChild(Until);
            DiyEditorScreen.Select(Op, draft.Op); DiyEditorScreen.Select(Target, draft.Target); DiyEditorScreen.Select(Until, draft.Until);
            Amount.Value = draft.Amount; Attack.Value = draft.Attack; Defense.Value = draft.Defense;
            foreach (var o in new[] { Op, Target, Until }) o.ItemSelected += _ => { if (!_updating) changed(); };
            foreach (var n in new[] { Amount, Attack, Defense }) n.ValueChanged += _ => { if (!_updating) changed(); };
        }
        internal DiyEffectDraft Read() => new()
        {
            Op = DiyEditorScreen.Value(Op), Target = DiyEditorScreen.Value(Op) is "draw" or "gainPoint" or "gainSlot" ? "self" : DiyEditorScreen.Value(Target),
            Amount = DiyEditorScreen.Value(Op) == "pin" ? 1 : (int)Amount.Value,
            Attack = (int)Attack.Value, Defense = (int)Defense.Value,
            Until = DiyEditorScreen.Value(Op) is "buff" or "costMod" ? DiyEditorScreen.Value(Until) : ""
        };
        internal void UpdateType(bool unit)
        {
            _updating = true;
            var op = DiyEditorScreen.Value(Op); var resource = op is "draw" or "gainPoint" or "gainSlot";
            Target.SetItemText(0, resource ? "自己" : "本单位");
            Target.Disabled = resource;
            Target.SetItemDisabled(0, !unit && !resource);
            if (!unit && !resource && Target.Selected == 0) Target.Select(1);
            var buff = op == "buff";
            _attackLabel.Visible = Attack.Visible = _defenseLabel.Visible = Defense.Visible = buff;
            _amountLabel.Visible = Amount.Visible = !buff && op != "silence";
            Amount.Editable = op != "pin"; if (op == "pin") Amount.Value = 1;
            Until.Visible = buff || op == "costMod";
            _updating = false;
        }
    }

    // ───────────────────────── OrC Inspector 结构编辑行 ─────────────────────────

    /// <summary>结构效果行：触发条目（模板＋守卫成对切换）＋动作序列（增删/排序/逐参数编辑）。</summary>
    internal sealed partial class OrcEffectRow : PanelContainer
    {
        private readonly OptionButton _trigger;
        private readonly Label _title, _error;
        private readonly VBoxContainer _opsBox;
        private readonly List<OrcOpRow> _ops = [];
        private readonly Action _changed;
        private readonly Action<OrcEffectRow> _remove;
        private readonly Action<OrcEffectRow, int> _move;
        private bool _mute = true;
        private OrcEffectDraft? _desktopDraft;

        internal OrcEffectRow(OrcEffectDraft draft, Action changed, Action<OrcEffectRow> remove, Action<OrcEffectRow, int> move)
        {
            _changed = changed; _remove = remove; _move = move;
            var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 8); AddChild(box);
            var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 8); box.AddChild(head);
            _title = UiStyles.Label("效果", 14, UiStyles.Gold); head.AddChild(_title);
            head.AddChild(UiStyles.Button("↑",()=>_move(this,-1)));
            head.AddChild(UiStyles.Button("↓",()=>_move(this,1)));
            if(draft.DesktopJson is { } graph)
            {
                _desktopDraft=draft.Snapshot();
                head.AddChild(UiStyles.Button("复制",()=>DisplayServer.ClipboardSet(_desktopDraft.DesktopJson!)));
                head.AddChild(UiStyles.Button("×",()=>_remove(this)));
                _trigger=null!;_opsBox=null!;
                box.AddChild(UiStyles.Label("完整声明：触发、变量、条件、嵌套效果与未知字段均保留。完整表单位于“完整 Inspector”标签。",12,UiStyles.Dim));
                var editor=new TextEdit {Text=System.Text.Json.Nodes.JsonNode.Parse(graph)!.ToJsonString(new(){WriteIndented=true}),
                    CustomMinimumSize=new(440,210),SizeFlagsHorizontal=SizeFlags.ExpandFill,WrapMode=TextEdit.LineWrappingMode.Boundary};
                box.AddChild(editor);_error=UiStyles.Label("",12,new("dca58b"));box.AddChild(_error);
                editor.TextChanged+=()=>
                {
                    try
                    {
                        using var doc=System.Text.Json.JsonDocument.Parse(editor.Text);
                        if(doc.RootElement.ValueKind!=System.Text.Json.JsonValueKind.Object)throw new System.Text.Json.JsonException("效果必须是对象");
                        _desktopDraft=_desktopDraft with {DesktopJson=doc.RootElement.GetRawText(),Template=doc.RootElement.TryGetProperty("trigger",out var t)?t.GetString()??"unknown":"unknown"};
                        _error.Text="";_changed();
                    }
                    catch(System.Text.Json.JsonException e){_error.Text="JSON 未生效，上一份有效结构仍保留："+e.Message;}
                };
                return;
            }
            _trigger = DiyEditorScreen.Option(OrcEffectEditing.Templates.Select(t => new UiDiyChoice(t.Id + "|" + t.Guard, t.Label)));
            _trigger.CustomMinimumSize = new(250, 34); head.AddChild(_trigger);
            head.AddChild(UiStyles.Button("×", () => _remove(this)));
            _opsBox = new VBoxContainer(); _opsBox.AddThemeConstantOverride("separation", 8); box.AddChild(_opsBox);
            var addRow = new HBoxContainer(); addRow.AddThemeConstantOverride("separation", 8); box.AddChild(addRow);
            var pick = DiyEditorScreen.Option(new[] { new UiDiyChoice("", "选择动作…") }
                .Concat(OrcEffectEditing.Ops.Select(o => new UiDiyChoice(o.Id, o.Label + " · " + o.Group))));
            pick.CustomMinimumSize = new(210, 34); addRow.AddChild(pick);
            addRow.AddChild(UiStyles.Button("添加动作", () =>
            {
                if (pick.Selected <= 0) return;
                AddOp(OrcEffectEditing.Seed(OrcEffectEditing.Ops[pick.Selected - 1].Id));
                _changed();
            }));
            _error = UiStyles.Label("", 12, new("dca58b"));
            _error.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(_error);
            foreach (var op in draft.Ops) AddOp(op);
            SelectTrigger(draft);
            _trigger.ItemSelected += _ => { if (!_mute) _changed(); };
            _mute = false;
        }

        private void SelectTrigger(OrcEffectDraft draft)
        {
            for (var i = 0; i < _trigger.ItemCount; i++)
                if (_trigger.GetItemMetadata(i).AsString() == draft.Template + "|" + draft.Guard) { _trigger.Select(i); return; }
        }

        private void AddOp(OrcOpDraft draft)
        {
            var row = new OrcOpRow(draft, _changed, r =>
            {
                _ops.Remove(r); _opsBox.RemoveChild(r); r.QueueFree(); _changed();
            }, (r, offset) =>
            {
                var index = _ops.IndexOf(r); var target = Math.Clamp(index + offset, 0, _ops.Count - 1);
                _ops.RemoveAt(index); _ops.Insert(target, r); _opsBox.MoveChild(r, target); _changed();
            });
            _ops.Add(row); _opsBox.AddChild(row);
        }

        internal OrcEffectDraft Read()
        {
            if(_desktopDraft is not null)return _desktopDraft.Snapshot();
            var entry = _trigger.Selected >= 0 && _trigger.Selected < OrcEffectEditing.Templates.Count
                ? OrcEffectEditing.Templates[_trigger.Selected] : null;
            return new OrcEffectDraft
            {
                Template = entry?.Id ?? "deploy_basic", Hook = entry?.Hook,
                Guard = (entry?.Guard ?? OrcTriggerGuard.SelfUnit).ToString(),
                Ops = _ops.Select(o => o.Read()).ToArray(),
            };
        }

        internal void SetIndex(int index) => _title.Text = "效果 " + index;
        internal void SetError(string? error) => _error.Text = error ?? "";
    }

    /// <summary>动作行：参数面按 op 目录显隐（目录＝编译面合法取值，编辑即所见）。</summary>
    internal sealed partial class OrcOpRow : VBoxContainer
    {
        private readonly OrcOpInfo _info;
        private readonly OptionButton _sel = null!, _side = null!, _selZone = null!, _fUnit = null!, _fKeyword = null!,
            _fFaction = null!, _keyword = null!, _opZone = null!, _until = null!, _cond = null!, _condDim = null!;
        private readonly SpinBox _selCount = null!, _amount = null!, _count = null!, _attack = null!, _defense = null!, _value = null!;
        private readonly LineEdit _name = null!, _condValue = null!;
        private readonly CheckBox _excludeSelf = null!;
        private readonly Label _summary = null!;
        private readonly List<(Control Wrap, Func<bool> Show)> _visibility = [];
        private readonly Action _changed;
        private bool _mute = true;

        internal OrcOpRow(OrcOpDraft draft, Action changed, Action<OrcOpRow> remove, Action<OrcOpRow, int> move)
        {
            _changed = changed;
            _info = OrcEffectEditing.Op(draft.Op) ?? OrcEffectEditing.Ops[0];
            var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 6); AddChild(head);
            _summary = UiStyles.Label("", 13);
            _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill; head.AddChild(_summary);
            head.AddChild(UiStyles.Button("↑", () => move(this, -1)));
            head.AddChild(UiStyles.Button("↓", () => move(this, 1)));
            head.AddChild(UiStyles.Button("×", () => remove(this)));
            var grid = new GridContainer { Columns = 4 };
            grid.AddThemeConstantOverride("h_separation", 12); grid.AddThemeConstantOverride("v_separation", 6);
            AddChild(grid);

            // 参数面：每个控件带小标签（标签在上），按 op 目录显隐——挤在一起的可读性问题在源头杜绝。
            _sel = DiyEditorScreen.Option(OrcEffectEditing.Sels); When(DiyEditorScreen.Field(grid, "选靶方式", _sel), () => _info.Target);
            _side = DiyEditorScreen.Option(OrcEffectEditing.Sides); When(DiyEditorScreen.Field(grid, "阵营", _side), () => _info.Target && V(_sel) != "self");
            _selZone = DiyEditorScreen.Option(OrcEffectEditing.SelZones); When(DiyEditorScreen.Field(grid, "区域", _selZone), () => _info.Target && V(_sel) != "self");
            _selCount = DiyEditorScreen.Number(1, 10); When(DiyEditorScreen.Field(grid, "数量", _selCount), () => _info.Target && V(_sel) == "random");
            _fUnit = DiyEditorScreen.Option(OrcEffectEditing.UnitTypes); When(DiyEditorScreen.Field(grid, "限兵种", _fUnit), () => _info.Target && V(_sel) != "self");
            _fKeyword = DiyEditorScreen.Option(new[] { new UiDiyChoice("", "不限词条") }.Concat(OrcEffectEditing.KeywordChoices));
            When(DiyEditorScreen.Field(grid, "限词条", _fKeyword), () => _info.Target && V(_sel) != "self");
            _fFaction = DiyEditorScreen.Option(OrcEffectEditing.Factions); When(DiyEditorScreen.Field(grid, "限阵营", _fFaction), () => _info.Target && V(_sel) != "self");
            _excludeSelf = new CheckBox { Text = "排除自身" }; When(DiyEditorScreen.Field(grid, "排除自身", _excludeSelf), () => _info.Target && V(_sel) != "self");
            _amount = DiyEditorScreen.Number(_info.AmountMin, Math.Max(_info.AmountMax, _info.AmountMin));
            When(DiyEditorScreen.Field(grid, _info.AmountLabel is { Length: > 0 } ? _info.AmountLabel : "数值", _amount), () => _info.Amount);
            _count = DiyEditorScreen.Number(1, 10); When(DiyEditorScreen.Field(grid, "张数", _count), () => _info.Count);
            _attack = DiyEditorScreen.Number(-20, 20); When(DiyEditorScreen.Field(grid, "攻击增减", _attack), () => _info.AttackDefense);
            _defense = DiyEditorScreen.Number(-20, 20); When(DiyEditorScreen.Field(grid, "防御增减", _defense), () => _info.AttackDefense);
            _keyword = DiyEditorScreen.Option(OrcEffectEditing.KeywordChoices); When(DiyEditorScreen.Field(grid, "词条", _keyword), () => _info.Keyword);
            _value = DiyEditorScreen.Number(0, 99); When(DiyEditorScreen.Field(grid, "词条参值（0＝无）", _value), () => _info.Keyword);
            _name = new LineEdit { MaxLength = 40 }; When(DiyEditorScreen.Field(grid, "卡名（需与卡牌全名一致）", _name), () => _info.Name);
            _opZone = DiyEditorScreen.Option(OrcEffectEditing.OpZones); When(DiyEditorScreen.Field(grid, "落点", _opZone), () => _info.Zone);
            _until = DiyEditorScreen.Option(OrcEffectEditing.UntilChoices); When(DiyEditorScreen.Field(grid, "期限", _until), () => _info.Until);
            _cond = DiyEditorScreen.Option(OrcEffectEditing.Conditions); When(DiyEditorScreen.Field(grid, "条件", _cond), () => true);
            _condDim = DiyEditorScreen.Option(OrcEffectEditing.ConditionDims); When(DiyEditorScreen.Field(grid, "过滤维度", _condDim), () => V(_cond) == OrcEffectEditing.EventCardFilter);
            _condValue = new LineEdit { MaxLength = 40 }; When(DiyEditorScreen.Field(grid, "过滤取值", _condValue), () => V(_cond) == OrcEffectEditing.EventCardFilter);

            Load(draft);
            foreach (var option in new[] { _sel, _side, _selZone, _fUnit, _fKeyword, _fFaction, _keyword, _opZone, _until, _cond, _condDim })
                option.ItemSelected += _ => { if (!_mute) { UpdateVisibility(); _changed(); } };
            foreach (var spin in new[] { _selCount, _amount, _count, _attack, _defense, _value })
                spin.ValueChanged += _ => { if (!_mute) _changed(); };
            _name.TextChanged += _ => { if (!_mute) _changed(); };
            _condValue.TextChanged += _ => { if (!_mute) _changed(); };
            _excludeSelf.Toggled += _ => { if (!_mute) _changed(); };
            UpdateVisibility();
            _mute = false;
        }

        private static string V(OptionButton option) => DiyEditorScreen.Value(option);

        private void Load(OrcOpDraft draft)
        {
            DiyEditorScreen.Select(_sel, draft.Sel ?? "one"); DiyEditorScreen.Select(_side, draft.Side ?? "enemy");
            DiyEditorScreen.Select(_selZone, draft.SelZone ?? ""); DiyEditorScreen.Select(_fUnit, draft.Filter?.UnitType ?? "");
            DiyEditorScreen.Select(_fKeyword, draft.Filter?.Keyword ?? ""); DiyEditorScreen.Select(_fFaction, draft.Filter?.Faction ?? "");
            _excludeSelf.ButtonPressed = draft.Filter?.ExcludeSelf ?? false;
            _selCount.Value = draft.SelCount ?? 1; _amount.Value = draft.Amount ?? _info.AmountMin;
            _count.Value = draft.Count ?? 1; _attack.Value = draft.Attack ?? 1; _defense.Value = draft.Defense ?? 1;
            DiyEditorScreen.Select(_keyword, draft.Keyword ?? OrcEffectEditing.DefaultKeyword);
            _value.Value = draft.Value ?? 0; _name.Text = draft.Name ?? "";
            DiyEditorScreen.Select(_opZone, draft.OpZone ?? "frontline"); DiyEditorScreen.Select(_until, draft.Until ?? "");
            DiyEditorScreen.Select(_cond, draft.Condition ?? ""); DiyEditorScreen.Select(_condDim, Dims(draft.ConditionRaw).dim);
            _condValue.Text = Dims(draft.ConditionRaw).value;
        }

        private static (string dim, string value) Dims(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return ("keyword", "");
            var split = raw.IndexOf(':', StringComparison.Ordinal);
            return split < 0 ? ("keyword", raw) : (raw[..split], raw[(split + 1)..]);
        }

        internal OrcOpDraft Read() => new()
        {
            Op = _info.Id,
            Sel = _info.Target ? V(_sel) : null,
            Side = _info.Target && V(_sel) != "self" ? V(_side) : null,
            SelZone = _info.Target && V(_sel) != "self" ? Null(V(_selZone)) : null,
            SelCount = _info.Target && V(_sel) == "random" ? (int)_selCount.Value : null,
            Filter = _info.Target && V(_sel) != "self" &&
                (V(_fUnit) != "" || V(_fKeyword) != "" || V(_fFaction) != "" || _excludeSelf.ButtonPressed)
                ? new OrcFilterDraft { UnitType = Null(V(_fUnit)), Keyword = Null(V(_fKeyword)), Faction = Null(V(_fFaction)), ExcludeSelf = _excludeSelf.ButtonPressed }
                : null,
            Amount = _info.Amount ? (int)_amount.Value : null,
            Count = _info.Count ? (int)_count.Value : null,
            Attack = _info.AttackDefense ? (int)_attack.Value : null,
            Defense = _info.AttackDefense ? (int)_defense.Value : null,
            Keyword = _info.Keyword ? V(_keyword) : null,
            Value = _info.Keyword && _value.Value > 0 ? (int)_value.Value : null,
            OpZone = _info.Zone ? V(_opZone) : null,
            Name = _info.Name ? _name.Text : null,
            Until = _info.Until ? Null(V(_until)) : null,
            Condition = Null(V(_cond)),
            ConditionRaw = V(_cond) == OrcEffectEditing.EventCardFilter ? V(_condDim) + ":" + _condValue.Text.Trim() : null,
        };

        private void UpdateVisibility()
        {
            foreach (var (wrap, show) in _visibility) wrap.Visible = show();
            _summary.Text = OrcEffectEditing.Summarize(Read());
        }

        private void When(Control wrap, Func<bool> show) => _visibility.Add((wrap, show));

        private static string? Null(string s) => s.Length == 0 ? null : s;
    }
}
