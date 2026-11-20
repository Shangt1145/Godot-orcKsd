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
            if (config.GetValue("lesson", "version", 1).AsInt32() == 1 && _tutorialStep < 7)
                _tutorialStep = 0;
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
        { _tutorialStep = 0; SaveTutorial(); RefreshTutorial(); }))));
        _tutorialStatus = MenuStyles.Label("", 13, MenuStyles.Dim); _tutorialStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart; box.AddChild(_tutorialStatus);
    }

    private void SaveTutorial()
    {
        var config = new ConfigFile(); config.SetValue("lesson", "pack", _tutorialPack);
        config.SetValue("lesson", "card", _tutorialCard); config.SetValue("lesson", "step", _tutorialStep);
        config.SetValue("lesson", "version", 2);
        if (config.Save(TutorialFile) != Error.Ok) _tutorialStatus.Text = "教程进度暂时无法写入磁盘；已保存的卡包草稿仍可在我的卡包中找到。";
    }

    private void StartTutorial()
    {
        _tutorialActive = true; _tutorialPanel.Show();
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
        var titles = new[] { "第一课 · 制作补给步兵", "填写卡牌属性", "添加闪击", "添加部署抽牌", "试打第一版", "改成抽两张", "再试一次", "第一课已完成" };
        var bodies = new[] {
            "目标：2 费、2 攻、3 防、行动费用 1 的步兵，带闪击，部署时抽 1 张牌。点击开始会新建独立练习卡包和空白卡牌。",
            "在“基础属性”分区把名称改为“补给步兵”，类型选单位、兵种选步兵；部署费用 2，攻击 2，防御 3，行动费用 1。国家任选。右侧卡面会实时更新。",
            "在“词条与效果”分区勾选“闪击”。词条描述卡牌自身的能力；下一步再制作部署时执行的效果。",
            "在“词条与效果”点“添加效果”，选抽牌，数量保留为 1。完成后，核对卡面上的“部署：抽 1 张牌”。",
            "进入试打后，把起手的补给步兵拖到我方支援线。确认抽到一张牌，然后点“返回改卡”。",
            "点抽牌效果旁的“编辑”，把数量从 1 改为 2。",
            "再次部署补给步兵。确认抽到两张牌，然后点“返回改卡”。",
            "练习卡留在练习卡包的草稿中。你可以继续改卡，也可以在卡包内应用修改或导出分享。"
        };
        _tutorialTitle.Text = titles[_tutorialStep]; _tutorialBody.Text = bodies[_tutorialStep];
        TutorialNext.Text = _tutorialStep switch { 0 => "创建练习卡包并开始", 4 or 6 => "试打这张卡", 7 => "完成教程", _ => "检查并继续 →" };
        _tutorialBack.Visible = _tutorialStep is 2 or 3;
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
        if (_tutorialStep is 4 or 6)
        {
            TryNativeDraft(_nativeAsset!);
            return;
        }
        if (_saveNativeCard?.Invoke() != true) { _tutorialStatus.Text = "草稿未保存，请查看工坊提示。"; return; }
        _tutorialStep++; SaveTutorial(); RefreshTutorial(); _tutorialFocus?.Invoke();
    });

    internal void ReportTrialError(string error) => _notice.Text = "试打未能开始：" + error;

    internal void FinishTutorialTrial(int? drawn)
    {
        if (!IsTutorialCard || _tutorialStep is not (4 or 6)) return;
        var expected = _tutorialStep == 4 ? 1 : 2;
        if (drawn != expected) { _tutorialStatus.Text = $"请部署练习卡并确认抽到 {expected} 张牌，再返回。"; return; }
        _tutorialStep++; SaveTutorial(); RefreshTutorial(); _tutorialFocus?.Invoke();
    }
}
