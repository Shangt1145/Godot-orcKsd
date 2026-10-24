using Godot;

namespace Kards.Ui;

/// <summary>
/// 触屏适配（手机版 MVP）：把"长按"映射成鼠标右键。
/// <para>
/// 桌面端整套交互建立在"左键拖动 + 右键取消 + Esc 退出"上，触屏一个都没有。
/// Godot 自带的 <c>emulate_mouse_from_touch</c> 已经把点按/拖动喂成了左键事件，
/// 所以只缺取消——按住不动 0.45 秒合成一次右键，走原来完全相同的取消路径。
/// </para>
/// <para>
/// 只在触屏设备上生效：桌面（含所有验证探针）行为一字不变。
/// </para>
/// </summary>
public partial class BattleScreen
{
    private const double LongPressSeconds = 0.45;
    private const float TouchSlopPixels = 12f;

    private Vector2 _touchOrigin;
    private bool _touchHeld;
    private int _touchToken;

    /// <summary>是否触屏环境。桌面没开 --mobile-sim 时返回 false，长按逻辑整体短路。</summary>
    private static bool TouchInputAvailable =>
        MobileSim.Enabled || DisplayServer.IsTouchscreenAvailable();

    /// <summary>记录触摸态；由 <see cref="BattleScreen"/> 的 _Input 每个事件调用一次。</summary>
    private void TrackTouch(InputEvent e)
    {
        if (!TouchInputAvailable) return;

        // 桌面模拟时鼠标不会产生 ScreenTouch 事件，把左键按下/移动/抬起映射成同一套状态机：
        // 按下后按住不动 0.45 秒就等价于手机长按，用来在电脑上验触屏取消动作。
        var simulateMouse = MobileSim.Enabled && !OS.HasFeature("mobile");

        switch (e)
        {
            case InputEventScreenTouch { Pressed: true } touch:
                BeginPress(touch.Position);
                break;
            case InputEventScreenTouch { Pressed: false }:
                CancelLongPress();
                break;
            case InputEventScreenDrag drag:
                MoveTo(drag.Position);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press when simulateMouse:
                BeginPress(press.Position);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when simulateMouse:
                CancelLongPress();
                break;
            case InputEventMouseMotion motion when simulateMouse && _touchHeld:
                MoveTo(motion.Position);
                break;
        }
    }

    private void BeginPress(Vector2 at)
    {
        _touchOrigin = at;
        _touchHeld = true;
        _ = FireLongPressAsync(++_touchToken);
    }

    private void MoveTo(Vector2 at)
    {
        // 手指一动就说明不是长按：拖牌/拖箭头不能被当成取消。
        if (_touchHeld && at.DistanceTo(_touchOrigin) > TouchSlopPixels) CancelLongPress();
    }

    private void CancelLongPress()
    {
        _touchHeld = false;
        _touchToken++;
    }

    private async System.Threading.Tasks.Task FireLongPressAsync(int token)
    {
        await ToSignal(GetTree().CreateTimer(LongPressSeconds), SceneTreeTimer.SignalName.Timeout);
        if (!_touchHeld || token != _touchToken || !IsInsideTree()) return;
        _touchHeld = false;

        // 合成右键而不是直接调取消方法：让"手势中 / 选靶中 / 选牌面板中"三条既有分支
        // 按原样各自处理，不在触屏路径上重写一遍判断。
        var at = _touchOrigin;
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Right, Pressed = true,
        }, true);
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Right, Pressed = false,
        }, true);
    }
}
