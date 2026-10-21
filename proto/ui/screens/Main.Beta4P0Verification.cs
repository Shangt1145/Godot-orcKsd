using Godot;
using Kards.Ui.Contracts;

namespace Kards.Ui;

public partial class Main
{
    private async void VerifyBeta4P0()
    {
        try
        {
            void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); }
            var folder = ProjectSettings.GlobalizePath("res://artifacts/beta4-p0/" + GetWindow().Size.X);
            Directory.CreateDirectory(folder);
            StopRealMatch(); Show("battle");
            var runner = new OrcMatchRunner { FirstPlayerIndex = 1, DeckSeed = 20261021, InteractiveMulligan = true };
            var arrivals = new List<UiDrawPresentation>();
            var turns = new List<UiTurnPresentation>();
            runner.PresentationReady += (resolution, _) =>
            {
                arrivals.AddRange(resolution.Steps.OfType<UiDrawPresentation>());
                turns.AddRange(resolution.Steps.OfType<UiTurnPresentation>().Where(t => t.Kind == UiTurnKind.TurnStarted));
                GD.Print($"BETA4_PRESENTATION seq={resolution.Sequence} turn={resolution.After.Turn} side={resolution.After.ActivePlayerSide} steps={string.Join(',',resolution.Steps.Select(s=>s.GetType().Name))}");
            };
            BindRunner(runner);
            await runner.StartAsync();
            await runner.SubmitAsync(new ChooseMulligan(runner.CurrentView!.SelfHand.Select(c => c.Uid).ToArray(), runner.MulliganRequestId));
            Check(await AwaitBoardAsync(() => runner.ActiveSeatOnTurn && !runner.IsSubmitting
                && arrivals.Any(d => d.Side == "self") && !_battle.MulliganPanelVisible && _battle.EndTurnEnabled, 20), "后手 AI／回合开始抽牌未自行推进");
            Check(await AwaitIdleAsync(20), "无输入时抽牌动画未自行完成");
            Check(runner.CurrentView!.Turn >= 2, "AI 首回合未推进");
            Check(arrivals.Any(d => d.Side == "self"), "回合开始抽牌未进入演出队列");
            var count = arrivals.Count;
            var until = Time.GetTicksMsec() + 700;
            while (Time.GetTicksMsec() < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(arrivals.Count == count, "兜底轮询重播抽牌");
            Check(_battle.EndTurnEnabled, "过期敌方事件把已经开始的友方回合重新锁住");
            Check(turns.Select(t => (t.Side, t.Turn)).Distinct().Count() == turns.Count, "回合提示重播");
            Check(_battle.MatchesProjection(runner.CurrentView), "空闲等待后的画面与引擎不一致");
            GD.Print("BETA4_IDLE_TURN_VERIFY_OK second-player-ai interactive-mulligan-closed no-play-command automatic-draw polling-no-replay input-unlocked");
            StopRealMatch();

            var layer = new CanvasLayer { Layer = 40 }; AddChild(layer);
            var background = new ColorRect { Color = new Color("20251f"), Size = GetViewportRect().Size, MouseFilter = Control.MouseFilterEnum.Ignore };
            layer.AddChild(background);
            var pairs = new List<(BattleCard Battle, CardFace Workshop)>();
            var types = new[] { "unit", "order", "counter" };
            var width = Math.Min(160f, (GetViewportRect().Size.X - 100) / 6);
            for (var i = 0; i < types.Length; i++)
            {
                var definition = new UiCardDefinition { CardId = "diy/p0-face-" + types[i], Name = "同源卡面", CardType = types[i],
                    UnitType = "infantry", Set = "av76", Cost = 2, BaseAttack = 3, BaseDefense = 4, Text = "部署：抽1张牌。" };
                var card = new BattleCard { Position = new(20 + i * (width * 2 + 20), 60), Size = new(width, width * 1.402f) };
                layer.AddChild(card);
                card.Bind(new UiCardView { Uid = definition.CardId, Definition = definition, Visibility = Visibility.Full, EffectiveCost = 2,
                    EffectiveAttack = 3, EffectiveDefense = 4 }, BattleCardMode.Inspect, _textures);
                var workshop = new CardFace { Position = card.Position + new Vector2(width + 10, 0), Size = card.Size };
                layer.AddChild(workshop); workshop.SetDefinition(definition); workshop.SetIllustration(null);
                Check(card.UsesGeneratedFace && card.GeneratedFace is not null, "对局仍使用旧卡面");
                Check(card.GeneratedFace!.ActiveMaster == CardFace.MasterFor(types[i]), "对局母版未自动切换");
                pairs.Add((card, workshop));
            }
            for (var i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var shot = GetViewport().GetTexture().GetImage(); shot.SavePng(folder + "/shared-faces.png");
            var screenScale = new Vector2(shot.GetWidth(), shot.GetHeight()) / GetViewportRect().Size;
            foreach (var (battle, workshop) in pairs)
            {
                var different = 0; var total = 0;
                for (var y = 12; y < (int)battle.Size.Y - 12; y += 3)
                    for (var x = 12; x < (int)battle.Size.X - 12; x += 3)
                    {
                        var aPoint = (battle.Position + new Vector2(x, y)) * screenScale;
                        var bPoint = (workshop.Position + new Vector2(x, y)) * screenScale;
                        var a = shot.GetPixel((int)aPoint.X, (int)aPoint.Y);
                        var b = shot.GetPixel((int)bPoint.X, (int)bPoint.Y);
                        if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > .08f) different++;
                        total++;
                    }
                Check(different < total * .03f, "对局与工坊卡面像素不一致：" + battle.View!.Definition.CardType + $" {different}/{total}");
                battle.DrawFaceDown = true; battle.QueueRedraw();
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            shot = GetViewport().GetTexture().GetImage(); shot.SavePng(folder + "/hidden-faces.png");
            Check(pairs.All(p => !p.Battle.GeneratedFace!.IsVisibleInTree()), "牌背状态泄漏生成卡面");
            layer.QueueFree();
            GD.Print("BETA4_CARD_FACE_VERIFY_OK shared-renderer unit-order-counter paired-pixel-comparison hidden-face-no-leak");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); StopRealMatch(); GetTree().Quit(1); }
    }
}
