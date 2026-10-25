using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class GameConsole
{
    internal async Task VerifyGraphicalDebugAsync()
    {
        async Task Frame(){await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
        async Task Click(Control control)
        {
            for(var node=control.GetParent();node is not null;node=node.GetParent())
                if(node is ScrollContainer scroll)scroll.EnsureControlVisible(control);
            await Frame();await Frame();
            var at=control.GetGlobalRect().GetCenter();
            GetViewport().PushInput(new InputEventMouseMotion{Position=at,GlobalPosition=at},true);
            GetViewport().PushInput(new InputEventMouseButton{Position=at,GlobalPosition=at,ButtonIndex=MouseButton.Left,Pressed=true},true);
            await Frame();
            GetViewport().PushInput(new InputEventMouseButton{Position=at,GlobalPosition=at,ButtonIndex=MouseButton.Left,Pressed=false},true);
            var deadline=Time.GetTicksMsec()+8000;
            while(_debugBusy&&Time.GetTicksMsec()<deadline)await Frame();
            await Frame();RefreshDebug();
        }
        await Click(_toggle);if(!_open)throw new Exception("Console toggle did not open through mouse input.");
        var start=ReadDebugState?.Invoke()??throw new Exception("No local debug state.");
        await Click(_enableDebug);if(ReadDebugState?.Invoke()?.Enabled!=true)throw new Exception("Enable debug button failed.");
        _debugAmount.Value=2;_debugSide.Select(1);
        await Click(_mutationButtons.Single(b=>b.Text=="抽牌"));
        if(ReadDebugState!()!.View.EnemyHandCount!=start.View.EnemyHandCount+2)throw new Exception("Graphical draw affected the wrong player.");
        RefreshDebug();var hq=_targetIds.IndexOf(start.View.EnemyHq!.Uid);_debugTargets.Select(hq);RefreshDebug();
        await Click(_mutationButtons.Single(b=>b.Text=="造成伤害"));
        if(ReadDebugState!()!.View.EnemyHq!.Health!=18)throw new Exception("Graphical damage did not reach selected HQ.");
        CopySnapshot();if(!DisplayServer.ClipboardGet().Contains("PendingSelector"))throw new Exception("Diagnostic snapshot was not copied.");
        // Logged text must remain literal even when it contains inspector/target JSON or BBCode.
        Kards.Ui.Core.GameLog.Info("debug-verify","[color=red]literal[/color] target=selected");
        await Frame();await Frame();
        _logSearch.Text="debug-verify";Redraw();
        if(!_log.GetParsedText().Contains("[color=red]literal[/color]"))throw new Exception("Log markup was interpreted.");
        _logSearch.Text="";Redraw();
        var rect=_panel.GetGlobalRect();var viewport=GetViewport().GetVisibleRect();
        if(!viewport.Encloses(rect))throw new Exception("Debug panel extends outside viewport.");
        await Click(_toggle);if(_open)throw new Exception("Console toggle did not close.");
        GD.Print("GRAPHICAL_DEBUG_VERIFY_OK draw=enemy+2 damage=selected-hq snapshot=clipboard logs=literal");
    }

    internal async Task CaptureDebugAsync(string path)
    {
        if(!_open)Toggle();RefreshDebug();
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(path);
        Toggle();
    }
}
