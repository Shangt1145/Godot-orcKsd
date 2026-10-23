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
    internal IReadOnlyList<OrcEffectRow> StructureRows => _orcRows;
    /// <summary>每次编辑实时上抛（主编辑器把结果并回草稿并刷新预览）。</summary>
    public event Action<OrcEffectDraft[], DiyEffectDraft[]>? Applied;
    /// <summary>「完成」返回主编辑器（Main 负责 Show 切屏）。</summary>
    public event Action? Finished;
    public event Action? DraftTouched;
    private VBoxContainer _counterBox=null!;
    private string _counterSignature="";
    private string _cardId = "diy/none", _cardType = "unit";
    private bool _loading;
    private readonly List<OrcEffectRow> _orcRows = [];
    private readonly List<EffectRow> _effects = [];
    private VBoxContainer _orcBox = null!, _effectBox = null!;
    private TextEdit _dslView = null!;
    private Label _dslNotice = null!;
    internal Label CapabilitySummary=null!;
    private Label _cardCapability=null!;
    private Label _editingCard=null!;
    internal TabContainer EditorTabs=>_editorTabs;
    internal VBoxContainer CounterReferences=>_counterBox;
    internal TextEdit CompilationReport=>_dslView;
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
        title.AddChild(MenuStyles.Label("效果搭建", 30, MenuStyles.Ink));
        _editingCard=MenuStyles.Label("先选时机，再设条件与动作。",13,MenuStyles.Dim);title.AddChild(_editingCard);
        UndoButton = MenuStyles.Button("撤销", Undo); legacyTools.AddChild(UndoButton);
        RedoButton = MenuStyles.Button("重做", Redo); legacyTools.AddChild(RedoButton);
        RecipePick = DiyEditorScreen.Option(new[] { new UiDiyChoice("", "从常见效果开始…") }
            .Concat(OrcEffectEditing.Recipes.Select((r, i) => new UiDiyChoice(i.ToString(), r.Name))).ToArray());
        legacyTools.AddChild(RecipePick);
        legacyTools.AddChild(MenuStyles.Button("添加", () =>
        {
            if (RecipePick.Selected <= 0) { OrcNotice.Text = "先选一个常见效果。"; return; }
            AddOrc(OrcEffectEditing.Recipes[RecipePick.Selected - 1].Effect);
            OrcNotice.Text = "已添加「" + OrcEffectEditing.Recipes[RecipePick.Selected - 1].Name + "」。";
        }));
        FinishButton = MenuStyles.Button("完成", () => {if(NativeInspector.CommitPendingRaw())Finished?.Invoke();}); heading.AddChild(FinishButton);
        SaveButton=MenuStyles.Button("保存卡牌",SaveNative);heading.AddChild(SaveButton);
        var compatibility = MenuStyles.Button("兼容工具", () => ShowCompatibility()); heading.AddChild(compatibility);
        compatibility.TooltipText = "需要处理旧卡数据时再打开结构与检查工具";
        MenuStyles.Primary(SaveButton);
        CapabilitySummary=MenuStyles.Label("正在检查效果…",15,MenuStyles.Gold);AddChild(CapabilitySummary);
        _cardCapability=MenuStyles.Label("",12,new("dca58b"));_cardCapability.AutowrapMode=TextServer.AutowrapMode.WordSmart;AddChild(_cardCapability);
        _editorTabs=new TabContainer{TabsVisible=true,SizeFlagsVertical=SizeFlags.ExpandFill};AddChild(_editorTabs);
        NativeInspector=new NativeEffectInspector{Name="图形搭建"};NativeInspector.Initialize();_editorTabs.AddChild(NativeInspector);
        NativeInspector.Edited+=NativeChanged;
        NativeInspector.CountersRequested+=()=>_editorTabs.CurrentTab=3;
        NativeInspector.UsePlayerAuthoring();
        NativeInspector.RawEdited+=()=>DraftTouched?.Invoke();

        var body = new HBoxContainer { Name="结构编辑", SizeFlagsVertical = SizeFlags.ExpandFill }; _editorTabs.AddChild(body);
        var editPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(editPanel);
        var margin = new MarginContainer();
        foreach (var (name, value) in new[] { ("margin_left", 12), ("margin_top", 8), ("margin_right", 12), ("margin_bottom", 12) })
            margin.AddThemeConstantOverride(name, value);
        editPanel.AddChild(margin);
        var editColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        margin.AddChild(editColumn);editColumn.AddChild(legacyTools);
        OrcNotice = MenuStyles.Label("", 12, MenuStyles.Dim);
        OrcNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart; AddChild(OrcNotice);
        var editScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        editColumn.AddChild(editScroll); EditorScroll = editScroll;
        var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        form.AddThemeConstantOverride("separation", 10); editScroll.AddChild(form);

        var variables=Section(form,"本卡引用的计数器");
        var variableHint=MenuStyles.Label(OrcCounterCodegen.Notice,12,MenuStyles.Dim);variableHint.AutowrapMode=TextServer.AutowrapMode.WordSmart;variables.AddChild(variableHint);
        _counterBox=new VBoxContainer();variables.AddChild(_counterBox);
        var structureSec = Section(form, "时机、条件与动作");
        structureSec.AddChild(MenuStyles.Label("结构是编译真源；规则文本不随结构修改回写（改卡面文字请回卡牌编辑器）。", 12, MenuStyles.Dim));
        _orcBox = new VBoxContainer(); _orcBox.AddThemeConstantOverride("separation", 10); structureSec.AddChild(_orcBox);
        AddOrcButton = MenuStyles.Button("＋ 再加一条效果", () => AddOrc(new OrcEffectDraft()));
        structureSec.AddChild(AddOrcButton);

        var legacySec = Section(form, "简易效果行（旧卡兼容）");
        legacySec.AddChild(MenuStyles.Label("九宫格行编辑器，表达不了引擎 21 动作面；新效果请用上方结构编辑。", 12, MenuStyles.Dim));
        _trigger = MenuStyles.Label("", 14, MenuStyles.Gold); legacySec.AddChild(_trigger);
        _effectBox = new VBoxContainer(); legacySec.AddChild(_effectBox);
        AddEffectButton = MenuStyles.Button("＋ 添加效果", () => AddEffect(new()));
        legacySec.AddChild(AddEffectButton);

        // ── 最右侧：原始 DSL（编译产物，实时刷新）──
        var dslPanel = new PanelContainer { Name="编译结果", SizeFlagsHorizontal=SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _editorTabs.AddChild(dslPanel);
        var dslMargin = new MarginContainer();
        foreach (var (name, value) in new[] { ("margin_left", 12), ("margin_top", 8), ("margin_right", 12), ("margin_bottom", 12) })
            dslMargin.AddThemeConstantOverride(name, value);
        dslPanel.AddChild(dslMargin);
        var dslColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        dslMargin.AddChild(dslColumn);
        dslColumn.AddChild(MenuStyles.Label("原始 DSL（编译产物）", 16, MenuStyles.Gold));
        dslColumn.AddChild(MenuStyles.Label("当前结构的引擎效果 DSL，编辑结构即实时更新；可直接复制给引擎工具核对。", 12, MenuStyles.Dim));
        _dslView = new TextEdit
        {
            Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new(0, 160)
        };
        dslColumn.AddChild(_dslView);
        _dslNotice = MenuStyles.Label("", 12, MenuStyles.Dim);
        _dslNotice.AutowrapMode = TextServer.AutowrapMode.WordSmart; dslColumn.AddChild(_dslNotice);
        dslColumn.AddChild(MenuStyles.Button("复制到剪贴板", () => DisplayServer.ClipboardSet(_dslView.Text)));
        var countersPage=new ScrollContainer{Name="对局计数器",SizeFlagsVertical=SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};
        _editorTabs.AddChild(countersPage);
        var variablesPanel=variables.GetParent().GetParent<PanelContainer>();variablesPanel.Reparent(countersPage);
        variablesPanel.SizeFlagsHorizontal=SizeFlags.ExpandFill;
        _orcTimer = new Godot.Timer { OneShot = true, WaitTime = 0.5 }; AddChild(_orcTimer);
        _orcTimer.Timeout += ()=>{OrcCheck();RefreshDsl();};
    }

    /// <summary>打开编辑器：以主编辑器当前草稿为工作副本（结构＋简易行），重置历史。</summary>
    internal void ShowCompatibility(){_editorTabs.SetTabHidden(1,false);_editorTabs.SetTabHidden(2,false);_editorTabs.CurrentTab=1;}
    public void Open(DiyCardDraft draft)
    {
        _openedDraft=draft.Snapshot();_counterSignature="";
        _editingCard.Text="正在编辑："+draft.Name+"  ·  修改实时保留在草稿，保存后生效。";
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
        _editorTabs.TabsVisible=true;
        _editorTabs.SetTabHidden(1,true); _editorTabs.SetTabHidden(2,true);
        _editorTabs.CurrentTab=0;
    }

    private void NativeChanged()
    {
        if(_nativeSync)return;
        DraftTouched?.Invoke();
        try
        {
            var updated=InspectorCardRecord.ApplyRecord(_openedDraft,NativeInspector.Document.Element());
            _openedDraft=updated;_cardId=updated.Id;_cardType=updated.CardType;
            _loading=true;
            foreach(var row in _orcRows){_orcBox.RemoveChild(row);row.QueueFree();}_orcRows.Clear();
            foreach(var effect in updated.OrcEffects)AddOrcRow(effect);
            foreach(var row in _effects){_effectBox.RemoveChild(row);row.QueueFree();}_effects.Clear();
            foreach(var effect in updated.Effects)AddEffect(effect);
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
            var probe=DiyCapability.Probe(current,Catalog?.Invoke()??[]);
            var blocked=probe.Count(r=>r.Source!="card"&&r.State==DiyPlayability.DraftOnly);
            OrcNotice.Text=check.Success?"卡牌已保存，可以用于对局。":blocked>0?$"已保存。其中 {blocked} 条效果尚不能进对局（点“仅草稿”看原因）。":"草稿已保存；卡牌本身尚未通过校验："+string.Join("；",check.Errors);
            OrcCheck();
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
        box.AddChild(MenuStyles.Label(title, 16, MenuStyles.Gold));
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
        AddOrcButton.Disabled = _orcRows.Count >= 128;
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
        Applied?.Invoke(OrcDrafts(), LegacyDrafts());_orcTimer.Start();
    }

    /// <summary>防抖编译校验：逐条编译编辑面，状态行＋行级错误。</summary>
    private void OrcCheck()
    {
        var drafts=OrcDrafts();RefreshCounters(drafts);
        NativeInspector.RefreshSourceCheck();
        var probe=DiyCapability.Probe(_openedDraft with{OrcEffects=drafts,Effects=LegacyDrafts()},Catalog?.Invoke()??[]);
        var effects=probe.Where(r=>r.Source!="card").ToArray();
        CapabilitySummary.Text=$"效果 {effects.Length} 条 · 可进对局 {effects.Count(r=>r.State==DiyPlayability.Playable)} · 仅草稿 {effects.Count(r=>r.State==DiyPlayability.DraftOnly)}";
        _cardCapability.Text=string.Join("；",probe.Where(r=>r.Source=="card").SelectMany(r=>r.Reasons));
        _cardCapability.Visible=_cardCapability.Text.Length>0;
        for(var i=0;i<_orcRows.Count;i++)if(probe.FirstOrDefault(r=>r.Source=="structured"&&r.EffectIndex==i) is { } row)_orcRows[i].SetCapability(row);
        for(var i=0;i<_effects.Count;i++)if(probe.FirstOrDefault(r=>r.Source=="legacy"&&r.EffectIndex==i) is { } row)_effects[i].SetCapability(row);
        var visible=probe.Where(r=>r.Source=="structured").Concat(probe.Where(r=>r.Source=="legacy").Select(r=>r with{EffectIndex=drafts.Length+r.EffectIndex})).ToArray();
        NativeInspector.SetCapabilities(visible);
        if(effects.Any(r=>r.State==DiyPlayability.DraftOnly))OrcNotice.Text="点效果标题旁的“仅草稿”查看具体原因。";
    }

    private void RefreshCounters(OrcEffectDraft[] drafts)
    {
        var names=OrcCounterCodegen.CountersIn(drafts);var signature=string.Join("\n",names);
        if(signature==_counterSignature&&_counterBox.GetChildCount()>0)return;
        _counterSignature=signature;MenuStyles.Clear(_counterBox);
        if(names.Count==0)_counterBox.AddChild(MenuStyles.Label("添加计数器动作，或勾选“数值来自计数器”后，这里会列出本卡引用。",12,MenuStyles.Dim));
        foreach(var name in names)
        {
            var row=new HBoxContainer();_counterBox.AddChild(row);
            var input=new LineEdit{Text=name,MaxLength=40,SizeFlagsHorizontal=SizeFlags.ExpandFill};row.AddChild(input);
            row.AddChild(MenuStyles.Button("重命名引用",()=>
            {
                try
                {
                    var renamed=OrcCounterCodegen.Rename(OrcDrafts(),name,input.Text);
                    _loading=true;foreach(var old in _orcRows){_orcBox.RemoveChild(old);old.QueueFree();}_orcRows.Clear();
                    foreach(var effect in renamed)AddOrcRow(effect);_loading=false;OrcChanged();OrcCheck();
                }
                catch(Exception error){_loading=false;OrcNotice.Text=error.Message;}
            }));
        }
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
                ? $"// 效果 {i + 1}（{OrcEffectEditing.Templates.FirstOrDefault(t => t.Id == e.Draft.Template && t.Guard.ToString() == e.Draft.Guard)?.Label ?? e.Draft.Template}）\n{System.Text.Json.Nodes.JsonNode.Parse(e.JsonText!)!.ToJsonString(new(){WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping})}"
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
            GD.Print($"[fx-move] loading={_loading} effects0={LegacyDrafts().FirstOrDefault()?.Op ?? "?"} count={_effects.Count}");
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
        private readonly DiyCapabilityBadge _capability;
        internal void SetCapability(DiyCapabilityRow row)=>_capability.Set(row);
        private bool _updating;
        internal EffectRow(DiyEffectDraft draft, Action changed, Action<EffectRow> remove, Action<EffectRow, int> move)
        {
            var box = new VBoxContainer(); AddChild(box);
            var top = new HBoxContainer(); box.AddChild(top);
            _capability=new DiyCapabilityBadge(box);top.AddChild(_capability);
            Op = DiyEditorScreen.Option(DiyCardCompiler.Operations); top.AddChild(Op);
            Target = DiyEditorScreen.Option(DiyCardCompiler.Targets); top.AddChild(Target);
            top.AddChild(MenuStyles.Button("↑", () => move(this, -1)));
            top.AddChild(MenuStyles.Button("↓", () => move(this, 1)));
            top.AddChild(MenuStyles.Button("×", () => remove(this)));
            var values = new HBoxContainer(); box.AddChild(values);
            _amountLabel = MenuStyles.Label("数量", 12, MenuStyles.Dim); values.AddChild(_amountLabel);
            Amount = DiyEditorScreen.Number(-20, 99); values.AddChild(Amount);
            _attackLabel = MenuStyles.Label("攻击", 12, MenuStyles.Dim); values.AddChild(_attackLabel);
            Attack = DiyEditorScreen.Number(-20, 20); values.AddChild(Attack);
            _defenseLabel = MenuStyles.Label("防御", 12, MenuStyles.Dim); values.AddChild(_defenseLabel);
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
        private readonly DiyCapabilityBadge _capability;
        internal void SetCapability(DiyCapabilityRow row){_capability.Set(row);_error.Text="";}
        private readonly VBoxContainer _opsBox;
        private readonly List<OrcOpRow> _ops = [];
        internal IReadOnlyList<OrcOpRow> OpRows=>_ops;
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
            _title = MenuStyles.Label("效果", 14, MenuStyles.Gold); head.AddChild(_title);_capability=new DiyCapabilityBadge(box);head.AddChild(_capability);
            head.AddChild(MenuStyles.Button("↑",()=>_move(this,-1)));
            head.AddChild(MenuStyles.Button("↓",()=>_move(this,1)));
            if(draft.PrefabJson is { } nativeJson)
            {
                _desktopDraft=draft.Snapshot();_trigger=null!;_opsBox=null!;
                head.AddChild(MenuStyles.Button("复制预制体",()=>DisplayServer.ClipboardSet(nativeJson)));head.AddChild(MenuStyles.Button("×",()=>_remove(this)));
                box.AddChild(MenuStyles.Label("原生预制体 · 只读保留。注入时机与效果程序不会被结构行覆盖。",12,MenuStyles.Dim));
                foreach(var injection in DiyInjectionCatalog.Read(nativeJson))
                {var label=MenuStyles.Label(injection.Summary+(injection.Error is null?"":"\n"+injection.Error),12,injection.Error is null?MenuStyles.Dim:new Color("dca58b"));label.AutowrapMode=TextServer.AutowrapMode.WordSmart;box.AddChild(label);}
                var source=new TextEdit{Text=nativeJson,Editable=false,CustomMinimumSize=new(0,180),WrapMode=TextEdit.LineWrappingMode.Boundary};box.AddChild(source);
                _error=MenuStyles.Label("",12,new("dca58b"));box.AddChild(_error);return;
            }
            if(draft.DesktopJson is { } graph)
            {
                _desktopDraft=draft.Snapshot();
                head.AddChild(MenuStyles.Button("复制",()=>DisplayServer.ClipboardSet(_desktopDraft.DesktopJson!)));
                head.AddChild(MenuStyles.Button("×",()=>_remove(this)));
                _trigger=null!;_opsBox=null!;
                box.AddChild(MenuStyles.Label("完整声明：触发、变量、条件、嵌套效果与未知字段均保留。完整表单位于“完整 Inspector”标签。",12,MenuStyles.Dim));
                var editor=new TextEdit {Text=System.Text.Json.Nodes.JsonNode.Parse(graph)!.ToJsonString(new(){WriteIndented=true}),
                    CustomMinimumSize=new(440,210),SizeFlagsHorizontal=SizeFlags.ExpandFill,WrapMode=TextEdit.LineWrappingMode.Boundary};
                box.AddChild(editor);_error=MenuStyles.Label("",12,new("dca58b"));box.AddChild(_error);
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
            head.AddChild(MenuStyles.Button("×", () => _remove(this)));
            _opsBox = new VBoxContainer(); _opsBox.AddThemeConstantOverride("separation", 8); box.AddChild(_opsBox);
            var addRow = new HBoxContainer(); addRow.AddThemeConstantOverride("separation", 8); box.AddChild(addRow);
            var pick = DiyEditorScreen.Option(new[] { new UiDiyChoice("", "选择动作…") }
                .Concat(OrcEffectEditing.Ops.Select(o => new UiDiyChoice(o.Id, o.Label + " · " + o.Group))));
            pick.CustomMinimumSize = new(210, 34); addRow.AddChild(pick);
            addRow.AddChild(MenuStyles.Button("添加动作", () =>
            {
                if (pick.Selected <= 0) return;
                AddOp(OrcEffectEditing.Seed(OrcEffectEditing.Ops[pick.Selected - 1].Id));
                _changed();
            }));
            _error = MenuStyles.Label("", 12, new("dca58b"));
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
        internal OptionButton ConditionPick=>_cond;
        internal readonly OptionButton ConditionComparison=null!;
        internal readonly SpinBox ConditionNumber=null!;
        private readonly List<string> _conditionTypes=[];
        private readonly HFlowContainer _conditionChips=null!;
        private readonly OptionButton _addConditionType=null!;
        internal readonly LineEdit CounterName=null!;
        internal readonly CheckBox CounterAmount=null!;
        private readonly OrcOpDraft? _unknownDraft;
        private readonly Label _summary = null!;
        private readonly List<(Control Wrap, Func<bool> Show)> _visibility = [];
        private readonly Action _changed;
        private bool _mute = true;

        internal OrcOpRow(OrcOpDraft draft, Action changed, Action<OrcOpRow> remove, Action<OrcOpRow, int> move)
        {
            _changed = changed;
            _info = OrcEffectEditing.Op(draft.Op) ?? OrcEffectEditing.Ops[0];
            var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 6); AddChild(head);
            _summary = MenuStyles.Label("", 13);
            _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill; head.AddChild(_summary);
            head.AddChild(MenuStyles.Button("↑", () => move(this, -1)));
            head.AddChild(MenuStyles.Button("↓", () => move(this, 1)));
            head.AddChild(MenuStyles.Button("×", () => remove(this)));
            if(OrcEffectEditing.Op(draft.Op) is null)
            {
                _unknownDraft=draft.Snapshot();_summary.Text="未接入的动作："+draft.Op;
                var note=MenuStyles.Label("这条动作的完整参数已保留。请通过原声明编辑，或移除此动作后重新添加。",12,MenuStyles.Dim);note.AutowrapMode=TextServer.AutowrapMode.WordSmart;AddChild(note);return;
            }
            GridContainer Group(string caption,Func<bool> visible)
            {
                var panel=new PanelContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};AddChild(panel);
                panel.AddThemeStyleboxOverride("panel",MenuStyles.Box(MenuStyles.Panel2,MenuStyles.Line,2,12));
                var column=new VBoxContainer();column.AddThemeConstantOverride("separation",8);panel.AddChild(column);
                column.AddChild(MenuStyles.Label(caption,12,MenuStyles.Dim));
                var grid=new GridContainer{Columns=3,SizeFlagsHorizontal=SizeFlags.ExpandFill};column.AddChild(grid);
                grid.AddThemeConstantOverride("h_separation",14);grid.AddThemeConstantOverride("v_separation",10);
                Resized+=()=>grid.Columns=Size.X<590?2:3;
                When(panel,visible);return grid;
            }
            var conditions=Group("生效条件",()=>true);
            _cond=DiyEditorScreen.Option(OrcEffectEditing.Conditions);DiyEditorScreen.Field(conditions,"前提",_cond);
            _condDim=DiyEditorScreen.Option(OrcEffectEditing.ConditionDims);When(DiyEditorScreen.Field(conditions,"过滤维度",_condDim),()=>V(_cond)==OrcEffectEditing.EventCardFilter);
            _condValue=new LineEdit{MaxLength=40};When(DiyEditorScreen.Field(conditions,"过滤取值",_condValue),()=>V(_cond)==OrcEffectEditing.EventCardFilter);
            _conditionChips=new HFlowContainer();var types=new VBoxContainer();types.AddChild(_conditionChips);
            _addConditionType=DiyEditorScreen.Option(new[]{new UiDiyChoice("","＋ 添加兵种")}.Concat(OrcEffectEditing.UnitTypes.Where(t=>t.Id.Length>0)));
            types.AddChild(_addConditionType);When(DiyEditorScreen.Field(conditions,"控制的兵种",types),()=>V(_cond)==OrcEffectEditing.OwnerControlsType);
            ConditionComparison=DiyEditorScreen.Option(OrcEffectEditing.Comparisons);When(DiyEditorScreen.Field(conditions,"比较",ConditionComparison),()=>V(_cond)==OrcEffectEditing.OwnerControlsType);
            ConditionNumber=DiyEditorScreen.Number(0,20);When(DiyEditorScreen.Field(conditions,"单位数量",ConditionNumber),()=>V(_cond)==OrcEffectEditing.OwnerControlsType);
            _addConditionType.ItemSelected+=_=>{if(V(_addConditionType) is {Length:>0} type){AddConditionType(type);_addConditionType.Select(0);}};
            var target=Group("作用对象",()=>_info.Target);
            _sel=DiyEditorScreen.Option(OrcEffectEditing.Sels);DiyEditorScreen.Field(target,"选择方式",_sel);
            _side=DiyEditorScreen.Option(OrcEffectEditing.Sides);When(DiyEditorScreen.Field(target,"哪一方",_side),()=>V(_sel)!="self");
            _selZone=DiyEditorScreen.Option(OrcEffectEditing.SelZones);When(DiyEditorScreen.Field(target,"所在区域",_selZone),()=>V(_sel)!="self");
            _selCount=DiyEditorScreen.Number(1,10);When(DiyEditorScreen.Field(target,"随机数量",_selCount),()=>V(_sel)=="random");
            _fUnit=DiyEditorScreen.Option(OrcEffectEditing.UnitTypes);When(DiyEditorScreen.Field(target,"兵种过滤",_fUnit),()=>V(_sel)!="self");
            _fKeyword=DiyEditorScreen.Option(new[]{new UiDiyChoice("","不限词条")}.Concat(OrcEffectEditing.KeywordChoices));When(DiyEditorScreen.Field(target,"词条过滤",_fKeyword),()=>V(_sel)!="self");
            _fFaction=DiyEditorScreen.Option(OrcEffectEditing.Factions);When(DiyEditorScreen.Field(target,"国家过滤",_fFaction),()=>V(_sel)!="self");
            _excludeSelf=new CheckBox{Text="排除本单位"};When(DiyEditorScreen.Field(target,"其他限制",_excludeSelf),()=>V(_sel)!="self");
            var counter=Group("对局计数器",()=>_info.Id.StartsWith("counter.",StringComparison.Ordinal)||_info.Id is "damage" or "buff");
            CounterAmount=new CheckBox{Text="使用计数器数值"};When(DiyEditorScreen.Field(counter,"数值来源",CounterAmount),()=>_info.Id is "damage" or "buff");
            CounterName=new LineEdit{MaxLength=40,PlaceholderText="例如：阵亡数"};When(DiyEditorScreen.Field(counter,"计数器名称（本局共享）",CounterName),()=>_info.Id.StartsWith("counter.",StringComparison.Ordinal)||CounterAmount.ButtonPressed);
            var values=Group("数值与期限",()=>_info.Amount||_info.Count||_info.AttackDefense||_info.Keyword||_info.Name||_info.Zone||_info.Until);
            _amount=DiyEditorScreen.Number(_info.AmountMin,Math.Max(_info.AmountMax,_info.AmountMin));When(DiyEditorScreen.Field(values,_info.AmountLabel is {Length:>0}?_info.AmountLabel:"数值",_amount),()=>_info.Amount&&!CounterAmount.ButtonPressed);
            _count=DiyEditorScreen.Number(1,10);When(DiyEditorScreen.Field(values,"张数",_count),()=>_info.Count);
            _attack=DiyEditorScreen.Number(-20,20);When(DiyEditorScreen.Field(values,"攻击增减 / 计数倍数",_attack),()=>_info.AttackDefense);
            _defense=DiyEditorScreen.Number(-20,20);When(DiyEditorScreen.Field(values,"防御增减 / 计数倍数",_defense),()=>_info.AttackDefense);
            _keyword=DiyEditorScreen.Option(OrcEffectEditing.KeywordChoices);When(DiyEditorScreen.Field(values,"获得的词条",_keyword),()=>_info.Keyword);
            _value=DiyEditorScreen.Number(0,99);When(DiyEditorScreen.Field(values,"词条参值（0＝无）",_value),()=>_info.Keyword);
            _name=new LineEdit{MaxLength=40};When(DiyEditorScreen.Field(values,"完整卡名",_name),()=>_info.Name);
            _opZone=DiyEditorScreen.Option(OrcEffectEditing.OpZones);When(DiyEditorScreen.Field(values,"移动落点",_opZone),()=>_info.Zone);
            _until=DiyEditorScreen.Option(OrcEffectEditing.UntilChoices);When(DiyEditorScreen.Field(values,"持续时间",_until),()=>_info.Until);


            Load(draft);
            foreach (var option in new[] { _sel, _side, _selZone, _fUnit, _fKeyword, _fFaction, _keyword, _opZone, _until, _cond, _condDim,ConditionComparison })
                option.ItemSelected += _ => { if (!_mute) { UpdateVisibility(); _changed(); } };
            foreach (var spin in new[] { _selCount, _amount, _count, _attack, _defense, _value,ConditionNumber })
                spin.ValueChanged += _ => { if (!_mute) {UpdateVisibility();_changed();} };
            CounterName.TextChanged+=_=>{if(!_mute){UpdateVisibility();_changed();}};
            CounterAmount.Toggled+=_=>{if(!_mute){UpdateVisibility();_changed();}};
            _name.TextChanged += _ => { if (!_mute) {UpdateVisibility();_changed();} };
            _condValue.TextChanged += _ => { if (!_mute) {UpdateVisibility();_changed();} };
            _excludeSelf.Toggled += _ => { if (!_mute) {UpdateVisibility();_changed();} };
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
            _conditionTypes.Clear();_conditionTypes.AddRange(draft.CondUnitTypes??[]);RefreshConditionChips();
            DiyEditorScreen.Select(ConditionComparison,draft.CondComparison??">=");ConditionNumber.Value=draft.CondValue??0;
            CounterName.Text=draft.Counter??"";CounterAmount.ButtonPressed=draft.AmountFromCounter;
        }

        private static (string dim, string value) Dims(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return ("keyword", "");
            var split = raw.IndexOf(':', StringComparison.Ordinal);
            return split < 0 ? ("keyword", raw) : (raw[..split], raw[(split + 1)..]);
        }

        internal OrcOpDraft Read() => _unknownDraft?.Snapshot() ?? new()
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
            CondUnitTypes=V(_cond)==OrcEffectEditing.OwnerControlsType?_conditionTypes.ToArray():null,
            CondComparison=V(_cond)==OrcEffectEditing.OwnerControlsType?V(ConditionComparison):null,
            CondValue=V(_cond)==OrcEffectEditing.OwnerControlsType?(int)ConditionNumber.Value:null,
            Counter=_info.Id.StartsWith("counter.",StringComparison.Ordinal)||CounterAmount.ButtonPressed?CounterName.Text:null,
            AmountFromCounter=(_info.Id is "damage" or "buff")&&CounterAmount.ButtonPressed,
        };
        internal void AddConditionType(string type)
        {
            if(!_conditionTypes.Contains(type))_conditionTypes.Add(type);RefreshConditionChips();if(!_mute)_changed();
        }
        private void RefreshConditionChips()
        {
            MenuStyles.Clear(_conditionChips);
            foreach(var type in _conditionTypes.ToArray())
                _conditionChips.AddChild(MenuStyles.Button((OrcEffectEditing.UnitTypes.FirstOrDefault(t=>t.Id==type)?.Label??type)+" ×",()=>{_conditionTypes.Remove(type);RefreshConditionChips();if(!_mute)_changed();}));
        }

        private void UpdateVisibility()
        {
            foreach (var (wrap, show) in _visibility) wrap.Visible = show();
            _summary.Text = OrcEffectEditing.Summarize(Read());
        }

        private void When(Control wrap, Func<bool> show) => _visibility.Add((wrap, show));

        private static string? Null(string s) => s.Length == 0 ? null : s;
    }
}
