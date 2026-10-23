using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class Main
{
    private int _navigationRequest;
    private bool _replacingSession;
    private bool HasBattleSession => _feed is not null;
    private string ResumeCaption => _replayFeed is not null ? "继续回放 →"
        : _feed is RemoteMatchFeed { IsObserver: true } ? "返回观战 →" : "返回战场 →";

    private void ResumeSession() { if (HasBattleSession) Show("battle"); }
    private async Task LeaveSessionAsync()
    {
        StopRealMatch(); await _room.CloseAsync(); Show("setup");
    }

    // Hiding a room is not leaving it. Only navigation away from the lobby tears it down;
    // entering the battlefield retains the same connection and hosted server.
    private async void NavigateFromRoom(string id)
    {
        var request = ++_navigationRequest;
        if (_active == "room" && id is not ("room" or "battle"))
        {
            if (_feed == _room.Feed && _feed is not null) StopRealMatch();
            await _room.CloseAsync();
            if (request != _navigationRequest || !IsInsideTree()) return;
        }
        ShowCore(id);
    }

    private void ReplaceSession(Action next)
    {
        if (_replacingSession) return;
        if (!HasBattleSession) { _ = ReplaceSessionAsync(next); return; }
        var dialog = new ConfirmationDialog { Title = "离开当前作战？", DialogText = "开始新的作战会结束当前对局或回放。已记录的回放仍会保留。", OkButtonText = "离开并继续", CancelButtonText = "保留当前作战" };
        AddChild(dialog);
        _replacingSession = true;
        dialog.Canceled += () => { _replacingSession = false; dialog.QueueFree(); };
        dialog.Confirmed += () => { dialog.QueueFree(); _replacingSession = false; _ = ReplaceSessionAsync(next); };
        dialog.PopupCentered();
    }

    private async Task ReplaceSessionAsync(Action next)
    {
        if (_replacingSession) return;
        _replacingSession = true;
        try
        {
            StopRealMatch();
            if (_room is not null) await _room.CloseAsync();
            if (IsInsideTree()) next();
        }
        finally { _replacingSession = false; UpdateMenuNavigation(); }
    }
}
