using Godot;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

/// <summary>Menu-only compiler status. Reasons stay beside the effect being edited.</summary>
internal sealed partial class DiyCapabilityBadge : VBoxContainer
{
    internal readonly Button Badge;
    private readonly Label _reason;
    internal DiyCapabilityBadge(Container? detailsHost=null)
    {
        _reason=MenuStyles.Label("",12,new("dca58b"));
        Badge=MenuStyles.Button("检查中…",()=>{if(_reason.Text.Length>0)_reason.Visible=!_reason.Visible;});AddChild(Badge);
        _reason.AutowrapMode=TextServer.AutowrapMode.WordSmart;_reason.SizeFlagsHorizontal=SizeFlags.ExpandFill;_reason.Hide();
        (detailsHost??this).AddChild(_reason);
    }
    internal void Set(DiyCapabilityRow row)
    {
        Badge.Text=row.State==DiyPlayability.Playable?"可对局":"仅草稿";
        Badge.AddThemeColorOverride("font_color",row.State==DiyPlayability.Playable?MenuStyles.Gold:new("dca58b"));
        _reason.Text=string.Join("\n",row.Reasons);Badge.TooltipText=_reason.Text;Badge.Disabled=false;
        if(row.Reasons.Count==0)_reason.Hide();
    }
}
