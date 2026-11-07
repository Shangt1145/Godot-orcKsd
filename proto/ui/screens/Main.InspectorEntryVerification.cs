using Godot;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyInspectorEntry()
    {
        try
        {
            _clock.SetReducedMotion(true);
            Show("editor");
            _workshop.ShowHome();
            await MenuSettledAsync();
            Require(_workshop.OpenInspectorButton.IsVisibleInTree(), "工坊首页没有新版 Inspector 入口");
            await DeckClickAsync(_workshop.OpenInspectorButton);
            Require(_active == "hdsl" && _blockEditor.IsVisibleInTree(), "新版入口未进入积木界面");
            await MenuSettledAsync();
            var directory = ProjectSettings.GlobalizePath("res://artifacts/inspector-entry");
            Directory.CreateDirectory(directory);
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(directory + "/new-inspector.png");
            }
            var done = _blockEditor.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "完成");
            await DeckClickAsync(done);
            Require(_active == "editor", "工坊打开的新版 Inspector 返回了旧编辑器");
            await MenuSettledAsync();
            _diyEditor.Load(new() { Name = "入口验证" });
            _diyEffects.Open(_diyEditor.Draft);
            Show("diyfx");
            await MenuSettledAsync();
            var legacyEntry = _diyEffects.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "积木编辑");
            await DeckClickAsync(legacyEntry);
            Require(_active == "hdsl", "原效果页的新版入口失效");
            await MenuSettledAsync();
            await DeckClickAsync(done);
            Require(_active == "diyfx", "原效果页打开的新版 Inspector 未返回来源页");
            GD.Print("INSPECTOR_ENTRY_VERIFY_OK: workshop -> hdsl -> workshop; legacy -> hdsl -> legacy");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
