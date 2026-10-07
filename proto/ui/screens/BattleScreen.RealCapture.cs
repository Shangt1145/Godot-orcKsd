using System;
using System.Threading.Tasks;
using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

/// <summary>
/// Real-machine audit driver. Every action below is pushed through the same pipeline a player's mouse
/// uses — the pointer moves, the button goes down on the card, the pointer drags, the button comes up
/// on the drop point — so a capture run *shows* what the UI does instead of asserting what it should
/// do. Nothing here is part of the shipped interaction; it exists so the interaction can be measured.
///
/// Coordinate spaces, measured rather than assumed (project.godot uses a 1600×900 base viewport with
/// stretch mode "canvas_items", and the board Control is scaled by Size/1280 = 1.25):
///   board/canvas  the space <see cref="BattleScreen.TryDrop"/> reasons in — 1280×720
///   viewport      board × the canvas control transform — what the GUI hit-test uses
///   window/OS     viewport × the viewport's final transform — what a real mouse reports
/// Coordinates here are board space; the two conversions below produce the other two. Getting this
/// wrong is invisible: the events are accepted and simply land somewhere else.
/// </summary>
public partial class BattleScreen
{
    /// <summary>Presentation activity; real-match input remains available from the latest engine view.</summary>
    public bool IsBusy => _busy;
    public bool EndTurnEnabled => !_endTurn.Disabled;
    public Task ClickEndTurnAsync() => ClickAsync(_endTurn.Position + _endTurn.Size / 2);

    public bool HasCard(string uid) => _cards.ContainsKey(uid);

    public UiCardView? ViewOf(string uid) => _cards.TryGetValue(uid, out var card) ? card.View : null;

    /// <summary>What the interaction layer currently holds, for the audit log to quote.</summary>
    public string Interaction
        => $"sel={Trim(_selected)} pressed={Trim(_pressed?.View?.Uid)} dragging={_dragging} busy={_busy}";

    /// <summary>Per-step channel trace of the last gesture: v = viewport, o = OS, x = refused.</summary>
    public string ChannelTrace { get; private set; } = "";

    /// <summary>
    /// Every transform involved in turning a pointer position into a board position, so a coordinate
    /// question is answered by measurement instead of by reading Godot's source from memory.
    /// </summary>
    public string CoordinateReport()
    {
        var window = GetWindow();
        var viewport = GetViewport();
        return $"window={window.Size} base={window.ContentScaleSize}/{window.ContentScaleMode} " +
               $"visible={viewport.GetVisibleRect()} final={viewport.GetFinalTransform()} " +
               $"control={_canvas.GetGlobalTransformWithCanvas()} board={_canvas.Size}";
    }

    /// <summary>
    /// Where a player's mouse would actually grab a card, in board space. Hand cards sit partly below
    /// the board, so the press goes on the upper part of the card that is on screen — the same place a
    /// player can reach.
    /// </summary>
    public Vector2 GrabPoint(string uid)
    {
        if (!_cards.TryGetValue(uid, out var card)) return new Vector2(-1, -1);
        var local = new Vector2(card.Size.X / 2, Mathf.Min(card.Size.Y / 2, 40));
        return card.Position + local;
    }

    /// <summary>Board-space centre of a card.</summary>
    public Vector2 CardPoint(string uid)
        => _cards.TryGetValue(uid, out var card) ? card.Position + card.Size / 2 : new Vector2(-1, -1);

    /// <summary>A drop at the far end of a line's band — the gesture "put it at the very left/right".</summary>
    public Vector2 DropAtRowEdge(bool front, bool right)
    {
        var area = front ? FrontBand : SelfBand;
        return new Vector2(right ? area.Position.X + area.Size.X - 8 : area.Position.X + 8,
            area.Position.Y + area.Size.Y / 2f);
    }

    /// <summary>A drop just past a named card's right edge — the gesture "put it next to this one".</summary>
    public Vector2 DropRightOf(string uid)
        => _cards.TryGetValue(uid, out var card)
            ? new Vector2(card.RestPosition.X + card.Size.X + 8, card.RestPosition.Y + card.Size.Y / 2f)
            : new Vector2(-1, -1);

    /// <summary>A drop on the middle of a line's band — the gesture "put it in the centre".</summary>
    public Vector2 DropAtRowMiddle(bool front)
    {
        var area = front ? FrontBand : SelfBand;
        return new Vector2(BoardSize.X / 2f, area.Position.Y + area.Size.Y / 2f);
    }

