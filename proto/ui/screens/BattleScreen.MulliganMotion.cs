using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    private Tween? _mulliganDepartureMotion, _mulliganArrivalMotion;
    private readonly List<BattleCard> _mulliganArrivalPapers = new();
    private readonly HashSet<string> _mulliganKept = new(), _mulliganIncoming = new();
    private int _mulliganReplaceCount;
    private string? _mulliganMotionMatch;
    private Task _mulliganArrivalTask = Task.CompletedTask;
    private TaskCompletionSource? _mulliganArrivalCompletion;
    private bool _mulliganArrivalWaiting;
    public bool MulliganMotionActive => _mulliganDepartureMotion is not null || _mulliganArrivalWaiting || _mulliganArrivalMotion is not null;

    private void BeginMulliganDeparture(ChooseMulligan command)
    {
        CancelMulliganMotion();
        _mulliganMotionMatch = _state.MatchId;
        _mulliganKept.UnionWith(command.KeepUids);
        _mulliganReplaceCount = _mulliganMarks.Count - command.KeepUids.Count;
        _mulliganConfirm.Visible = _mulliganCaption.Visible = _mulliganNote.Visible = _mulliganInstructionPlate.Visible = false;
        var cells = _mulliganRow.GetChildren().OfType<Control>().ToArray();
        var motion = CreateTween(); _mulliganDepartureMotion = motion;
        var count = cells.Length;
        var index = 0;
        foreach (var cell in cells)
        {
            var card = cell.GetChildren().OfType<BattleCard>().First();
            if (_mulliganKept.Contains(card.Uid)) continue;
            cell.PivotOffset = cell.Size / 2;
            var delay = index++ * .055;
            if (_clock.ReducedMotion)
                motion.Parallel().TweenProperty(cell, "modulate:a", 0f, .14 * _clock.Scale).SetDelay(delay * _clock.Scale);
            else
                PreludePaperFlight(motion, cell, card, new Vector2(1360, -130) - _mulliganRow.Position - cell.PivotOffset,
                    Vector2.One * .8f, .52f, .44, delay, lift: 50, turn: -.07f, retire: true, accelerate: true);
        }
        // The kept cards stay in place while rejected cards leave with their red stamps.
        if (index == 0) motion.TweenInterval(.04 * _clock.Scale);
        motion.TweenInterval(.04 * _clock.Scale);
        index = 0;
        foreach (var cell in cells)
        {
            var card = cell.GetChildren().OfType<BattleCard>().First();
            if (!_mulliganKept.Contains(card.Uid)) continue;
            var slot = card.View!.SlotIndex;
            var target = HandPose(slot, count, true);
            cell.PivotOffset = cell.Size / 2;
            var destination = target.Position + new Vector2(72, 101) - _mulliganRow.Position - cell.PivotOffset;
            var first = index++ == 0;
            if (_clock.ReducedMotion)
            {
                cell.Position = destination; cell.Scale = new(144f / 198, 202f / 277);
                (first ? motion : motion.Parallel()).TweenProperty(cell, "modulate:a", 0f, .10 * _clock.Scale);
            }
            else PreludePaperFlight(motion, cell, card, destination, new(144f / 198, 202f / 277), target.Rotation,
                .38, (index - 1) * .018, parallel: !first, lift: 24, turn: -.035f);
        }
        PresentationPhase?.Invoke("mulligan-selection-return");
        motion.Finished += () =>
        {
            if (_mulliganDepartureMotion != motion) return;
            _mulliganDepartureMotion = null;
            _mulligan.Visible = false; UiStyles.Clear(_mulliganRow); _mulliganMarks.Clear();
            _mulliganArrivalWaiting = _mulliganReplaceCount > 0;
            _mulliganWaitingPhase = true; _mulliganWaiting.Visible = true;
            foreach (var card in _cards.Values.Where(c => c.Mode == BattleCardMode.Hand)) card.Visible = _mulliganKept.Contains(card.Uid);
            PresentationPhase?.Invoke("mulligan-selection-settled");
            CommandRequested?.Invoke(command);
        };
    }

    // Silent replacement has no ordinary draw event. Consume only the returned authoritative hand.
    private void StartMulliganArrival(UiMatchView view)
    {
        if (!_mulliganArrivalWaiting || _mulliganMotionMatch != view.MatchId) return;
        var incoming = view.SelfHand.Where(c => !_mulliganKept.Contains(c.Uid)).OrderBy(c => c.SlotIndex).Take(_mulliganReplaceCount).ToArray();
        if (incoming.Length < _mulliganReplaceCount || view.Phase != "play") return;
        _mulliganArrivalWaiting = false;
        _mulliganArrivalCompletion = new(); _mulliganArrivalTask = _mulliganArrivalCompletion.Task;
        var motion = CreateTween(); _mulliganArrivalMotion = motion;
        for (var i = 0; i < incoming.Length; i++)
        {
            var face = incoming[i]; _mulliganIncoming.Add(face.Uid);
            var target = HandPose(face.SlotIndex, view.SelfHand.Count, true);
            var actual = _cards.GetValueOrDefault(face.Uid) ?? AddCard(face, BattleCardMode.Hand, new(144, 202), target.Position, target.Rotation);
            actual.UpdateView(face); actual.Position = actual.RestPosition = target.Position;
            actual.Rotation = actual.RestRotation = target.Rotation; actual.Visible = false;
            var paper = new BattleCard { Size = new(144, 202), Position = _clock.ReducedMotion ? target.Position : new Vector2(1210, 690) - new Vector2(72, 101), PivotOffset = new(72, 101), Rotation = .16f, ZIndex = 255, MouseFilter = MouseFilterEnum.Ignore };
            paper.Bind(_clock.ReducedMotion ? face : null, BattleCardMode.Hand, _textures);
            _canvas.AddChild(paper); _mulliganArrivalPapers.Add(paper);
            if (_clock.ReducedMotion)
                (i == 0 ? motion : motion.Parallel()).TweenProperty(paper, "rotation", target.Rotation, .12 * _clock.Scale);
            else PreludePaperFlight(motion, paper, paper, target.Position, Vector2.One, target.Rotation,
                .52, i * .075, parallel: i != 0, reveal: true, definition: face, lift: 105, turn: -.09f, fadeIn: true);
        }
        HideMulliganArrivals();
        PresentationPhase?.Invoke("mulligan-replacements-arriving");
        motion.Finished += () =>
        {
            if (_mulliganArrivalMotion != motion) return;
            _mulliganArrivalMotion = null;
            var completion = _mulliganArrivalCompletion; _mulliganArrivalCompletion = null;
            ClearMulliganArrivalPapers();
            PresentationPhase?.Invoke("mulligan-replacements-settled");
            completion?.TrySetResult(); RefreshTurnInput();
        };
    }

    private void HideMulliganArrivals()
    {
        if (_mulliganDepartureMotion is not null) SetMulliganHandHidden(true);
        foreach (var uid in _mulliganIncoming)
            if (_cards.TryGetValue(uid, out var card)) card.Visible = false;
        if (_mulliganArrivalWaiting)
            foreach (var card in _cards.Values.Where(c => c.Mode == BattleCardMode.Hand && !_mulliganKept.Contains(c.Uid))) card.Visible = false;
    }
    private void ClearMulliganArrivalPapers()
    {
        foreach (var paper in _mulliganArrivalPapers)
            if (GodotObject.IsInstanceValid(paper)) { paper.GetParent()?.RemoveChild(paper); paper.QueueFree(); }
        _mulliganArrivalPapers.Clear();
        foreach (var uid in _mulliganIncoming)
            if (_cards.TryGetValue(uid, out var card)) card.Visible = true;
        _mulliganIncoming.Clear();
        _mulliganArrivalCompletion?.TrySetResult(); _mulliganArrivalCompletion = null;
    }
    private void CancelMulliganMotion()
    {
        if (MulliganMotionActive) SetMulliganHandHidden(false);
        if (_mulliganDepartureMotion is not null)
        {
            _mulligan.Visible = false; UiStyles.Clear(_mulliganRow); _mulliganMarks.Clear(); SetMulliganHandHidden(false);
        }
        _mulliganDepartureMotion?.Kill(); _mulliganDepartureMotion = null;
        _mulliganArrivalMotion?.Kill(); _mulliganArrivalMotion = null;
        _mulliganArrivalWaiting = false; _mulliganMotionMatch = null; _mulliganKept.Clear();
        ClearMulliganArrivalPapers();
    }
}
