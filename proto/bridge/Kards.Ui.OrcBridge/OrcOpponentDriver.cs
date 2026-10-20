using Orc.Ai;
using Orc.Game;
using Orc.Game.Players;
using Orc.Game.Ui;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 对手驱动：宿主循环调用引擎的 <see cref="AiPilot"/>（HeuristicPolicy），一次一步，直到回合交回。
/// 决策全在引擎侧（击杀优先 → 敌方总部 → 最强目标；失败意图拉黑不再尝试）；
/// 本类只负责步进节奏、预算与安全网——AI 没能自己交回回合时兜底，保住可玩性。
/// </summary>
public static class OrcOpponentDriver
{
    /// <summary>One turn stops after this many accepted actions. Public so the setup screen can show the real value.</summary>
    public const int MaxActions = 12;
    /// <summary>
    /// Ceiling for one engine request and for passing the turn. The driver answers requests on the
    /// viewer's behalf, so a request that refuses to settle must not strand the turn forever.
    /// </summary>
    public static readonly TimeSpan ActionBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 裸对局入口（测试夹具／无宿主场景）：没有选择器桥，AI 只做动作决策，
    /// 选靶交给桥原有的自动应答策略——行为等同引入 AiPilot 之前的驱动。
    /// </summary>
    public static Task PlayTurnAsync(Match match, Player opponent, Action<string>? log = null,
        CancellationToken ct = default, Action? actionCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(match);
        return PlayTurnAsync(match, null, match.UiProjections.CreateProvider(null, "opponent-driver"),
            opponent, log, ct, actionCompleted);
    }

    /// <summary>生产入口：带宿主投影与 AI 选择器桥（选择器挂起后由 AiPilot 决策）。</summary>
    public static Task PlayTurnAsync(OrcMatchHost host, OrcTargeterBridge bridge, Player opponent,
        Action<string>? log = null, CancellationToken ct = default, Action? actionCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        return PlayTurnAsync(host.Match, bridge, host.CreateAiProjectionProvider(), opponent, log, ct, actionCompleted);
    }

    private static async Task PlayTurnAsync(Match match, OrcTargeterBridge? bridge, IUiProjectionProvider? projection,
        Player opponent, Action<string>? log, CancellationToken ct, Action? actionCompleted)
    {
        var actions = 0;
        try
        {
            if (projection is null)
            {
                log?.Invoke("AI 投影提供者不可用（.ui 插件未装配），对手动作跳过。");
                return;
            }
            // AiPilot 默认占用座位 1（产品里玩家永远是座位 0）。
            var pilot = new AiPilot(new OrcAiGateway(match, bridge), projection);
            while (match.State == MatchState.InProgress && match.Phase == MatchPhase.Play
                && ReferenceEquals(match.CurrentPlayer, opponent) && actions < MaxActions)
            {
                AiStepResult step;
                try { step = await pilot.StepAsync(ct).WaitAsync(ActionBudget, ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception e)
                {
                    log?.Invoke($"ai step failed: {e.GetType().Name}: {e.Message}");
                    break;
                }
                actionCompleted?.Invoke();
                if (!step.Performed) break;   // 未轮到/无事可做
                if (step.TurnEnded) break;    // 回合已交回
                actions++;
                await Task.Delay(80, ct);
            }
        }
        finally
        {
            // 安全网：AI 没能自己交回回合（Idle 但仍是对手回合、或中途异常），替它交。
            if (match.State == MatchState.InProgress && ReferenceEquals(match.CurrentPlayer, opponent))
            {
                try { await match.EndTurn(ct).WaitAsync(ActionBudget, ct); }
                catch (Exception e) { log?.Invoke($"opponent could not pass the turn: {e.GetType().Name}: {e.Message}"); }
                actionCompleted?.Invoke();
            }
        }
    }
}