    /// <summary>The player's own headquarters card, as the anchor for "right of my HQ".</summary>
    public string SelfHqUid() => _cards.Values
        .FirstOrDefault(c => c.View is { IsHq: true, OwnerSide: "self" })?.View?.Uid ?? "";

    /// <summary>The player's own front-line card, as the anchor for "right of my front unit".</summary>
    public string SelfFrontUid() => _cards.Values
        .FirstOrDefault(c => c.View is { OwnerSide: "self", Zone: "frontline" })?.View?.Uid ?? "";

    /// <summary>Which drop zone a board point falls in — the drop is ignored unless one of these hits.</summary>
    public string AreaHit(Vector2 boardPoint)
        => $"inSelf={SelfBand.HasPoint(boardPoint)} inFront={FrontBand.HasPoint(boardPoint)} " +
           $"selfX={SelfBand.Position.X:0}..{SelfBand.Position.X + SelfBand.Size.X:0} " +
           $"frontY={FrontBand.Position.Y:0}..{FrontBand.Position.Y + FrontBand.Size.Y:0}";

    /// <summary>What a line currently holds, in drawing order, for the audit log.</summary>
    public string RowLayout(bool front)
    {
        var cards = (front
                ? _cards.Values.Where(c => c.View?.Zone == "frontline")
                : _cards.Values.Where(c => c.View?.OwnerSide == "self" && (c.View.IsHq || c.View.Zone == "support")))
            .Where(c => c.Mode != BattleCardMode.Hand)
            .OrderBy(c => c.RestPosition.X).ToArray();
        return string.Join(' ', cards.Select(c =>
            $"{c.View!.OwnerSide}:{c.View.Definition.Name}[{c.View.SlotIndex}]@{c.RestPosition.X:0}"));
    }

    /// <summary>
    /// The slot numbers of a drawn line, in the order the cards are painted from left to right.
    ///
    /// A packed row — no gaps, one card per occupied slot — only reads as the engine's line if this
    /// sequence ascends: the order the cards are painted in has to agree with the order the engine holds
    /// them in. When it does not, a unit placed to the right of the headquarters is painted to its left.
    /// Every published slot index is checked against the engine's view, so a wrong number is caught too.
    /// </summary>
    public int[] RowSlotOrder(bool front) => (front
            ? _cards.Values.Where(c => c.View?.Zone == "frontline")
            : _cards.Values.Where(c => c.View?.OwnerSide == "self" && (c.View.IsHq || c.View.Zone == "support")))
        .Where(c => c.Mode != BattleCardMode.Hand)
        .OrderBy(c => c.RestPosition.X).Select(c => c.View!.SlotIndex).ToArray();

