using Godot;

namespace Kards.Ui;

public partial class Main
{
    public override void _Notification(int what)
    {
        // 切后台/回前台：逻辑本身与平台无关（探针直接调），这里只负责在移动端把系统通知接过来。
        // 桌面也有同名通知（窗口失焦），但桌面进程不会被挂起，接过来只会制造噪音。
        // 常量在 Godot 的 C# 绑定里是 long，_Notification 给的是 int，直接比会 CS0266，所以显式转。
        var paused = what == (int)NotificationApplicationPaused;
        var resumed = what == (int)NotificationApplicationResumed;
        if ((paused || resumed) && OS.HasFeature("mobile"))
        {
            if (paused) HandleApplicationPaused();
            else HandleApplicationResumed();
            return;
        }
        if (what != NotificationWMGoBackRequest || !OS.HasFeature("mobile")) return;
        if(_console?.IsOpen==true)
        {GetViewport().PushInput(new InputEventKey{Keycode=Key.Escape,Pressed=true},true);return;}
        var modal = FindChildren("*", "Window", true, false).OfType<Window>().Any(window => window.Visible);
        var cancelBattle = _active == "battle" && _battle?.HasCancelableInteraction == true;
        // Let modal dialogs and the battle's existing cancel handling consume Escape first.
        GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, Pressed = true }, true);
        if (modal || cancelBattle) return;
        if (_active == "battle") Show("setup");
        else if (_active is "deck" or "editor" or "diyfx") Show(_active == "deck" ? "decklib" : "home");
        else if (_active == "home")
        {
            var confirm = new ConfirmationDialog { Title = "退出游戏？", DialogText = "退出会关闭本机房间。卡组和工坊内容已保存在本机。", OkButtonText = "退出", CancelButtonText = "继续游戏" };
            AddChild(confirm); confirm.Confirmed += () => GetTree().Quit(); confirm.Canceled += confirm.QueueFree; confirm.PopupCentered();
        }
        else Show(_active == "room" ? "setup" : "home");
    }
}
