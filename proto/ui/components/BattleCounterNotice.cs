using Godot;

namespace Kards.Ui;

public partial class BattleCounterNotice : Control
{
    public string Caption { get; init; } = "";
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; Modulate = new(1, 1, 1, 0); }
    public override void _Draw()
    {
        DrawRect(new Rect2(160, 345, 960, 28), new Color(0, 0, 0, .72f));
        var font = GetThemeDefaultFont(); var width = font.GetStringSize(Caption, fontSize: 17).X;
        DrawString(font, new(640 - width / 2, 365), Caption, fontSize: 17, modulate: new("eee5cb"));
    }
}
