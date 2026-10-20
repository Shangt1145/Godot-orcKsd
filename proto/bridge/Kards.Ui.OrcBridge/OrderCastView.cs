using Orc.Cards;
using Orc.Core;

namespace Kards.Ui.OrcBridge;

/// <summary>
/// 指令施放的载荷视图。
/// <para>
/// 只声明 OrderFlow 实际下发的三个键（<c>Source</c> / <c>Target</c> / <c>Amount</c>）。
/// **不能用引擎的 <see cref="OrderFlowView"/> 当 <c>ActiveEffect</c> 的视图**：它还有必填的 <c>Orders</c>，
/// 而 <c>ActiveEffect.CastAsync</c> 的施放载荷里不带这个键；
/// <c>ContextViewBinder.EnsureRequired</c> 会因此抛 <c>KeyNotFoundException</c>，
/// 异常又被 <c>Trigger.InvokeAsync</c> 的契约兜底吞掉——
/// 表现就是「效果确实在 card.Effects 里，但 handler 永远不执行」。
/// </para>
/// </summary>
[ContextView]
public class OrderCastView
{
    /// <summary>施放来源（持有该效果的指令卡）。</summary>
    [Read]
    public virtual Card Source { get; set; } = null!;

    /// <summary>施放目标。引擎只保留捕获箱里第一个可解析的 <see cref="Card"/>（可空）。</summary>
    [Optional]
    [Read]
    public virtual Card? Target { get; set; }

    /// <summary>数值（当前指令施放固定为 0）。</summary>
    [Mutate]
    public virtual int Amount { get; set; }
}
