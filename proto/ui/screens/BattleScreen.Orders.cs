using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    private readonly Dictionary<string, DeploymentPose> _orderOrigins = new();

    private UiOrderTargeting? OrderTargeting(string uid) => InteractionActions.OrderTargets.FirstOrDefault(t => t.Uid == uid);
    private bool IsTargetedOrder(string uid) => OrderTargeting(uid) is not null;
    private bool HasPendingHandPlay(string uid) => _deploymentOrigins.ContainsKey(uid) || _orderOrigins.ContainsKey(uid);

    private BattleCard? OrderTargetAt(string uid, Vector2 at)
    {
        var targeting = OrderTargeting(uid);
        return targeting is null ? null : _cards.Values.FirstOrDefault(c => c.View?.Visibility == Visibility.Full
            && c.View.Zone != "hand" && targeting.TargetUids.Contains(c.Uid) && new Rect2(c.Position, c.Size).HasPoint(at));
    }

    private void UpdateOrderAim(BattleCard source, Vector2 at)
    {
        var target = OrderTargetAt(source.Uid, at);
        foreach (var card in _cards.Values) { card.Targeted = card == target; card.QueueRedraw(); }
        _aim.From = source.Position + source.Size / 2;
        _aim.To = target is null ? at : target.Position + target.Size / 2;
        _aim.Moving = false; _aim.InvalidTarget = target is null; _aim.Preview = null;
        _aim.Visible = true; _aim.QueueRedraw();
    }

    private void RememberOrderPose(string uid, DeploymentPose? pose)
    {
        if (pose is not { } released) return;
        _orderOrigins[uid] = released;
        RestorePendingDeploymentPoses();
    }
}
