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
            var home = ProjectSettings.GlobalizePath("res://artifacts/inspector-entry");
            Directory.CreateDirectory(home);
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng(home + "/workshop-home.png");
            }
            GD.Print($"[inspector-entry] session={_workshop.HasNativeSession} notice=[{_workshop.NoticeText}]");
            // 首页没有打开的卡包＝没有保存目标。此时进去只能搭不能存，入口必须先把人带到卡包列表。
            await DeckClickAsync(_workshop.OpenInspectorButton);
            Require(_active == "editor" && !_blockEditor.IsVisibleInTree(), "没有卡包会话时入口不该进积木界面");
            Require(_workshop.NoticeText.Contains("先打开一个卡包"), "入口被挡时没有说明原因");
            await MenuSettledAsync();
            // 再点一次仍然是同样的结果——不能第一次挡、第二次就放进去。
            await DeckClickAsync(_workshop.OpenInspectorButton);
            Require(_active == "editor" && !_blockEditor.IsVisibleInTree(), "无会话时第二次点击仍应停在工坊");
            await MenuSettledAsync();

            // 有会话的那条路（工坊 → 打开卡包 → 积木）需要真的打开一个原生卡包，
            // 依赖玩家本机的卡包库，不适合做确定性探针；这里用原效果页的入口覆盖画布本身：
            // 进去、截图、点完成、回到来源页。
            _diyEditor.Load(new() { Name = "入口验证" });
            _diyEffects.Open(_diyEditor.Draft);
            Show("diyfx");
            await MenuSettledAsync();
            var legacyEntry = _diyEffects.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == "积木编辑");
            await DeckClickAsync(legacyEntry);
            Require(_active == "hdsl", "原效果页的新版入口失效");
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
            Require(_active == "diyfx", "原效果页打开的新版 Inspector 未返回来源页");
            GD.Print("INSPECTOR_ENTRY_VERIFY_OK: workshop(no-session) -> blocked+notice; legacy -> hdsl -> legacy");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