    /// <summary>
    /// What a drop point resolves to: the line's capacity, the insertion rank the drop reads as, which
    /// slots are taken and which are free, and the slot finally chosen.
    ///
    /// The chosen slot on its own cannot explain a placement that looks wrong — "it always ends up on the
    /// right" is a claim about the rank-to-slot mapping, and that mapping has to be visible to be judged.
    /// </summary>
    public string DropTrace(bool front, float boardX)
    {
        var capacity = front ? _state.FrontLineSlotCount : _state.SupportLineSlotCount;
        var row = (front
                ? _cards.Values.Where(c => c.View?.Zone == "frontline")
                : _cards.Values.Where(c => c.View?.OwnerSide == "self" && (c.View.IsHq || c.View.Zone == "support")))
            .ToArray();
        var drawn = row.Where(c => c.Mode != BattleCardMode.Hand && c.View is not null)
            .OrderBy(c => c.RestPosition.X).ToArray();
        var taken = drawn.Select(c => c.View!.SlotIndex).OrderBy(s => s).ToArray();
        var free = Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).ToArray();
        // The front line is arranged by the player, so its drop answers with a position in the drawn row;
        // a support deployment answers with a slot, because the headquarters keeps its place there.
        var position = 0;
        var slot = front ? FrontDrop(boardX, capacity, row, out position) : DropSlot(boardX, capacity, row);
        return front
            ? $"cap={capacity} x={boardX:0} pos={position}/{drawn.Length} taken=[{string.Join(',', taken)}] " +
              $"free=[{string.Join(',', free)}] -> slot={slot}"
            : $"cap={capacity} x={boardX:0} taken=[{string.Join(',', taken)}] " +
              $"free=[{string.Join(',', free)}] -> slot={slot}";
    }

    /// <summary>Board-space centre of the end-turn button.</summary>
    public Vector2 EndTurnPoint() => _endTurn.Position + _endTurn.Size / 2;

    /// <summary>A card's board-space rectangle, for logging where a drop landed relative to it.</summary>
    public Rect2 BoardRect(string uid)
        => _cards.TryGetValue(uid, out var card) ? new Rect2(card.Position, card.Size) : new Rect2();

    private static string Trim(string? uid)
        => uid is null ? "-" : uid[..Math.Min(6, uid.Length)];

    /// <summary>Simulates a full mouse drag: hover, press on the card, drag to the point, release there.</summary>
    public async Task DragAsync(string uid, Vector2 boardTarget, Func<Task>? onHeld = null)
    {
        ChannelTrace = "";
        var from = GrabPoint(uid);
        if (from.X < 0) { ChannelTrace = "nocards"; return; }
        await SendAsync(Motion(from, false), () => true);
        await SendAsync(Button(from, true), () => _pressed is not null);
        await SendAsync(Motion(boardTarget, true), () => _dragging);
        // Read the landing overlay while the card is still held: it is gone the moment it is released, and
        // it is the only thing that says which positions the gesture is offering.
        LastDragHints = SlotHintReport();
        if (onHeld is not null) await onHeld();
        await SendAsync(Button(boardTarget, false), () => _pressed is null);
    }

    /// <summary>What the landing-position overlay shows right now: the free slots and where they are drawn.</summary>
    public string SlotHintReport() => _slotHints.Count == 0 && _attackHints.Count == 0
        ? "none"
        : $"aim={_slotHintAim} slots=[{string.Join(',', _slotHints.Select(h => $"{h.Slot}@{h.X:0}"))}] " +
          $"targets=[{string.Join(',', _attackHints)}]";

    /// <summary>The overlay as it read during the last drag, captured before the card was released.</summary>
    public string LastDragHints { get; private set; } = "none";

    /// <summary>Simulates a plain click at a board-space point.</summary>
    public async Task ClickAsync(Vector2 boardPoint)
    {
        ChannelTrace = "";
        await SendAsync(Motion(boardPoint, false), () => true);
        await SendAsync(Button(boardPoint, true), () => true);
        await SendAsync(Button(boardPoint, false), () => true);
    }

    /// <summary>
    /// Sends one event into the viewport in local coordinates and reports whether the UI reacted. The
    /// viewport channel is tried first because <c>Input.ParseInputEvent</c> pushes the position back
    /// through the stretch transform on the way in, which silently doubles the board's 1.25 scale.
    /// The OS channel is kept as a fallback so a refusal is visible instead of looking like success.
    /// </summary>
    private async Task SendAsync(InputEvent e, Func<bool> accepted)
    {
        var board = e switch
        {
            InputEventMouseMotion motion => motion.Position,
            InputEventMouseButton button => button.Position,
            _ => Vector2.Zero,
        };
        Place(e, ToViewportPoint(board));
        GetViewport().PushInput(e, true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (accepted()) { ChannelTrace += "v"; return; }

        var osEvent = e switch
        {
            InputEventMouseMotion motion => Motion(ToWindowPoint(motion.Position), motion.ButtonMask != 0),
            InputEventMouseButton button => Button(ToWindowPoint(button.Position), button.Pressed),
            _ => e,
        };
        Input.ParseInputEvent(osEvent);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        ChannelTrace += accepted() ? "o" : "x";
    }

    private static void Place(InputEvent e, Vector2 at)
    {
        switch (e)
        {
            case InputEventMouseMotion motion: motion.Position = at; motion.GlobalPosition = at; break;
            case InputEventMouseButton button: button.Position = at; button.GlobalPosition = at; break;
        }
    }

    /// <summary>Board space → viewport space (the space the GUI hit-test runs in).</summary>
    private Vector2 ToViewportPoint(Vector2 boardPoint) => _canvas.GetGlobalTransformWithCanvas() * boardPoint;

    /// <summary>Viewport space → window space, which is what a physical mouse reports.</summary>
    private Vector2 ToWindowPoint(Vector2 viewportPoint)
    {
        var final = GetViewport().GetFinalTransform();
        return final * viewportPoint;
    }

    private static InputEventMouseMotion Motion(Vector2 at, bool dragging) => new()
    {
        Position = at,
        GlobalPosition = at,
        ButtonMask = dragging ? MouseButtonMask.Left : (MouseButtonMask)0,
    };

    private static InputEventMouseButton Button(Vector2 at, bool pressed) => new()
    {
        Position = at,
        GlobalPosition = at,
        ButtonIndex = MouseButton.Left,
        ButtonMask = pressed ? MouseButtonMask.Left : (MouseButtonMask)0,
        Pressed = pressed,
    };
}
