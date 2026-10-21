namespace Kards.Ui.Contracts;

/// <summary>
/// 一局对战的数据源。单机与联机共用同一套接线，区别只在谁来推进它。
///
/// <para>
/// **为什么要有这层**：<see cref="OrcMatchRunner"/> 把引擎跑在客户端进程里（每帧 <see cref="Advance"/>），
/// 而联机模式下这张盘由服务端按座位投影推送，没有"帧"的概念。表现层（<c>BattleScreen</c>）只认
/// "给我一盘棋 + 我点了什么"这两个方向，本来就不该知道盘从哪来——这层接口就是那条缝。
/// </para>
///
/// <para>
/// **不进接口的东西**（有意为之，不是遗漏）：
/// <c>KeywordSkills</c>（远端暂无对应工具）、<c>CardPoolReport</c>（构筑期信息，联机由服务端校验卡组）、
/// <c>ActiveSeat</c> / <c>SwitchViewerForVerification</c>（本地验证夹具专用）、
/// <c>IsRunning</c>（单机的"局是否在跑"与联机的"连接是否活跃"不是同一件事）。
/// 远端实现如果将来需要，单独加，不要往这里塞语义不同的成员。
/// </para>
/// </summary>
public interface IMatchFeed
{
    /// <summary>本局标识；单机为 runner 自生成，联机取 <c>room.info.roomId</c>。</summary>
    string MatchId { get; }

    /// <summary>当前视角的整盘投影。对局开始前为 null（本地尚未建局／远端尚未收到首帧）。</summary>
    UiMatchView? CurrentView { get; }

    /// <summary>当前视角可执行的行动集。选靶挂起时调用方应视为空集。</summary>
    UiBattleActions CurrentActions { get; }

    bool IsSubmitting { get; }
    bool MulliganPending { get; }
    bool TargetPending { get; }
    string? MulliganRequestId { get; }

    /// <summary>输入门禁：回答"现在能不能点"。单机由引擎判定器驱动，联机由相位推导。</summary>
    Func<UiActionInteractionState>? InteractionStateReader { get; set; }

    /// <summary>提交一条契约命令。拒绝通过 <see cref="CommandRefused"/> 与 <see cref="HintRequested"/> 表达。</summary>
    Task SubmitAsync(UiCommand command, CancellationToken ct = default);

    /// <summary>
    /// 推进一帧。单机＝排空引擎队列并调度对手；联机＝空实现（数据由服务端推送驱动）。
    /// 调用方每帧调用一次，不要依赖它有副作用。
    /// </summary>
    void Advance(double delta);

    event Action<UiMatchView, UiBattleActions>? ProjectionReady;
    event Action<UiPresentationResolution, UiBattleActions>? PresentationReady;
    event Action<IReadOnlyList<UiOrderImpact>, UiMatchView, UiBattleActions>? CombatReady;
    event Action? MulliganRequested;
    event Action<UiTargetRequest, IUiTargetResponder>? TargetRequested;
    event Action? TargetClosed;
    event Action<UiCommand>? CommandRefused;
    event Action<string>? ErrorRaised;
    event Action<string>? HintRequested;
}
