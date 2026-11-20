using Godot;
using System.Text.Json.Nodes;
using Kards.Ui.OrcBridge;
using Kards.Ui.OrcBridge.HandlerDsl;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private void ShowWorkshopGuide(string section = "制作卡牌")
    {
        var chapters = new Dictionary<string, string>
        {
            ["制作卡牌"] = "1. 在工坊创建或打开卡包。做扩展卡时，先选择基础卡包，再创建扩展包。\n\n2. 在这个包内点「新建卡牌」或「从模板新建」。卡牌会保存到当前包。\n\n3. 在「基础属性」填名称和数值，右侧可以看卡面。在「词条与效果」点「添加效果」，选抽牌、伤害或增强，填数值后点「完成」。",
            ["保存和试打"] = "「保存草稿」会保留本次修改。「试打这张卡」也会先保存草稿，接着打开练习对局。起手就是这张卡，场上准备了练习目标。\n\n单位拖到我方支援线，指令拖向合法目标。试完点「返回改卡」，可以继续修改。\n\n想让正式构筑和新对局使用草稿时，在卡包内点「应用修改」。",
            ["效果为什么没触发"] = "打开「词条与效果」，检查有没有配置实际的对局效果。卡面上写的文字不会自行执行。\n\n确认时机和目标：部署只在单位进场时发生，伤害敌方单位需要合法的敌军目标。\n\n从文字解析时，检查页面列出的未识别句子。复杂条件可以进高级积木。保存被拒绝时，按提示修正属性或规则，再试打。",
            ["模板与 DSL"] = "「从模板新建」会在当前包创建一张可独立修改的卡。\n\n包内保存过的积木效果，可从「打开 DSL」选择并重新编辑。也可以导入 .hdsl.json 文件。如果文件只含动作，先确认触发时机，再保存到当前包。",
            ["管理卡包"] = "打开卡包，选择「导出草稿」可以分享还在制作的版本。要分享已经应用的版本，从「我的卡包」导出。朋友导入扩展包前，需要对应的基础卡包。\n\n要移除作品，从工坊首页点相应包的「管理 / 分享」，再点「删除卡包」或「删除扩展包」。确认后会留下本机备份。有扩展包依赖的基础包需要先处理依赖。"
        };
        var dialog = new AcceptDialog { Title = "工坊说明书", OkButtonText = "关闭" };
        AddChild(dialog);
        var box = new VBoxContainer { CustomMinimumSize = new(430, 0) }; dialog.AddChild(box);
        var tabs = new HFlowContainer(); box.AddChild(tabs);
        var text = new TextEdit { Editable = false, Text = chapters[section], WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new(0, 265) };
        foreach (var (title, chapter) in chapters)
            tabs.AddChild(MenuStyles.Button(title, () => { text.Text = chapter; text.ScrollVertical = 0; }));
        box.AddChild(text);
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }

    private void ShowCardTemplates(string packId)
    {
        var dialog = new AcceptDialog { Title = "卡牌模板", OkButtonText = "取消" };
        AddChild(dialog);
        var choices = new VBoxContainer(); dialog.AddChild(choices);
        foreach (var (name, type, effect) in new[]
        {
            ("补给步兵", "unit", new WorkshopQuickEffect()),
            ("火力打击", "order", new WorkshopQuickEffect("damage", 2)),
            ("增援指令", "order", new WorkshopQuickEffect("buff"))
        })
        {
            choices.AddChild(MenuStyles.Button(name + " · " + effect.Description(type), () => NativeTry(() =>
            {
                if (_native?.SessionPackage != packId) throw new InvalidOperationException("请先打开要保存到的卡包或扩展包。");
                var localId = "card." + Guid.NewGuid().ToString("N");
                var rule = effect.Compile($"pack/{packId}/{localId}", type, _service.Cards);
                var card = _native.CreateCard(localId);
                try
                {
                    var document = new NativeCardDocument(_native.Read(card.Path));
                    document.Rename(name);
                    document.SetType(type, "infantry");
                    document.Set("factionCost", "kredits", JsonValue.Create(2));
                    if (type == "unit")
                    {
                        document.Set("battleStats", "attack", JsonValue.Create(2));
                        document.Set("battleStats", "defense", JsonValue.Create(3));
                    }
                    _native.SaveCardDocument(card, document.Json, effect.Description(type), "", [rule]);
                }
                catch { _native.DeleteAsset(card); throw; }
                dialog.QueueFree();
                OpenNativeCard(packId, card);
            })));
        }
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }

    private void OpenDslFile(string packId)
    {
        var dialog = new AcceptDialog { Title = "打开 DSL", OkButtonText = "关闭" };
        AddChild(dialog);
        var box = new VBoxContainer(); dialog.AddChild(box);
        var authoring = _native!.HandlerAuthoring!;
        var scroll = new ScrollContainer { CustomMinimumSize = new(400, 220) }; box.AddChild(scroll);
        var choices = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(choices);
        foreach (var effect in _native.Effects())
        {
            var id = effect.Asset.Id;
            if (!File.Exists(Path.Combine(authoring.PackRoot, "handler-dsl", id + ".hdsl.json")) && authoring.ReadExistingDsl(id) is null) continue;
            choices.AddChild(MenuStyles.Button(effect.Name, () =>
            {
                dialog.QueueFree();
                LaunchBlocks(effect.Name, id, null, () => OpenNativePack(packId));
            }));
        }
        if (choices.GetChildCount() == 0) choices.AddChild(MenuStyles.Label("这个包里还没有保存过 DSL。", 14));
        var hint = MenuStyles.Label("从文件打开后，请确认触发时机再保存。", 14, MenuStyles.Dim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(hint);
        box.AddChild(MenuStyles.Button("从文件打开 .hdsl.json…", () =>
        {
            dialog.Hide();
            dialog.QueueFree();
            var picker = new FileDialog { Name = "WorkshopDslPicker", Access = FileDialog.AccessEnum.Filesystem,
                FileMode = FileDialog.FileModeEnum.OpenFile, UseNativeDialog = DisplayServer.GetName() != "headless",
                Filters = ["*.hdsl.json ; 积木 DSL"], Title = "打开积木 DSL" };
            AddChild(picker);
            picker.FileSelected += path =>
            {
                picker.QueueFree();
                NativeTry(() =>
                {
                    var source = PlatformFiles.ReadLocalCopy(path, ".hdsl.json");
                    if (new FileInfo(source).Length > 4 * 1024 * 1024) throw new InvalidDataException("DSL 文件不能超过 4 MiB。");
                    var document = HandlerDocument.FromJson(File.ReadAllText(source), HandlerCatalog.Default);
                    LaunchBlocks(Path.GetFileNameWithoutExtension(path), null, null, () => OpenNativePack(packId), document);
                });
            };
            picker.Canceled += picker.QueueFree;
            picker.PopupCentered();
        }));
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }
}
