using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private readonly List<WorkshopChoiceCard> _choiceCards = [];
    private readonly List<Tween> _choiceMotions = [];
    internal string ChoicePhase { get; private set; } = "idle";
    internal event Action<string>? ChoicePhaseChanged;
    internal IReadOnlyList<WorkshopChoiceCard> ChoiceCards => _choiceCards;
    private void Phase(string phase) { ChoicePhase = phase; ChoicePhaseChanged?.Invoke(phase); }
    private void StopChoiceMotion()
    {
        foreach (var motion in _choiceMotions) if (GodotObject.IsInstanceValid(motion)) motion.Kill();
        _choiceMotions.Clear(); _animating = false;
    }
    private void BeginChoicesEntrance()
    {
        Phase("entering");
        if (IsInsideTree()) _ = EnterChoicesAsync(_navigationVersion);
        else Ready += () => _ = EnterChoicesAsync(_navigationVersion);
    }
    private HBoxContainer ChoiceRow(string hint)
    {
        StopChoiceMotion(); _choiceCards.Clear(); MenuStyles.Clear(_navigation);
        _navigation.AddChild(MenuStyles.Label(hint, 18, MenuStyles.Ink));
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsVertical = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 38); _navigation.AddChild(row); return row;
    }
    private WorkshopChoiceCard Choice(Node row, string title, string detail, string badge = "", IEnumerable<string>? faces = null)
    {
        var card = new WorkshopChoiceCard { SizeFlagsHorizontal = SizeFlags.ShrinkCenter, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        card.Initialize(_textures, title, detail, badge, faces); row.AddChild(card); _choiceCards.Add(card); return card;
    }
    internal void ShowHome()
    {
        _navigationVersion++; _workspace.Hide(); _editor.Hide(); _navigation.Show(); _pack = null;
        _notice.Text = _store.LoadError ?? "选择制作卡包或扩展包，再选择创建或修改。";
        var row = ChoiceRow("选择你要制作的内容");
        PackChoice = Choice(row, "卡包", "定义自己的国家、词条与卡牌。", "独立内容");
        ExpansionChoice = Choice(row, "扩展包", "继承基础卡包，继续添加新内容。", "扩展内容");
        PackChoice.Pressed += () => SelectChoice((WorkshopChoiceCard)PackChoice, () => ShowActionChoices(false));
        ExpansionChoice.Pressed += () => SelectChoice((WorkshopChoiceCard)ExpansionChoice, () => ShowActionChoices(true));
        BeginChoicesEntrance();
    }
    private void ShowActionChoices(bool expansion)
    {
        _navigationVersion++; var row = ChoiceRow(expansion ? "扩展包：选择下一步" : "卡包：选择下一步");
        CreateChoice = Choice(row, "创建", expansion ? "选择基础卡包，制作新的扩展包。" : "从空白卡包开始制作。", "新建内容");
        ModifyChoice = Choice(row, "修改", "查看已保存的卡包卡面与内容，再进入编辑。", "已有内容");
        CreateChoice.Pressed += () => SelectChoice((WorkshopChoiceCard)CreateChoice, () => OpenSelection(expansion, false));
        ModifyChoice.Pressed += () => SelectChoice((WorkshopChoiceCard)ModifyChoice, () => OpenSelection(expansion, true));
        BeginChoicesEntrance();
    }
    // Measured against the supplied 60 fps KARDS recording: front-facing cards
    // spread horizontally in about 0.18 s and collapse into one stack in 0.16 s.
    private const double ChoiceSpreadSeconds = .18;
    private const double ChoiceCollapseSeconds = .16;
    private async Task EnterChoicesAsync(int version)
    {
        _animating = true; var cards = _choiceCards.ToArray();
        foreach (var card in cards) { card.Disabled = true; card.Modulate = new(1,1,1,0); }
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (version != _navigationVersion) return;
        var destinations = cards.Select(c => c.Position).ToArray();
        // The two navigation choices use a shared central stack. Saved-pack
        // galleries keep their grid; choosing a saved item is not a new decision.
        var spread = cards.Length == 2 && cards[0].GetParent() is HBoxContainer;
        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i]; card.Scale = Vector2.One; card.Rotation = 0;
            card.FaceDown = false; card.Modulate = Colors.White; card.ZIndex = cards.Length - i;
            if (spread && !_reduced()) card.Position = new Vector2((card.GetParent<Control>().Size.X - card.Size.X) / 2, destinations[i].Y);
        }
        Phase("entering");
        if (spread && !_reduced())
        {
            var origins = cards.Select(c => c.Position).ToArray();
            var tween = CreateTween(); _choiceMotions.Add(tween);
            tween.TweenMethod(Callable.From<float>(p =>
            {
                if (version != _navigationVersion) return;
                var q = p * p * (3 - 2 * p);
                for (var i = 0; i < cards.Length; i++)
                    if (GodotObject.IsInstanceValid(cards[i])) cards[i].Position = origins[i].Lerp(destinations[i],q);
            }), 0f, 1f, ChoiceSpreadSeconds);
            await ToSignal(tween, Tween.SignalName.Finished);
        }
        if (version != _navigationVersion) return;
        for (var i = 0; i < cards.Length; i++) if (GodotObject.IsInstanceValid(cards[i]))
        { cards[i].Position = destinations[i]; cards[i].Disabled = false; cards[i].ZIndex = 0; }
        _animating = false; Phase("ready");
    }
    private async void SelectChoice(WorkshopChoiceCard selected, Action next)
    {
        if (_animating) return; _animating = true; var version = _navigationVersion;
        var cards = _choiceCards.Where(GodotObject.IsInstanceValid).ToArray();
        foreach (var card in cards) card.Disabled = true;
        Phase("selecting");
        if (!_reduced() && selected.GetParent() is HBoxContainer)
        {
            var starts = cards.Select(c => c.Position).ToArray();
            var centerX = selected.GetParent<Control>().Size.X / 2;
            selected.ZIndex = 5;
            var tween = CreateTween(); _choiceMotions.Add(tween);
            tween.TweenMethod(Callable.From<float>(p =>
            {
                if (version != _navigationVersion) return;
                var q = p * p * (3 - 2 * p);
                for (var i = 0; i < cards.Length; i++)
                    if (GodotObject.IsInstanceValid(cards[i])) cards[i].Position = starts[i].Lerp(new Vector2(centerX - cards[i].Size.X / 2, starts[i].Y),q);
            }), 0f, 1f, ChoiceCollapseSeconds);
            await ToSignal(tween, Tween.SignalName.Finished);
            if (version != _navigationVersion) return;
            Phase("stacked");
            await ToSignal(GetTree().CreateTimer(.08), SceneTreeTimer.SignalName.Timeout);
        }
        if (version != _navigationVersion) return;
        _animating = false; Phase("selected"); next();
    }
    private IEnumerable<string> PackFaces(string id) => _service.Cards.Where(c=>DeckCardPacks.RawOf(c.CardId)==id).Select(c=>c.ArtPath).Where(p=>p.Length>0).Take(3);
    private string PackSummary(DiyPackDraft pack)
    {
        var cards=_service.Cards.Where(c=>DeckCardPacks.RawOf(c.CardId)==pack.Id).ToArray();
        var drafts=_cards.Cards.Where(c=>c.PackId==pack.Id).ToArray();
        var count=cards.Select(c=>c.CardId).Concat(drafts.Select(c=>c.RuntimeId)).Distinct().Count();
        var playable=cards.Count(c=>_service.PlayableIds.Contains(c.CardId));
        var parent=BuiltInWorkshopCatalog.Packs(_store).FirstOrDefault(p=>p.Id==pack.BasePackId);
        return $"{count} 张卡牌 · {playable} 张可用\n自定义国家 {pack.Nations.Length+(parent?.Nations.Length??0)} · 词条 {pack.Keywords.Length+(parent?.Keywords.Length??0)}"
            +(pack.IsBuiltIn?$"\n内置 / 自带 · 已修改 {drafts.Count(c=>c.SourceCardId.Length>0)} 张":"")
            +(pack.IsExpansion?"\n包含基础卡包的继承内容":"");
    }
    internal void OpenSelection(bool expansion, bool modifying)
    {
        _navigationVersion++; StopChoiceMotion(); _choiceCards.Clear(); MenuStyles.Clear(_navigation);
        _workspace.Hide(); _editor.Hide(); _navigation.Show();
        _notice.Text = modifying ? "选择已有内容，查看卡面与数据后进入编辑。" : expansion ? "先选择基础卡包，再为扩展包命名。" : "为新卡包命名，进入工作区后制作国家、词条与卡牌。";
        _navigation.AddChild(MenuStyles.Label((modifying ? "修改" : "创建") + (expansion ? "扩展包" : "卡包"), 24, MenuStyles.Ink));
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddThemeConstantOverride("separation",28); _navigation.AddChild(body);
        var gallery = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; body.AddChild(gallery);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; gallery.AddChild(scroll);
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill }; grid.AddThemeConstantOverride("h_separation",24); grid.AddThemeConstantOverride("v_separation",24); scroll.AddChild(grid);
        scroll.Resized += () => grid.Columns = Math.Max(1,(int)((scroll.Size.X + 24)/250));
        var panel = new PanelContainer { CustomMinimumSize = new(360,0), SizeFlagsVertical = SizeFlags.ShrinkBegin }; body.AddChild(panel);
        var form = new VBoxContainer(); form.AddThemeConstantOverride("separation",14); panel.AddChild(form);
        var selectedTitle = MenuStyles.Label(modifying ? "选择要修改的内容" : "新内容",22,MenuStyles.Ink); selectedTitle.AutowrapMode = TextServer.AutowrapMode.WordSmart; form.AddChild(selectedTitle);
        var summary = MenuStyles.Label("",14,MenuStyles.Dim); summary.AutowrapMode = TextServer.AutowrapMode.WordSmart; form.AddChild(summary);
        DiyPackDraft draft = new() { Name = expansion ? "新扩展包" : "新卡包" };
        ModifyPick = new OptionButton { Visible = false }; form.AddChild(ModifyPick);
        ModifyPick.AddItem("请选择要修改的内容"); ModifyPick.SetItemMetadata(0,"");
        PackName = new LineEdit { Text = modifying ? "" : draft.Name, MaxLength = 60, Editable = !modifying }; DiyEditorScreen.Field(form,"名称",PackName);
        BaseChoice = new OptionButton { Visible = false, FitToLongestItem = false, ClipText = true }; form.AddChild(BaseChoice);
        BaseChoice.AddItem("请选择基础卡包"); BaseChoice.SetItemMetadata(0,"");
        var bases = new Dictionary<string,string> { ["source"] = "太空战争" };
        foreach (var native in OrcEngineHost.PackDescriptors.Where(p => p.BasePackId is null)) bases["pack/"+native.Id] = native.Name;
        foreach (var pack in _store.Packs.Where(p => !p.IsExpansion)) bases[pack.Id] = pack.Name;
        foreach (var pair in bases) { BaseChoice.AddItem(pair.Value); BaseChoice.SetItemMetadata(BaseChoice.ItemCount-1,pair.Key); }
        if (expansion && modifying)
        { form.RemoveChild(BaseChoice); BaseChoice.Show(); DiyEditorScreen.Field(form,"基础卡包",BaseChoice); }
        void UpdateSummary()
        {
            if (!modifying) summary.Text = expansion ? "在左侧选择基础卡包。其国家与词条会继承到扩展包。" : "进入工作区后可创建国家、词条与卡牌。";
            if (expansion && DiyEditorScreen.Value(BaseChoice).Length > 0)
                summary.Text = (modifying ? PackSummary(draft)+"\n" : "") + "基础卡包："+BaseChoice.GetItemText(BaseChoice.Selected);
            ConfirmPack.Disabled = string.IsNullOrWhiteSpace(PackName.Text) || (expansion && DiyEditorScreen.Value(BaseChoice).Length == 0)
                || (modifying && DiyEditorScreen.Value(ModifyPick).Length == 0);
        }
        ConfirmPack = MenuStyles.Button(modifying ? "保存并进入编辑  →" : "创建并进入  →", () =>
        {
            try
            {
                var parent = expansion ? DiyEditorScreen.Value(BaseChoice) : null;
                if (expansion && string.IsNullOrEmpty(parent)) throw new InvalidDataException("请先选择基础卡包");
                if (modifying && DiyEditorScreen.Value(ModifyPick).Length == 0) throw new InvalidDataException("请选择要修改的内容");
                var pack = draft with { Name = PackName.Text.Trim(), BasePackId = parent };
                _store.Save(pack); _changed(); OpenPack(_store.Packs.Single(p => p.Id == pack.Id));
            }
            catch (Exception error) { _notice.Text = error.Message; }
        }); MenuStyles.Primary(ConfirmPack); form.AddChild(ConfirmPack);
        form.AddChild(MenuStyles.Button("返回上一步",()=>ShowActionChoices(expansion)));
        PackName.TextChanged += _ => UpdateSummary(); BaseChoice.ItemSelected += _ => UpdateSummary();
        ModifyPick.ItemSelected += _ =>
        {
            var saved = BuiltInWorkshopCatalog.Packs(_store).FirstOrDefault(p => p.Id == DiyEditorScreen.Value(ModifyPick));
            if (saved is null) return;
            draft = saved.Snapshot(); PackName.Editable = true; PackName.Text = saved.Name;
            selectedTitle.Text = saved.Name; summary.Text = PackSummary(saved);
            if (expansion) DiyEditorScreen.Select(BaseChoice,saved.BasePackId!);
            BaseChoice.Disabled=saved.IsBuiltIn;
            if(saved.IsBuiltIn) summary.Text+="\n修改保存在本机，原卡编号保留。";
            UpdateSummary();
        };
        if (modifying)
        {
            foreach (var pack in BuiltInWorkshopCatalog.Packs(_store).Where(p => p.IsExpansion == expansion))
            {
                ModifyPick.AddItem(pack.Name); ModifyPick.SetItemMetadata(ModifyPick.ItemCount-1,pack.Id);
                var card = Choice(grid,pack.Name,PackSummary(pack),(pack.IsBuiltIn?"内置 / 自带":"已保存") + (expansion?" · 扩展包":" · 卡包"),PackFaces(pack.Id));
                card.Pressed += () => SelectChoice(card,()=>
                {
                    DiyEditorScreen.Select(ModifyPick,pack.Id); ModifyPick.EmitSignal(OptionButton.SignalName.ItemSelected,(long)ModifyPick.Selected);
                    foreach (var c in _choiceCards) { c.Scale = Vector2.One; c.Modulate = Colors.White; c.FaceDown = false; c.Disabled = false; c.ZIndex = 0; }
                    grid.QueueSort();
                });
            }
            if (_choiceCards.Count == 0) gallery.AddChild(MenuStyles.Label("还没有已保存的内容。返回上一步，选择创建。",16,MenuStyles.Dim));
        }
        else if (expansion)
        {
            foreach (var pair in bases)
            {
                var saved = _store.Packs.FirstOrDefault(p => p.Id == pair.Key);
                var samples = saved is null ? _service.Cards.Where(c => DeckCardPacks.RawOf(c.CardId) == pair.Key).Select(c => c.ArtPath) : PackFaces(saved.Id);
                var card = Choice(grid,pair.Value,saved is null ? $"{_service.PlayableCards.Count(c=>DeckCardPacks.Of(c.CardId)==pair.Key)} 张可用卡牌\n继承国家与词条" : PackSummary(saved),"选择基础卡包",samples);
                card.Pressed += () => SelectChoice(card,()=>
                {
                    DiyEditorScreen.Select(BaseChoice,pair.Key); BaseChoice.EmitSignal(OptionButton.SignalName.ItemSelected,(long)BaseChoice.Selected);
                    foreach (var c in _choiceCards) { c.Scale = Vector2.One; c.Modulate = Colors.White; c.FaceDown = false; c.Disabled = false; c.ZIndex = 0; }
                    grid.QueueSort();
                });
            }
        }
        else
        {
            var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; grid.AddChild(center);
            var preview = Choice(center,draft.Name,"创建国家与词条，制作你的卡牌。","新卡包");
            preview.MouseFilter = MouseFilterEnum.Ignore;
            PackName.TextChanged += name => { preview.Caption = name; preview.QueueRedraw(); };
        }
        UpdateSummary(); if (_choiceCards.Count > 0) BeginChoicesEntrance();
    }
}
