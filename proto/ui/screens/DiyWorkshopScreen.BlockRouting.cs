using Godot;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    public event Action<string>? ConstructRequested;
    public event Action<UiCardDefinition>? TrialRequested;

    private void ApplyNativeCardAndUse(string packId, NativeWorkshopAsset asset, bool trial) => NativeTry(() =>
    {
        if (_saveNativeCard?.Invoke() != true) return;
        if (!_native!.PreviewCards().Any(c => c.CardId == asset.AppId))
            throw new InvalidDataException("此卡尚未通过引擎准入，请先修正属性或效果。");
        _native.Promote(); _nativeChanged!(); TutorialApplied(packId); ShowNativePackages();
        var card = _service.Cards.Single(c => c.CardId == asset.AppId);
        if (trial) TrialRequested?.Invoke(card); else ConstructRequested?.Invoke(card.CardId);
    });
    internal string BlockTitle { get; private set; } = "新效果";
    internal HandlerDocument BlockDocument { get; private set; } = new();
    internal string? BlockEffectId { get; private set; }
    internal string? BlockParentId { get; private set; }
    private NativeWorkshopAsset? _blockCard;
    private Action? _blockReturn;

    private void LaunchBlocks(string title, string? effectId, NativeWorkshopAsset? card, Action back)
        => _editor.GuardNavigation(() => GuardNative(() => NativeTry(() =>
        {
            if (_native?.HasSession != true) { ShowNativePackages(); _notice.Text = "请先打开一个卡包。"; return; }
            BlockTitle = string.IsNullOrWhiteSpace(title) ? "新效果" : title; BlockEffectId = effectId; _blockCard = card; _blockReturn = back;
            BlockDocument = new(); BlockParentId = null;
            if (effectId is not null)
            {
                var authoring = _native.HandlerAuthoring!;
                var dsl = "handler-dsl/" + effectId + ".hdsl.json";
                var draft = "modings/" + effectId + ".draft.moding.json";
                if (System.IO.File.Exists(System.IO.Path.Combine(authoring.PackRoot, dsl)))
                {
                    BlockDocument = HandlerDocument.FromJson(_native.Read(dsl), HandlerCatalog.Default);
                    if (System.IO.File.Exists(System.IO.Path.Combine(authoring.PackRoot, draft)))
                        BlockParentId = JsonNode.Parse(_native.Read(draft))?["asset"]?["extends"]?.ToString();
                }
                else
                {
                    var existing = authoring.ReadExistingDsl(effectId);
                    if (existing is not null)
                    {
                        BlockDocument = HandlerDocument.FromJson(existing, HandlerCatalog.Default);
                        var derived = "modings/" + HandlerDslAuthoring.ReplaceProductIdOf(effectId) + ".draft.moding.json";
                        if (System.IO.File.Exists(System.IO.Path.Combine(authoring.PackRoot, derived)))
                            BlockParentId = JsonNode.Parse(_native.Read(derived))?["asset"]?["extends"]?.ToString();
                    }
                }
            }
            foreach (var window in GetChildren().OfType<Window>()) window.Hide();
            BlockAuthoringRequested?.Invoke();
        })));

    private void OpenCardBlocks(string packId, NativeWorkshopAsset card)
    {
        var dialog = new AcceptDialog { Title = "卡牌效果 · 积木 Inspector", OkButtonText = "关闭", MinSize = new(600, 360) }; AddChild(dialog);
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 12); dialog.AddChild(box);
        var references = (_cardDocument?.Component("effects")?["prefabs"] as JsonArray ?? new()).Select(n => n!.ToString()).ToArray();
        var hint = MenuStyles.Label("选择已有积木效果继续编辑，或添加一条新效果。保存后会自动挂到当前卡牌；已有的其他效果保留。", 15, MenuStyles.Ink);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(hint);
        var effects = _native!.Effects();
        foreach (var id in references)
        {
            var effect = effects.FirstOrDefault(e => e.Asset.Id == id);
            box.AddChild(MenuStyles.Button("编辑 · " + (effect?.Name ?? id), () =>
            { dialog.QueueFree(); LaunchBlocks(effect?.Name ?? id, id, card, () => OpenNativeCard(packId, card)); }));
        }
        box.AddChild(MenuStyles.Button("＋ 添加积木效果", () =>
        { dialog.QueueFree(); LaunchBlocks("卡牌新效果", null, card, () => OpenNativeCard(packId, card)); }));
        dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered();
    }

    internal void AcceptBlockSave(HandlerDslSaveResult result)
    {
        if (!result.Success || result.ProductId is null) return;
        if (_blockCard is { } card)
        {
            var document = new NativeCardDocument(_native!.Read(card.Path));
            var references = (document.Component("effects")?["prefabs"] as JsonArray ?? new()).Select(n => n!.ToString()).ToList();
            if (BlockEffectId is { } old) references.RemoveAll(id => id == old);
            if (!references.Contains(result.ProductId)) references.Add(result.ProductId);
            document.Set("effects", "prefabs", new JsonArray(references.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()));
            _native.Commit(card.Path, document.Json);
        }
        _native!.SaveEffectInfo(result.ProductId, BlockTitle, "使用积木 Inspector 制作的效果");
        // SaveEffectInfo checkpoints the entire recovery package, including generated files.
        BlockEffectId = result.ProductId;
    }

    internal void ReturnFromBlocks() { _blockReturn?.Invoke(); _blockReturn = null; }

    private bool TutorialBlockReady()
    {
        if (_native?.HandlerAuthoring is not { } authoring || _cardDocument is null) return false;
        var references = _cardDocument.Component("effects")?["prefabs"] as JsonArray ?? new();
        foreach (var reference in references)
        {
            var id = reference?.ToString();
            var dsl = System.IO.Path.Combine(authoring.PackRoot, "handler-dsl", id + ".hdsl.json");
            var draft = System.IO.Path.Combine(authoring.PackRoot, "modings", id + ".draft.moding.json");
            if (!System.IO.File.Exists(dsl) || !System.IO.File.Exists(draft)) continue;
            var parent = JsonNode.Parse(System.IO.File.ReadAllText(draft))?["asset"]?["extends"]?.ToString();
            var timing = authoring.DescribeParents().FirstOrDefault(p => p.ParentId == parent);
            var doc = HandlerDocument.FromJson(System.IO.File.ReadAllText(dsl), HandlerCatalog.Default);
            if (timing is not null && DiyTutorialLesson.BlockEffectReady(doc, timing.Signal, timing.Guard)) return true;
        }
        return false;
    }

    internal void OpenCompatibleBlocks() => _editor.GuardNavigation(() => NativeTry(() =>
    {
        if (!_editor.Save()) return;
        var pack = _compatiblePack ?? _pack ?? throw new InvalidOperationException("请先打开卡包。");
        // Preserve the original library and make a native working copy, including its artwork.
        OpenCompatibleCopy(pack, _editor.Draft);
    }));

    private void OpenKeywordBlocks(Kards.Ui.Contracts.DiyPackDraft pack, Kards.Ui.Contracts.DiyKeywordDraft keyword, Window owner) => NativeTry(() =>
    {
        var source = new Kards.Ui.Contracts.DiyCardDraft { Name = keyword.Name + " · 词条练习卡", PackId = pack.Id,
            OrcEffects = keyword.Effects, EffectText = keyword.EffectText, UseStructuredEffects = true, Keywords = [keyword.Id] };
        // Keep even unsaved keyword edits in the copy's vocabulary.
        var vocabulary = pack with { Keywords = pack.Keywords.Where(k => k.Id != keyword.Id).Append(keyword).ToArray() };
        OpenCompatibleCopy(vocabulary, source);
        if (owner != GetWindow()) owner.QueueFree();
    });

    private void OpenCompatibleCopy(Kards.Ui.Contracts.DiyPackDraft pack, Kards.Ui.Contracts.DiyCardDraft source)
    {
        var ids = pack.Nations.ToDictionary(n => n.Id, _ => "nation-" + Guid.NewGuid().ToString("N"));
        foreach (var keyword in pack.Keywords) ids[keyword.Id] = "kw-" + Guid.NewGuid().ToString("N");
        T Remap<T>(T value)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(value);
            foreach (var pair in ids) json = json.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
            return System.Text.Json.JsonSerializer.Deserialize<T>(json)!;
        }
        var title = source.Name[..Math.Min(source.Name.Length, 45)] + " · 积木工作副本";
        var copy = new Kards.Ui.Contracts.DiyPackDraft { Name = title,
            Nations = pack.Nations.Select(Remap).ToArray(), Keywords = pack.Keywords.Select(Remap).ToArray() };
        foreach (var nation in pack.Nations.Where(n => n.IconFile.Length > 0))
        {
            var copied = copy.Nations.Single(n => n.Id == ids[nation.Id]);
            _store.SaveNationIcon(copied, _cards.ReadArt(nation.IconFile));
        }
        var card = Remap(source) with { Id = "diy/" + Guid.NewGuid().ToString("N"), PackId = copy.Id, SourceCardId = "" };
        var zip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orc-block-copy-" + Guid.NewGuid().ToString("N") + ".zip");
        _store.Save(copy); _changed(); // Export resolves vocabulary through the authoring library.
        try
        {
            var exported = DiyPackExport.Export(copy, [card], _service.Cards, _cards.ArtDirectory, zip);
            if (exported.Archive is null) throw new InvalidDataException(string.Join("；", exported.Problems));
            _native!.Suspend(); var installed = _native.Import(zip); _nativeChanged!(); OpenNativePack(installed.Pack.Id);
            var asset = _native.Assets().Single(a => a.Kind == "Card"); OpenNativeCard(installed.Pack.Id, asset);
            _notice.Text = "已保留原卡牌，并创建原生积木工作副本。后续保存和应用修改作用于此副本。";
            LaunchBlocks(card.Name, null, asset, () => OpenNativeCard(installed.Pack.Id, asset));
        }
        finally { if (System.IO.File.Exists(zip)) System.IO.File.Delete(zip); }
    }
}
