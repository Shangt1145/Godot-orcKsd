using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

internal partial class NativeEffectInspector
{
    private IReadOnlyList<DiyCapabilityRow> _capabilities=[];
    private readonly List<(int Index,DiyCapabilityBadge Badge)> _capabilityBadges=[];
    internal void SetCapabilities(IReadOnlyList<DiyCapabilityRow> rows)
    {
        _capabilities=rows;
        foreach(var (index,badge) in _capabilityBadges)
            if(GodotObject.IsInstanceValid(badge)&&rows.FirstOrDefault(r=>r.EffectIndex==index) is { } row)badge.Set(row);
    }
    private void CapabilityBadge(Container parent,int index,Container? detailsHost=null)
    {
        if(_playerAuthoring)return;
        var badge=new DiyCapabilityBadge(detailsHost);parent.AddChild(badge);_capabilityBadges.Add((index,badge));
        if(_capabilities.FirstOrDefault(r=>r.EffectIndex==index) is { } row)badge.Set(row);
    }
}
