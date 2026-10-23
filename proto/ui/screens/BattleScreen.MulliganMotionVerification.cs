using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class BattleScreen
{
    public async Task VerifyMulliganMotionAsync(string folder)
    {
        void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
        async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        async Task Capture(string name)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, name + ".png"));
        }
        var speed = _clock.Current; var quiet = _clock.ReducedMotion;
        var sent = new List<ChooseMulligan>(); var phases = new List<string>();
        void Sent(UiCommand command) { if (command is ChooseMulligan choice) sent.Add(choice); }
        void Phase(string phase) => phases.Add(phase);
        CommandRequested += Sent; PresentationPhase += Phase;
        try
        {
            _clock.SetSpeed(AnimClock.Speed.Normal); _clock.SetReducedMotion(false);
            foreach (var (replace, reduced) in new[] { (0, false), (1, false), (5, false), (1, true) })
            {
                ResetDemo(); sent.Clear(); phases.Clear(); _clock.SetReducedMotion(reduced);
                var before = _state with { MatchId = "mulligan-motion-" + replace + reduced, Phase = "mulligan", Turn = 0 };
                ApplyProjection(before, new()); ShowMulliganPanel(before); FinishOpening();
                var chosen = before.SelfHand.Take(replace).Select(c => c.Uid).ToHashSet();
                foreach (var uid in chosen) MarkMulliganReplacement(uid);
                var keptCell = _mulliganRow.GetChildren().OfType<Control>().FirstOrDefault(c => !chosen.Contains(c.GetChildren().OfType<BattleCard>().First().Uid));
                var keptPosition = keptCell?.Position;
                ConfirmMulligan(); ConfirmMulligan();
                var frozenMark = _mulliganMarks.First(); var wasMarked = frozenMark.Value.Marked;
                ToggleMulliganMark(frozenMark.Key, frozenMark.Value);
                Check(frozenMark.Value.Marked == wasMarked, "确认后仍可修改换牌标记");
                Check(!_mulliganOpen && _mulligan.Visible && sent.Count == 0, "确认后直接清屏或提前提交");
                if (replace == 1 && !reduced)
                {
                    await Wait(.15);
                    Check(keptCell!.Position == keptPosition && _mulliganMarks.Values.Any(s => s.Visible), "退牌时保留牌提前移动或红叉丢失");
                    await Capture("mulligan-rejected-leaving");
                }
                await Wait(1.1);
                Check(sent.Count == 1 && sent[0].KeepUids.Count == before.SelfHand.Count - replace, "换牌重复提交或保留列表错误");
                var after = before with { Phase = "play", Turn = 1, SelfHand = before.SelfHand.Select(c => chosen.Contains(c.Uid) ? c with { Uid = c.Uid + "-replacement" } : c).ToArray() };
                ApplyProjection(after, new());
                var turn = PresentSequenceAsync(new(after.MatchId, [new UiTurnPresentation("self", 1)], after), new());
                if (replace > 0)
                {
                    Check(_mulliganArrivalPapers.Count == replace && !phases.Contains("turn-banner"), "补牌未演出或回合横幅抢先");
                    await Wait(.14); if (!reduced) await Capture("mulligan-arrival-" + replace);
                    await _mulliganArrivalTask;
                    Check(after.SelfHand.Where(c => !sent[0].KeepUids.Contains(c.Uid)).All(c => _cards.TryGetValue(c.Uid, out var card) && card.Visible), "补牌交接后在首回合横幅期间消失");
                }
                await turn;
                Check(!MulliganMotionActive && _mulliganArrivalPapers.Count == 0 && _mulliganIncoming.Count == 0, "换牌残留节点或锁");
                if (replace > 0) Check(phases.IndexOf("mulligan-replacements-settled") < phases.IndexOf("turn-banner"), "首回合没有等待补牌");
            }
            _clock.SetReducedMotion(false); ResetDemo(); sent.Clear();
            var cancel = _state with { MatchId = "mulligan-cancel", Phase = "mulligan" };
            ApplyProjection(cancel, new()); ShowMulliganPanel(cancel); FinishOpening(); ConfirmMulligan(); ResetDemo(); await Wait(1.2);
            Check(sent.Count == 0 && !MulliganMotionActive && !_mulligan.Visible, "退出后仍提交旧换牌");
            GD.Print("MULLIGAN_MOTION_VERIFY_OK zero-one-all red-stamp kept-still duplicate-confirm authoritative-arrivals turn-order quiet cancel-cleanup");
        }
        finally { CommandRequested -= Sent; PresentationPhase -= Phase; ResetDemo(); _clock.SetSpeed(speed); _clock.SetReducedMotion(quiet); }
    }
}
