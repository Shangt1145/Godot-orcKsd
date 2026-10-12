using Godot;

namespace Kards.Ui;

/// <summary>Reports actual initialization stages; no fixed loading timer.</summary>
public partial class MenuSplash : PanelContainer
{
    private Label _stage = null!;
    private ProgressBar _progress = null!;
    internal float Progress => (float)_progress.Value;
    internal string Stage => _stage.Text;
    internal void Initialize()
    {
        Theme = UiStyles.CreateMenuTheme(); ZIndex = 150; MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", UiStyles.Box(new("111b16"), new("111b16"), 0, 0));
        var center = new CenterContainer(); AddChild(center);
        var content = new VBoxContainer { CustomMinimumSize = new(460, 0) }; content.AddThemeConstantOverride("separation", 20); center.AddChild(content);
        var mark = UiStyles.Label("✦", 70, UiStyles.Gold); mark.HorizontalAlignment = HorizontalAlignment.Center; content.AddChild(mark);
        var title = UiStyles.Label("OrC-KSD", 42, UiStyles.MenuInk); title.HorizontalAlignment = HorizontalAlignment.Center; content.AddChild(title);
        var caption = UiStyles.Label("正在准备指挥部", 18, UiStyles.MenuDim); caption.HorizontalAlignment = HorizontalAlignment.Center; content.AddChild(caption);
        _progress = new ProgressBar { MinValue = 0, MaxValue = 1, Step = .01, ShowPercentage = false, CustomMinimumSize = new(0, 4), MouseFilter = MouseFilterEnum.Ignore }; content.AddChild(_progress);
        _progress.AddThemeStyleboxOverride("background", UiStyles.Box(new("333e2f"), new("333e2f"), 0, 0));
        _progress.AddThemeStyleboxOverride("fill", UiStyles.Box(UiStyles.Gold, UiStyles.Gold, 0, 0));
        _stage = UiStyles.Label("", 14, UiStyles.MenuDim); _stage.HorizontalAlignment = HorizontalAlignment.Center; content.AddChild(_stage);
        IgnoreChildInput(this);
    }
    private static void IgnoreChildInput(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is Control control) control.MouseFilter = MouseFilterEnum.Ignore;
            IgnoreChildInput(child);
        }
    }
    internal void SetStage(float progress, string stage) { _progress.Value = progress; _stage.Text = stage; }
    internal void Fail(string reason) { _stage.Text = "启动失败：" + reason; _stage.AutowrapMode = TextServer.AutowrapMode.WordSmart; }
}
