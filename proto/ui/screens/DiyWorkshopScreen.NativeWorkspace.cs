using Godot;
using System.Text.Json.Nodes;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private Func<bool>? _saveNativeCard;
    private NativeCardDocument? _cardDocument;
    internal OptionButton NativeNationPicker = null!;
    private void BuildNativeSessionHeader(string id)
    {
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 14); _workspace.AddChild(row);
        row.AddChild(MenuStyles.Button("‹ 卡包管理", () => GuardNative(ShowNativePackages)));
        var title = MenuStyles.Label(_native!.WorkingPackage?.Name ?? id, 23, MenuStyles.Gold);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill; row.AddChild(title);
        row.AddChild(MenuStyles.Button("导出草稿…", () => GuardNative(() => NativeFile(true, path => NativeTry(() => _native.Export(id, path, true))))));
        if (_native.SessionWarnings.Count > 0) row.AddChild(MenuStyles.Button("编辑会话提示", () => NativeText("引擎返回的会话提示", string.Join("\n", _native.SessionWarnings))));
        NativePromoteButton = MenuStyles.Button("应用修改", () => GuardNative(() => NativeTry(() =>
        { _native.Promote(); _nativeChanged!(); ShowNativePackages(); _notice.Text = "卡包已转正，构筑和新对局使用此版本。"; })));
        row.AddChild(NativePromoteButton); MenuStyles.Primary(NativePromoteButton);
    }
    private void BuildNativeLibrary(string id)
    {
        _notice.Text = _native!.RecoveredDraft ? "已恢复上次保存的草稿。继续编辑，完成后点击“应用修改”。" : "点击卡牌进入制作；保存后点击“应用修改”，用于构筑和新对局。";
        var tools = new HBoxContainer(); _workspace.AddChild(tools);
        var search = new LineEdit { PlaceholderText = "搜索卡牌", SizeFlagsHorizontal = SizeFlags.ExpandFill }; tools.AddChild(search);
        tools.AddChild(MenuStyles.Button("＋ 新建卡牌", () => NativeNameDialog("新建卡牌", "卡牌名称", name =>
        { var asset = _native!.CreateCard("card." + Guid.NewGuid().ToString("N")); var document = new NativeCardDocument(_native.Read(asset.Path)); document.Rename(name); _native.Commit(asset.Path, document.Json); OpenNativeCard(id, asset); })));
        EffectLibraryButton = MenuStyles.Button("词条与效果", OpenEffectLibrary); tools.AddChild(EffectLibraryButton);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; _workspace.AddChild(scroll);
        var grid = new GridContainer { Columns = 5, SizeFlagsHorizontal = SizeFlags.ExpandFill }; grid.AddThemeConstantOverride("h_separation", 22); grid.AddThemeConstantOverride("v_separation", 18); scroll.AddChild(grid);
        var paging = new HBoxContainer(); _workspace.AddChild(paging); var pageLabel = MenuStyles.Label("", 14); var page = 0;
        var cards = _native!.PreviewCards().Select(c => c.ArtPath.Length > 0 ? c : c with { ArtPath = _service.Cards.FirstOrDefault(original => original.CardId == c.CardId)?.ArtPath ?? "" }).ToList(); var assets = _native.Assets().Where(a => a.Kind == "Card").ToDictionary(a => a.AppId);
        foreach (var asset in assets.Values.Where(a => cards.All(c => c.CardId != a.AppId)))
        {
            var preview = new NativeCardDocument(_native.Read(asset.Path)).Preview(asset.AppId, _native.PreviewArtFor(asset.AppId));
            if (preview.Card is { } card) cards.Add(card);
        }
        void Render()
        {
            MenuStyles.Clear(grid); var selected = assets.Values.Select(asset => (Asset: asset, Card: cards.FirstOrDefault(c => c.CardId == asset.AppId)))
                .Where(entry => (entry.Card?.Name ?? entry.Asset.Id).Contains(search.Text, StringComparison.OrdinalIgnoreCase) || entry.Asset.AppId.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            var pages = Math.Max(1, (selected.Length + 23) / 24); page = Math.Clamp(page, 0, pages - 1);
            pageLabel.Text = $"{selected.Length} 张卡牌 · {page + 1} / {pages}";
            foreach (var entry in selected.Skip(page * 24).Take(24))
            {
                var asset = entry.Asset;
                if (entry.Card is not { } definition)
                {
                    var repair = MenuStyles.Button("待修复的卡牌\n暂时无法预览\n点击继续编辑", () => OpenNativeCard(id, asset)); repair.CustomMinimumSize = new(154, 216); grid.AddChild(repair); continue;
                }
                var card = new CardControl { UseEditableFace = true }; grid.AddChild(card); card.Bind(definition, _textures, 154);
                card.Activated += _ => OpenNativeCard(id, asset);
            }
            if (selected.Length == 0) grid.AddChild(MenuStyles.Label("没有匹配的卡牌。可清空搜索或新建卡牌。", 16));
        }
        paging.AddChild(MenuStyles.Button("上一页", () => { page--; Render(); })); paging.AddChild(pageLabel);
        paging.AddChild(MenuStyles.Button("下一页", () => { page++; Render(); }));
        search.TextChanged += _ => { page = 0; Render(); }; scroll.Resized += () => grid.Columns = Math.Max(1, (int)(scroll.Size.X / 176)); Render();
    }
    private void OpenNativeCard(string packId, NativeWorkshopAsset asset) => GuardNative(() => NativeTry(() =>
    {
        NativeWorkspace(); BuildNativeSessionHeader(packId); _nativeAsset = asset;
        _cardDocument = new NativeCardDocument(_native!.Read(asset.Path)); _nativeDirty = false;
        var presentation = _native.CardPresentation(asset.Id); var text = presentation.Text; var flavor = presentation.Flavor;
        var rules = presentation.EditableRules; var rulesChanged = false;
        var controls = new HBoxContainer(); _workspace.AddChild(controls);
        controls.AddChild(MenuStyles.Button("‹ 卡牌一览", () => OpenNativePack(packId)));
        NativeSaveButton = MenuStyles.Button("保存草稿", () => _saveNativeCard!()); controls.AddChild(NativeSaveButton); MenuStyles.Primary(NativeSaveButton);
        controls.AddChild(MenuStyles.Button("删除卡牌…", () => GuardNative(() => NativeTry(() =>
        {
            var affected = _native!.DeleteImpact(asset);
            var dialog = new ConfirmationDialog { Title = "删除草稿卡牌", DialogText = "将删除以下资产，转正后生效：\n" + string.Join("\n", affected.Select(a => KindLabel(a.Kind) + " · " + a.Id)), OkButtonText = "从草稿删除" }; AddChild(dialog);
            dialog.Confirmed += () => NativeTry(() => { _native.DeleteAsset(asset); _nativeDirty = false; OpenNativePack(packId); dialog.QueueFree(); }); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
        }))));
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddThemeConstantOverride("separation", 32); _workspace.AddChild(body);
        var tabs = new TabContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(tabs);
        VBoxContainer Page(string name)
        {
            var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; tabs.AddChild(scroll);
            var form = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; form.AddThemeConstantOverride("separation", 16); scroll.AddChild(form); return form;
        }
        var properties = Page("属性"); var rulePage = Page("规则"); var art = Page("卡图"); var story = Page("描述");
        var preview = new VBoxContainer { CustomMinimumSize = new(300, 0) }; body.AddChild(preview);
        preview.AddChild(MenuStyles.Label("卡面预览", 18, MenuStyles.Gold));
        var face = new CardFace(); face.FitToWidth(300); preview.AddChild(face);
        var status = MenuStyles.Label("", 13, MenuStyles.Dim); status.AutowrapMode = TextServer.AutowrapMode.WordSmart; preview.AddChild(status);
        void Preview()
        {
            var artPath = _native.PreviewArtFor(asset.AppId);
            if (artPath.Length == 0) artPath = _service.Cards.FirstOrDefault(c => c.CardId == asset.AppId)?.ArtPath ?? "";
            var result = _cardDocument.Preview(asset.AppId, artPath, text, flavor);
            if (result.Card is { } card) { face.SetDefinition(card); face.SetIllustration(_textures.Get(card.ArtPath)); }
            status.Text = result.Diagnostics.Count > 0 ? string.Join("\n", result.Diagnostics) : "实时预览 · 尚未转正";
        }
        void Changed() { NativeTouched(); Preview(); }
        var name = new LineEdit { Text = _cardDocument.Name }; NativeField(properties, "名称", name); name.TextChanged += value => { _cardDocument.Rename(value); Changed(); };
        var columns = new HBoxContainer(); columns.AddThemeConstantOverride("separation", 28); properties.AddChild(columns);
        var identity = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; identity.AddThemeConstantOverride("separation", 16); columns.AddChild(identity);
        var combat = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; combat.AddThemeConstantOverride("separation", 16); columns.AddChild(combat);
        identity.AddChild(MenuStyles.Label("归属与分类", 16, MenuStyles.Gold)); combat.AddChild(MenuStyles.Label("费用与战斗属性", 16, MenuStyles.Gold));
        var type = new OptionButton(); foreach (var item in new[] { ("unit", "单位"), ("order", "指令"), ("counter", "反制") }) { type.AddItem(item.Item2); type.SetItemMetadata(type.ItemCount - 1, item.Item1); }
        if (_cardDocument.CardType == "unknown") { type.AddItem("未知类型（保留原值）"); type.SetItemMetadata(3, "unknown"); }
        DiyEditorScreen.Select(type, _cardDocument.CardType); NativeField(identity, "卡牌类型", type);
        var unit = new OptionButton(); foreach (var item in DiyCardCompiler.UnitTypes) { unit.AddItem(item.Label); unit.SetItemMetadata(unit.ItemCount - 1, item.Id); } DiyEditorScreen.Select(unit, _cardDocument.Unit); NativeField(identity, "兵种", unit);
        var stats = new VBoxContainer(); stats.AddThemeConstantOverride("separation", 16); combat.AddChild(stats);
        void TypeVisibility() { ((Control)unit.GetParent()).Visible = _cardDocument.CardType == "unit"; stats.Visible = _cardDocument.CardType == "unit"; }
        type.ItemSelected += index => { var selected = type.GetItemMetadata((int)index).AsString(); if (selected != "unknown") _cardDocument.SetType(selected, _cardDocument.Unit); TypeVisibility(); Changed(); };
        unit.ItemSelected += index => { _cardDocument.SetType("unit", unit.GetItemMetadata((int)index).AsString()); Changed(); };
        var nations = new OptionButton(); NativeNationPicker = nations;
        foreach (var item in DiyCardCompiler.Nations.Concat(_native.DraftNations).DistinctBy(n => n.Id)) { nations.AddItem(item.Label); nations.SetItemMetadata(nations.ItemCount - 1, item.Id); }
        var currentNation = _cardDocument.String("factionCost", "faction");
        var chosenNation = Enumerable.Range(0, nations.ItemCount).FirstOrDefault(i => string.Equals(nations.GetItemMetadata(i).AsString(), currentNation, StringComparison.OrdinalIgnoreCase), -1);
        if (chosenNation < 0) { nations.AddItem("导入的国家（保留）"); nations.SetItemMetadata(nations.ItemCount - 1, currentNation); chosenNation = nations.ItemCount - 1; }
        nations.Select(chosenNation); NativeField(identity, "国家", nations);
        nations.ItemSelected += index => { _cardDocument.Set("factionCost", "faction", JsonValue.Create(nations.GetItemMetadata((int)index).AsString())); Changed(); };
        identity.AddChild(MenuStyles.Button("＋ 自定义国家…", () => NativeNameDialog("创建国家", "国家名称（可用中文）", label =>
        {
            var created = _native.CreateNation(label); nations.AddItem(created.Label); nations.SetItemMetadata(nations.ItemCount - 1, created.Id); nations.Select(nations.ItemCount - 1);
            nations.EmitSignal(OptionButton.SignalName.ItemSelected, (long)nations.Selected); _notice.Text = "国家已加入当前草稿，随卡包一起导出。";
        })));
        void Number(VBoxContainer parent, string label, string component, string key)
        { var input = new SpinBox { MinValue = -1000000, MaxValue = 1000000, Value = _cardDocument.Number(component, key) }; NativeField(parent, label, input); input.ValueChanged += value => { _cardDocument.Set(component, key, JsonValue.Create((int)value)); Changed(); }; }
        Number(combat, "部署费用", "factionCost", "kredits"); combat.MoveChild(combat.GetChild(combat.GetChildCount() - 1), 1); Number(stats, "攻击", "battleStats", "attack"); Number(stats, "防御", "battleStats", "defense"); Number(stats, "行动费用", "battleStats", "operationCost");
        var rarity = new OptionButton(); var currentRarity = _cardDocument.String("tagData", "rarity", "Standard");
        foreach (var item in new[] { ("standard", "普通"), ("limited", "限定"), ("special", "特殊"), ("elite", "精英") }) { rarity.AddItem(item.Item2); rarity.SetItemMetadata(rarity.ItemCount - 1, item.Item1); }
        var rarityKey = currentRarity.ToLowerInvariant();
        if (!new[] { "standard", "limited", "special", "elite" }.Contains(rarityKey)) { rarity.AddItem("导入的稀有度（保留）"); rarity.SetItemMetadata(4, currentRarity); rarityKey = currentRarity; }
        DiyEditorScreen.Select(rarity, rarityKey); NativeField(identity, "稀有度", rarity); rarity.ItemSelected += index => { _cardDocument.Set("tagData", "rarity", JsonValue.Create(rarity.GetItemMetadata((int)index).AsString())); Changed(); };
        rulePage.AddChild(MenuStyles.Label("词条与效果", 22, MenuStyles.Gold));
        var attributes = (_cardDocument.Component("keywords")?["attributes"] as JsonArray ?? new()).Select(n => n!.ToString()).ToList();
        var keywordRow = new HFlowContainer(); rulePage.AddChild(keywordRow);
        var keywordChoices = DiyCardCompiler.Keywords.Where(k => k.Id is "blitz" or "fury" or "smokescreen" or "ambush" or "mobilize" or "pincer" or "covert").ToArray();
        foreach (var keyword in keywordChoices)
        {
            var check = new CheckBox { Text = keyword.Label, ButtonPressed = attributes.Contains(keyword.Id) }; keywordRow.AddChild(check);
            check.Toggled += enabled => { attributes.RemoveAll(a => a == keyword.Id); if (enabled) attributes.Add(keyword.Id); _cardDocument.Set("keywords", "attributes", new JsonArray(attributes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())); Changed(); };
        }
        if (attributes.Any(a => keywordChoices.All(k => k.Id != a)) || _cardDocument.Component("keywords")?["keywords"] is JsonArray { Count: > 0 })
            rulePage.AddChild(MenuStyles.Label("此卡还有导入词条，原有内容会完整保留。", 14, MenuStyles.Dim));
        var shared = _native.Effects();
        if (shared.Count > 0)
        {
            rulePage.AddChild(MenuStyles.Label("使用包内效果", 18, MenuStyles.Gold));
            var selected = (_cardDocument.Component("effects")?["prefabs"] as JsonArray ?? new()).Select(n => n!.ToString()).ToList();
            foreach (var effect in shared)
            {
                var check = new CheckBox { Text = effect.Name, ButtonPressed = selected.Contains(effect.Asset.Id), TooltipText = effect.Description }; rulePage.AddChild(check);
                check.Toggled += enabled => { selected.RemoveAll(id => id == effect.Asset.Id); if (enabled) selected.Add(effect.Asset.Id); _cardDocument.Set("effects", "prefabs", new JsonArray(selected.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())); Changed(); };
            }
        }
        var ruleHint = MenuStyles.Label("导入包的原生效果完整保留。可编排的效果使用通用的时机、条件、目标和动作。", 14, MenuStyles.Dim); ruleHint.AutowrapMode = TextServer.AutowrapMode.WordSmart; rulePage.AddChild(ruleHint);
        rulePage.AddChild(MenuStyles.Button("编排可编辑效果…", () =>
        {
            var dialog = new ConfirmationDialog { Title = "规则编排", OkButtonText = "应用到当前卡牌", DialogHideOnOk = false, MinSize = new(1040, 680) }; AddChild(dialog);
            var scroll = new ScrollContainer { CustomMinimumSize = new(1020, 580), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; dialog.AddChild(scroll);
            var inspector = new NativeEffectInspector { CustomMinimumSize = new(1000, 540), SizeFlagsHorizontal = SizeFlags.ExpandFill, Catalog = () => _service.Cards }; scroll.AddChild(inspector); inspector.Initialize(); inspector.UsePlayerAuthoring();
            var draft = new DiyCardDraft { Name = _cardDocument.Name, OrcEffects = rules, UseStructuredEffects = true }; inspector.Open(InspectorCardRecord.FromDraft(draft));
            dialog.Confirmed += () => NativeTry(() =>
            {
                if (!inspector.CommitPendingRaw()) return;
                var updated = InspectorCardRecord.ApplyRecord(draft, inspector.Document.Element());
                if (updated.Effects.Length > 0) throw new InvalidDataException("请使用通用规则编排；旧简易效果行仍保留在原编辑器中。");
                _native.ComposeRules(_cardDocument.Json, asset.Id, updated.OrcEffects); // validate without changing the native source yet
                rules = updated.OrcEffects; rulesChanged = true; Changed(); dialog.QueueFree();
            }); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
        }));
        var displayText = new TextEdit { Text = text, CustomMinimumSize = new(0, 150), WrapMode = TextEdit.LineWrappingMode.Boundary }; DiyEditorScreen.Field(rulePage, "卡面说明（独立于规则执行）", displayText); displayText.TextChanged += () => { text = displayText.Text; Changed(); };
        art.AddChild(MenuStyles.Label("卡牌母版随单位、指令、反制自动切换。", 16));
        void ImportArt(bool full)
        {
            GuardNative(() =>
            {
                var picker = new FileDialog { Title = full ? "导入完整卡面" : "导入插画", FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Filters = ["*.png,*.jpg,*.jpeg,*.webp ; 图片"] }; AddChild(picker);
                picker.FileSelected += path => NativeTry(() => { var image = Image.LoadFromFile(path); if (image is null || image.GetWidth() > 8192 || image.GetHeight() > 8192) throw new InvalidDataException("图片无效或超过 8192×8192。"); _native.SetArtwork(asset.Id, image.SavePngToBuffer(), full); _textures.Clear(); Preview(); _notice.Text = "卡图已写入草稿，转正后生效。"; picker.QueueFree(); }); picker.Canceled += () => picker.QueueFree(); picker.PopupCentered();
            });
        }
        art.AddChild(MenuStyles.Button("导入插画…", () => ImportArt(false))); art.AddChild(MenuStyles.Button("导入完整卡面…", () => ImportArt(true)));
        var flavorEdit = new TextEdit { Text = flavor, CustomMinimumSize = new(0, 200), WrapMode = TextEdit.LineWrappingMode.Boundary }; DiyEditorScreen.Field(story, "背景描述", flavorEdit); flavorEdit.TextChanged += () => { flavor = flavorEdit.Text; Changed(); };
        _saveNativeCard = () =>
        {
            try
            {
                _native.SaveCardDocument(asset, _cardDocument.Json, text, flavor, rulesChanged ? rules : null);
                _nativeDirty = false; NativeSaveButton.Text = "保存草稿";
                _notice.Text = _native.PreviewCards().Any(c => c.CardId == asset.AppId)
                    ? "已保存到临时包；转正后才更新构筑与新对局。"
                    : "草稿已保存，但尚未通过引擎卡牌准入，请检查规则和属性。仍可继续编辑。";
                return true;
            }
            catch (Exception ex) { _notice.Text = ex.Message; return false; }
        };
        TypeVisibility(); Preview();
    }));
}
