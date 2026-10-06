using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.Core;

namespace Kards.Ui;

/// <summary>
/// Stage five, part one: the opening-hand mulligan panel. Layout follows the frame-verified
/// original (2026-10-06 recording): a centered row of full cards over the dimmed board, the
/// caption "选择要替换的卡牌", a red ✕ stamp marking cards chosen for replacement, and a single
/// 确认 button. The engine owns the replacement draw and the confirm; the UI only reports which
/// cards to keep, so the panel never reads the deck or decides legality.
/// </summary>
public partial class BattleScreen
{
    private Control _mulligan = null!;
    private Control _mulliganRow = null!;
    private Control _mulliganWaiting = null!;
    private readonly Dictionary<string, MulliganStamp> _mulliganMarks = new();
    private bool _mulliganOpen, _mulliganWaitingPhase;

    /// <summary>True while the panel is up; used by the verify and capture hooks.</summary>
    public bool MulliganPanelVisible => _mulliganOpen;

    private void InitializeMulligan()
    {
        _mulligan = new Control { Size = BoardSize, ZIndex = 260, Visible = false, MouseFilter = MouseFilterEnum.Stop };
        _mulligan.AddChild(new ColorRect { Size = BoardSize, Color = new Color(.02f, .03f, .02f, .55f), MouseFilter = MouseFilterEnum.Stop });
        var caption = Text("选择要替换的卡牌", new(0, 512), new(BoardSize.X, 30), 17, "ece5ce");
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        _mulligan.AddChild(caption);
        _mulliganRow = new Control { Position = new(0, 118), Size = new(BoardSize.X, 380), MouseFilter = MouseFilterEnum.Ignore };
        _mulligan.AddChild(_mulliganRow);
        var confirm = new Button { Text = "确认", Position = new(BoardSize.X / 2f - 80, 556), Size = new(160, 38) };
        foreach (var style in new[] { "normal", "hover", "pressed", "focus" })
            confirm.AddThemeStyleboxOverride(style, _endTurn.GetThemeStylebox(style));
        confirm.AddThemeColorOverride("font_color", new("e9e3cc"));
        confirm.Pressed += ConfirmMulligan;
        _mulligan.AddChild(confirm);
        // Waiting band, shown while the engine replaces the draw and the other side answers.
        var band = new ColorRect { Color = new Color(.07f, .08f, .06f, .88f), Position = new(0, 322), Size = new(BoardSize.X, 54), MouseFilter = MouseFilterEnum.Ignore };
        var waiting = Text("敌方正在选择起手牌", new(0, 336), new(BoardSize.X, 30), 19, "e8dec1");
        waiting.HorizontalAlignment = HorizontalAlignment.Center;
        _mulliganWaiting = new Control { Size = BoardSize, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _mulliganWaiting.AddChild(band);
        _mulliganWaiting.AddChild(waiting);
        _mulligan.AddChild(_mulliganWaiting);
        _canvas.AddChild(_mulligan);
    }

    /// <summary>Opens the panel with the viewer's opening hand. Called when the engine parks a mulligan request.</summary>
    public void ShowMulliganPanel(UiMatchView? view)
    {
        if (view is null || view.Phase != "mulligan" || view.SelfHand.Count == 0 || _mulliganOpen) return;
        _mulliganOpen = true;
        _mulliganWaiting.Visible = false;
        _mulliganWaitingPhase = false;
        UiStyles.Clear(_mulliganRow);
        _mulliganMarks.Clear();
        var hand = view.SelfHand.OrderBy(c => c.SlotIndex).ToArray();
        var cardSize = new Vector2(198, 277);
        var gap = 24;
        var x0 = (BoardSize.X - (hand.Length * cardSize.X + (hand.Length - 1) * gap)) / 2f;
        for (var i = 0; i < hand.Length; i++)
        {
            var uid = hand[i].Uid;
            var cell = new Control { Position = new(x0 + i * (cardSize.X + gap), (380 - cardSize.Y) / 2f), Size = cardSize };
            var card = new BattleCard { Size = cardSize, PivotOffset = cardSize / 2 };
            card.Bind(hand[i], BattleCardMode.Inspect, _textures);
            var stamp = new MulliganStamp { Size = cardSize, Visible = false, MouseFilter = MouseFilterEnum.Ignore, Rotation = -.10f };
            stamp.PivotOffset = cardSize / 2;
            card.Pressed += (_, _) => ToggleMulliganMark(uid, stamp);
            cell.AddChild(card);
            cell.AddChild(stamp);
            _mulliganRow.AddChild(cell);
            _mulliganMarks[uid] = stamp;
        }
        _mulligan.Visible = true;
    }

    private void ToggleMulliganMark(string uid, MulliganStamp stamp)
    {
        stamp.SetMarked(!stamp.Marked);
    }

    /// <summary>Programmatic mark for verify and capture hooks; mirrors a click on the card.</summary>
    public void MarkMulliganReplacement(string uid)
    {
        if (_mulliganMarks.TryGetValue(uid, out var stamp)) stamp.SetMarked(true);
    }

    /// <summary>Submits the keep list (everything without a ✕ stamp) and shows the waiting band.</summary>
    public void ConfirmMulligan()
    {
        if (!_mulliganOpen) return;
        var keep = _mulliganMarks.Where(kv => !kv.Value.Marked).Select(kv => kv.Key).ToArray();
        _mulliganOpen = false;
        _mulligan.Visible = false;
        UiStyles.Clear(_mulliganRow);
        _mulliganMarks.Clear();
        _mulliganWaitingPhase = true;
        _mulliganWaiting.Visible = true;
        CommandRequested?.Invoke(new ChooseMulligan(keep));
    }

    /// <summary>Reconciles the panel with projections: the waiting band clears when play begins.</summary>
    private void MulliganProjection(string phase)
    {
        if (_mulliganWaitingPhase && phase == "play")
        {
            _mulliganWaitingPhase = false;
            _mulliganWaiting.Visible = false;
        }
        if (_mulliganOpen && phase != "mulligan")
        {
            // A stale panel (match moved on without an answer): close instead of blocking the board.
            _mulliganOpen = false;
            _mulligan.Visible = false;
            UiStyles.Clear(_mulliganRow);
            _mulliganMarks.Clear();
        }
    }
}

/// <summary>The red rejection stamp the original overlays on a card chosen for replacement.</summary>
public partial class MulliganStamp : Control
{
    public bool Marked { get; private set; }

    public void SetMarked(bool value)
    {
        Marked = value;
        Visible = value;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!Marked) return;
        var center = Size / 2f;
        var radius = MathF.Min(Size.X, Size.Y) * .27f;
        var ink = new Color("c8402e") with { A = .94f };
        DrawArc(center, radius, 0, MathF.Tau, 48, ink, 6, antialiased: true);
        var arm = radius * .58f;
        DrawLine(center + new Vector2(-arm, -arm), center + new Vector2(arm, arm), ink, 9);
        DrawLine(center + new Vector2(-arm, arm), center + new Vector2(arm, -arm), ink, 9);
    }
}
