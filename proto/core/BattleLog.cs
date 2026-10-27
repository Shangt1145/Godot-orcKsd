using Kards.Ui.Contracts;

namespace Kards.Ui.Core;

/// <summary>战报条目类别。用于筛选与图标，不参与规则判定。</summary>
public enum BattleLogKind
{
    Turn, Deploy, Move, Order, Attack, Damage, Death, Discard, Burn, Counter, Mulligan, Shuffle, Status, Result
}

/// <summary>
/// 一条战报。<paramref name="Text"/> 是直接上屏的中文，由 <see cref="BattleLog"/> 从引擎给的数据拼出来——
/// 不猜规则、不补数字：伤害值、卡名、回合数都来自投影或表现步骤本身。
/// </summary>
public sealed record BattleLogEntry(int Turn, string Side, BattleLogKind Kind, string Text, string? CardId = null, int Amount = 0)
{
    public string SideLabel => Side == "enemy" ? "对手" : "我方";
}

/// <summary>
/// 一局之内的完整行动序列。
///
/// 数据来源只有两个：引擎侧的表现步骤（<see cref="UiPresentationResolution"/>）和前端自己发起的动作
/// （移动没有独立演出段，靠前后快照差分补记）。所以战报里出现的每一条都能在投影里找到出处。
///
/// 刻意不记的东西（写在这里，免得以后被当成遗漏）：
/// <list type="bullet">
/// <item>资源点变化：顶部资源条已经逐点显示，写进战报只会淹没真正的行动。</item>
/// <item>逐张抽牌：抽牌有手牌区动画，战报按"一次结算抽了几张"合并成一条。</item>
/// </list>
/// </summary>
public sealed class BattleLog
{
    /// <summary>条目上限。超了就丢最旧的——战报是给人看的，不是审计日志。</summary>
    public const int Capacity = 2000;

    private readonly List<BattleLogEntry> _entries = [];
    public IReadOnlyList<BattleLogEntry> Entries => _entries;
    public int Turn { get; private set; }
    public int Dropped { get; private set; }

    public void Reset()
    {
        _entries.Clear(); Turn = 0; Dropped = 0;
    }

    /// <summary>
    /// 把回合号同步到投影给出的值（只增不减）。
    /// 表现步骤里不一定有回合信息——"拖动进攻"这类前端自己记的条目就完全没有，
    /// 不同步就会写出一堆"0 回合"。
    /// </summary>
    public void Observe(int turn)
    {
        if (turn > Turn) Turn = turn;
    }

    public void Note(BattleLogKind kind, string side, string text, string? cardId = null, int amount = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _entries.Add(new BattleLogEntry(Turn, side, kind, text, cardId, amount));
        if (_entries.Count <= Capacity) return;
        var drop = _entries.Count - Capacity;
        _entries.RemoveRange(0, drop);
        Dropped += drop;
    }

    /// <summary>某一方的弃牌堆（含手牌上限烧掉的牌——两者的差别在界面上标出来，但都在这一堆里）。</summary>
    public IReadOnlyList<BattleLogEntry> Discards(string side) =>
        _entries.Where(e => e.Side == side && e.Kind is BattleLogKind.Discard or BattleLogKind.Burn).ToArray();

