using Godot;

namespace Kards.Ui;

/// <summary>Original layout: large available Kredits, small K, slot count separately under the rule.</summary>
public partial class KreditsDisplay : Control
{
    private static readonly Font Stencil = new SystemFont { FontNames = ["Impact", "Bahnschrift Condensed"], AllowSystemFallback = true };
    public int? Available { get; private set; }
    public int? Slots { get; private set; }
    public string PlayerName { get; private set; } = "";
    public bool Bottom { get; set; }
    public override void _Ready()
    {
        Material = new ShaderMaterial { Shader = new Shader { Code = """
            shader_type canvas_item;
            float grain(vec2 p) {return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}
            void fragment() {
                bool orange=COLOR.r>0.75 && COLOR.g<0.75 && COLOR.b<0.4;
                vec4 ink=texture(TEXTURE,UV)*COLOR;
                if(orange) ink.a*=1.0-step(0.94,grain(floor(FRAGCOORD.xy)))*step(0.55,grain(floor(FRAGCOORD.xy/3.0)))*0.85;
                COLOR=ink;
            }
            """ } };
    }
    public void Bind(int? available, int? slots, string playerName)
    {
        Available = available; Slots = slots; PlayerName = playerName;
        TooltipText = $"指挥点：{available?.ToString() ?? "未知"}\n指挥点槽：{slots?.ToString() ?? "未知"}";
        QueueRedraw();
    }
    public override void _Draw()
    {
        var text = Available?.ToString() ?? "?";
        var width = Stencil.GetStringSize(text, HorizontalAlignment.Left, -1, 60).X;
        DrawString(Stencil, new(3, 62), text, HorizontalAlignment.Left, -1, 60, new Color(0, 0, 0, .65f));
        DrawString(Stencil, new(1, 60), text, HorizontalAlignment.Left, -1, 60, new("e99e2b"));
        DrawString(Stencil, new(width + 2, 33), "K", HorizontalAlignment.Left, -1, 22, new("e99e2b"));
        DrawLine(new(-10, 73), new(34, 73), new("dedac4"), 3);
        DrawLine(new(-5, 75), new(8, 75), new("dedac4"), 1);
        var slots = Slots?.ToString() ?? "?";
        var slotsWidth = Stencil.GetStringSize(slots, HorizontalAlignment.Left, -1, 20).X;
        DrawString(Stencil, new(12 - slotsWidth / 2, 93), slots, HorizontalAlignment.Left, -1, 20, new("e9e4ce"));
        DrawString(GetThemeDefaultFont(), new(Math.Max(66, width + 27), Bottom ? 73 : 43), PlayerName, HorizontalAlignment.Left, 140, 16, new("e9e4ce"));
    }
}
