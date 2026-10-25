using Godot;

namespace Kards.Ui;

public partial class Main
{
    public override void _Notification(int what)
    {
        if (what != NotificationWMGoBackRequest || !OS.HasFeature("mobile")) return;
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