    /// <summary>把一次结算的表现步骤折成战报条目。顺序与引擎给的一致，不做重排。</summary>
    public void Ingest(UiPresentationResolution resolution)
    {
        // 结算后的投影才是回合号的权威值：步骤里没带回合时，条目也要落在正确回合上。
        Observe(resolution.After.Turn);
        var draws = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in resolution.Steps)
        {
            switch (step)
            {
                case UiTurnPresentation turn:
                    Turn = Math.Max(Turn, turn.Turn);
                    Note(turn.Kind == UiTurnKind.Fatigue ? BattleLogKind.Damage : BattleLogKind.Turn, turn.Side,
                        turn.Kind == UiTurnKind.Fatigue
                            ? $"疲劳：受到 {turn.Damage ?? 0} 点伤害"
                            : $"第 {turn.Turn} 回合 · {(turn.Side == "enemy" ? "对手" : "我方")}",
                        amount: turn.Damage ?? 0);
                    break;
                case UiMulliganPresentation mulligan:
                    Note(BattleLogKind.Mulligan, mulligan.Side, $"调度：换掉 {mulligan.Replaced} 张", amount: mulligan.Replaced);
                    break;
                case UiDeploymentPresentation deploy:
                    // 费用未知就不写"（? 费）"——投影没给就别在战报里留个问号。
                    var cost = deploy.Card.EffectiveCost is { } paid ? $"（{paid} 费）" : "";
                    Note(BattleLogKind.Deploy, deploy.Card.OwnerSide, $"部署 {Name(deploy.Card)}{cost}", deploy.Card.CardId);
                    break;
                case UiOrderPresentation order:
                    Note(BattleLogKind.Order, order.Card.OwnerSide, $"打出 {Name(order.Card)}", order.Card.CardId);
                    break;
                case UiDrawPresentation draw:
                    draws[draw.Side] = draws.GetValueOrDefault(draw.Side) + 1;
                    break;
                case UiBoardImpactsPresentation board:
                    foreach (var hit in board.Impacts) AddImpact(hit);
                    break;
                case UiRemovalPresentation removal:
                    Note(BattleLogKind.Death, removal.Card.OwnerSide, $"{Name(removal.Card)} 离场", removal.Card.CardId);
                    break;
                case UiDiscardPresentation discard:
                    var burned = discard.Kind == UiDiscardKind.Burn;
                    var verb = burned ? "手牌上限烧掉" : "弃掉";
                    // 对手的弃牌不带牌面（引擎只给本方可见的卡），写成"1 张牌"而不是"未知卡牌"。
                    Note(burned ? BattleLogKind.Burn : BattleLogKind.Discard, discard.Side,
                        discard.Card is { } discarded
                            ? $"{verb} {Name(discarded)}"
                            : $"{verb} 1 张牌（{Side(discard.Side)}手牌不可见）",
                        discard.Card?.CardId);
                    break;
                case UiCounterPresentation counter:
                    Note(BattleLogKind.Counter, counter.Side, counter.Stage switch
                    {
                        UiCounterStage.Armed => $"布置反制 {Name(counter.Card)}",
                        UiCounterStage.Disarmed => $"解除反制 {Name(counter.Card)}",
                        _ => counter.BlockedCard is { } blocked ? $"反制触发，拦下 {Name(blocked)}" : "反制触发",
                    }, counter.Card?.CardId);
                    break;
                case UiShufflePresentation shuffle:
                    Note(BattleLogKind.Shuffle, shuffle.Side, "洗牌");
                    break;
                case UiStatusPresentation status:
                    // Damaged 不记：同一次伤害已经由 board-impacts 记成"受到 N 点伤害"，
                    // 再记一条"受伤"就是同一件事写两遍。
                    if (status.Status == UiStatusKind.Damaged) break;
                    Note(BattleLogKind.Status, status.After.OwnerSide, StatusText(status), status.After.CardId);
                    break;
            }
        }
        foreach (var (side, count) in draws)
            Note(BattleLogKind.Status, side, $"抽牌 {count} 张", amount: count);
        if (resolution.After.Phase == "over")
            Note(BattleLogKind.Result, "self", $"对局结束 · {resolution.After.ResultTitle ?? "结果未提供"}"
                + (resolution.After.FinalTurn is { } finalTurn ? $"（第 {finalTurn} 回合）" : ""));
    }

    private void AddImpact(UiOrderImpact hit)
    {
        var name = Name(hit.Before);
        if (hit.After is null)
        {
            Note(BattleLogKind.Death, hit.Before.OwnerSide, $"{name} 阵亡", hit.Before.CardId);
            return;
        }
        if (hit.Damage <= 0) return;
        var source = hit.Source is { } src ? $"（{Name(src)}）" : "";
        Note(BattleLogKind.Damage, hit.Before.OwnerSide, $"{name} 受到 {hit.Damage} 点伤害{source}", hit.Before.CardId, hit.Damage);
    }

    private static string StatusText(UiStatusPresentation status) => status.Status switch
    {
        UiStatusKind.Heal => $"{Name(status.After)} 回复生命",
        UiStatusKind.Buff => $"{Name(status.After)} 属性提升",
        UiStatusKind.Damaged => $"{Name(status.After)} 受伤",
        UiStatusKind.Suppressed => $"{Name(status.After)} 被压制",
        UiStatusKind.Cleared => $"{Name(status.After)} 状态清除",
        UiStatusKind.CostChanged => $"{Name(status.After)} 费用变化",
        UiStatusKind.Silenced => $"{Name(status.After)} 被抑制",
        UiStatusKind.Veteran => $"{Name(status.After)} 成为老兵",
        UiStatusKind.Revealed => $"{Name(status.After)} 亮明",
        UiStatusKind.KeywordsChanged => $"{Name(status.After)} 词条变化",
        _ => $"{Name(status.After)} 类型变化",
    };

    private static string Name(UiCardView? card) =>
        card is null ? "未知卡牌" : card.Definition.Name.Length > 0 ? card.Definition.Name : card.CardId;

    private static string Side(string side) => side == "enemy" ? "对手" : "我方";
}
