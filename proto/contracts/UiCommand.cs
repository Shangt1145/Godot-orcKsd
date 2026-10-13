namespace Kards.Ui.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// 前端输入契约（迁移后仍归前端）：view 三件已上移至引擎中立程序集 Orc.UI.Contracts，
// 本文件只剩 UI → 引擎的**输入命令**（语义 + string uid，不携带引擎引用）。
// ─────────────────────────────────────────────────────────────────────────────

public abstract record UiCommand;
public sealed record PlayCard(string Uid, int? SupportIndex = null, string? TargetUid = null, string? GapId = null) : UiCommand;
public sealed record MoveUnit(string Uid, string ToZone, int? SlotIndex = null) : UiCommand;
/// <summary>
/// A drag on a unit the player owns. The gesture is stated, the decision is not: the engine merges the
/// move and attack candidates and dispatches on what was pointed at — enemy unit or headquarters for an
/// attack, empty slot for a move (OrC-KSD's <c>CommandManager.BeginCommandAsync</c> →
/// <c>DispatchSelectedAsync</c>). Splitting this into two commands makes the UI guess which it was, and a
/// drop on something that turns out to be an illegal target then reads as "nothing happened".
///
/// <paramref name="TargetUid"/> and <paramref name="SlotIndex"/> are what the pointer landed on, handed on
/// so the bridge can answer the engine's question with it. Both null means the drop named nothing the
/// engine can be told about; the bridge refuses the incomplete gesture.
/// </summary>
public sealed record CommandUnit(string Uid, string? TargetUid = null, int? SlotIndex = null) : UiCommand;
public sealed record AttackUnit(string AttackerUid, string DefenderUid) : UiCommand;
public sealed record RetreatUnit(string Uid) : UiCommand;
public sealed record EndTurn : UiCommand;
public sealed record Concede : UiCommand;
public sealed record ChooseMulligan(IReadOnlyList<string> KeepUids, string? RequestId = null) : UiCommand;
public sealed record ChooseTarget(string Uid) : UiCommand;
