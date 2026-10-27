using Godot;
using Kards.Ui.Contracts;
using Kards.Ui.OrcBridge;

namespace Kards.Ui;

public partial class BattleScreen
{
    public async Task VerifyDiscardAnimationAsync(NativeStateFeedback fixture, Func<string,Task>? capture = null)
    {
        var speed = _clock.Current; var quiet = _clock.ReducedMotion; var checks = 0;
        try
        {
            _clock.SetSpeed(AnimClock.Speed.Normal);
            foreach (var reduced in new[] { false, true })
            {
                _clock.SetReducedMotion(reduced); ApplyProjection(fixture.Initial, new() { CanEndTurn = true });
                foreach (var resolution in fixture.Resolutions)
                {
                    var discard = resolution.Steps.OfType<UiDiscardPresentation>().Single();
                    var marked = false; var retired = false; var valid = false; var photographed = false;
                    void Phase(string phase)
                    {
                        if (phase == "discard-marked")
                        {
                            marked = true;
                            var paper = _sequence.GetChildren().OfType<BattleCard>().Single();
                            valid = paper.Position.Y >= 0 && paper.Position.Y + paper.Size.Y <= BoardSize.Y
                                && (discard.Side == "self" ? paper.View?.Uid == discard.Card?.Uid
                                    : paper.Mode == BattleCardMode.Hidden && paper.View is null);
                            if (discard.Card is { } card && _cards.TryGetValue(card.Uid,out var source)) valid &= !source.Visible;
                        }
                        if (phase == "discard-exit") retired = true;
                    }
                    _sequence.PhaseChanged += Phase;
                    try
                    {
                        var playback = PresentSequenceAsync(resolution,new() { CanEndTurn = true });
                        while (!playback.IsCompleted)
                        {
                            if (marked && !photographed && !reduced && capture is not null)
                            { photographed = true; await capture("discard-"+discard.Side); }
                            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                        }
                        await playback;
                    }
                    finally { _sequence.PhaseChanged -= Phase; }
                    if (!marked || !retired || !valid || _sequence.LiveCardCount != 0 || !MatchesProjection(resolution.After))
                        throw new Exception($"Discard animation failed side={discard.Side} reduced={reduced} marked={marked} retired={retired} visible={valid}.");
                    checks++;
                }
            }
            // P6：真弃牌必须进弃牌堆页签；我方带牌面，对手那张只能是"1 张牌"（引擎本来就没给）。
            if (LogDiscardCount("self") == 0) throw new Exception("真实弃牌没有进我方弃牌堆。");
            if (LogDiscardCount("enemy") == 0) throw new Exception("真实弃牌没有进对手弃牌堆。");
            if (!_log.Discards("self").Any(d => d.CardId is { Length: > 0 })) throw new Exception("我方弃牌没带牌面。");
            if (_log.Discards("enemy").Any(d => d.CardId is { Length: > 0 })) throw new Exception("对手弃牌带了牌面（泄漏牌面）。");
            GD.Print($"DISCARD_ANIMATION_VERIFY_OK checks={checks} native-signal own-face enemy-back hand-origin visible-lift stamp retire normal-reduced cleanup discard-pile");
        }
        finally { ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); }
    }
}
