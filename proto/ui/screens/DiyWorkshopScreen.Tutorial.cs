using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class DiyWorkshopScreen
{
    private static readonly string TutorialFile = OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--verify-") || a.StartsWith("--capture-"))
        ? "user://verification-diy-tutorial-" + Guid.NewGuid().ToString("N") + ".cfg" : "user://diy-tutorial.cfg";
    private PanelContainer _tutorialPanel = null!;
    private Label _tutorialTitle = null!, _tutorialBody = null!, _tutorialStatus = null!;
    internal Button TutorialButton = null!, TutorialNext = null!;
    private Button _tutorialLocate = null!, _tutorialBack = null!;
    private string _tutorialPack = "", _tutorialCard = "";
    private int _tutorialStep;
    private bool _tutorialActive;
    private Func<bool>? _tutorialCheck;
    private Action? _tutorialFocus;
    private bool _tutorialSaved;
    internal int TutorialStep => _tutorialStep;
    internal string TutorialStatus => _tutorialStatus.Text;

    private void InitializeTutorial(HBoxContainer toolbar)
    {
        var config = new ConfigFile();
        if (config.Load(TutorialFile) == Error.Ok)
        {
            _tutorialPack = config.GetValue("lesson", "pack", "").AsString();
            _tutorialCard = config.GetValue("lesson", "card", "").AsString();
            _tutorialStep = Math.Clamp(config.GetValue("lesson", "step", 0).AsInt32(), 0, 7);
        }
        TutorialButton = MenuStyles.Button("DIY 引导教程", () => _editor.GuardNavigation(() => GuardNative(StartTutorial)));
        toolbar.AddChild(TutorialButton);
        _tutorialPanel = new PanelContainer { Visible = false }; AddChild(_tutorialPanel);
        var style = new StyleBoxFlat { BgColor = MenuStyles.Panel, BorderColor = MenuStyles.Gold,
            BorderWidthLeft = 3, ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 10 };
        _tutorialPanel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 6); _tutorialPanel.AddChild(box);
        _tutorialTitle = MenuStyles.Label("", 19, MenuStyles.Gold); box.AddChild(_tutorialTitle);
        _tutorialBody = MenuStyles.Label("", 14, MenuStyles.Ink); _tutorialBody.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(_tutorialBody);
        var row = new HFlowContainer(); box.AddChild(row);
        _tutorialBack = MenuStyles.Button("上一步", () => { _tutorialStep = Math.Max(1, _tutorialStep - 1); SaveTutorial(); RefreshTutorial(); _tutorialFocus?.Invoke(); }); row.AddChild(_tutorialBack);
        _tutorialLocate = MenuStyles.Button("定位操作区域", LocateTutorial); row.AddChild(_tutorialLocate);
        TutorialNext = MenuStyles.Button("", AdvanceTutorial); MenuStyles.Primary(TutorialNext); row.AddChild(TutorialNext);
        row.AddChild(MenuStyles.Button("暂时退出", () => _editor.GuardNavigation(() => GuardNative(() =>
        { _tutorialActive = false; _tutorialPanel.Hide(); SaveTutorial(); }))));
        row.AddChild(MenuStyles.Button("从头练习（新卡包）", () => _editor.GuardNavigation(() => GuardNative(() =>
        { _tutorialStep = 0; _tutorialSaved = false; SaveTutorial(); RefreshTutorial(); }))));
        _tutorialStatus = MenuStyles.Label("", 13, MenuStyles.Dim); _tutorialStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(_tutorialStatus);
    }

    private void SaveTutorial()
    {
        var config = new ConfigFile(); config.SetValue("lesson", "pack", _tutorialPack);
        config.SetValue("lesson", "card", _tutorialCard); config.SetValue("lesson", "step", _tutorialStep);
        if (config.Save(TutorialFile) != Error.Ok) _tutorialStatus.Text = "教程进度暂时无法写入磁盘；已保存的卡包草稿仍可在我的卡包中找到。";
    }

    private void StartTutorial()
    {
        _tutorialActive = true; _tutorialPanel.Show();
        _tutorialSaved = false;
        if (_tutorialStep == 6) _tutorialStep = 5;
        if (_tutorialStep is > 0 and < 7)
        {
            LocateTutorial();
            return;
        }
        RefreshTutorial();
    }

    private void LocateTutorial() => _editor.GuardNavigation(() => GuardNative(() => NativeTry(() =>
    {
        if (_tutorialStep is 0 or 7) return;
        OpenNativePack(_tutorialPack);
        if (_native?.HasSession != true || _native.WorkingPackage.Id != _tutorialPack)
            throw new InvalidOperationException("练习卡包无法打开。请在“我的卡包”检查是否已被删除。");
        var asset = _native.Assets().FirstOrDefault(a => a.Kind == "Card" && a.Id == _tutorialCard);
        if (asset is null) { _tutorialStatus.Text = "练习卡牌已被删除。暂时退出教程后，可在卡包内继续自由制作。"; return; }
        OpenNativeCard(_tutorialPack, asset); _tutorialFocus?.Invoke();
    })));

    private bool IsTutorialCard => _tutorialActive && _native?.HasSession == true
        && _native.WorkingPackage.Id == _tutorialPack && _nativeAsset?.Id == _tutorialCard && _workspace.Visible;

    private void RefreshTutorial()
    {
        if (!_tutorialActive) return;
        var titles = new[] { "第一课 · 制作补给步兵", "1 / 6 · 确定卡牌属性", "2 / 6 · 添加闪击词条", "3 / 6 · 编排部署抽牌", "4 / 6 · 写清卡面说明", "5 / 6 · 保存并检查草稿", "6 / 6 · 应用到游戏", "第一课已完成" };
        var bodies = new[] {
            "目标：2 费、2 攻、3 防、行动费用 1 的步兵，带闪击，部署时抽 1 张牌。点击开始会新建独立练习卡包和空白卡牌。",
            "在“属性”页把名称改为“补给步兵”，类型选单位、兵种选步兵；部署费用 2，攻击 2，防御 3，行动费用 1。国家任选。右侧卡面会实时更新。",
            "在“规则”页勾选“闪击”。词条描述卡牌自身的能力；下一步再制作部署时执行的效果。",
            "在“规则”页打开“编辑效果 · 积木 Inspector…”，添加积木效果。顶部时机选择单位部署后的 Self 模板，在工具箱添加“抽牌”，数量填 1。点击“保存到卡包”，再点“完成”返回；效果会自动关联当前卡牌。",
            "在“规则”页的“卡面说明”填写“闪击。部署：抽 1 张牌。”。这段文字给玩家阅读；真正的抽牌行为来自上一步的效果。卡图可以在完成教程后导入。",
            "点击下方“保存并检查”。教程会检查属性、词条和效果，并确认卡牌已通过引擎准入。保存草稿后，构筑和新对局还不会更新。",
            "点击下方“应用修改”。成功后，这张练习卡会进入当前启用的卡池，可去卡组构筑中选择练习卡包并加入卡组。",
            "你已完成属性 → 词条 → 效果 → 说明 → 草稿 → 应用。练习卡包保留在“我的卡包”。接下来可导入卡图、修改抽牌数量，再保存并应用；也可导出卡包分享。"
        };
        _tutorialTitle.Text = titles[_tutorialStep]; _tutorialBody.Text = bodies[_tutorialStep];
        TutorialNext.Text = _tutorialStep switch { 0 => "创建练习卡包并开始", 5 => "保存并检查", 6 => "应用修改", 7 => "完成教程", _ => "检查并继续 →" };
        _tutorialBack.Visible = _tutorialStep is > 1 and < 6;
        _tutorialLocate.Visible = _tutorialStep is > 0 and < 7;
        _tutorialStatus.Text = "";
    }

    private void AdvanceTutorial()
    {
        if (_tutorialStep == 0) _editor.GuardNavigation(() => GuardNative(AdvanceTutorialCore));
        else AdvanceTutorialCore();
    }

    private void AdvanceTutorialCore() => NativeTry(() =>
    {
        if (_tutorialStep == 7) { _tutorialActive = false; _tutorialPanel.Hide(); return; }
        if (_tutorialStep == 0)
        {
            if (_native is null) { _tutorialStatus.Text = "原生工坊尚未就绪。"; return; }
            var reference = _native.Packages().Where(p => p.Enabled).OrderByDescending(p => p.Id == "builtin").FirstOrDefault();
            if (reference is null) { _tutorialStatus.Text = "请先启用一个基础卡包，再开始教程。"; return; }
            _native.Suspend(); _tutorialPack = "user.tutorial." + Guid.NewGuid().ToString("N");
            _native.Create(_tutorialPack, "DIY 练习 · 补给步兵", reference.Id);
            var asset = _native.CreateCard("tutorial.supply-infantry"); _tutorialCard = asset.Id;
            _tutorialStep = 1; SaveTutorial(); OpenNativeCard(_tutorialPack, asset); RefreshTutorial(); return;
        }
        if (!IsTutorialCard) { _tutorialStatus.Text = "请点击“定位操作区域”返回练习卡牌。"; return; }
        if (_tutorialCheck?.Invoke() != true) return;
        if (_tutorialStep == 5)
        {
            if (_saveNativeCard?.Invoke() != true) { _tutorialStatus.Text = "草稿未保存，请查看上方错误提示。"; return; }
            if (!_native!.PreviewCards().Any(c => c.CardId == _nativeAsset!.AppId)) { _tutorialStatus.Text = "草稿已保存，但未通过引擎准入。请检查上方提示和规则。"; return; }
            _tutorialSaved = true;
        }
        if (_tutorialStep == 6)
        {
            if (_nativeDirty || !_tutorialSaved) { _tutorialStep = 5; RefreshTutorial(); return; }
            NativePromoteButton.EmitSignal(Button.SignalName.Pressed); return;
        }
        _tutorialStep++; SaveTutorial(); RefreshTutorial(); _tutorialFocus?.Invoke();
    });

    private void TutorialApplied(string id)
    {
        if (!_tutorialActive || id != _tutorialPack || _tutorialStep != 6) return;
        _tutorialStep = 7; SaveTutorial(); RefreshTutorial();
    }
}
